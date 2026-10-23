using ModuleRepository.Contracts;

namespace ModuleRepository.Client;

public class RepositoryClientException(string code, string message, int? httpStatus = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
}

public sealed record InstalledModule
{
    public string InstallationId { get; init; } = "";
    public string RepositoryId { get; init; } = "";
    public string ServerUrl { get; init; } = "";
    public string ModuleId { get; init; } = "";
    public string Name { get; init; } = "";
    public string TargetDirectory { get; init; } = "";
    public string Version { get; init; } = "";
    public long Revision { get; init; }
    public string ContentHash { get; init; } = "";
    public DateTime InstalledAt { get; init; }
    public List<ModuleFileInfo> Files { get; init; } = [];
}

public sealed record ProjectInstallState
{
    public int SchemaVersion { get; init; } = 1;
    public List<InstalledModule> Installations { get; init; } = [];
    public List<PendingInstall> PendingOperations { get; init; } = [];
}

public sealed record PendingInstall
{
    public InstalledModule Target { get; init; } = new();
    public string Status { get; set; } = "applying";
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public string? Error { get; set; }
    public List<FileOperationResult> CompletedFiles { get; init; } = [];
}

public enum FileOperationKind { Add, Overwrite, Delete, Unchanged, Preserve, Conflict }
public sealed record FileObservation(string Kind, string? Hash = null);
public sealed record PlannedFile(string Path, FileOperationKind Kind, FileObservation Before, string? Note = null);
public sealed record FileOperationResult(string Path, FileOperationKind Kind);

public sealed class InstallPlan
{
    public string ProjectDirectory { get; init; } = "";
    public InstalledModule Target { get; init; } = new();
    public List<PlannedFile> Files { get; init; } = [];
    public bool HasConflicts => Files.Any(x => x.Kind == FileOperationKind.Conflict);
    public string? PreviousVersion { get; init; }
    public bool RecoveringIncompleteOperation { get; init; }
    internal PreparedModule Package { get; init; } = null!;
    internal string RecordFingerprint { get; init; } = "";
}

public sealed record InstallResult
{
    public bool Success { get; init; }
    public string Status { get; init; } = "installed";
    public string InstallationId { get; init; } = "";
    public string ModuleId { get; init; } = "";
    public string TargetDirectory { get; init; } = "";
    public string Version { get; init; } = "";
    public List<FileOperationResult> CompletedFiles { get; init; } = [];
    public List<string> RemainingFiles { get; init; } = [];
    public string? Error { get; init; }
}

public sealed class InstallApplyException(InstallResult result) : RepositoryClientException("incomplete", result.Error ?? "安装未完成。")
{
    public InstallResult Result { get; } = result;
}

public sealed record UpdateCheck(string InstallationId, string ModuleId, string TargetDirectory,
    string InstalledVersion, string Status, string? LatestVersion = null, long? LatestRevision = null, string? Message = null);

public sealed class PreparedModule
{
    public PublishedModuleManifest Manifest { get; init; } = new();
    internal Dictionary<string, byte[]> Contents { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    internal byte[] Zip { get; init; } = [];
}
