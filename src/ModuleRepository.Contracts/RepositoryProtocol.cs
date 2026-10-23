using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModuleRepository.Contracts;

public static class RepositoryProtocol
{
    public const int SchemaVersion = 1;
    public const int MaxFiles = 500;
    public const long MaxContentBytes = 32 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = CreateJson();
    public static readonly JsonSerializerOptions IndentedJson = new(Json) { WriteIndented = true };
    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string ManifestHash(ModuleMetadata metadata, IEnumerable<ModuleFileInfo> files)
        => Hash(JsonSerializer.SerializeToUtf8Bytes(new {
            metadata, files = files.Select(x => new { x.Path, x.Hash, x.Size }) }, Json));
}

public sealed record RepositoryInfo(string RepositoryId, string Version, int SchemaVersion = RepositoryProtocol.SchemaVersion);

public sealed record ModuleFileInfo(string Path, string Hash, long Size);

public sealed record PublishedModuleManifest
{
    public int SchemaVersion { get; init; } = RepositoryProtocol.SchemaVersion;
    public string RepositoryId { get; init; } = "";
    public string ModuleId { get; init; } = "";
    public string Version { get; init; } = "";
    public long Revision { get; init; }
    public string ContentHash { get; init; } = "";
    public DateTime PublishedAt { get; init; }
    public string ChangeNotes { get; init; } = "";
    public ModuleMetadata Metadata { get; init; } = new();
    public List<ModuleFileInfo> Files { get; init; } = [];
}

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
