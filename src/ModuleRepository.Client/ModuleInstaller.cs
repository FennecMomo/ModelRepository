using ModuleRepository.Contracts;

namespace ModuleRepository.Client;

public sealed class ModuleInstaller
{
    public async Task<ProjectInstallState> ListAsync(string project, CancellationToken cancellationToken = default)
        => (await new ProjectStore(project).ReadAsync(cancellationToken)).State;

    public static InstalledModule Select(ProjectInstallState state, string? installationId, string? moduleId, string? target = null)
    {
        var all = state.Installations.Concat(state.PendingOperations.Select(x => x.Target)
            .Where(x => !state.Installations.Any(i => i.InstallationId == x.InstallationId)));
        var candidates = all.Where(x => (installationId is null || x.InstallationId == installationId)
            && (moduleId is null || x.ModuleId == moduleId)
            && (target is null || LocalPaths.Comparer.Equals(x.TargetDirectory, target.Replace('\\', '/')))).ToList();
        if (candidates.Count != 1)
            throw new RepositoryClientException("ambiguousInstallation", candidates.Count == 0
                ? "没有找到对应安装记录，请先 install。" : "存在多个安装副本，请通过 --installation 指定 list 输出的安装 ID。");
        return candidates[0];
    }

    public async Task<InstallPlan> PlanAsync(RepositoryClient client, string project, string target, string moduleId,
        CancellationToken cancellationToken = default)
    {
        var store = new ProjectStore(project);
        var relative = LocalPaths.RelativeTarget(store.ProjectDirectory, target);
        var package = await client.PrepareAsync(moduleId, cancellationToken);
        var (state, fingerprint) = await store.ReadAsync(cancellationToken);
        var manifest = package.Manifest;
        var all = state.Installations.Concat(state.PendingOperations.Select(x => x.Target)).ToList();
        var matching = all.FirstOrDefault(x => x.RepositoryId == manifest.RepositoryId && x.ModuleId == moduleId
            && LocalPaths.Comparer.Equals(x.TargetDirectory, relative));
        var installationId = matching?.InstallationId ?? Guid.NewGuid().ToString("N");
        if (all.Any(x => x.InstallationId != installationId && LocalPaths.Overlaps(x.TargetDirectory, relative)))
            throw new RepositoryClientException("targetOverlap", "目标目录与另一个已安装或未完成的模块重叠，请使用独立目录。");
        var installed = state.Installations.SingleOrDefault(x => x.InstallationId == installationId);
        var pending = state.PendingOperations.SingleOrDefault(x => x.Target.InstallationId == installationId);
        var owned = (installed?.Files ?? []).ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        // Recover files actually written by an interrupted first install, without backing up old source.
        if (pending is not null) {
            foreach (var file in pending.Target.Files) {
                var path = LocalPaths.FilePath(store.ProjectDirectory, relative, file.Path);
                if (LocalPaths.Observe(path) == new FileObservation("file", file.Hash)) owned.TryAdd(file.Path, file);
            }
        }
        var latest = manifest.Files.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var actions = new List<PlannedFile>();
        var targetRoot = LocalPaths.Target(store.ProjectDirectory, relative);
        if (File.Exists(targetRoot)) throw new RepositoryClientException("targetConflict", "指定目标是文件，请选择目录。");

        foreach (var file in manifest.Files) {
            try {
                var path = LocalPaths.FilePath(store.ProjectDirectory, relative, file.Path);
                var before = LocalPaths.Observe(path);
                var ancestorFile = FileAncestor(targetRoot, path);
                if (before.Kind == "directory" || ancestorFile is not null)
                    actions.Add(new(file.Path, FileOperationKind.Conflict, before, "文件与目录类型发生冲突，请先通过 Git 整理目标路径。"));
                else if (before.Kind == "file" && !owned.ContainsKey(file.Path))
                    actions.Add(new(file.Path, FileOperationKind.Conflict, before, "此位置存在非受管文件，未自动覆盖。"));
                else actions.Add(new(file.Path, before.Kind == "missing" ? FileOperationKind.Add
                    : before.Hash == file.Hash ? FileOperationKind.Unchanged : FileOperationKind.Overwrite, before));
            }
            catch (RepositoryClientException exception) when (exception.Code is "linkedPath" or "invalidTarget") {
                actions.Add(new(file.Path, FileOperationKind.Conflict, new("unavailable"), exception.Message));
            }
        }
        foreach (var file in owned.Values.Where(x => !latest.ContainsKey(x.Path))) {
            var path = LocalPaths.FilePath(store.ProjectDirectory, relative, file.Path);
            var before = LocalPaths.Observe(path);
            actions.Add(new(file.Path, before.Kind == "file" ? FileOperationKind.Delete
                : before.Kind == "missing" ? FileOperationKind.Unchanged : FileOperationKind.Conflict, before,
                before.Kind == "directory" ? "旧受管文件的位置已经变成目录，未自动删除。" : null));
        }
        var affected = actions.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in LocalPaths.ExtraFiles(targetRoot).Where(x => !affected.Contains(x)))
            actions.Add(new(extra, FileOperationKind.Preserve, new("unmanaged"), "项目额外文件或目录，保留。"));

        return new InstallPlan {
            ProjectDirectory = store.ProjectDirectory, RecordFingerprint = fingerprint, Package = package,
            PreviousVersion = installed?.Version, RecoveringIncompleteOperation = pending is not null, Files = actions,
            Target = new InstalledModule {
                InstallationId = installationId, RepositoryId = manifest.RepositoryId, ServerUrl = client.Server.AbsoluteUri,
                ModuleId = moduleId, Name = manifest.Metadata.Name, TargetDirectory = relative,
                Version = manifest.Version, Revision = manifest.Revision, ContentHash = manifest.ContentHash,
                Files = manifest.Files.Select(x => x with { }).ToList()
            }
        };
    }

    private static string? FileAncestor(string root, string path)
    {
        var cursor = Path.GetDirectoryName(path);
        while (cursor is not null && !LocalPaths.Comparer.Equals(cursor, root)) {
            if (File.Exists(cursor)) return cursor;
            cursor = Path.GetDirectoryName(cursor);
        }
        return null;
    }

    public async Task<InstallResult> InstallAsync(RepositoryClient client, string project, string target, string moduleId,
        CancellationToken cancellationToken = default)
        => await ApplyAsync(client, await PlanAsync(client, project, target, moduleId, cancellationToken), cancellationToken);

    public async Task<InstallResult> UpdateAsync(RepositoryClient client, string project, string installationId,
        CancellationToken cancellationToken = default)
    {
        var state = await ListAsync(project, cancellationToken);
        var installed = Select(state, installationId, null);
        var plan = await PlanAsync(client, project, installed.TargetDirectory, installed.ModuleId, cancellationToken);
        if (plan.Target.RepositoryId != installed.RepositoryId)
            throw new RepositoryClientException("repositoryChanged", "服务地址指向了不同的仓库，未覆盖已有模块。");
        return await ApplyAsync(client, plan, cancellationToken);
    }

    public async Task<InstallResult> ApplyAsync(RepositoryClient client, InstallPlan plan, CancellationToken cancellationToken = default)
    {
        if (plan.HasConflicts) throw new RepositoryClientException("fileConflict", "安装计划中存在路径冲突，请使用 plan 或 --dry-run 查看后整理目标目录。");
        if (plan.Package is null || plan.Package.Manifest.ContentHash != plan.Target.ContentHash)
            throw new RepositoryClientException("invalidPlan", "请使用本次进程生成的安装计划。");
        var current = await client.GetManifestAsync(plan.Target.ModuleId, cancellationToken);
        if (current.RepositoryId != plan.Target.RepositoryId || current.Revision != plan.Target.Revision || current.ContentHash != plan.Target.ContentHash)
            throw new RepositoryClientException("publicationChanged", "计划生成后当前发布发生变化，请重新生成计划。");
        var store = new ProjectStore(plan.ProjectDirectory);
        using var held = store.Lock();
        var (state, fingerprint) = await store.ReadAsync(cancellationToken);
        if (fingerprint != plan.RecordFingerprint)
            throw new RepositoryClientException("planChanged", "项目安装记录已变化，请重新生成计划。");
        foreach (var file in plan.Files.Where(x => x.Kind != FileOperationKind.Preserve)) {
            var path = LocalPaths.FilePath(store.ProjectDirectory, plan.Target.TargetDirectory, file.Path);
            if (LocalPaths.Observe(path) != file.Before)
                throw new RepositoryClientException("planChanged", $"计划生成后文件已变化：{file.Path}。请重新生成计划。");
        }
        var pending = new PendingInstall { Target = plan.Target };
        state = state with { PendingOperations = state.PendingOperations.Where(x => x.Target.InstallationId != plan.Target.InstallationId)
            .Append(pending).ToList() };
        await store.SaveAsync(state, cancellationToken);
        var work = plan.Files.Where(x => x.Kind != FileOperationKind.Preserve)
            .OrderBy(x => x.Kind == FileOperationKind.Delete ? 0 : 1).ToList();
        var completed = new List<FileOperationResult>();
        try {
            foreach (var file in work) {
                cancellationToken.ThrowIfCancellationRequested();
                var path = LocalPaths.FilePath(store.ProjectDirectory, plan.Target.TargetDirectory, file.Path);
                if (LocalPaths.Observe(path) != file.Before)
                    throw new RepositoryClientException("planChanged", $"执行期间文件已变化：{file.Path}。");
                if (file.Kind == FileOperationKind.Delete) File.Delete(path);
                else if (file.Kind is FileOperationKind.Add or FileOperationKind.Overwrite) {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    LocalPaths.EnsureNoLinks(store.ProjectDirectory, path);
                    await AtomicFile.WriteAsync(path, plan.Package.Contents[file.Path], cancellationToken);
                }
                var outcome = new FileOperationResult(file.Path, file.Kind);
                completed.Add(outcome); pending.CompletedFiles.Add(outcome);
                await store.SaveAsync(state, cancellationToken);
            }
            var installed = plan.Target with { InstalledAt = DateTime.UtcNow };
            var success = state with {
                Installations = state.Installations.Where(x => x.InstallationId != installed.InstallationId).Append(installed).ToList(),
                PendingOperations = state.PendingOperations.Where(x => x.Target.InstallationId != installed.InstallationId).ToList()
            };
            await store.SaveAsync(success, cancellationToken);
            return new InstallResult { Success = true, InstallationId = installed.InstallationId, ModuleId = installed.ModuleId,
                TargetDirectory = installed.TargetDirectory, Version = installed.Version,
                CompletedFiles = completed.Concat(plan.Files.Where(x => x.Kind == FileOperationKind.Preserve)
                    .Select(x => new FileOperationResult(x.Path, x.Kind))).ToList() };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or RepositoryClientException) {
            pending.Status = "incomplete";
            pending.Error = exception is OperationCanceledException ? "操作已取消，部分文件可能已写入。" : "安装未完成，请检查本次命令结果，排除问题后重新执行。";
            try { await store.SaveAsync(state, CancellationToken.None); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            var done = completed.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            throw new InstallApplyException(new InstallResult { Success = false, Status = "incomplete",
                InstallationId = plan.Target.InstallationId, ModuleId = plan.Target.ModuleId, TargetDirectory = plan.Target.TargetDirectory,
                Version = plan.Target.Version, CompletedFiles = completed, RemainingFiles = work.Where(x => !done.Contains(x.Path)).Select(x => x.Path).ToList(),
                Error = exception is RepositoryClientException ? exception.Message : pending.Error });
        }
    }

    public async Task<List<UpdateCheck>> CheckUpdatesAsync(string project, Func<string, RepositoryClient> clientFactory,
        string? serverOverride = null, CancellationToken cancellationToken = default)
    {
        var state = await ListAsync(project, cancellationToken);
        var results = new List<UpdateCheck>();
        foreach (var installed in state.Installations) {
            if (state.PendingOperations.Any(x => x.Target.InstallationId == installed.InstallationId)) {
                results.Add(new(installed.InstallationId, installed.ModuleId, installed.TargetDirectory, installed.Version, "incomplete", Message: "上次安装未完成。"));
                continue;
            }
            try {
                var manifest = await clientFactory(serverOverride ?? installed.ServerUrl).GetManifestAsync(installed.ModuleId, cancellationToken);
                var status = manifest.RepositoryId != installed.RepositoryId ? "repositoryChanged"
                    : manifest.Revision == installed.Revision && manifest.ContentHash == installed.ContentHash ? "upToDate" : "updateAvailable";
                results.Add(new(installed.InstallationId, installed.ModuleId, installed.TargetDirectory, installed.Version,
                    status, manifest.Version, manifest.Revision));
            }
            catch (Exception exception) when (exception is RepositoryClientException or HttpRequestException or TaskCanceledException) {
                if (cancellationToken.IsCancellationRequested) throw;
                var status = exception is RepositoryClientException { HttpStatus: 401 or 403 or 404 } ? "unavailableOrMissing" : "unavailable";
                results.Add(new(installed.InstallationId, installed.ModuleId, installed.TargetDirectory, installed.Version, status,
                    Message: exception is RepositoryClientException ? exception.Message : "服务暂时不可用。"));
            }
        }
        foreach (var pending in state.PendingOperations.Where(x => !state.Installations.Any(i => i.InstallationId == x.Target.InstallationId)))
            results.Add(new(pending.Target.InstallationId, pending.Target.ModuleId, pending.Target.TargetDirectory, "", "incomplete",
                Message: "首次安装未完成，尚无成功安装版本。"));
        return results;
    }
}
