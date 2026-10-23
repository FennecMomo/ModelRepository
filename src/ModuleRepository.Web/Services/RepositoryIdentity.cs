using ModuleRepository.Contracts;

namespace ModuleRepository.Web.Services;

public sealed class RepositoryIdentity
{
    public string Id { get; }
    // Constructed after acquiring the existing data-directory process lock.
    public RepositoryIdentity(RepositoryPaths paths)
    {
        var path = Path.Combine(paths.Root, "repository-id.json");
        if (File.Exists(path))
        {
            var value = System.Text.Json.JsonSerializer.Deserialize<IdentityFile>(File.ReadAllText(path), RepositoryProtocol.Json);
            if (value is null || !Guid.TryParseExact(value.RepositoryId, "N", out _))
                throw new InvalidDataException("仓库身份文件无效，请恢复原 repository-id.json，避免改变项目模块来源。");
            Id = value.RepositoryId;
        }
        else
        {
            Id = Guid.NewGuid().ToString("N");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(new IdentityFile(Id), RepositoryProtocol.Json));
            File.Move(temporary, path);
        }
    }
    private sealed record IdentityFile(string RepositoryId);
}
