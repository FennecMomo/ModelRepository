using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ModuleRepository.Web.Persistence;

namespace ModuleRepository.Web.Services;

public sealed partial class ModuleService(RepositoryDb db, RepositoryGate gate, ContentStore store,
    ILogger<ModuleService> logger)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,79}$")]
    private static partial Regex ModuleIdPattern();
    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$")]
    private static partial Regex VersionPattern();

    public bool CanManage(Module module, ClaimsPrincipal actor, string? grant)
        => !Security.IsToken(actor) && (Security.IsAdmin(actor) || module.OwnerId == Security.UserId(actor) || grant == "maintainer");
    public bool CanGrant(Module module, ClaimsPrincipal actor)
        => !Security.IsToken(actor) && (Security.IsAdmin(actor) || module.OwnerId == Security.UserId(actor));

    private async Task<(Module Module, bool Manage, bool Grant)> AccessAsync(string id, ClaimsPrincipal actor,
        bool manage = false, bool grant = false)
    {
        var userId = Security.UserId(actor);
        var module = await db.Modules.SingleOrDefaultAsync(x => x.Id == id) ?? throw ApiException.Missing();
        var role = await db.Grants.Where(x => x.ModuleId == id && x.UserId == userId).Select(x => x.Role).SingleOrDefaultAsync();
        var adminRead = actor.IsInRole("admin");
        if (!adminRead && module.OwnerId != userId && role is null) throw ApiException.Missing();
        var mayManage = CanManage(module, actor, role);
        var mayGrant = CanGrant(module, actor);
        if ((!module.Enabled || module.PublishedJson is null) && !mayManage) throw ApiException.Missing();
        if ((manage && !mayManage) || (grant && !mayGrant)) throw ApiException.Forbidden();
        return (module, mayManage, mayGrant);
    }

    public async Task<object> ListAsync(ClaimsPrincipal actor, string? query, string? categoryId,
        string? language, string? environment, string? tag)
    {
        var userId = Security.UserId(actor);
        var modules = await db.Modules.AsNoTracking().Where(x => actor.IsInRole("admin") || x.OwnerId == userId
            || db.Grants.Any(g => g.ModuleId == x.Id && g.UserId == userId)).ToListAsync();
        var grants = await db.Grants.AsNoTracking().Where(x => x.UserId == userId).ToDictionaryAsync(x => x.ModuleId, x => x.Role);
        var categoryIds = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(categoryId))
        {
            categoryIds.Add(categoryId);
            var categories = await db.Categories.AsNoTracking().ToListAsync();
            bool changed;
            do
            {
                changed = false;
                foreach (var category in categories)
                    if (category.ParentId is not null && categoryIds.Contains(category.ParentId)) changed |= categoryIds.Add(category.Id);
            } while (changed);
        }
        var results = new List<object>();
        foreach (var module in modules.OrderByDescending(x => x.UpdatedAt))
        {
            var mayManage = CanManage(module, actor, grants.GetValueOrDefault(module.Id));
            if ((!module.Enabled || module.PublishedJson is null) && !mayManage) continue;
            var metadata = JsonData.Read<ModuleMetadata>(mayManage ? module.DraftJson : module.PublishedJson!);
            var haystack = $"{module.Id} {metadata.Name} {metadata.Summary} {metadata.Documentation} {string.Join(' ', metadata.Tags)}";
            var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (terms.Any(x => !haystack.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
            if (categoryIds.Count > 0 && !categoryIds.Contains(metadata.CategoryId ?? "")) continue;
            if (!string.IsNullOrWhiteSpace(language) && !metadata.Language.Contains(language, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(environment) && !metadata.Environment.Contains(environment, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(tag) && !metadata.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            results.Add(new { module.Id, Metadata = metadata, module.Enabled, module.Revision,
                module.PublishedVersion, module.PublishedAt, module.UpdatedAt, CanManage = mayManage });
        }
        return results;
    }

    public async Task<object> GetAsync(string id, ClaimsPrincipal actor)
    {
        using var held = await gate.EnterAsync();
        var (module, manage, grant) = await AccessAsync(id, actor);
        var owner = await db.Accounts.AsNoTracking().Where(x => x.Id == module.OwnerId)
            .Select(x => new { x.Id, x.Username, x.DisplayName }).SingleAsync();
        return new { module.Id, Owner = owner, module.Enabled, CanManage = manage, CanGrant = grant,
            Draft = manage ? JsonData.Read<ModuleMetadata>(module.DraftJson) : null,
            DraftRevision = manage ? module.DraftRevision : (long?)null,
            Publication = module.PublishedJson is null ? null : new { Metadata = JsonData.Read<ModuleMetadata>(module.PublishedJson),
                Version = module.PublishedVersion, module.Revision, module.ChangeNotes, module.ContentHash, module.PublishedAt },
            DraftFiles = manage ? await FileViewsAsync(id, "draft") : null,
            PublishedFiles = await FileViewsAsync(id, "published") };
    }

    public async Task RequireManageAsync(string id, ClaimsPrincipal actor)
    {
        await AccessAsync(id, actor, manage: true);
        // Multipart parsing happens after this early authorization check. Do not retain a
        // tracked draft that could become stale while the upload body is being read.
        db.ChangeTracker.Clear();
    }

    private async Task<object> FileViewsAsync(string id, string kind) => await db.Files.AsNoTracking()
        .Where(x => x.ModuleId == id && x.Kind == kind).OrderBy(x => x.Path)
        .Select(x => new { x.Path, x.Hash, x.Size }).ToListAsync();

    private async Task<ModuleMetadata> ValidateMetadataAsync(ModuleMetadata input, bool publishing = false)
    {
        if (input.Tags is null || input.Tags.Length > 20) throw ApiException.Bad("标签最多 20 个。");
        if (input.CategoryId is not null && !await db.Categories.AnyAsync(x => x.Id == input.CategoryId))
            throw ApiException.Bad("分类不存在。");
        if (publishing && input.CategoryId is null) throw ApiException.Bad("发布前请选择分类。");
        return input with {
            Name = AccountService.Text(input.Name, 100, "模块名称"),
            Summary = AccountService.Text(input.Summary, 500, "功能摘要"),
            Language = AccountService.Text(input.Language, 100, "开发语言"),
            Environment = AccountService.Text(input.Environment, 300, "应用环境"),
            Tags = input.Tags.Select(x => AccountService.Text(x, 40, "标签")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Documentation = AccountService.Text(input.Documentation, 100_000, "使用文档", publishing),
            Dependencies = AccountService.Text(input.Dependencies, 10_000, "依赖说明", false),
            Limitations = AccountService.Text(input.Limitations, 10_000, "已知限制", false),
            Validation = AccountService.Text(input.Validation, 10_000, "验证说明", false),
            Source = AccountService.Text(input.Source, 1000, "来源", false),
            License = AccountService.Text(input.License, 1000, "使用许可", false)
        };
    }

    private static void CheckDraft(Module module, long revision)
    {
        if (module.DraftRevision != revision) throw ApiException.Conflict("草稿已被其他操作修改，请重新加载后再保存。");
    }
    private static void Touch(Module module)
    { module.DraftRevision++; module.UpdatedAt = DateTime.UtcNow; }

    public async Task<string> CreateAsync(CreateModule input, ClaimsPrincipal actor)
    {
        using var held = await gate.EnterAsync();
        var id = (input.Id ?? "").Trim();
        if (!ModuleIdPattern().IsMatch(id)) throw ApiException.Bad("模块 ID 须为 2–80 位小写字母、数字、点、下划线或短横线，以字母或数字开头。");
        // Do not reveal whether a private ID already exists.
        if (await db.Modules.AnyAsync(x => x.Id == id)) throw ApiException.Conflict("该 ID 不可使用，请选择其他 ID。");
        var metadata = await ValidateMetadataAsync(input.Metadata);
        db.Modules.Add(new Module { Id = id, OwnerId = Security.UserId(actor), CategoryId = metadata.CategoryId,
            DraftJson = JsonData.Serialize(metadata) });
        await db.SaveChangesAsync();
        return id;
    }

    public async Task SaveDraftAsync(string id, SaveDraft input, ClaimsPrincipal actor)
    {
        using var held = await gate.EnterAsync();
        var (module, _, _) = await AccessAsync(id, actor, manage: true);
        CheckDraft(module, input.ExpectedDraftRevision);
        var metadata = await ValidateMetadataAsync(input.Metadata);
        module.DraftJson = JsonData.Serialize(metadata);
        module.CategoryId = metadata.CategoryId;
        Touch(module);
        await db.SaveChangesAsync();
    }

    public async Task SaveFilesAsync(string id, List<IncomingFile> incoming, bool replace, long revision, ClaimsPrincipal actor)
    {
        using var held = await gate.EnterAsync();
        var (module, _, _) = await AccessAsync(id, actor, manage: true);
        CheckDraft(module, revision);
        ContentStore.ValidateSet(incoming.Select(x => (x.Path, x.Bytes.LongLength)));
        var existing = await db.Files.Where(x => x.ModuleId == id && x.Kind == "draft").ToListAsync();
        var incomingKeys = incoming.Select(x => x.Path.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var retained = replace ? [] : existing.Where(x => !incomingKeys.Contains(x.PathKey)).ToList();
        ContentStore.ValidateSet(retained.Select(x => (x.Path, x.Size)).Concat(incoming.Select(x => (x.Path, x.Bytes.LongLength))));
        foreach (var file in existing.Where(x => replace || incomingKeys.Contains(x.PathKey))) db.Files.Remove(file);
        // Flush deletes and replacement inserts in one transaction to avoid tracked duplicate keys.
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.SaveChangesAsync();
        foreach (var file in incoming)
        {
            var hash = await store.PutAsync(file.Bytes);
            db.Files.Add(new ModuleFile { ModuleId = id, Path = file.Path, PathKey = file.Path.ToUpperInvariant(),
                Hash = hash, Size = file.Bytes.LongLength });
        }
        Touch(module);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await CollectAsync();
    }

    public async Task DeleteFileAsync(string id, DraftFileDelete input, ClaimsPrincipal actor)
    {
        using var held = await gate.EnterAsync();
        var (module, _, _) = await AccessAsync(id, actor, manage: true);
        CheckDraft(module, input.ExpectedDraftRevision);
        var key = ContentStore.NormalizePath(input.Path).ToUpperInvariant();
        var file = await db.Files.SingleOrDefaultAsync(x => x.ModuleId == id && x.Kind == "draft" && x.PathKey == key)
            ?? throw new ApiException(404, "文件不存在。");
        db.Files.Remove(file);
        Touch(module);
        await db.SaveChangesAsync();
        await CollectAsync();
    }

    public async Task PublishAsync(string id, PublishModule input, ClaimsPrincipal actor)
    {
        using var held = await gate.EnterAsync();
        var (module, _, _) = await AccessAsync(id, actor, manage: true);
        CheckDraft(module, input.ExpectedDraftRevision);
        if (!module.Enabled) throw ApiException.Bad("停用的模块不能发布。");
        var version = AccountService.Text(input.Version, 60, "版本号");
        if (!VersionPattern().IsMatch(version)) throw ApiException.Bad("版本号使用 1.0.0 或 1.0.0-beta 形式。");
        if (module.PublishedVersion == version) throw ApiException.Conflict("请使用新的版本号发布，当前版本不能原地覆盖。");
        var notes = AccountService.Text(input.ChangeNotes, 10_000, "本次更新说明");
        var metadata = await ValidateMetadataAsync(JsonData.Read<ModuleMetadata>(module.DraftJson), true);
        var files = await db.Files.AsNoTracking().Where(x => x.ModuleId == id && x.Kind == "draft").OrderBy(x => x.Path).ToListAsync();
        if (files.Count == 0) throw ApiException.Bad("发布前至少上传一个文件。");
        ContentStore.ValidateSet(files.Select(x => (x.Path, x.Size)));
        foreach (var file in files) await store.ReadAsync(file);
        var serialized = JsonData.Serialize(metadata);
        var manifest = JsonData.Serialize(new { metadata, files = files.Select(x => new { x.Path, x.Hash, x.Size }) });
        var contentHash = ContentStore.Hash(Encoding.UTF8.GetBytes(manifest));
        await using var transaction = await db.Database.BeginTransactionAsync();
        var prior = await db.Files.Where(x => x.ModuleId == id && x.Kind == "published").ToListAsync();
        db.Files.RemoveRange(prior);
        await db.SaveChangesAsync();
        foreach (var file in files)
            db.Files.Add(new ModuleFile { ModuleId = id, Kind = "published", Path = file.Path,
                PathKey = file.PathKey, Hash = file.Hash, Size = file.Size });
        module.PublishedJson = serialized;
        module.PublishedVersion = version;
        module.ChangeNotes = notes;
        module.ContentHash = contentHash;
        module.Revision++;
        module.PublishedAt = DateTime.UtcNow;
        module.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await CollectAsync();
    }

    public async Task<byte[]> DownloadAsync(string id, long revision, ClaimsPrincipal actor)
    {
        using var held = await gate.EnterAsync();
        var (module, _, _) = await AccessAsync(id, actor);
        if (!module.Enabled || module.PublishedJson is null) throw ApiException.Missing();
        if (module.Revision != revision) throw ApiException.Conflict("当前发布已变化，请重新读取模块信息。");
        var files = await db.Files.AsNoTracking().Where(x => x.ModuleId == id && x.Kind == "published").OrderBy(x => x.Path).ToListAsync();
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Fastest);
                await using var target = entry.Open();
                var bytes = await store.ReadAsync(file);
                await target.WriteAsync(bytes);
            }
        }
        return output.ToArray();
    }

    public async Task<object> PreviewAsync(string id, string kind, string path, ClaimsPrincipal actor)
    {
        using var held = await gate.EnterAsync();
        await AccessAsync(id, actor, manage: kind == "draft");
        if (kind is not ("draft" or "published")) throw ApiException.Bad("文件区域无效。");
        var key = ContentStore.NormalizePath(path).ToUpperInvariant();
        var file = await db.Files.AsNoTracking().SingleOrDefaultAsync(x => x.ModuleId == id && x.Kind == kind && x.PathKey == key)
            ?? throw new ApiException(404, "文件不存在。");
        if (file.Size > 256 * 1024) return new { file.Path, Binary = true, Text = "文件超过 256 KiB，请下载查看。" };
        var bytes = await store.ReadAsync(file);
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Contains('\0')) throw new DecoderFallbackException();
            return new { file.Path, Binary = false, Text = text };
        }
        catch (DecoderFallbackException) { return new { file.Path, Binary = true, Text = "非 UTF-8 文本文件，请下载查看。" }; }
    }

    public async Task<object> GrantsAsync(string id, ClaimsPrincipal actor)
    {
        await AccessAsync(id, actor, grant: true);
        return await (from grant in db.Grants where grant.ModuleId == id
            join user in db.Accounts on grant.UserId equals user.Id
            select new { user.Id, user.Username, user.DisplayName, user.Enabled, grant.Role }).ToListAsync();
    }

    public async Task SetGrantAsync(string id, GrantInput input, ClaimsPrincipal actor)
    {
        using var held = await gate.EnterAsync();
        var (module, _, _) = await AccessAsync(id, actor, grant: true);
        if (input.Role is not ("reader" or "maintainer" or "none")) throw ApiException.Bad("授权角色无效。");
        var username = (input.Username ?? "").Trim().ToUpperInvariant();
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.NormalizedUsername == username)
            ?? throw new ApiException(404, "账号不存在。");
        if (account.Id == module.OwnerId) throw ApiException.Bad("所有者无需额外授权。");
        var existing = await db.Grants.FindAsync(id, account.Id);
        if (input.Role == "none") { if (existing is not null) db.Grants.Remove(existing); }
        else if (existing is not null) existing.Role = input.Role;
        else db.Grants.Add(new ModuleGrant { ModuleId = id, UserId = account.Id, Role = input.Role });
        await db.SaveChangesAsync();
    }

    public async Task SetEnabledAsync(string id, bool enabled, ClaimsPrincipal actor)
    {
        using var held = await gate.EnterAsync();
        var (module, _, _) = await AccessAsync(id, actor, grant: true);
        module.Enabled = enabled;
        await db.SaveChangesAsync();
    }

    public async Task CollectAsync()
    {
        var references = (await db.Files.AsNoTracking().Select(x => x.Hash).Distinct().ToListAsync()).ToHashSet(StringComparer.Ordinal);
        store.Collect(references, logger);
    }
}
