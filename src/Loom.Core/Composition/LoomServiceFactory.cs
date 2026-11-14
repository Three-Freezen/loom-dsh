using Loom.Core.Abstractions;
using Loom.Core.Check;
using Loom.Core.Environment;
using Loom.Core.Evaluation;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Reporting;
using Loom.Core.Restore;
using Loom.Core.Snapshot;

namespace Loom.Core.Composition;

public sealed class LoomOptions
{
    public string? DataRoot { get; set; }
    public string? InstallDir { get; set; }
    public string? DshHome { get; set; }
    public string? ProfileDir { get; set; }
    public string? LogLevel { get; set; }

    /// <summary>Local directory containing <c>loom-dsh-Setup.exe</c>; offline test source for the update flow.</summary>
    public string? UpdateSource { get; set; }
}

public static class LoomServiceFactory
{
    public static LoomService Create(LoomOptions? options = null)
    {
        options ??= new LoomOptions();

        // Data-root resolution + auto migration must happen before anything reads config/state:
        // installed/portable builds use <exe>\Data; the first start copies legacy %LOCALAPPDATA%\Loom
        // data into it (idempotent, copy-only) and rewrites absolute paths in config/state.
        // --data-root stays the highest-priority override; development runs keep the legacy default.
        var resolution = DataRootResolver.Resolve(options.DataRoot);
        var paths = new LoomPaths(resolution.Root);
        var migration = DataMigration.TryAutoMigrate(paths, resolution);
        paths.EnsureDirectories();

        var config = ConfigStore.Load(paths);
        if (!string.IsNullOrWhiteSpace(options.InstallDir)) config.InstallDir = options.InstallDir;
        if (!string.IsNullOrWhiteSpace(options.DshHome)) config.DshHome = options.DshHome;
        if (!string.IsNullOrWhiteSpace(options.ProfileDir)) config.ProfileDir = options.ProfileDir;

        var state = StateStore.Load(paths);
        var level = ParseLogLevel(options.LogLevel ?? config.LogLevel);
        var logger = new FileLogger(paths, level);
        logger.Log(LogLevel.Info, $"数据目录：{paths.DataRoot}（{resolution.SourceLabel}）");
        LogMigration(logger, migration);
        var clock = SystemClock.Instance;

        IProcessProbe processes = new ProcessProbe(logger);
        IInstallerLocator locator = new InstallerLocator(paths, logger);
        ILockManager locks = new LockManager(processes, logger);
        IUpdaterMonitor updater = new UpdaterMonitor(logger);
        IProfileReader profileReader = new ProfileReader(logger);
        ICompatibilityEvaluator evaluator = new ElectronEvaluator(paths, logger);
        ISnapshotService snapshots = new SnapshotService(paths, logger);
        ICheckService checks = new CheckService(profileReader, evaluator, logger);
        IRestoreService restore = new RestoreService(logger);
        IReportService reports = new ReportService();
        IDoctorService doctor = new DoctorService(paths, logger);
        IAutostartService autostart = new AutostartService(logger);
        IOperationGate operationGate = new OperationGate();

        var service = new LoomService(
            paths,
            config,
            state,
            logger,
            clock,
            locator,
            processes,
            locks,
            updater,
            profileReader,
            snapshots,
            checks,
            restore,
            reports,
            doctor,
            autostart,
            operationGate);

        if (!string.IsNullOrWhiteSpace(options.UpdateSource))
        {
            service.Updates.SourceOverride = options.UpdateSource;
        }

        return service;
    }

    public static LogLevel ParseLogLevel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "error" => LogLevel.Error,
        "warn" or "warning" => LogLevel.Warn,
        "debug" or "verbose" => LogLevel.Debug,
        _ => LogLevel.Info,
    };

    /// <summary>Records the first-start legacy-data migration in the normal log (never blocks startup).</summary>
    private static void LogMigration(ILogger logger, DataMigrationResult? migration)
    {
        if (migration is null) return;
        if (!migration.Success)
        {
            var detail = migration.ErrorMessage ?? migration.Message;
            if (migration.Errors.Count > 0) detail += "（" + string.Join("；", migration.Errors.Take(3)) + "）";
            logger.Log(LogLevel.Warn, "旧数据目录自动迁移失败（已跳过，不影响启动）：" + detail);
            return;
        }

        if (migration.FilesCopied > 0)
        {
            logger.Log(
                LogLevel.Info,
                $"旧数据目录已迁移：{migration.SourceRoot} -> {migration.TargetRoot}，"
                + $"复制 {migration.FilesCopied} 个文件，跳过 {migration.FilesSkipped} 个，"
                + $"重写路径 {migration.PathsRewritten} 处。");
        }
    }
}
