using System.Text.Json;
using ModuleRepository.Contracts;

namespace ModuleRepository.Client;

internal sealed class ProjectStore
{
    public string ProjectDirectory { get; }
    private string MetadataDirectory => Path.Combine(ProjectDirectory, ".module-repository");
    public string RecordPath => Path.Combine(MetadataDirectory, "modules.json");
    public ProjectStore(string project) => ProjectDirectory = LocalPaths.Project(project);

    public async Task<(ProjectInstallState State, string Fingerprint)> ReadAsync(CancellationToken cancellationToken = default)
    {
        LocalPaths.EnsureNoLinks(ProjectDirectory, RecordPath);
        if (!File.Exists(RecordPath)) return (new(), "missing");
        var bytes = await File.ReadAllBytesAsync(RecordPath, cancellationToken);
        ProjectInstallState state;
        try { state = JsonSerializer.Deserialize<ProjectInstallState>(bytes, RepositoryProtocol.Json)
            ?? throw new JsonException(); }
        catch (JsonException) { throw new RepositoryClientException("invalidProjectRecord", "项目安装记录无效，请通过项目 Git 检查或恢复 modules.json。"); }
        if (state.SchemaVersion != 1 || state.Installations is null || state.PendingOperations is null)
            throw new RepositoryClientException("invalidProjectRecord", "项目安装记录版本不兼容。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var installed in state.Installations) {
            Validate(installed);
            if (!ids.Add(installed.InstallationId)) throw new RepositoryClientException("invalidProjectRecord", "项目安装记录包含重复安装 ID。");
        }
        var pendingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pending in state.PendingOperations) {
            Validate(pending.Target);
            if (!pendingIds.Add(pending.Target.InstallationId)) throw new RepositoryClientException("invalidProjectRecord", "项目存在重复的未完成记录。");
        }
        return (state, RepositoryProtocol.Hash(bytes));
    }

    private void Validate(InstalledModule installed)
    {
        if (string.IsNullOrWhiteSpace(installed.InstallationId) || string.IsNullOrWhiteSpace(installed.RepositoryId)
            || string.IsNullOrWhiteSpace(installed.ModuleId) || installed.Revision < 1 || installed.Files is null
            || string.IsNullOrWhiteSpace(installed.TargetDirectory) || Path.IsPathRooted(installed.TargetDirectory))
            throw new RepositoryClientException("invalidProjectRecord", "项目模块记录不完整或包含本机绝对路径。");
        if (LocalPaths.RelativeTarget(ProjectDirectory, installed.TargetDirectory) != installed.TargetDirectory)
            throw new RepositoryClientException("invalidProjectRecord", "项目目标目录记录不是规范相对路径。");
        LocalPaths.ValidateFileSet(installed.Files);
    }

    public IDisposable Lock()
    {
        LocalPaths.EnsureNoLinks(ProjectDirectory, MetadataDirectory);
        Directory.CreateDirectory(MetadataDirectory);
        var path = Path.Combine(MetadataDirectory, "operation.lock");
        LocalPaths.EnsureNoLinks(ProjectDirectory, path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { throw new RepositoryClientException("projectBusy", "该项目已有另一个模块安装操作，请等待它结束后再执行。"); }
    }

    public async Task SaveAsync(ProjectInstallState state, CancellationToken cancellationToken = default)
    {
        LocalPaths.EnsureNoLinks(ProjectDirectory, RecordPath);
        await AtomicFile.WriteAsync(RecordPath, JsonSerializer.SerializeToUtf8Bytes(state, RepositoryProtocol.IndentedJson), cancellationToken);
    }
}
