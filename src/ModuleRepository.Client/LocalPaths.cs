using System.Text;
using ModuleRepository.Contracts;

namespace ModuleRepository.Client;

internal static class LocalPaths
{
    public static readonly StringComparer Comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public static string ModulePath(string path)
    {
        var normalized = (path ?? "").Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (normalized.Length is 0 or > 240 || normalized.StartsWith('/') || normalized.Contains(':'))
            throw new RepositoryClientException("invalidPath", "模块文件需要使用有效的相对路径。");
        foreach (var segment in normalized.Split('/')) {
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c))
                || Reserved.Contains(segment.Split('.')[0].TrimEnd(' '))
                || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".module-repository", StringComparison.OrdinalIgnoreCase))
                throw new RepositoryClientException("invalidPath", $"模块文件路径无效：{normalized}");
        }
        return normalized;
    }

    public static void ValidateFileSet(IEnumerable<ModuleFileInfo> files)
    {
        var list = files.ToList();
        if (list.Count > RepositoryProtocol.MaxFiles || list.Any(x => x.Size < 0) || list.Sum(x => x.Size) > RepositoryProtocol.MaxContentBytes)
            throw new RepositoryClientException("invalidManifest", "模块文件数量或大小超过当前协议范围。");
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in list) {
            if (!set.Add(ModulePath(file.Path)) || file.Hash is null || file.Hash.Length != 64 || !file.Hash.All(char.IsAsciiHexDigit))
                throw new RepositoryClientException("invalidManifest", "文件清单包含重复路径或无效哈希。");
        }
        foreach (var path in set) {
            var slash = path.IndexOf('/');
            while (slash >= 0) {
                if (set.Contains(path[..slash])) throw new RepositoryClientException("invalidManifest", "文件与目录路径冲突。");
                slash = path.IndexOf('/', slash + 1);
            }
        }
    }

    public static string Project(string project)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        if (!Directory.Exists(root)) throw new RepositoryClientException("invalidProject", "项目目录不存在，请指定已有项目目录。");
        EnsureNoLinks(root, root);
        return root;
    }

    public static string RelativeTarget(string project, string target)
    {
        var full = Path.GetFullPath(target.Replace('/', Path.DirectorySeparatorChar), project);
        RequireWithin(project, full);
        var relative = Path.GetRelativePath(project, full).Replace('\\', '/');
        if (relative.Split('/').Any(x => x.Equals(".git", StringComparison.OrdinalIgnoreCase) || x.Equals(".module-repository", StringComparison.OrdinalIgnoreCase)))
            throw new RepositoryClientException("invalidTarget", "目标目录不能是项目 Git 或模块安装记录目录。");
        EnsureNoLinks(project, full);
        return relative;
    }

    public static string Target(string project, string target) => Path.GetFullPath(target.Replace('/', Path.DirectorySeparatorChar), project);
    public static string FilePath(string project, string target, string modulePath)
    {
        var targetRoot = Target(project, target);
        var full = Path.GetFullPath(ModulePath(modulePath).Replace('/', Path.DirectorySeparatorChar), targetRoot);
        RequireWithin(targetRoot, full);
        EnsureNoLinks(project, full);
        return full;
    }
    public static bool Overlaps(string first, string second)
        => Comparer.Equals(first, second) || first == "." || second == "."
            || first.StartsWith(second.TrimEnd('/') + "/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || second.StartsWith(first.TrimEnd('/') + "/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void RequireWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new RepositoryClientException("invalidTarget", "目标路径必须位于指定项目目录内。");
    }

    public static void EnsureNoLinks(string project, string full)
    {
        RequireWithin(project, full);
        var cursor = full;
        while (true) {
            try {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new RepositoryClientException("linkedPath", "目标路径包含链接或重解析点，请使用实际项目目录。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (Comparer.Equals(cursor, project)) break;
            cursor = Path.GetDirectoryName(cursor) ?? throw new RepositoryClientException("invalidTarget", "路径不在项目目录内。");
        }
    }

    public static FileObservation Observe(string path)
    {
        if (Directory.Exists(path)) return new("directory");
        if (!File.Exists(path)) return new("missing");
        using var stream = File.OpenRead(path);
        return new("file", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)));
    }

    public static IEnumerable<string> ExtraFiles(string targetRoot)
    {
        if (!Directory.Exists(targetRoot)) yield break;
        var pending = new Stack<string>(); pending.Push(targetRoot);
        while (pending.TryPop(out var directory)) {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal)) {
                var attributes = File.GetAttributes(path);
                var relative = Path.GetRelativePath(targetRoot, path).Replace('\\', '/');
                if ((attributes & FileAttributes.ReparsePoint) != 0 || Path.GetFileName(path) is ".git" or ".module-repository")
                    yield return relative + ((attributes & FileAttributes.Directory) != 0 ? "/" : "");
                else if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else yield return relative;
            }
        }
    }
}

internal static class AtomicFile
{
    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
