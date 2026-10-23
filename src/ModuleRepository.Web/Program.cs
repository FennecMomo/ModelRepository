using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ModuleRepository.Web;
using ModuleRepository.Web.Persistence;
using ModuleRepository.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
var paths = new RepositoryPaths(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<RepositoryIdentity>();
var accessSettings = new RepositoryAccessSettings(builder.Configuration.GetValue<bool>("Repository:RequireLogin"));
builder.Services.AddSingleton(accessSettings);
builder.Services.AddSingleton<RepositoryGate>();
builder.Services.AddSingleton<ContentStore>();
builder.Services.AddDbContext<RepositoryDb>(options => options.UseSqlite(
    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {
        DataSource = Path.Combine(paths.Root, "repository.db"), ForeignKeys = true }.ToString()));
builder.Services.AddScoped<ModuleService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<CookieValidation>();
builder.Services.AddScoped<IPasswordHasher<Account>, PasswordHasher<Account>>();
builder.Services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
var protection = builder.Services.AddDataProtection().SetApplicationName("ModuleRepository")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.Keys));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddAuthentication("repository")
    .AddPolicyScheme("repository", null, options => options.ForwardDefaultSelector = context =>
        !accessSettings.RequireLogin ? Security.PersonalScheme :
        context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? Security.TokenScheme : Security.CookieScheme)
    .AddCookie(Security.CookieScheme, options => {
        options.Cookie.Name = "ModuleRepository.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.EventsType = typeof(CookieValidation);
    })
    .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(Security.TokenScheme, _ => { })
    .AddScheme<AuthenticationSchemeOptions, PersonalAuthenticationHandler>(Security.PersonalScheme, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => {
    options.HeaderName = "X-CSRF-Token";
    options.Cookie.Name = "ModuleRepository.Csrf";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.Configure<FormOptions>(options => {
    options.MultipartBodyLengthLimit = ContentStore.MaxBytes + 1024 * 1024;
    options.ValueLengthLimit = 128 * 1024;
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = ContentStore.MaxBytes + 2 * 1024 * 1024);

var app = builder.Build();
// A single writer process owns this data directory. Do not run multiple instances against it.
var processLock = new FileStream(Path.Combine(paths.Root, ".instance.lock"), FileMode.OpenOrCreate,
    FileAccess.ReadWrite, FileShare.None);
app.Lifetime.ApplicationStopped.Register(processLock.Dispose);
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RepositoryDb>();
    await db.Database.EnsureCreatedAsync();
    _ = scope.ServiceProvider.GetRequiredService<RepositoryIdentity>();
    if (!accessSettings.RequireLogin)
        await PersonalAuthenticationHandler.EnsureAccountAsync(db,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher<Account>>());
    await scope.ServiceProvider.GetRequiredService<ModuleService>().CollectAsync();
}

app.Use(async (context, next) => {
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api")) {
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
    try { await next(context); }
    catch (ApiException exception) {
        context.Response.StatusCode = exception.Status;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message });
    }
    catch (AntiforgeryValidationException) {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "页面验证已过期，请刷新后重试。" });
    }
    catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException or System.Text.Json.JsonException) {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "请求格式无效，或上传内容超过限制。" });
    }
    catch (Exception exception) {
        app.Logger.LogError(exception, "请求处理失败。操作未完成。");
        if (!context.Response.HasStarted) {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "操作失败，请重试或联系管理员检查服务状态。" });
        }
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) => {
    if (accessSettings.RequireLogin && context.Request.Path.StartsWithSegments("/api")
        && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        if (context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = context.User.Identity?.IsAuthenticated == true ? 403 : 401;
            await context.Response.WriteAsJsonAsync(new { error = "访问令牌仅允许读取。" });
            return;
        }
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    }
    await next(context);
});

app.MapRepositoryApi();
app.MapGet("/health", () => Results.Ok(new { status = "ready", version = BuildInfo.Version }));
app.Run();

public partial class Program;
