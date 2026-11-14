using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Restore;

/// <summary>
/// Applies a file-level restore plan (design 12.5 / 12.6 / 12.7): backs up the current profile files,
/// performs atomic, idempotent writes, journals every stage and rolls back the files it touched when a
/// write or the post-apply verification fails. No package manager is invoked in this version.
/// </summary>
public sealed class RestoreService : IRestoreService
{
    private static readonly string[] CoreProfileFiles =
    {
        DshDefaults.ManifestFile,
        DshDefaults.LockfileFile,
        DshDefaults.PatchFile,
        DshDefaults.CompatibilityFile,
        DshDefaults.CordisFile,
    };

    private readonly ILogger _logger;

    public RestoreService(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<RestoreResult> ApplyAsync(
        RestoreApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var result = new RestoreResult
        {
            OperationId = request.OperationId,
            Plan = request.Plan ?? new RestorePlan(),
            Outcome = OperationOutcome.Failed,
        };
        var plan = result.Plan;
        var installation = request.Installation;
        var paths = request.Paths;

        if (installation is null || string.IsNullOrWhiteSpace(installation.ProfileDir))
        {
            result.Messages.Add("还原失败：未提供 profile 目录。");
            return result;
        }

        if (paths is null)
        {
            result.Messages.Add("还原失败：未提供工具数据目录。");
            return result;
        }

        result.ConflictCount = plan.Conflicts.Count;
        var profileDir = installation.ProfileDir;
        var changeActions = plan.Actions
            .Where(action => action.Kind is not (RestoreActionKind.Skip
                or RestoreActionKind.KeepPatch
                or RestoreActionKind.SkipCompatibility))
            .ToList();

        if (request.DryRun)
        {
            result.Outcome = OperationOutcome.DryRun;
            result.RestartRequired = changeActions.Count > 0;
            result.Messages.Add($"演练：计划 {changeActions.Count} 项改动，{plan.Actions.Count - changeActions.Count} 项跳过，{result.ConflictCount} 项冲突；未写入任何文件。");
            return result;
        }

        foreach (var skip in plan.Actions.Where(action => action.Kind is RestoreActionKind.Skip))
        {
            // The count is incremented once in ApplyAction; only carry the human-readable message here.
            result.Messages.Add("[已跳过] " + skip.Description);
        }

        foreach (var conflict in plan.Conflicts)
        {
            result.Messages.Add($"[冲突待处理] {conflict.Subject}：{conflict.Reason}");
        }

        if (changeActions.Count == 0)
        {
            result.Outcome = OperationOutcome.Succeeded;
            result.RestartRequired = false;
            result.Messages.Add("当前状态已与快照一致，无需写入（幂等）。");
            return result;
        }

        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? LoomPaths.NewOperationId("restore")
            : request.OperationId;
        result.OperationId = operationId;
        var journal = new JournalWriter(paths, operationId);
        var snapshotProfileDir = string.IsNullOrWhiteSpace(plan.SnapshotDir)
            ? null
            : Path.Combine(plan.SnapshotDir, "profile");
        var written = new List<string>();
        var backup = new BackupSession(profileDir, paths.RestoreBackupDirFor(operationId));

        try
        {
            // ---------------------------------------------------------- backup
            Directory.CreateDirectory(backup.BackupDir);
            foreach (var fileName in CoreProfileFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                backup.BackupIfExists(fileName, result.BackupFiles);
            }

            backup.WriteManifest();
            result.BackupDir = backup.BackupDir;
            journal.Append(operationId, OperationStage.Backup, "done",
                $"备份 {result.BackupFiles.Count} 个文件到 {backup.BackupDir}");

            // ---------------------------------------------------------- apply
            var sourceMissing = false;
            foreach (var action in plan.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = ApplyAction(action, request, snapshotProfileDir, written, result, journal, ref sourceMissing);
                if (outcome == ActionOutcome.Failed)
                {
                    throw new LoomException(LoomErrorCodes.Failed, action.Description);
                }
            }

            // ---------------------------------------------------------- verify
            var verification = VerifyApplied(plan, profileDir);
            if (verification.Count > 0)
            {
                throw new LoomException(LoomErrorCodes.Failed, "写入后校验失败：" + string.Join("；", verification));
            }

            journal.Append(operationId, OperationStage.Verify, "done",
                $"已写 {written.Count} 个文件，JSON 可解析，bundles/依赖包含恢复项");
            journal.Append(operationId, OperationStage.Done, "done",
                $"还原完成：{result.RestoredCount} 项已还原，{result.SkippedCount} 项跳过");

            result.Outcome = sourceMissing ? OperationOutcome.NeedsManual : OperationOutcome.Succeeded;
            result.RestartRequired = result.RestoredCount > 0;
            result.Messages.Insert(0,
                $"已还原 {result.RestoredCount} 项，跳过 {result.SkippedCount} 项，冲突 {result.ConflictCount} 项" +
                (result.RestartRequired ? "；重启 DSH 后生效。" : "。"));
            return result;
        }
        catch (OperationCanceledException)
        {
            Rollback(backup, written, result, journal, operationId);
            result.Outcome = OperationOutcome.Cancelled;
            result.Messages.Insert(0, "还原已取消，已回滚本次写入。");
            return result;
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "Restore failed; rolling back", operationId);
            Rollback(backup, written, result, journal, operationId);
            result.Outcome = OperationOutcome.Failed;
            result.Messages.Insert(0, "还原失败，已回滚本次写入：" + exception.Message);
            return result;
        }
    }

    private ActionOutcome ApplyAction(
        RestoreAction action,
        RestoreApplyRequest request,
        string? snapshotProfileDir,
        List<string> written,
        RestoreResult result,
        JournalWriter journal,
        ref bool sourceMissing)
    {
        var profileDir = request.Installation.ProfileDir;
        switch (action.Kind)
        {
            case RestoreActionKind.Skip:
            case RestoreActionKind.KeepPatch:
            case RestoreActionKind.SkipCompatibility:
                result.SkippedCount++;
                if (action.Kind != RestoreActionKind.Skip) result.Messages.Add("[已跳过] " + action.Description);
                return ActionOutcome.Skipped;

            case RestoreActionKind.RestorePackageJson:
            {
                var target = Path.Combine(profileDir, DshDefaults.ManifestFile);
                if (File.Exists(target))
                {
                    result.SkippedCount++;
                    return ActionOutcome.Skipped;
                }

                var source = snapshotProfileDir is null ? null : Path.Combine(snapshotProfileDir, DshDefaults.ManifestFile);
                if (source is null || !File.Exists(source))
                {
                    sourceMissing = true;
                    result.SkippedCount++;
                    result.Messages.Add("[待人工] 快照中缺少 package.json，无法恢复基础文件。");
                    return ActionOutcome.Skipped;
                }

                CopyAtomic(source, target);
                written.Add(target);
                result.RestoredCount++;
                result.Messages.Add("[已还原] package.json（来自快照）");
                journal.Append(result.OperationId, OperationStage.Apply, "done", "restore package.json");
                return ActionOutcome.Applied;
            }

            case RestoreActionKind.MergeBundles:
                return ApplyBundleMerge(action, profileDir, written, result, journal);

            case RestoreActionKind.MergeDependencies:
                return ApplyDependencyMerge(action, request, profileDir, written, result, journal);

            case RestoreActionKind.RestoreLockfile:
            {
                if (snapshotProfileDir is null)
                {
                    sourceMissing = true;
                    result.SkippedCount++;
                    result.Messages.Add("[待人工] 缺少快照目录，未还原 pnpm-lock.yaml。");
                    return ActionOutcome.Skipped;
                }

                var target = Path.Combine(profileDir, DshDefaults.LockfileFile);
                var source = Path.Combine(snapshotProfileDir, DshDefaults.LockfileFile);
                if (!File.Exists(source))
                {
                    sourceMissing = true;
                    result.SkippedCount++;
                    result.Messages.Add("[待人工] 快照中没有 pnpm-lock.yaml，未还原 lockfile。");
                    return ActionOutcome.Skipped;
                }

                if (Hashing.Sha256HexOrDefault(target) == Hashing.Sha256Hex(source))
                {
                    result.SkippedCount++;
                    return ActionOutcome.Skipped;
                }

                CopyAtomic(source, target);
                written.Add(target);
                result.RestoredCount++;
                result.Messages.Add("[已还原] pnpm-lock.yaml（来自快照）");
                journal.Append(result.OperationId, OperationStage.Apply, "done", "restore pnpm-lock.yaml");
                return ActionOutcome.Applied;
            }

            case RestoreActionKind.RestorePatch:
            {
                string? source;
                if (string.Equals(action.Detail, "snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    source = snapshotProfileDir is null ? null : Path.Combine(snapshotProfileDir, DshDefaults.PatchFile);
                }
                else
                {
                    source = action.Detail;
                }

                if (source is null || !File.Exists(source))
                {
                    sourceMissing = true;
                    result.SkippedCount++;
                    result.Messages.Add("[待人工] cordis.patch.yml 的来源文件不存在，未还原。");
                    return ActionOutcome.Skipped;
                }

                var target = Path.Combine(profileDir, DshDefaults.PatchFile);
                if (Hashing.Sha256HexOrDefault(target) == Hashing.Sha256Hex(source))
                {
                    result.SkippedCount++;
                    return ActionOutcome.Skipped;
                }

                CopyAtomic(source, target);
                written.Add(target);
                result.RestoredCount++;
                result.Messages.Add(string.Equals(action.Detail, "snapshot", StringComparison.OrdinalIgnoreCase)
                    ? "[已还原] cordis.patch.yml（来自快照）"
                    : $"[已还原] cordis.patch.yml（来自备份 {Path.GetFileName(action.Detail ?? "")}）");
                journal.Append(result.OperationId, OperationStage.Apply, "done", "restore cordis.patch.yml");
                return ActionOutcome.Applied;
            }

            case RestoreActionKind.RestoreCompatibility:
            {
                if (request.Snapshot is null)
                {
                    result.SkippedCount++;
                    result.Messages.Add("[待人工] 缺少快照元数据，未还原 compatibility.json。");
                    return ActionOutcome.Skipped;
                }

                return ApplyCompatibilityMerge(action, request, profileDir, written, result, journal);
            }

            default:
                result.SkippedCount++;
                result.Messages.Add("[已跳过] 未知动作：" + action.Kind);
                return ActionOutcome.Skipped;
        }
    }

    private ActionOutcome ApplyBundleMerge(
        RestoreAction action,
        string profileDir,
        List<string> written,
        RestoreResult result,
        JournalWriter journal)
    {
        var target = Path.Combine(profileDir, DshDefaults.ManifestFile);
        var root = ReadJsonObject(target);
        if (root is null)
        {
            result.SkippedCount++;
            result.Messages.Add("[待人工] package.json 不可读，跳过 bundle 合并。");
            return ActionOutcome.Skipped;
        }

        var current = ReadBundleArray(root);
        var merged = RestorePlanner.MergeBundleOrder(current, action.Items);
        if (RestorePlanner.SequencesEqual(current, merged))
        {
            result.SkippedCount++;
            return ActionOutcome.Skipped;
        }

        EnsureDshProfile(root)["bundles"] = ToJsonArray(merged);
        WriteJsonAtomic(target, root);
        written.Add(target);
        result.RestoredCount++;
        var added = merged.Where(name => !current.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        result.Messages.Add("[已还原] dsh.profile.bundles（新增 " + added.Count + " 项：" + string.Join("，", added) + "）");
        journal.Append(result.OperationId, OperationStage.Apply, "done",
            "merge bundles: " + string.Join(",", added));
        return ActionOutcome.Applied;
    }

    private ActionOutcome ApplyDependencyMerge(
        RestoreAction action,
        RestoreApplyRequest request,
        string profileDir,
        List<string> written,
        RestoreResult result,
        JournalWriter journal)
    {
        if (request.Snapshot is null)
        {
            result.SkippedCount++;
            result.Messages.Add("[待人工] 缺少快照元数据，未补回依赖 spec。");
            return ActionOutcome.Skipped;
        }

        var target = Path.Combine(profileDir, DshDefaults.ManifestFile);
        var root = ReadJsonObject(target);
        if (root is null)
        {
            result.SkippedCount++;
            result.Messages.Add("[待人工] package.json 不可读，跳过依赖合并。");
            return ActionOutcome.Skipped;
        }

        var dependencies = root["dependencies"] is JsonObject dependenciesObject
            ? dependenciesObject
            : new JsonObject();
        var added = new List<string>();
        foreach (var name in action.Items)
        {
            if (dependencies.ContainsKey(name)) continue;
            if (!request.Snapshot.Profile.Dependencies.TryGetValue(name, out var snapshotDependency)) continue;
            dependencies[name] = snapshotDependency.Spec;
            added.Add(name);
        }

        if (added.Count == 0)
        {
            result.SkippedCount++;
            return ActionOutcome.Skipped;
        }

        root["dependencies"] = dependencies;
        WriteJsonAtomic(target, root);
        if (!written.Contains(target, StringComparer.OrdinalIgnoreCase)) written.Add(target);
        result.RestoredCount++;
        result.Messages.Add("[已还原] dependencies（补回 " + string.Join("，", added) + "）");
        journal.Append(result.OperationId, OperationStage.Apply, "done", "merge dependencies: " + string.Join(",", added));
        return ActionOutcome.Applied;
    }

    private ActionOutcome ApplyCompatibilityMerge(
        RestoreAction action,
        RestoreApplyRequest request,
        string profileDir,
        List<string> written,
        RestoreResult result,
        JournalWriter journal)
    {
        var snapshot = request.Snapshot!;
        var runtimeVersion = request.Installation.DshVersion;
        if (string.IsNullOrWhiteSpace(runtimeVersion)
            || string.Equals(runtimeVersion, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            result.SkippedCount++;
            result.Messages.Add("[待人工] 当前运行时版本未知，未写入 compatibility.json。");
            return ActionOutcome.Skipped;
        }

        var target = Path.Combine(profileDir, DshDefaults.CompatibilityFile);
        var exemptions = new JsonObject();
        if (File.Exists(target))
        {
            var existing = ReadJsonObject(target);
            if (existing is null)
            {
                // Damaged files are never overwritten (design 12.3 / P1-6).
                result.SkippedCount++;
                result.Messages.Add("[待人工] compatibility.json 无法解析：不覆盖，请手工修复。");
                return ActionOutcome.Skipped;
            }

            foreach (var pair in existing)
            {
                exemptions[pair.Key] = pair.Value?.DeepClone();
            }
        }

        var changed = false;
        foreach (var key in action.Items)
        {
            if (!snapshot.Profile.Compatibility.Exemptions.ContainsKey(key)) continue;
            var versions = exemptions[key] is JsonArray existingVersions ? existingVersions : new JsonArray();
            var contains = versions
                .Select(node => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .Any(text => string.Equals(text, runtimeVersion, StringComparison.Ordinal));
            if (!contains)
            {
                versions.Add(runtimeVersion);
                changed = true;
            }

            exemptions[key] = versions;
        }

        if (!changed)
        {
            result.SkippedCount++;
            return ActionOutcome.Skipped;
        }

        WriteJsonAtomic(target, exemptions);
        written.Add(target);
        result.RestoredCount++;
        result.Messages.Add("[已还原] compatibility.json（当前运行时 " + runtimeVersion + " 的有效豁免）");
        journal.Append(result.OperationId, OperationStage.Apply, "done", "merge compatibility exemptions");
        return ActionOutcome.Applied;
    }

    private List<string> VerifyApplied(RestorePlan plan, string profileDir)
    {
        var problems = new List<string>();

        var manifestPath = Path.Combine(profileDir, DshDefaults.ManifestFile);
        var manifest = ReadJsonObject(manifestPath);
        if (manifest is null)
        {
            problems.Add("package.json 不可解析");
        }

        var bundles = manifest is null ? new List<string>() : ReadBundleArray(manifest);
        foreach (var action in plan.Actions.Where(action => action.Kind == RestoreActionKind.MergeBundles))
        {
            var expected = action.Detail?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           ?? Array.Empty<string>();
            foreach (var name in expected)
            {
                if (!bundles.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"bundles 缺少 {name}");
                }
            }
        }

        foreach (var action in plan.Actions.Where(action => action.Kind == RestoreActionKind.MergeDependencies))
        {
            if (manifest?["dependencies"] is not JsonObject dependencies) continue;
            foreach (var name in action.Items)
            {
                if (!dependencies.ContainsKey(name)) problems.Add($"dependencies 缺少 {name}");
            }
        }

        // Only verify compatibility.json when this restore actually wrote it. A pre-existing corrupt
        // file is preserved by design (SkipCompatibility, design 12.3) and must not fail the restore.
        var compatibilityPath = Path.Combine(profileDir, DshDefaults.CompatibilityFile);
        var wroteCompatibility = plan.Actions.Any(action => action.Kind == RestoreActionKind.RestoreCompatibility);
        if (wroteCompatibility && File.Exists(compatibilityPath) && ReadJsonObject(compatibilityPath) is null)
        {
            problems.Add("compatibility.json 不可解析");
        }

        foreach (var action in plan.Actions.Where(action => action.Kind == RestoreActionKind.RestoreLockfile))
        {
            var lockPath = Path.Combine(profileDir, DshDefaults.LockfileFile);
            if (!File.Exists(lockPath)) problems.Add("pnpm-lock.yaml 未写入");
            _ = action;
        }

        return problems;
    }

    private void Rollback(
        BackupSession backup,
        List<string> written,
        RestoreResult result,
        JournalWriter journal,
        string operationId)
    {
        journal.Append(operationId, OperationStage.Rollback, "start", "rollback of written files");
        foreach (var path in written.Distinct(StringComparer.OrdinalIgnoreCase).Reverse())
        {
            try
            {
                if (backup.TryGetBackupPath(path, out var backupPath))
                {
                    File.Copy(backupPath, path, overwrite: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "Rollback failed for " + path, operationId);
                result.Messages.Add("[回滚失败] " + path + "：" + exception.Message);
            }
        }

        result.Messages.Add("[已回滚] 本次写入已用 restore-backup 恢复，操作标记为失败。");
        journal.Append(operationId, OperationStage.Rollback, "done", "rollback finished");
    }

    // ---------------------------------------------------------------- JSON helpers

    private static JsonObject? ReadJsonObject(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ReadBundleArray(JsonObject root)
    {
        var result = new List<string>();
        if (root["dsh"] is not JsonObject dsh) return result;
        if (dsh["profile"] is not JsonObject profile) return result;
        if (profile["bundles"] is not JsonArray bundles) return result;
        foreach (var node in bundles)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text)) result.Add(text);
        }

        return result;
    }

    private static JsonObject EnsureDshProfile(JsonObject root)
    {
        if (root["dsh"] is not JsonObject dsh)
        {
            dsh = new JsonObject();
            root["dsh"] = dsh;
        }

        if (dsh["profile"] is not JsonObject profile)
        {
            profile = new JsonObject();
            dsh["profile"] = profile;
        }

        return profile;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    /// <summary>Serialises a JsonNode with the shared indented options, UTF-8 without BOM, temp + rename.</summary>
    private static void WriteJsonAtomic(string path, JsonNode node)
    {
        var json = JsonSerializer.Serialize(node, JsonDefaults.Options) + "\n";
        AtomicFile.WriteAllText(path, json);
    }

    private static void CopyAtomic(string source, string destination)
    {
        var bytes = File.ReadAllBytes(source);
        AtomicFile.WriteAllBytes(destination, bytes);
    }

    private enum ActionOutcome
    {
        Applied,
        Skipped,
        Failed,
    }

    /// <summary>Copies the core profile files into restore-backup\&lt;opId&gt; and records their hashes.</summary>
    private sealed class BackupSession
    {
        private readonly string _profileDir;
        private readonly List<BackupRecord> _records = new();

        public BackupSession(string profileDir, string backupDir)
        {
            _profileDir = profileDir;
            BackupDir = backupDir;
        }

        public string BackupDir { get; }

        public void BackupIfExists(string fileName, List<string> backupFiles)
        {
            var source = Path.Combine(_profileDir, fileName);
            if (!File.Exists(source))
            {
                _records.Add(new BackupRecord { Name = fileName, Existed = false });
                return;
            }

            var destination = Path.Combine(BackupDir, fileName);
            Directory.CreateDirectory(BackupDir);
            File.Copy(source, destination, overwrite: true);
            backupFiles.Add(destination);
            _records.Add(new BackupRecord
            {
                Name = fileName,
                Existed = true,
                Sha256 = Hashing.Sha256Hex(source),
                Size = new FileInfo(source).Length,
            });
        }

        public void WriteManifest()
        {
            Directory.CreateDirectory(BackupDir);
            AtomicFile.WriteJson(Path.Combine(BackupDir, "backup.json"), new
            {
                schemaVersion = 1,
                takenAt = DateTimeOffset.Now,
                profileDir = _profileDir,
                files = _records,
            });
        }

        public bool TryGetBackupPath(string targetPath, out string backupPath)
        {
            backupPath = "";
            var name = Path.GetFileName(targetPath);
            var record = _records.FirstOrDefault(item =>
                item.Existed && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (record is null) return false;
            var candidate = Path.Combine(BackupDir, record.Name);
            if (!File.Exists(candidate)) return false;
            backupPath = candidate;
            return true;
        }

        private sealed class BackupRecord
        {
            public string Name { get; set; } = "";
            public bool Existed { get; set; }
            public string? Sha256 { get; set; }
            public long Size { get; set; }
        }
    }
}
