using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using ModuleRepository.Web.Persistence;
using ModuleRepository.Web.Services;

namespace ModuleRepository.Web;

public static class Api
{
    public static void MapRepositoryApi(this WebApplication app)
    {
        app.MapGet("/api/session", async (HttpContext context, RepositoryDb db, IAntiforgery antiforgery) => {
            var tokens = antiforgery.GetAndStoreTokens(context);
            Account? user = null;
            if (context.User.Identity?.IsAuthenticated == true)
                user = await db.Accounts.AsNoTracking().FindAccountAsync(Security.UserId(context.User));
            return Results.Ok(new { needsSetup = !await db.Accounts.AnyAsync(),
                user = user is null ? null : AccountService.View(user), csrfToken = tokens.RequestToken });
        });
        app.MapPost("/api/setup", async (CreateAccount input, HttpContext context, AccountService accounts) => {
            if (!IsLocal(context)) throw new ApiException(403, "首次初始化须从服务所在电脑的 localhost 地址完成。");
            var account = await accounts.CreateAsync(input, bootstrap: true);
            await Security.SignInAsync(context, account);
            return Results.Ok(AccountService.View(account));
        }).RequireRateLimiting("login");
        app.MapPost("/api/login", async (Credentials input, HttpContext context, AccountService accounts) => {
            var account = await accounts.LoginAsync(input);
            if (account is null) throw new ApiException(401, "账号或密码错误，或账号已停用。");
            await Security.SignInAsync(context, account);
            return Results.Ok(AccountService.View(account));
        }).RequireRateLimiting("login");

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/logout", async (HttpContext context) => {
            await context.SignOutAsync(Security.CookieScheme);
            return Results.NoContent();
        });

        api.MapGet("/users", async (ClaimsPrincipal user, RepositoryDb db) => {
            Security.RequireAdmin(user);
            return Results.Ok(await db.Accounts.AsNoTracking().OrderBy(x => x.Username)
                .Select(x => new { x.Id, x.Username, x.DisplayName, x.IsAdmin, x.Enabled }).ToListAsync());
        });
        api.MapPost("/users", async (CreateAccount input, ClaimsPrincipal user, AccountService accounts) => {
            Security.RequireAdmin(user);
            return Results.Ok(AccountService.View(await accounts.CreateAsync(input)));
        });
        api.MapPut("/users/{id}", async (string id, UpdateAccount input, ClaimsPrincipal user, AccountService accounts) => {
            Security.RequireAdmin(user);
            await accounts.UpdateAsync(id, input);
            return Results.NoContent();
        });

        api.MapGet("/tokens", async (ClaimsPrincipal user, RepositoryDb db) => {
            if (Security.IsToken(user)) throw ApiException.Forbidden();
            var id = Security.UserId(user);
            return Results.Ok(await db.AccessTokens.AsNoTracking().Where(x => x.UserId == id)
                .Select(x => new { x.Id, x.Name, x.CreatedAt }).ToListAsync());
        });
        api.MapPost("/tokens", async (TokenInput input, ClaimsPrincipal user, RepositoryDb db, RepositoryGate gate) => {
            using var held = await gate.EnterAsync();
            var id = Security.UserId(user);
            if (await db.AccessTokens.CountAsync(x => x.UserId == id) >= 20) throw ApiException.Bad("最多保留 20 个访问令牌。");
            var raw = "mr_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var token = new AccessToken { UserId = id, Name = AccountService.Text(input.Name, 80, "令牌名称"),
                Hash = Security.TokenHash(raw) };
            db.AccessTokens.Add(token);
            await db.SaveChangesAsync();
            return Results.Ok(new { token.Id, token.Name, token.CreatedAt, secret = raw });
        });
        api.MapDelete("/tokens/{id}", async (string id, ClaimsPrincipal user, RepositoryDb db, RepositoryGate gate) => {
            using var held = await gate.EnterAsync();
            var userId = Security.UserId(user);
            var token = await db.AccessTokens.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId)
                ?? throw new ApiException(404, "令牌不存在。");
            db.AccessTokens.Remove(token);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        api.MapGet("/categories", async (RepositoryDb db) => Results.Ok(await db.Categories.AsNoTracking().OrderBy(x => x.Name).ToListAsync()));
        api.MapPost("/categories", async (SaveCategory input, ClaimsPrincipal user, RepositoryDb db, RepositoryGate gate) => {
            Security.RequireAdmin(user);
            using var held = await gate.EnterAsync();
            if (input.ParentId is not null && !await db.Categories.AnyAsync(x => x.Id == input.ParentId))
                throw ApiException.Bad("上级分类不存在。");
            var name = AccountService.Text(input.Name, 80, "分类名称");
            if (await db.Categories.AnyAsync(x => x.Name == name && x.ParentId == input.ParentId))
                throw ApiException.Conflict("同一层级已有同名分类。");
            var category = new Category { Name = name, ParentId = input.ParentId };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
            return Results.Ok(category);
        });
        api.MapPut("/categories/{id}", async (string id, SaveCategory input, ClaimsPrincipal user, RepositoryDb db, RepositoryGate gate) => {
            Security.RequireAdmin(user);
            using var held = await gate.EnterAsync();
            var category = await db.Categories.FindAsync(id) ?? throw new ApiException(404, "分类不存在。");
            var all = await db.Categories.AsNoTracking().ToListAsync();
            var cursor = input.ParentId;
            while (cursor is not null)
            {
                if (cursor == id) throw ApiException.Bad("分类不能移动到自身或子分类下面。");
                var parent = all.SingleOrDefault(x => x.Id == cursor) ?? throw ApiException.Bad("上级分类不存在。");
                cursor = parent.ParentId;
            }
            var name = AccountService.Text(input.Name, 80, "分类名称");
            if (all.Any(x => x.Id != id && x.ParentId == input.ParentId && x.Name == name))
                throw ApiException.Conflict("同一层级已有同名分类。");
            category.Name = name;
            category.ParentId = input.ParentId;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
        api.MapDelete("/categories/{id}", async (string id, ClaimsPrincipal user, RepositoryDb db, RepositoryGate gate) => {
            Security.RequireAdmin(user);
            using var held = await gate.EnterAsync();
            var category = await db.Categories.FindAsync(id) ?? throw new ApiException(404, "分类不存在。");
            var moduleJson = await db.Modules.AsNoTracking().Select(x => new { x.DraftJson, x.PublishedJson }).ToListAsync();
            if (await db.Categories.AnyAsync(x => x.ParentId == id) || moduleJson.Any(x =>
                JsonData.Read<ModuleMetadata>(x.DraftJson).CategoryId == id
                || (x.PublishedJson is not null && JsonData.Read<ModuleMetadata>(x.PublishedJson).CategoryId == id)))
                throw ApiException.Conflict("分类仍有子分类或被草稿、当前发布使用，不能删除。");
            db.Categories.Remove(category);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        api.MapGet("/modules", async (string? q, string? categoryId, string? language, string? environment, string? tag,
            ClaimsPrincipal user, ModuleService modules) => Results.Ok(await modules.ListAsync(user, q, categoryId, language, environment, tag)));
        api.MapPost("/modules", async (CreateModule input, ClaimsPrincipal user, ModuleService modules) => {
            if (input.Metadata is null) throw ApiException.Bad("请填写模块信息。");
            return Results.Ok(new { id = await modules.CreateAsync(input, user) });
        });
        api.MapGet("/modules/{id}", async (string id, ClaimsPrincipal user, ModuleService modules)
            => Results.Ok(await modules.GetAsync(id, user)));
        api.MapPut("/modules/{id}/draft", async (string id, SaveDraft input, ClaimsPrincipal user, ModuleService modules) => {
            if (input.Metadata is null) throw ApiException.Bad("请填写模块信息。");
            await modules.SaveDraftAsync(id, input, user);
            return Results.NoContent();
        });
        api.MapPost("/modules/{id}/files", async (string id, HttpContext context, ModuleService modules) => {
            await modules.RequireManageAsync(id, context.User);
            var form = await context.Request.ReadFormAsync();
            if (!long.TryParse(form["expectedDraftRevision"], out var revision)) throw ApiException.Bad("缺少草稿版本。");
            var files = await ContentStore.ReadUploadAsync(form.Files, form["format"] == "zip");
            await modules.SaveFilesAsync(id, files, form["mode"] == "replace", revision, context.User);
            return Results.NoContent();
        });
        api.MapPost("/modules/{id}/snippet", async (string id, SnippetInput input, ClaimsPrincipal user, ModuleService modules) => {
            if (input.Content is null || Encoding.UTF8.GetByteCount(input.Content) > ContentStore.MaxBytes)
                throw ApiException.Bad("代码内容无效或过大。");
            var path = ContentStore.NormalizePath(input.Path);
            await modules.SaveFilesAsync(id, [new IncomingFile(path, Encoding.UTF8.GetBytes(input.Content))],
                false, input.ExpectedDraftRevision, user);
            return Results.NoContent();
        });
        api.MapDelete("/modules/{id}/files", async (string id, [Microsoft.AspNetCore.Mvc.FromBody] DraftFileDelete input,
            ClaimsPrincipal user, ModuleService modules) => {
            await modules.DeleteFileAsync(id, input, user);
            return Results.NoContent();
        });
        api.MapGet("/modules/{id}/preview", async (string id, string kind, string path, ClaimsPrincipal user, ModuleService modules)
            => Results.Ok(await modules.PreviewAsync(id, kind, path, user)));
        api.MapPost("/modules/{id}/publish", async (string id, PublishModule input, ClaimsPrincipal user, ModuleService modules) => {
            await modules.PublishAsync(id, input, user);
            return Results.NoContent();
        });
        api.MapGet("/modules/{id}/download", async (string id, long revision, ClaimsPrincipal user, ModuleService modules) =>
            Results.File(await modules.DownloadAsync(id, revision, user), "application/zip", $"{id}-r{revision}.zip"));
        api.MapGet("/modules/{id}/grants", async (string id, ClaimsPrincipal user, ModuleService modules)
            => Results.Ok(await modules.GrantsAsync(id, user)));
        api.MapPut("/modules/{id}/grants", async (string id, GrantInput input, ClaimsPrincipal user, ModuleService modules) => {
            await modules.SetGrantAsync(id, input, user);
            return Results.NoContent();
        });
        api.MapPut("/modules/{id}/enabled", async (string id, EnableInput input, ClaimsPrincipal user, ModuleService modules) => {
            await modules.SetEnabledAsync(id, input.Enabled, user);
            return Results.NoContent();
        });
    }

    private static bool IsLocal(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        var host = context.Request.Host.Host;
        return remote is not null && IPAddress.IsLoopback(remote)
            && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)));
    }
}
