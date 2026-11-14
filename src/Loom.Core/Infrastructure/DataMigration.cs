using Loom.Core.Models;

namespace Loom.Core.Infrastructure;

/// <summary>Inputs for one explicit data-root migration (CLI <c>migrate-data</c> or the startup auto migration).</summary>
public sealed class DataMigrationOptions
{
    /// <summary>Old data root to copy from (usually <c>%LOCALAPPDATA%\Loom</c>).</summary>
    public string SourceRoot { get; init; } = "";

    /// <summary>New data root to copy into (usually <c>&lt;install&gt;\Data</c>).</summary>
    public string TargetRoot { get; init; } = "";

    /// <summary>Delete the source root after a successful migration (manual CLI only; auto migration never removes).</summary>
    public bool RemoveSource { get; init; }

    /// <summary>Plan only: count what would be copied, write nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>Tool version recorded in the migration marker.</summary>
    public string ToolVersion { get; init; } = LoomPaths.ToolVersion;
}

/// <summary>Outcome of one migration attempt. Never throws for expected failures; see <see cref="Success"/>.</summary>
public sealed class DataMigrationResult
{
    public bool Success { get; set; } = true;
    public bool DryRun { get; set; }
    public string SourceRoot { get; set; } = "";
    public string TargetRoot { get; set; } = "";

    /// <summary>Set when nothing needed to be done: <c>source-missing</c> / <c>target-initialized</c>.</summary>
    public string? SkippedReason { get; set; }

    /// <summary>Human readable summary (Chinese, shown by CLI and logs).</summary>
    public string Message { get; set; } = "";

    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public int PathsRewritten { get; set; }
    public bool MarkerWritten { get; set; }
    public string? MarkerPath { get; set; }
    public bool SourceRemoved { get; set; }
    public DateTimeOffset? MigratedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Per-file problems (best effort: the migration continues and the rest still copies).</summary>
    public List<string> Errors { get; set; } = new();
}

/// <summary>Contents of <c>&lt;data-root&gt;\migration.json</c> — proves the install-dir data root came from the legacy root.</summary>
public sealed class DataMigrationRecord
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset MigratedAt { get; set; }
    public string SourceRoot { get; set; } = "";
    public string TargetRoot { get; set; } = "";
    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public int PathsRewritten { get; set; }
    public bool RemoveSource { get; set; }
    public int Errors { get; set; }
    public string? ToolVersion { get; set; }
}

/// <summary>
/// Copies an old tool data root (default <c>%LOCALAPPDATA%\Loom</c>) into the install-directory data root.
/// Rules:
/// <list type="bullet">
///   <item>idempotent — an initialized target (config.json present) is never migrated again;</item>
///   <item>never overwrites existing target files;</item>
///   <item>skips transient files: <c>queue\**</c>, <c>*.lock</c>, <c>*.tmp</c>, <c>*.tmp-*</c>, <c>*.corrupt-*</c>, <c>*.part</c>, stale <c>migration.json</c>;</item>
///   <item>rewrites absolute paths stored in config.json / state.json from the old root prefix to the new root:</item>
///   <item><c>LoomState.LastSnapshot.Path</c>, <c>PreUpdateSnapshot.Path</c>, <c>LastOperation.ReportPath</c>, <c>LastCheck.ReportPath</c>, <c>LoomConfig.DataRoot</c>;</item>
///   <item>writes <c>migration.json</c> into the target root; failures never throw (callers must not block startup).</item>
/// </list>
/// </summary>
public static class DataMigration
{
    public const string MarkerFileName = "migration.json";
    public const string ConfigFileName = "config.json";
    public const string StateFileName = "state.json";

    /// <summary><c>&lt;root&gt;\migration.json</c> marker path.</summary>
    public static string MarkerPathFor(string dataRoot) => Path.Combine(dataRoot, MarkerFileName);

    /// <summary>True when both paths point at the same directory (case-insensitive on Windows).</summary>
    public static bool SameRoot(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Startup hook: migrate the legacy root into <c>&lt;install&gt;\Data</c> the first time an installed
    /// (or portable) build starts with an empty data root. Returns null when no migration applies.
    /// Never throws: a failure is reported in the result and must not block startup.
    /// </summary>
    public static DataMigrationResult? TryAutoMigrate(LoomPaths paths, DataRootResolution resolution)
    {
        try
        {
            if (resolution.Source is not (DataRootSource.Installed or DataRootSource.Portable)) return null;
            if (!resolution.UsesInstalledDataDirectory) return null;
            if (!SameRoot(paths.DataRoot, resolution.InstalledDataRoot)) return null;
            if (File.Exists(paths.ConfigFile)) return null;

            var legacyRoot = resolution.LegacyRoot;
            if (string.IsNullOrWhiteSpace(legacyRoot) || !Directory.Exists(legacyRoot)) return null;
            if (SameRoot(legacyRoot, paths.DataRoot)) return null;

            return Migrate(new DataMigrationOptions
            {
                SourceRoot = legacyRoot,
                TargetRoot = paths.DataRoot,
                RemoveSource = false,
                DryRun = false,
            });
        }
        catch (Exception exception)
        {
            return new DataMigrationResult
            {
                Success = false,
                ErrorCode = "auto-migrate-exception",
                ErrorMessage = exception.Message,
                Message = "自动迁移旧数据目录失败（已跳过，不影响启动）：" + exception.Message,
            };
        }
    }

    /// <summary>Run one migration. Never throws for ordinary IO failures; inspect the returned result.</summary>
    public static DataMigrationResult Migrate(DataMigrationOptions options)
    {
        var result = new DataMigrationResult { DryRun = options.DryRun };

        string source;
        string target;
        try
        {
            source = NormalizeRoot(options.SourceRoot);
            target = NormalizeRoot(options.TargetRoot);
        }
        catch (Exception exception)
        {
            return Fail(result, "invalid-root", "数据目录路径无效：" + exception.Message);
        }

        result.SourceRoot = source;
        result.TargetRoot = target;
        if (source.Length == 0 || target.Length == 0)
        {
            return Fail(result, "invalid-root", "源目录或目标目录为空。");
        }

        if (SameRoot(source, target))
        {
            return Fail(result, "same-root", "源目录与目标目录相同，无需迁移：" + source);
        }

        result.MarkerPath = MarkerPathFor(target);

        if (!Directory.Exists(source))
        {
            result.SkippedReason = "source-missing";
            result.Message = "旧数据目录不存在，无需迁移：" + source;
            return result;
        }

        // Idempotency: a data root that already has config.json is initialized and must never be re-migrated.
        if (File.Exists(Path.Combine(target, ConfigFileName)))
        {
            result.SkippedReason = "target-initialized";
            result.Message = "目标数据目录已初始化（已有 config.json），跳过迁移：" + target;
            if (options.RemoveSource)
            {
                TryRemoveSourceAfterPriorMigration(result, source, target);
            }

            return result;
        }

        if (!options.DryRun)
        {
            try
            {
                Directory.CreateDirectory(target);
            }
            catch (Exception exception)
            {
                return Fail(result, "target-create-failed", "无法创建目标数据目录：" + exception.Message);
            }
        }

        var targetInsideSource = IsUnder(target, source);
        var sourceHasConfig = File.Exists(Path.Combine(source, ConfigFileName));
        foreach (var file in EnumerateSourceFiles(source, result))
        {
            var relative = Path.GetRelativePath(source, file);
            if (IsTransient(relative))
            {
                result.FilesSkipped++;
                continue;
            }

            // Defensive: never copy the target back into itself when it lives under the source root.
            if (targetInsideSource && IsUnder(file, target))
            {
                result.FilesSkipped++;
                continue;
            }

            var destination = Path.Combine(target, relative);
            if (File.Exists(destination))
            {
                // Never overwrite data that may already be in use.
                result.FilesSkipped++;
                continue;
            }

            if (options.DryRun)
            {
                result.FilesCopied++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: false);
                result.FilesCopied++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                result.Errors.Add(relative + ": " + exception.Message);
            }
        }

        if (options.DryRun)
        {
            result.Message = "演练模式：未写入任何文件。";
            return result;
        }

        result.MigratedAt = DateTimeOffset.Now;
        if (sourceHasConfig && !File.Exists(Path.Combine(target, ConfigFileName)))
        {
            result.Success = false;
            result.ErrorCode = "config-copy-failed";
            result.ErrorMessage = "旧目录的 config.json 未能复制到新目录，迁移未完成。";
        }
        else
        {
            result.PathsRewritten = RewriteAbsolutePaths(target, source, result.Errors);
        }

        WriteMarker(result, options);

        if (options.RemoveSource && result.Success)
        {
            TryRemoveSource(result, source, target);
        }

        if (result.Message.Length == 0)
        {
            result.Message = result.Success
                ? $"已迁移 {result.FilesCopied} 个文件（跳过 {result.FilesSkipped} 个）到 {target}。"
                : "迁移失败：" + (result.ErrorMessage ?? "见错误列表。");
        }

        return result;
    }

    /// <summary>
    /// Rewrite absolute old-root paths stored in the migrated config.json / state.json. Returns the number
    /// of rewritten values. Used by tests and the migration marker.
    /// </summary>
    public static int RewriteAbsolutePaths(string targetRoot, string sourceRoot, List<string>? errors = null)
    {
        errors ??= new List<string>();
        var rewritten = 0;

        var statePath = Path.Combine(targetRoot, StateFileName);
        if (File.Exists(statePath))
        {
            try
            {
                var state = AtomicFile.ReadJson<LoomState>(statePath);
                if (state is not null)
                {
                    var changed = 0;
                    changed += RewriteSnapshotRef(state.LastSnapshot, sourceRoot, targetRoot);
                    changed += RewriteSnapshotRef(state.PreUpdateSnapshot, sourceRoot, targetRoot);
                    if (state.LastOperation?.ReportPath is { Length: > 0 } operationReport)
                    {
                        var value = RewritePath(operationReport, sourceRoot, targetRoot);
                        if (!string.Equals(value, operationReport, StringComparison.Ordinal))
                        {
                            state.LastOperation.ReportPath = value;
                            changed++;
                        }
                    }

                    if (state.LastCheck?.ReportPath is { Length: > 0 } checkReport)
                    {
                        var value = RewritePath(checkReport, sourceRoot, targetRoot);
                        if (!string.Equals(value, checkReport, StringComparison.Ordinal))
                        {
                            state.LastCheck.ReportPath = value;
                            changed++;
                        }
                    }

                    if (changed > 0)
                    {
                        AtomicFile.WriteJson(statePath, state);
                    }

                    rewritten += changed;
                }
            }
            catch (Exception exception)
            {
                errors.Add(StateFileName + ": " + exception.Message);
            }
        }

        var configPath = Path.Combine(targetRoot, ConfigFileName);
        if (File.Exists(configPath))
        {
            try
            {
                var config = AtomicFile.ReadJson<LoomConfig>(configPath);
                if (config?.DataRoot is { Length: > 0 } dataRoot)
                {
                    var value = RewritePath(dataRoot, sourceRoot, targetRoot);
                    if (!string.Equals(value, dataRoot, StringComparison.Ordinal))
                    {
                        config.DataRoot = value;
                        AtomicFile.WriteJson(configPath, config);
                        rewritten++;
                    }
                }
            }
            catch (Exception exception)
            {
                errors.Add(ConfigFileName + ": " + exception.Message);
            }
        }

        return rewritten;
    }

    /// <summary>Rewrite one absolute path from the old root prefix to the new root. Unrelated paths are returned unchanged.</summary>
    public static string RewritePath(string value, string sourceRoot, string targetRoot)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        try
        {
            var full = Path.GetFullPath(value);
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot));

            if (string.Equals(Path.TrimEndingDirectorySeparator(full), source, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }

            var prefix = source + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(target, full.Substring(prefix.Length));
            }
        }
        catch
        {
            // Unparseable value: keep it untouched rather than corrupting user data.
        }

        return value;
    }

    private static int RewriteSnapshotRef(SnapshotRef? snapshot, string sourceRoot, string targetRoot)
    {
        if (snapshot?.Path is not { Length: > 0 }) return 0;
        var value = RewritePath(snapshot.Path, sourceRoot, targetRoot);
        if (string.Equals(value, snapshot.Path, StringComparison.Ordinal)) return 0;
        snapshot.Path = value;
        return 1;
    }

    private static void WriteMarker(DataMigrationResult result, DataMigrationOptions options)
    {
        if (result.MarkerPath is null) return;
        try
        {
            var record = new DataMigrationRecord
            {
                MigratedAt = result.MigratedAt ?? DateTimeOffset.Now,
                SourceRoot = result.SourceRoot,
                TargetRoot = result.TargetRoot,
                FilesCopied = result.FilesCopied,
                FilesSkipped = result.FilesSkipped,
                PathsRewritten = result.PathsRewritten,
                RemoveSource = options.RemoveSource,
                Errors = result.Errors.Count,
                ToolVersion = options.ToolVersion,
            };
            AtomicFile.WriteJson(result.MarkerPath, record);
            result.MarkerWritten = true;
        }
        catch (Exception exception)
        {
            result.Success = false;
            result.ErrorCode ??= "marker-write-failed";
            result.Errors.Add(MarkerFileName + ": " + exception.Message);
        }
    }

    private static void TryRemoveSourceAfterPriorMigration(DataMigrationResult result, string source, string target)
    {
        try
        {
            var record = AtomicFile.ReadJson<DataMigrationRecord>(MarkerPathFor(target));
            if (record is null || !SameRoot(record.SourceRoot, source))
            {
                result.Message += "（目标目录没有指向该源的迁移记录，未删除旧目录）";
                return;
            }

            TryRemoveSource(result, source, target);
        }
        catch (Exception exception)
        {
            result.Errors.Add("读取迁移记录失败：" + exception.Message);
        }
    }

    private static void TryRemoveSource(DataMigrationResult result, string source, string target)
    {
        try
        {
            var root = Path.GetPathRoot(source) ?? "";
            if (SameRoot(source, root))
            {
                result.Errors.Add("拒绝删除磁盘根目录：" + source);
                return;
            }

            // Deleting the source must never delete the target (e.g. target nested under source).
            if (IsUnder(target, source) || SameRoot(target, source))
            {
                result.Errors.Add("目标目录位于源目录内，拒绝删除源目录。");
                return;
            }

            Directory.Delete(source, recursive: true);
            result.SourceRemoved = true;
            result.Message += " 已删除旧数据目录。";
        }
        catch (Exception exception)
        {
            result.Errors.Add("删除旧数据目录失败：" + exception.Message);
        }
    }

    private static List<string> EnumerateSourceFiles(string source, DataMigrationResult result)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            };
            return Directory.EnumerateFiles(source, "*", options)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception)
        {
            result.Errors.Add("枚举旧数据目录失败：" + exception.Message);
            return new List<string>();
        }
    }

    /// <summary>Transient/derived files that must not be migrated (locks, queue IPC, atomic-write temps).</summary>
    internal static bool IsTransient(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return true;
        var normalized = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return true;

        // queue\ contains one-shot IPC requests/results of a running tray; never worth copying.
        if (segments[0].Equals("queue", StringComparison.OrdinalIgnoreCase)) return true;

        var fileName = segments[^1];
        if (segments.Length == 1 && fileName.Equals(MarkerFileName, StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.Contains(".tmp-", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.Contains(".corrupt-", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsUnder(string candidate, string parent)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeRoot(string path) =>
        string.IsNullOrWhiteSpace(path) ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static DataMigrationResult Fail(DataMigrationResult result, string code, string message)
    {
        result.Success = false;
        result.ErrorCode = code;
        result.ErrorMessage = message;
        result.Message = message;
        return result;
    }
}
