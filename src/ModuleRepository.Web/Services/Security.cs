using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModuleRepository.Web.Persistence;

namespace ModuleRepository.Web.Services;

public static class Security
{
    public const string CookieScheme = "repository-cookie";
    public const string TokenScheme = "repository-token";

    public static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new ApiException(401, "请先登录。");
    public static bool IsToken(ClaimsPrincipal user) => user.HasClaim("readonly-token", "true");
    public static bool IsAdmin(ClaimsPrincipal user) => !IsToken(user) && user.IsInRole("admin");
    public static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!IsAdmin(user)) throw ApiException.Forbidden();
    }
    public static ClaimsPrincipal Principal(Account account, string scheme, bool token = false)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, account.Id), new(ClaimTypes.Name, account.Username),
            new("stamp", account.SecurityStamp)];
        if (account.IsAdmin) claims.Add(new(ClaimTypes.Role, "admin"));
        if (token) claims.Add(new("readonly-token", "true"));
        return new(new ClaimsIdentity(claims, scheme));
    }
    public static string TokenHash(string value) => ContentStore.Hash(Encoding.UTF8.GetBytes(value));
    public static string Username(string value)
    {
        var text = (value ?? "").Trim();
        if (text.Length is < 3 or > 40 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw ApiException.Bad("账号须为 3–40 位英文字母、数字、点、下划线或短横线。");
        return text;
    }
    public static void ValidatePassword(string? password)
    {
        if (password is null || password.Length is < 12 or > 256)
            throw ApiException.Bad("密码长度须为 12–256 字符。");
    }
    public static async Task SignInAsync(HttpContext context, Account account)
        => await context.SignInAsync(CookieScheme, Principal(account, CookieScheme));
}

public sealed class TokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, RepositoryDb db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var raw = header[7..].Trim();
        if (raw.Length is < 20 or > 200) return AuthenticateResult.Fail("无效凭据。");
        var hash = Security.TokenHash(raw);
        var token = await db.AccessTokens.AsNoTracking().SingleOrDefaultAsync(x => x.Hash == hash);
        var account = token is null ? null : await db.Accounts.AsNoTracking().FindAccountAsync(token.UserId);
        if (account is null || !account.Enabled) return AuthenticateResult.Fail("无效凭据。");
        return AuthenticateResult.Success(new(Security.Principal(account, Scheme.Name, true), Scheme.Name));
    }
}

public static class AccountQueries
{
    public static Task<Account?> FindAccountAsync(this IQueryable<Account> query, string id)
        => query.SingleOrDefaultAsync(x => x.Id == id);
}

public sealed class CookieValidation(RepositoryDb db) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var account = id is null ? null : await db.Accounts.AsNoTracking().FindAccountAsync(id);
        if (account is null || !account.Enabled || context.Principal?.FindFirstValue("stamp") != account.SecurityStamp)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Security.CookieScheme);
        }
    }
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    { context.Response.StatusCode = 401; return Task.CompletedTask; }
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    { context.Response.StatusCode = 403; return Task.CompletedTask; }
}

public sealed class AccountService(RepositoryDb db, RepositoryGate gate, IPasswordHasher<Account> hasher)
{
    public static object View(Account user) => new { user.Id, user.Username, user.DisplayName, user.Enabled, user.IsAdmin };

    public async Task<Account> CreateAsync(CreateAccount input, bool bootstrap = false)
    {
        using var held = await gate.EnterAsync();
        if (bootstrap && await db.Accounts.AnyAsync()) throw ApiException.Conflict("初始化已经完成，请登录。");
        var username = Security.Username(input.Username);
        var normalized = username.ToUpperInvariant();
        Security.ValidatePassword(input.Password);
        if (await db.Accounts.AnyAsync(x => x.NormalizedUsername == normalized))
            throw ApiException.Conflict("该账号已存在。");
        var account = new Account { Username = username, NormalizedUsername = normalized,
            DisplayName = Text(input.DisplayName, 80, "显示名称"), IsAdmin = input.IsAdmin || bootstrap };
        account.PasswordHash = hasher.HashPassword(account, input.Password);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    public async Task UpdateAsync(string id, UpdateAccount input)
    {
        using var held = await gate.EnterAsync();
        var account = await db.Accounts.FindAsync(id) ?? throw new ApiException(404, "账号不存在。");
        if (account.IsAdmin && (!input.Enabled || !input.IsAdmin)
            && !await db.Accounts.AnyAsync(x => x.Id != id && x.IsAdmin && x.Enabled))
            throw ApiException.Bad("必须保留至少一个启用的管理员。");
        account.DisplayName = Text(input.DisplayName, 80, "显示名称");
        account.Enabled = input.Enabled;
        account.IsAdmin = input.IsAdmin;
        if (!string.IsNullOrEmpty(input.NewPassword))
        {
            Security.ValidatePassword(input.NewPassword);
            account.PasswordHash = hasher.HashPassword(account, input.NewPassword);
            db.AccessTokens.RemoveRange(await db.AccessTokens.Where(x => x.UserId == id).ToListAsync());
        }
        // Any admin change invalidates existing browser sessions; bearer tokens consult live state.
        account.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();
    }

    public async Task<Account?> LoginAsync(Credentials input)
    {
        var name = (input.Username ?? "").Trim().ToUpperInvariant();
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.NormalizedUsername == name);
        // Keep password verification work for unknown users too.
        var dummy = account ?? new Account();
        var hash = account?.PasswordHash ?? DummyHash.Value;
        var result = hasher.VerifyHashedPassword(dummy, hash, input.Password ?? "");
        if (account is null || !account.Enabled || result == PasswordVerificationResult.Failed) return null;
        return account;
    }
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<Account>().HashPassword(new Account(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));

    public static string Text(string? value, int maximum, string label, bool required = true)
    {
        var text = (value ?? "").Trim();
        if ((required && text.Length == 0) || text.Length > maximum)
            throw ApiException.Bad($"{label}{(required ? "不能为空，且" : "")}不能超过 {maximum} 字符。");
        return text;
    }
}
