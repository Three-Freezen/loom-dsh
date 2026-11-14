using Loom.Core.Abstractions;
using Loom.Core.Check;
using Loom.Core.Environment;
using Loom.Core.Evaluation;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Recovery;
using Loom.Core.Reporting;
using Loom.Core.Restore;
using Loom.Core.Snapshot;
using Loom.Core.Upgrade;

// Upgrade 与 Recovery 各自声明了一个同形的 IDshCli / DshCliAdapter（不同命名空间），
// 组合根必须只创建一个真正的 DshCli 实例，再各自套一层轻量适配器。
using UpgradeDshCliAdapter = Loom.Core.Upgrade.DshCliAdapter;
using RecoveryDshCliAdapter = Loom.Core.Recovery.DshCliAdapter;
using UpgradeDshCli = Loom.Core.Upgrade.IDshCli;
using RecoveryDshCli = Loom.Core.Recovery.IDshCli;

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
    /// <summary>
    /// 创建服务。<paramref name="operationGate"/> 由 CLI 传入“已预占跨进程门”的实例
    /// （upgrade/recovery 长操作：拿不到门时 CLI 直接返回 Busy，拿到后由 CLI 持有到命令结束）；
    /// 传 null 时创建普通实例。
    /// </summary>
    public static LoomService Create(LoomOptions? options = null, IOperationGate? operationGate = null)
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
        // P2-R2 / P3-R1：操作门必须按数据根派生（互斥量名 + 数据根文件锁），不同数据根互不阻塞。
        IOperationGate gate = operationGate ?? new OperationGate(paths.DataRoot);

        // 二期：插件升级（策略 A）与包级恢复共用同一个 DshCli 实例，各自只包一层薄适配器
        // （Upgrade.IDshCli / Recovery.IDshCli 只是两个命名空间下的同形端口）。这样 dsh.cmd 的
        // 启动、超时与子进程树清理逻辑只有一份，组合根里不会出现两个 CLI 实例。
        var dshCli = new DshCli(logger);
        UpgradeDshCli upgradeCli = new UpgradeDshCliAdapter(dshCli);
        RecoveryDshCli recoveryCli = new RecoveryDshCliAdapter(dshCli);
        IPackageRegistry packageRegistry = new DshCliPackageRegistry(upgradeCli, logger);

        LoomService? service = null;
        // RequireAppExitForUpgrade=false 时把探针当作“DSH 已退出”；包装器只读，绝不结束进程。
        // 配置在运行期可改，因此读服务的当前配置，而不是构造时的快照。
        IProcessProbe upgradeProcessProbe = new AppExitRequirementProbe(
            processes,
            () => service?.Config.RequireAppExitForUpgrade ?? true);

        // 体验：候选兼容性用目标 dshVersion 复检；升级失败/成功后由 LoomService 写报告与通知。
        var upgrades = new UpgradeService(packageRegistry, upgradeCli, logger, evaluator, upgradeProcessProbe);
        var packageRestore = new PackageRestoreService(recoveryCli, logger);
        var rollback = new OfflineRollbackService(paths, recoveryCli, logger);

        service = new LoomService(
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
            gate,
            upgrades,
            packageRestore,
            rollback);

        if (!string.IsNullOrWhiteSpace(options.UpdateSource))
        {
            service.Updates.SourceOverride = options.UpdateSource;
        }

        return service;
    }

    /// <summary>
    /// 按配置决定是否报告“DSH 仍在运行”的只读进程探针包装：
    /// <c>RequireAppExitForUpgrade=false</c> 时返回空表（执行器视为已退出），否则原样转发。
    /// 绝不结束进程。
    /// </summary>
    private sealed class AppExitRequirementProbe : IProcessProbe
    {
        private readonly IProcessProbe _inner;
        private readonly Func<bool> _required;

        public AppExitRequirementProbe(IProcessProbe inner, Func<bool> required)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _required = required ?? throw new ArgumentNullException(nameof(required));
        }

        public IReadOnlyList<DshProcess> FindDshProcesses(string installDir) =>
            _required() ? _inner.FindDshProcesses(installDir) : Array.Empty<DshProcess>();

        public bool IsProcessAlive(int pid) => _inner.IsProcessAlive(pid);

        public bool HasAny(string processName) => _inner.HasAny(processName);
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
