using System.Net.Http.Headers;
using System.Text.Json;
using ModuleRepository.Client;
using ModuleRepository.Contracts;

return await LocalCommands.RunAsync(args);

internal static class LocalCommands
{
    private const string Help = """
        module-repo — 模块仓库本地接入

        download      --server URL --module ID --output FILE.zip
        install       --server URL --module ID --target RELATIVE_DIR [--project DIR] [--dry-run]
        plan          --server URL --module ID --target RELATIVE_DIR [--project DIR]
        update        --installation ID [--project DIR] [--server URL] [--dry-run]
                      也可用 --module ID [--target RELATIVE_DIR] 选择已有安装副本
        list          [--project DIR]
        check-updates [--project DIR] [--server URL]
        search        --server URL [--query TEXT] [--language TEXT] [--environment TEXT] [--tag TEXT]

        --project 默认为当前目录。自用模式不需要凭据。
        可通过 MODULEREPO_SERVER 设置默认仓库地址。
        可选账号模式从 MODULEREPO_TOKEN 读取令牌，不在命令行或项目文件中保存。
        除 --help 外均输出 JSON。退出码：0 成功；1 错误；2 参数错误；3 冲突；4 安装未完成；130 取消。
        update 覆盖受管文件中的本地修改，旧内容由项目 Git 管理。
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help") { Console.WriteLine(Help); return 0; }
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try {
            var command = args[0].ToLowerInvariant();
            var options = Parse(args[1..]);
            var project = Path.GetFullPath(options.GetValueOrDefault("project") ?? Environment.CurrentDirectory);
            var server = options.GetValueOrDefault("server") ?? Environment.GetEnvironmentVariable("MODULEREPO_SERVER");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var token = Environment.GetEnvironmentVariable("MODULEREPO_TOKEN");
            if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            RepositoryClient Client(string? address) => new(http, address
                ?? throw new RepositoryClientException("arguments", "请使用 --server 或 MODULEREPO_SERVER 指定仓库地址。"));
            var installer = new ModuleInstaller();
            switch (command) {
                case "list":
                    Output(new { success = true, project, state = await installer.ListAsync(project, cancellation.Token) });
                    return 0;
                case "check-updates":
                    Output(new { success = true, updates = await installer.CheckUpdatesAsync(project, address => Client(address), server, cancellation.Token) });
                    return 0;
                case "search":
                    Output(new { success = true, modules = await Client(server).SearchAsync(options.GetValueOrDefault("query") ?? "",
                        options.GetValueOrDefault("language"), options.GetValueOrDefault("environment"), options.GetValueOrDefault("tag"), cancellation.Token) });
                    return 0;
                case "download":
                    var path = Path.GetFullPath(Required(options,"output"));
                    var manifest = await Client(server).DownloadAsync(Required(options,"module"), path, cancellation.Token);
                    Output(new { success = true, output = path, manifestPath = path + ".manifest.json", manifest });
                    return 0;
                case "install":
                case "plan":
                case "update":
                    string moduleId, target;
                    InstalledModule? installed = null;
                    if (command == "update" || options.ContainsKey("installation")) {
                        var state = await installer.ListAsync(project, cancellation.Token);
                        installed = ModuleInstaller.Select(state, options.GetValueOrDefault("installation"),
                            options.GetValueOrDefault("module"), options.GetValueOrDefault("target"));
                        moduleId = installed.ModuleId; target = installed.TargetDirectory; server ??= installed.ServerUrl;
                    }
                    else { moduleId = Required(options,"module"); target = Required(options,"target"); }
                    var client = Client(server);
                    var plan = await installer.PlanAsync(client, project, target, moduleId, cancellation.Token);
                    if (installed is not null && plan.Target.RepositoryId != installed.RepositoryId)
                        throw new RepositoryClientException("repositoryChanged", "服务地址指向不同仓库，未覆盖已有模块。");
                    if (command == "plan" || options.ContainsKey("dry-run") || plan.HasConflicts) {
                        Output(new { success = !plan.HasConflicts, status = plan.HasConflicts ? "conflict" : "planned", plan });
                        return plan.HasConflicts ? 3 : 0;
                    }
                    Output(await installer.ApplyAsync(client, plan, cancellation.Token));
                    return 0;
                default: throw new RepositoryClientException("arguments", "未知命令，请运行 --help 查看用法。");
            }
        }
        catch (InstallApplyException exception) { Output(exception.Result, error: true); return 4; }
        catch (RepositoryClientException exception) {
            Output(new { success = false, error = new { code = exception.Code, message = exception.Message } }, error: true);
            return exception.Code == "arguments" ? 2 : exception.Code is "fileConflict" or "planChanged" or "publicationChanged" or "targetOverlap" or "repositoryChanged" ? 3 : 1;
        }
        catch (OperationCanceledException) {
            Output(new { success = false, error = new { code = cancellation.IsCancellationRequested ? "cancelled" : "timeout",
                message = cancellation.IsCancellationRequested ? "操作已取消。" : "连接或下载超时，尚未完成本次操作。" } }, error:true);
            return cancellation.IsCancellationRequested ? 130 : 1;
        }
        catch (Exception exception) {
            Output(new { success = false, error = new { code = "operationFailed", message = exception.Message } }, error: true);
            return 1;
        }
    }

    private static Dictionary<string,string> Parse(string[] args)
    {
        var allowed = new HashSet<string>(["server","project","module","target","installation","output","query","language","environment","tag","dry-run"], StringComparer.Ordinal);
        var result = new Dictionary<string,string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++) {
            var argument = args[index];
            if (!argument.StartsWith("--")) throw new RepositoryClientException("arguments", "参数需要使用 --name value 格式。");
            var pair = argument[2..].Split('=',2);
            var name = pair[0];
            if (!allowed.Contains(name) || result.ContainsKey(name)) throw new RepositoryClientException("arguments", $"未知或重复参数：--{name}");
            if (name == "dry-run") {
                if (pair.Length != 1) throw new RepositoryClientException("arguments", "--dry-run 不需要值。");
                result.Add(name,"true");
            }
            else {
                var value = pair.Length == 2 ? pair[1] : ++index < args.Length ? args[index] : "";
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--"))
                    throw new RepositoryClientException("arguments", $"--{name} 缺少值。");
                result.Add(name,value);
            }
        }
        return result;
    }
    private static string Required(Dictionary<string,string> options, string key) => options.GetValueOrDefault(key)
        ?? throw new RepositoryClientException("arguments", $"缺少 --{key}。");
    private static void Output<T>(T value, bool error = false)
        => (error ? Console.Error : Console.Out).WriteLine(JsonSerializer.Serialize(value, RepositoryProtocol.IndentedJson));
}
