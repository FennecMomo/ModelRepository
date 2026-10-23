using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModuleRepository.Contracts;

namespace ModuleRepository.Client;

public sealed class RepositoryClient
{
    private readonly HttpClient http;
    public Uri Server { get; }

    public RepositoryClient(HttpClient http, string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new RepositoryClientException("invalidServer", "请提供 HTTP(S) 仓库根地址，不在地址中携带凭据或查询参数。");
        this.http = http;
        Server = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    private async Task<HttpResponseMessage> GetAsync(string route, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try { response = await http.GetAsync(new Uri(Server, route), HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException) { throw new RepositoryClientException("unavailable", "无法连接模块仓库，请检查服务地址和运行状态。"); }
        if (response.IsSuccessStatusCode) return response;
        var status = (int)response.StatusCode;
        var message = response.StatusCode switch {
            HttpStatusCode.Conflict => "当前发布已变化，请重新执行下载或安装计划。",
            HttpStatusCode.NotFound => "模块不存在、未发布、不可访问，或服务端尚未提供阶段 B 接口。",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "当前账号不能访问模块。自用模式不需要令牌；账号模式可设置 MODULEREPO_TOKEN。",
            _ => $"模块仓库返回错误（HTTP {status}）。"
        };
        response.Dispose();
        throw new RepositoryClientException(status == 409 ? "publicationChanged" : "requestFailed", message, status);
    }

    private async Task<T> ReadAsync<T>(string route, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(route, cancellationToken);
        try {
            return await response.Content.ReadFromJsonAsync<T>(RepositoryProtocol.Json, cancellationToken)
                ?? throw new RepositoryClientException("invalidResponse", "仓库返回了空数据。");
        }
        catch (JsonException) { throw new RepositoryClientException("invalidResponse", "仓库返回的数据格式无效，请检查服务版本。"); }
    }

    public async Task<RepositoryInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var info = await ReadAsync<RepositoryInfo>("api/repository", cancellationToken);
        if (info.SchemaVersion != RepositoryProtocol.SchemaVersion || string.IsNullOrWhiteSpace(info.RepositoryId))
            throw new RepositoryClientException("unsupportedProtocol", "仓库接入协议不兼容。");
        return info;
    }

    public async Task<PublishedModuleManifest> GetManifestAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var manifest = await ReadAsync<PublishedModuleManifest>($"api/modules/{Uri.EscapeDataString(moduleId)}/manifest", cancellationToken);
        if (manifest.SchemaVersion != RepositoryProtocol.SchemaVersion || manifest.ModuleId != moduleId || manifest.Revision < 1
            || string.IsNullOrWhiteSpace(manifest.RepositoryId) || string.IsNullOrWhiteSpace(manifest.Version)
            || manifest.Metadata is null || manifest.Files is null || manifest.Files.Count == 0)
            throw new RepositoryClientException("invalidManifest", "模块当前发布清单无效。");
        LocalPaths.ValidateFileSet(manifest.Files);
        if (RepositoryProtocol.ManifestHash(manifest.Metadata, manifest.Files) != manifest.ContentHash)
            throw new RepositoryClientException("invalidManifest", "模块元数据与文件清单的内容哈希不一致。");
        return manifest;
    }

    public Task<JsonElement> SearchAsync(string query = "", string? language = null, string? environment = null,
        string? tag = null, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string?> { ["q"] = query, ["language"] = language,
            ["environment"] = environment, ["tag"] = tag };
        var suffix = string.Join('&', parameters.Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value!)}"));
        return ReadAsync<JsonElement>("api/modules?" + suffix, cancellationToken);
    }

    public async Task<PreparedModule> PrepareAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(cancellationToken);
        var manifest = await GetManifestAsync(moduleId, cancellationToken);
        if (manifest.RepositoryId != info.RepositoryId)
            throw new RepositoryClientException("repositoryChanged", "下载期间仓库身份变化，请重新执行。");
        using var response = await GetAsync($"api/modules/{Uri.EscapeDataString(moduleId)}/download?revision={manifest.Revision}", cancellationToken);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var zipBytes = await ReadBoundedAsync(input, RepositoryProtocol.MaxContentBytes + 1024 * 1024, cancellationToken);
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        if (zip.Entries.Count != manifest.Files.Count)
            throw new RepositoryClientException("invalidPackage", "下载包与当前文件清单数量不一致。");
        var expected = manifest.Files.ToDictionary(x => LocalPaths.ModulePath(x.Path), StringComparer.OrdinalIgnoreCase);
        var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var path = LocalPaths.ModulePath(entry.FullName);
            if (!expected.TryGetValue(path, out var file) || contents.ContainsKey(path) || entry.Length != file.Size)
                throw new RepositoryClientException("invalidPackage", "下载包路径、大小或重复文件与清单不一致。");
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode is not (0 or 0x8000)) throw new RepositoryClientException("invalidPackage", "模块包需要普通文件内容。");
            await using var stream = entry.Open();
            var bytes = await ReadBoundedAsync(stream, file.Size, cancellationToken);
            if (bytes.LongLength != file.Size || RepositoryProtocol.Hash(bytes) != file.Hash)
                throw new RepositoryClientException("invalidPackage", $"文件内容校验失败：{path}");
            contents.Add(path, bytes);
        }
        return new PreparedModule { Manifest = manifest, Contents = contents, Zip = zipBytes };
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, long limit, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0) {
            if (output.Length + count > limit) throw new RepositoryClientException("invalidPackage", "下载内容超过发布清单或模块大小限制。");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return output.ToArray();
    }

    public async Task<PublishedModuleManifest> DownloadAsync(string moduleId, string outputPath, CancellationToken cancellationToken = default)
    {
        var package = await PrepareAsync(moduleId, cancellationToken);
        var path = Path.GetFullPath(outputPath);
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new RepositoryClientException("invalidOutput", "下载输出请使用 .zip 文件路径。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicFile.WriteAsync(path, package.Zip, cancellationToken);
        await AtomicFile.WriteAsync(path + ".manifest.json",
            JsonSerializer.SerializeToUtf8Bytes(package.Manifest, RepositoryProtocol.IndentedJson), cancellationToken);
        return package.Manifest;
    }
}
