using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModuleRepository.Web;
using ModuleRepository.Web.Persistence;
using ModuleRepository.Web.Services;

namespace ModuleRepository.Tests;

public sealed class RepositoryFactory : WebApplicationFactory<Program>
{
    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), "ModuleRepositoryTests", Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Repository:DataDirectory", DataPath);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, LoopbackFilter>());
    }
    private sealed class LoopbackFilter : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next)
            => app => { app.Use((context, nextMiddleware) => {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return nextMiddleware(context);
            }); next(app); };
    }
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions {
        BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false });
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // Only remove the exact GUID-owned temporary test directory.
        if (disposing && Directory.Exists(DataPath)) Directory.Delete(DataPath, true);
    }
}

public sealed class RepositoryTests
{
    private static string Password() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task RefreshCsrf(HttpClient client)
    {
        var session = await Json(await client.GetAsync("/api/session"));
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", session.GetProperty("csrfToken").GetString());
    }
    private static async Task<HttpClient> Admin(RepositoryFactory factory)
    {
        var client = factory.Client(); await RefreshCsrf(client);
        await Json(await client.PostAsJsonAsync("/api/setup", new { username = "admin", password = Password(), displayName = "管理员" }));
        await RefreshCsrf(client); return client;
    }
    private static async Task<(HttpClient Client, string Id)> User(RepositoryFactory factory, HttpClient admin, string username)
    {
        var password = Password();
        var created = await Json(await admin.PostAsJsonAsync("/api/users", new { username, password, displayName = username }));
        var client = factory.Client(); await RefreshCsrf(client);
        await Json(await client.PostAsJsonAsync("/api/login", new { username, password }));
        await RefreshCsrf(client); return (client, created.GetProperty("id").GetString()!);
    }
    private static async Task<string> Category(HttpClient admin)
    {
        var result = await Json(await admin.PostAsJsonAsync("/api/categories", new { name = "通用功能", parentId = (string?)null }));
        return result.GetProperty("id").GetString()!;
    }
    private static ModuleMetadata Metadata(string? category, string documentation = "# 用法\n\n调用 Run()，完成后释放。") => new() {
        Name = "文件下载", Summary = "支持取消和进度", Language = "C#", Environment = ".NET 10 / Windows",
        CategoryId = category, Tags = ["网络", "下载"], Documentation = documentation, Validation = "集成测试" };
    private static async Task<JsonElement> Detail(HttpClient client, string id = "common.download")
        => await Json(await client.GetAsync($"/api/modules/{id}"));
    private static async Task Create(HttpClient client, string? category, string id = "common.download")
        => await Json(await client.PostAsJsonAsync("/api/modules", new { id, metadata = Metadata(category) }));
    private static async Task Snippet(HttpClient client, string content, string path = "src/Download.cs", string id = "common.download")
    {
        var detail = await Detail(client, id);
        var response = await client.PostAsJsonAsync($"/api/modules/{id}/snippet", new {
            path, content, expectedDraftRevision = detail.GetProperty("draftRevision").GetInt64() });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    private static async Task Publish(HttpClient client, string version = "1.0.0", string id = "common.download")
    {
        var detail = await Detail(client, id);
        var response = await client.PostAsJsonAsync($"/api/modules/{id}/publish", new {
            version, changeNotes = "更新实现与文档", expectedDraftRevision = detail.GetProperty("draftRevision").GetInt64() });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (path, content) in files) {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(content);
            }
        return output.ToArray();
    }
    private static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] zip, long revision, bool replace = true)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("zip"), "format");
        content.Add(new StringContent(replace ? "replace" : "merge"), "mode");
        content.Add(new StringContent(revision.ToString()), "expectedDraftRevision");
        content.Add(new ByteArrayContent(zip), "files", "module.zip");
        return await client.PostAsync("/api/modules/common.download/files", content);
    }
    private static async Task<Dictionary<string, string>> Download(HttpClient client, long revision)
    {
        var response = await client.GetAsync($"/api/modules/common.download/download?revision={revision}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var files = new Dictionary<string,string>();
        foreach (var entry in zip.Entries) { using var reader = new StreamReader(entry.Open()); files.Add(entry.FullName, await reader.ReadToEndAsync()); }
        return files;
    }

    [Fact]
    public async Task Setup_is_one_time_and_csrf_required()
    {
        using var factory = new RepositoryFactory(); using var raw = factory.Client();
        var input = new { username = "admin", password = Password(), displayName = "管理员" };
        Assert.Equal(HttpStatusCode.BadRequest, (await raw.PostAsJsonAsync("/api/setup", input)).StatusCode);
        await RefreshCsrf(raw);
        await Json(await raw.PostAsJsonAsync("/api/setup", input));
        await RefreshCsrf(raw);
        Assert.Equal(HttpStatusCode.Conflict, (await raw.PostAsJsonAsync("/api/setup", input)).StatusCode);
        using var anonymous = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/modules")).StatusCode);
        raw.DefaultRequestHeaders.Remove("X-CSRF-Token");
        Assert.Equal(HttpStatusCode.BadRequest, (await raw.PostAsJsonAsync("/api/categories", new { name = "test" })).StatusCode);
    }

    [Fact]
    public async Task Private_module_is_hidden_across_list_details_preview_and_download_and_revocation_is_live()
    {
        using var factory = new RepositoryFactory(); using var admin = await Admin(factory);
        var (reader, _) = await User(factory, admin, "reader"); using var user = reader;
        var category = await Category(admin); await Create(admin, category); await Snippet(admin, "version one"); await Publish(admin);
        Assert.Equal(0, (await Json(await user.GetAsync("/api/modules?q=文件"))).GetArrayLength());
        foreach (var path in new[] { "", "/preview?kind=published&path=src/Download.cs", "/download?revision=1", "/grants" })
            Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync("/api/modules/common.download" + path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync("/api/modules/absent.module")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/modules/common.download/grants", new { username = "reader", role = "reader" })).StatusCode);
        Assert.Equal(1, (await Json(await user.GetAsync("/api/modules?language=C%23&tag=下载"))).GetArrayLength());
        var detail = await Detail(user);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("draft").ValueKind);
        Assert.Equal("version one", (await Download(user, 1))["src/Download.cs"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/modules/common.download/preview?kind=draft&path=src/Download.cs")).StatusCode);
        await admin.PutAsJsonAsync("/api/modules/common.download/grants", new { username = "reader", role = "none" });
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync("/api/modules/common.download/download?revision=1")).StatusCode);
    }

    [Fact]
    public async Task Maintainer_can_publish_but_cannot_grant_or_disable_and_account_disable_invalidates_existing_session()
    {
        using var factory = new RepositoryFactory(); using var admin = await Admin(factory);
        var (maintainer, userId) = await User(factory, admin, "maintainer"); using var user = maintainer;
        await Create(admin, await Category(admin));
        await admin.PutAsJsonAsync("/api/modules/common.download/grants", new { username = "maintainer", role = "maintainer" });
        await Snippet(user, "maintainer update"); await Publish(user);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync("/api/modules/common.download/grants", new { username = "admin", role = "reader" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync("/api/modules/common.download/enabled", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/users")).StatusCode);
        await admin.PutAsJsonAsync($"/api/users/{userId}", new { displayName = "maintainer", enabled = false, isAdmin = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/modules")).StatusCode);
    }

    [Fact]
    public async Task Draft_is_isolated_and_publish_replaces_current_content_without_history()
    {
        using var factory = new RepositoryFactory(); using var admin = await Admin(factory);
        var (reader, _) = await User(factory, admin, "reader"); using var user = reader;
        var category = await Category(admin); await Create(admin, category);
        await Snippet(admin, "old content"); await Snippet(admin, "stable guid", "Download.cs.meta"); await Publish(admin);
        await admin.PutAsJsonAsync("/api/modules/common.download/grants", new { username = "reader", role = "reader" });
        var detail = await Detail(admin);
        await admin.PutAsJsonAsync("/api/modules/common.download/draft", new {
            metadata = Metadata(category, "新版私有草稿说明"), expectedDraftRevision = detail.GetProperty("draftRevision").GetInt64() });
        await Snippet(admin, "new content");
        Assert.Equal("old content", (await Download(user, 1))["src/Download.cs"]);
        var visible = await Detail(user);
        Assert.DoesNotContain("新版私有草稿说明", visible.GetProperty("publication").GetRawText());
        await Publish(admin, "1.1.0");
        Assert.Equal(HttpStatusCode.Conflict, (await user.GetAsync("/api/modules/common.download/download?revision=1")).StatusCode);
        var files = await Download(user, 2);
        Assert.Equal("new content", files["src/Download.cs"]); Assert.Equal("stable guid", files["Download.cs.meta"]);
        Assert.Contains("新版私有草稿说明", (await Detail(user)).GetProperty("publication").GetRawText());
        var hashes = Directory.GetFiles(Path.Combine(factory.DataPath, "blobs")).Select(Path.GetFileName).ToArray();
        Assert.DoesNotContain(ContentStore.Hash(Encoding.UTF8.GetBytes("old content")), hashes);
    }

    [Fact]
    public async Task Invalid_publication_and_stale_draft_leave_current_publication_intact()
    {
        using var factory = new RepositoryFactory(); using var admin = await Admin(factory);
        await Create(admin, await Category(admin)); await Snippet(admin,"good"); await Publish(admin);
        var before = await Detail(admin); var staleRevision = before.GetProperty("draftRevision").GetInt64();
        await Snippet(admin, "later");
        var stale = await admin.PostAsJsonAsync("/api/modules/common.download/publish", new { version="1.1.0",changeNotes="test",expectedDraftRevision=staleRevision });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await Detail(admin);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/modules/common.download/publish", new {
            version="1.0.0",changeNotes="test",expectedDraftRevision=current.GetProperty("draftRevision").GetInt64() })).StatusCode);
        await admin.PutAsJsonAsync("/api/modules/common.download/draft",new { metadata=Metadata(null), expectedDraftRevision=current.GetProperty("draftRevision").GetInt64() });
        current=await Detail(admin);
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.PostAsJsonAsync("/api/modules/common.download/publish",new {
            version="1.1.0",changeNotes="test",expectedDraftRevision=current.GetProperty("draftRevision").GetInt64() })).StatusCode);
        Assert.Equal("good",(await Download(admin,1))["src/Download.cs"]);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("/absolute.cs")]
    [InlineData("C:/outside.cs")]
    [InlineData("src/../outside.cs")]
    [InlineData(".git/config")]
    [InlineData("a/NUL.txt")]
    [InlineData("src/trailing. /bad.cs")]
    public async Task Unsafe_zip_paths_are_rejected_before_draft_changes(string path)
    {
        using var factory = new RepositoryFactory(); using var admin=await Admin(factory);
        await Create(admin,await Category(admin)); await Snippet(admin,"retained");
        var detail=await Detail(admin); var revision=detail.GetProperty("draftRevision").GetInt64();
        Assert.Equal(HttpStatusCode.BadRequest,(await Upload(admin,Zip((path,"unsafe")),revision)).StatusCode);
        detail=await Detail(admin);
        Assert.Equal(revision,detail.GetProperty("draftRevision").GetInt64());
        Assert.Single(detail.GetProperty("draftFiles").EnumerateArray());
    }

    [Fact]
    public async Task Zip_duplicates_file_directory_collision_and_oversized_content_do_not_replace_draft()
    {
        using var factory=new RepositoryFactory(); using var admin=await Admin(factory);
        await Create(admin,await Category(admin)); await Snippet(admin,"retained");
        var revision=(await Detail(admin)).GetProperty("draftRevision").GetInt64();
        Assert.Equal(HttpStatusCode.BadRequest,(await Upload(admin,Zip(("A.cs","one"),("a.cs","two")),revision)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Upload(admin,Zip(("src","one"),("src/code.cs","two")),revision)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Upload(admin,Zip(("large.txt",new string('x',(int)ContentStore.MaxBytes+1))),revision)).StatusCode);
        using var output=new MemoryStream();
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true)) {
            var link=zip.CreateEntry("shortcut"); link.ExternalAttributes=unchecked((int)0xA1FF0000);
        }
        Assert.Equal(HttpStatusCode.BadRequest,(await Upload(admin,output.ToArray(),revision)).StatusCode);
        Assert.Equal(revision,(await Detail(admin)).GetProperty("draftRevision").GetInt64());
    }

    [Fact]
    public async Task Zip_replace_preserves_paths_and_removes_files_only_on_publication()
    {
        using var factory=new RepositoryFactory(); using var admin=await Admin(factory);
        await Create(admin,await Category(admin)); await Snippet(admin,"old"); await Publish(admin);
        var revision=(await Detail(admin)).GetProperty("draftRevision").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent,(await Upload(admin,Zip(("src/New.cs","new"),("src/New.cs.meta","guid")),revision)).StatusCode);
        Assert.Contains("src/Download.cs",(await Download(admin,1)).Keys);
        await Publish(admin,"2.0.0");
        var files=await Download(admin,2);
        Assert.Equal(2,files.Count); Assert.DoesNotContain("src/Download.cs",files.Keys); Assert.Equal("guid",files["src/New.cs.meta"]);
    }

    [Fact]
    public async Task Reader_token_is_read_only_revocable_and_uses_live_module_permissions()
    {
        using var factory=new RepositoryFactory(); using var admin=await Admin(factory);
        var (reader,_) = await User(factory,admin,"reader"); using var user=reader;
        await Create(admin,await Category(admin)); await Snippet(admin,"code"); await Publish(admin);
        await admin.PutAsJsonAsync("/api/modules/common.download/grants",new {username="reader",role="reader"});
        var created=await Json(await user.PostAsJsonAsync("/api/tokens",new {name="integration"}));
        using var token=factory.Client();
        token.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",created.GetProperty("secret").GetString());
        Assert.Equal(HttpStatusCode.OK,(await token.GetAsync("/api/modules/common.download")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await token.PostAsJsonAsync("/api/modules",new {id="not.allowed",metadata=Metadata(null)})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await token.GetAsync("/api/tokens")).StatusCode);
        await admin.PutAsJsonAsync("/api/modules/common.download/grants",new {username="reader",role="none"});
        Assert.Equal(HttpStatusCode.NotFound,(await token.GetAsync("/api/modules/common.download/download?revision=1")).StatusCode);
        await user.DeleteAsync("/api/tokens/"+created.GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized,(await token.GetAsync("/api/modules")).StatusCode);
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<RepositoryDb>();
        Assert.Empty(await db.AccessTokens.ToListAsync());
    }

    [Fact]
    public async Task Category_tree_rejects_cycles_and_cannot_remove_published_category()
    {
        using var factory=new RepositoryFactory(); using var admin=await Admin(factory);
        var parent=await Category(admin);
        var child=(await Json(await admin.PostAsJsonAsync("/api/categories",new {name="下载",parentId=parent}))).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.PutAsJsonAsync("/api/categories/"+parent,new {name="通用",parentId=child})).StatusCode);
        await Create(admin,parent); await Snippet(admin,"code"); await Publish(admin);
        var detail=await Detail(admin);
        await admin.PutAsJsonAsync("/api/modules/common.download/draft",new {metadata=Metadata(child),expectedDraftRevision=detail.GetProperty("draftRevision").GetInt64()});
        Assert.Equal(HttpStatusCode.Conflict,(await admin.DeleteAsync("/api/categories/"+parent)).StatusCode);
        Assert.Equal(1,(await Json(await admin.GetAsync("/api/modules?categoryId="+parent))).GetArrayLength());
    }

    [Fact]
    public async Task Content_corruption_prevents_publication_without_overwriting_current_metadata()
    {
        using var factory=new RepositoryFactory(); using var admin=await Admin(factory);
        await Create(admin,await Category(admin)); await Snippet(admin,"good"); await Publish(admin);
        await Snippet(admin,"new");
        var detail=await Detail(admin);
        var hash=detail.GetProperty("draftFiles")[0].GetProperty("hash").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(factory.DataPath,"blobs",hash),"corrupt");
        Assert.Equal(HttpStatusCode.InternalServerError,(await admin.PostAsJsonAsync("/api/modules/common.download/publish",new {
            version="1.1.0",changeNotes="test",expectedDraftRevision=detail.GetProperty("draftRevision").GetInt64() })).StatusCode);
        Assert.Equal("good",(await Download(admin,1))["src/Download.cs"]);
        Assert.Equal(1,(await Detail(admin)).GetProperty("publication").GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task Last_admin_cannot_be_disabled_and_disabled_module_is_hidden_from_reader()
    {
        using var factory=new RepositoryFactory(); using var admin=await Admin(factory);
        var users=await Json(await admin.GetAsync("/api/users")); var adminId=users[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.PutAsJsonAsync("/api/users/"+adminId,new {
            displayName="admin",enabled=false,isAdmin=true })).StatusCode);
        var (reader,_) =await User(factory,admin,"reader"); using var user=reader;
        await Create(admin,await Category(admin)); await Snippet(admin,"code"); await Publish(admin);
        await admin.PutAsJsonAsync("/api/modules/common.download/grants",new {username="reader",role="reader"});
        await admin.PutAsJsonAsync("/api/modules/common.download/enabled",new {enabled=false});
        Assert.Equal(0,(await Json(await user.GetAsync("/api/modules"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound,(await user.GetAsync("/api/modules/common.download/download?revision=1")).StatusCode);
    }

    [Fact]
    public async Task Upload_authorization_precheck_does_not_keep_a_stale_tracked_draft()
    {
        using var factory = new RepositoryFactory(); using var admin = await Admin(factory);
        await Create(admin, await Category(admin));
        var oldRevision = (await Detail(admin)).GetProperty("draftRevision").GetInt64();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ModuleService>();
        var db = scope.ServiceProvider.GetRequiredService<RepositoryDb>();
        var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.IsAdmin);
        var principal = Security.Principal(account, Security.CookieScheme);
        await service.RequireManageAsync("common.download", principal);
        await Snippet(admin, "another editor changed the draft");
        var error = await Assert.ThrowsAsync<ApiException>(() => service.SaveFilesAsync("common.download",
            [new IncomingFile("stale.cs", Encoding.UTF8.GetBytes("stale"))], true, oldRevision, principal));
        Assert.Equal(409, error.Status);
        var detail = await Detail(admin);
        Assert.Equal("src/Download.cs", detail.GetProperty("draftFiles")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Ordinary_multi_file_upload_and_file_removal_keep_publication_unchanged_until_publish()
    {
        using var factory = new RepositoryFactory(); using var admin = await Admin(factory);
        await Create(admin, await Category(admin)); await Snippet(admin, "original"); await Publish(admin);
        var revision = (await Detail(admin)).GetProperty("draftRevision").GetInt64();
        using var body = new MultipartFormDataContent();
        body.Add(new StringContent(revision.ToString()), "expectedDraftRevision");
        body.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("# Readme")), "files", "README.md");
        body.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("example")), "files", "examples/example.cs");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/modules/common.download/files", body)).StatusCode);
        var detail = await Detail(admin); Assert.Equal(3, detail.GetProperty("draftFiles").GetArrayLength());
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/modules/common.download/files") {
            Content = JsonContent.Create(new { path = "src/Download.cs", expectedDraftRevision = detail.GetProperty("draftRevision").GetInt64() }) };
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(request)).StatusCode);
        Assert.Equal("original", (await Download(admin,1))["src/Download.cs"]);
        await Publish(admin, "1.1.0");
        var files = await Download(admin,2); Assert.Equal(2,files.Count); Assert.DoesNotContain("src/Download.cs",files.Keys);
    }

    [Fact]
    public async Task Database_failure_mid_publish_rolls_back_file_manifest_and_metadata_together()
    {
        using var factory = new RepositoryFactory(); using var admin = await Admin(factory);
        await Create(admin, await Category(admin)); await Snippet(admin, "current"); await Publish(admin);
        await Snippet(admin, "replacement");
        using (var scope = factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<RepositoryDb>();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER test_deny_publication BEFORE INSERT ON Files
                WHEN NEW.Kind = 'published'
                BEGIN SELECT RAISE(ABORT, 'injected publication failure'); END;
                """);
        }
        var draft = await Detail(admin);
        var failed = await admin.PostAsJsonAsync("/api/modules/common.download/publish", new {
            version = "1.1.0", changeNotes = "will fail", expectedDraftRevision = draft.GetProperty("draftRevision").GetInt64() });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("current", (await Download(admin,1))["src/Download.cs"]);
        var detail = await Detail(admin);
        Assert.Equal(1, detail.GetProperty("publication").GetProperty("revision").GetInt64());
        Assert.Equal("1.0.0", detail.GetProperty("publication").GetProperty("version").GetString());
        Assert.Single(detail.GetProperty("publishedFiles").EnumerateArray());
    }

    [Fact]
    public async Task Password_reset_revokes_existing_cookie_and_bearer_credentials()
    {
        using var factory = new RepositoryFactory(); using var admin = await Admin(factory);
        var (reader,id) = await User(factory,admin,"reader"); using var user = reader;
        var created = await Json(await user.PostAsJsonAsync("/api/tokens", new { name = "test-device" }));
        using var token = factory.Client();
        token.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",created.GetProperty("secret").GetString());
        Assert.Equal(HttpStatusCode.OK,(await token.GetAsync("/api/modules")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/users/"+id, new {
            displayName="reader",enabled=true,isAdmin=false,newPassword=Password() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await user.GetAsync("/api/modules")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await token.GetAsync("/api/modules")).StatusCode);
    }
}
