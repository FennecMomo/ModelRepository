using System.Text.Json;

namespace ModuleRepository.Web;

public sealed record RepositoryAccessSettings(bool RequireLogin);
public static class BuildInfo
{
    public static string Version => typeof(BuildInfo).Assembly.GetName().Version!.ToString(3);
}

public static class JsonData
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidOperationException("存储数据无效。");
}

public sealed record Credentials(string Username, string Password);
public sealed record CreateAccount(string Username, string Password, string DisplayName, bool IsAdmin = false);
public sealed record UpdateAccount(string DisplayName, bool Enabled, bool IsAdmin, string? NewPassword);
public sealed record SaveCategory(string Name, string? ParentId);
public sealed record CreateModule(string Id, ModuleMetadata Metadata);
public sealed record SaveDraft(ModuleMetadata Metadata, long ExpectedDraftRevision);
public sealed record PublishModule(string Version, string ChangeNotes, long ExpectedDraftRevision);
public sealed record GrantInput(string Username, string Role);
public sealed record SnippetInput(string Path, string Content, long ExpectedDraftRevision);
public sealed record DraftFileDelete(string Path, long ExpectedDraftRevision);
public sealed record TokenInput(string Name);
public sealed record EnableInput(bool Enabled);

public sealed record ModuleMetadata
{
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Language { get; init; } = "";
    public string Environment { get; init; } = "";
    public string? CategoryId { get; init; }
    public string[] Tags { get; init; } = [];
    public string Documentation { get; init; } = "";
    public string Dependencies { get; init; } = "";
    public string Limitations { get; init; } = "";
    public string Validation { get; init; } = "未验证";
    public string Source { get; init; } = "";
    public string License { get; init; } = "";
}

public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
    public static ApiException Bad(string message) => new(400, message);
    public static ApiException Missing() => new(404, "模块不存在或无权访问。");
    public static ApiException Forbidden() => new(403, "没有执行此操作的权限。");
    public static ApiException Conflict(string message) => new(409, message);
}
