using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ModuleRepository.Web.Persistence;

namespace ModuleRepository.Web.Services;

public sealed class RepositoryPaths
{
    public string Root { get; }
    public string Blobs => Path.Combine(Root, "blobs");
    public string Keys => Path.Combine(Root, "keys");
    public RepositoryPaths(IConfiguration config, IWebHostEnvironment environment)
    {
        Root = Path.GetFullPath(config["Repository:DataDirectory"] ?? "data", environment.ContentRootPath);
        Directory.CreateDirectory(Blobs);
        Directory.CreateDirectory(Keys);
    }
}

// Single-instance V1: serializes mutations and content reads against garbage collection.
public sealed class RepositoryGate
{
    private readonly SemaphoreSlim semaphore = new(1, 1);
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }
    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}

public sealed record IncomingFile(string Path, byte[] Bytes);

public sealed class ContentStore(RepositoryPaths paths)
{
    public const long MaxBytes = 32 * 1024 * 1024;
    public const int MaxFiles = 500;
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
      "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$" };

    public static string NormalizePath(string? raw)
    {
        var value = (raw ?? "").Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (value.Length is 0 or > 240 || value.StartsWith('/') || value.Contains(':'))
            throw ApiException.Bad("文件必须使用模块内相对路径，长度不超过 240 字符。");
        var segments = value.Split('/');
        foreach (var segment in segments)
        {
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c))
                || ReservedNames.Contains(segment.Split('.')[0].TrimEnd(' '))
                || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".module-repository", StringComparison.OrdinalIgnoreCase))
                throw ApiException.Bad("文件路径包含非法、保留或越界名称。");
        }
        return value;
    }

    public static void ValidateSet(IEnumerable<(string Path, long Size)> files)
    {
        var list = files.ToList();
        if (list.Count > MaxFiles || list.Sum(x => x.Size) > MaxBytes)
            throw ApiException.Bad("模块最多 500 个文件，未压缩总大小不超过 32 MiB。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in list)
        {
            var normalized = NormalizePath(file.Path);
            if (!paths.Add(normalized)) throw ApiException.Bad("文件路径重复（包括大小写差异）。");
        }
        foreach (var path in paths)
        {
            var index = path.IndexOf('/');
            while (index >= 0)
            {
                if (paths.Contains(path[..index])) throw ApiException.Bad("同一路径不能同时是文件和目录。");
                index = path.IndexOf('/', index + 1);
            }
        }
    }

    public static async Task<List<IncomingFile>> ReadUploadAsync(IFormFileCollection uploads, bool zip)
    {
        if (uploads.Count == 0) throw ApiException.Bad("请选择文件。");
        if (uploads.Count > MaxFiles) throw ApiException.Bad("一次最多上传 500 个文件。");
        var result = new List<IncomingFile>();
        long remaining = MaxBytes;
        if (zip)
        {
            if (uploads.Count != 1) throw ApiException.Bad("一次只能解包一个 ZIP。");
            await using var stream = uploads[0].OpenReadStream();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxFiles * 2) throw ApiException.Bad("ZIP 条目过多。");
            foreach (var entry in archive.Entries)
            {
                var mode = (entry.ExternalAttributes >> 16) & 0xF000;
                if (mode is not (0 or 0x8000 or 0x4000)) throw ApiException.Bad("ZIP 不允许包含链接或特殊文件。");
                if (entry.FullName.EndsWith('/'))
                {
                    NormalizePath(entry.FullName.TrimEnd('/'));
                    continue;
                }
                var path = NormalizePath(entry.FullName);
                if (entry.Length > remaining) throw ApiException.Bad("ZIP 解压后的总大小超过 32 MiB。");
                await using var input = entry.Open();
                var bytes = await ReadBoundedAsync(input, remaining);
                remaining -= bytes.LongLength;
                result.Add(new(path, bytes));
                if (result.Count > MaxFiles) throw ApiException.Bad("文件数量超过 500。");
            }
        }
        else
        {
            foreach (var file in uploads)
            {
                var path = NormalizePath(file.FileName);
                if (file.Length > remaining) throw ApiException.Bad("文件总大小超过 32 MiB。");
                await using var input = file.OpenReadStream();
                var bytes = await ReadBoundedAsync(input, remaining);
                remaining -= bytes.LongLength;
                result.Add(new(path, bytes));
            }
        }
        if (result.Count == 0) throw ApiException.Bad("上传内容中没有文件。");
        ValidateSet(result.Select(x => (x.Path, x.Bytes.LongLength)));
        return result;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, long limit)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer)) > 0)
        {
            if (output.Length + count > limit) throw ApiException.Bad("内容超出 32 MiB 限制。");
            await output.WriteAsync(buffer.AsMemory(0, count));
        }
        return output.ToArray();
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public async Task<string> PutAsync(byte[] bytes)
    {
        var hash = Hash(bytes);
        var destination = Path.Combine(paths.Blobs, hash);
        var validExisting = File.Exists(destination) && new FileInfo(destination).Length == bytes.LongLength
            && Hash(await File.ReadAllBytesAsync(destination)) == hash;
        if (!validExisting)
        {
            var temporary = Path.Combine(paths.Blobs, $".upload-{Guid.NewGuid():N}");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes);
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return hash;
    }

    public async Task<byte[]> ReadAsync(ModuleFile file)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(paths.Blobs, file.Hash));
        if (bytes.LongLength != file.Size || Hash(bytes) != file.Hash)
            throw new IOException("存储内容校验失败。");
        return bytes;
    }

    public void Collect(ISet<string> referenced, ILogger logger)
    {
        foreach (var path in Directory.EnumerateFiles(paths.Blobs))
        {
            if (referenced.Contains(Path.GetFileName(path))) continue;
            try { File.Delete(path); }
            catch (IOException) { logger.LogWarning("内容清理暂未完成，将在下次操作或启动时重试。"); }
            catch (UnauthorizedAccessException) { logger.LogWarning("内容清理权限不足，将在下次操作或启动时重试。"); }
        }
    }
}
