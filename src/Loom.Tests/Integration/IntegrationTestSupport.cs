using Loom.Core;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Recovery;
using Loom.Core.Reporting;
using Loom.Core.Restore;
using Loom.Core.Snapshot;
using Loom.Core.Update;
using Loom.Core.Upgrade;
using Loom.Tests.CoreFlows;

namespace Loom.Tests.Integration;

// ---------------------------------------------------------------------------
// 二期集成测试的共享脚手架：
//   * 全部使用 %TEMP% 沙箱（LoomSandbox），不触碰真实 profile / 安装目录 / 网络；
//   * 只用假 registry / 假 dsh.cmd，绝不在测试里真的启动 dsh.cmd；
//   * LoomService 用真实的 ProfileReader / SnapshotService / ReportService，
//     其余端口（定位、进程、锁、更新、检查、体检、自启、操作闸门）全部可配置。
// ---------------------------------------------------------------------------

/// <summary>可配置的假依赖集合 + <see cref="LoomService"/> 构造器（懒创建，便于先配置后构造）。</summary>
internal sealed class LoomServiceHarness : IDisposable
{
    private LoomService? _service;
    private bool _disposed;

    public LoomServiceHarness(string dshVersion = "0.3.0", IOperationGate? operationGate = null, bool useRealOperationGate = false)
    {
        Sandbox = new LoomSandbox(dshVersion: dshVersion, appVersion: "0.3.0");
        Config = new LoomConfig
        {
            // 沙箱里的安装目录显式指定，避免测试依赖真实安装位置。
            InstallDir = Sandbox.InstallDir,
            AutoUpgradePolicy = "A",
            AutoSnapshotOnStartup = false,
        };
        Locator = new FakeInstallerLocator(Sandbox.Installation);
        Paths = Sandbox.Paths;
        Gate = operationGate
               ?? (useRealOperationGate ? new OperationGate(Paths.DataRoot) : new FakeOperationGate());
    }

    public LoomSandbox Sandbox { get; }
    public LoomPaths Paths { get; }
    public LoomConfig Config { get; }
    public LoomState State { get; } = new();
    public FakeInstallerLocator Locator { get; }
    public FakeProcessProbe Processes { get; } = new();
    public FakeLockManager Locks { get; } = new();
    public FakeUpdaterMonitor Updater { get; } = new();
    public FakeCheckService Checks { get; } = new();
    public FakeRegistry Registry { get; } = new();
    public FakeDshCli Cli { get; } = new();

    /// <summary>操作门：默认是进程内假门；跨进程门用例可传入真实 <see cref="OperationGate"/>。</summary>
    public IOperationGate Gate { get; }

    /// <summary>使用 <c>useRealOperationGate</c> 时暴露真实门（测试用真 mutex/文件锁的实例名）。</summary>
    public OperationGate? RealGate => Gate as OperationGate;

    /// <summary>默认假门（未注入真实门时可用）。</summary>
    public FakeOperationGate FakeGate =>
        Gate as FakeOperationGate ?? throw new InvalidOperationException("本 harness 未使用 FakeOperationGate");

    public List<LoomNotification> Notifications { get; } = new();
    public List<LoomStatus> Statuses { get; } = new();

    public LoomService Service => _service ??= Build();

    private LoomService Build()
    {
        var logger = new NoopLogger();
        var service = new LoomService(
            Paths,
            Config,
            State,
            logger,
            SystemClock.Instance,
            Locator,
            Processes,
            Locks,
            Updater,
            new ProfileReader(logger),
            new SnapshotService(Paths, logger),
            Checks,
            new RestoreService(logger),
            new ReportService(),
            new FakeDoctorService(),
            new FakeAutostartService(),
            Gate,
            // 与组合根一致：注入真实（假）进程探针，App 未退出时执行器只拒绝、不查询、不安装。
            new UpgradeService(Registry, Cli, logger, evaluator: null, processProbe: Processes),
            new PackageRestoreService(Cli, logger),
            new OfflineRollbackService(Paths, Cli, logger));
        service.NotificationRaised += (_, notification) => Notifications.Add(notification);
        service.StatusChanged += (_, status) => Statuses.Add(status);
        return service;
    }

    /// <summary>第一次 Tick 的“应用已运行”基线所需：无（app 未运行时 Tick 直接走自动升级分支）。</summary>
    public CheckResult SeedCheck(params PluginSnapshot[] plugins)
    {
        var result = new CheckResult
        {
            OperationId = "seed-check",
            CheckedAt = DateTimeOffset.Now,
            Summary = plugins.Length == 0 ? "没有插件问题" : $"{plugins.Length} 个插件不兼容",
            IncompatibleCount = plugins.Count(plugin => plugin.Compatibility == CompatibilityLevel.Incompatible),
            Plugins = plugins.ToList(),
            RequiresManualReview = false,
        };
        Checks.Result = result;
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _service?.Dispose();
        }
        catch
        {
            // best effort
        }

        Sandbox.Dispose();
    }
}

internal sealed class FakeInstallerLocator : IInstallerLocator
{
    public FakeInstallerLocator(DshInstallation? installation) => Installation = installation;

    public DshInstallation? Installation { get; set; }
    public int Invalidations;

    /// <summary>可选钩子：返回 null 时走默认；测试可用它阻塞某一次 Tick 的定位阶段。</summary>
    public Func<CancellationToken, Task<DshInstallation?>>? Handler { get; set; }

    public Task<DshInstallation?> LocateAsync(LoomConfig config, CancellationToken cancellationToken = default) =>
        Handler is null ? Task.FromResult(Installation) : Handler(cancellationToken);

    public void Invalidate() => Invalidations++;
}

internal sealed class FakeProcessProbe : IProcessProbe
{
    public List<DshProcess> Processes { get; } = new();

    public IReadOnlyList<DshProcess> FindDshProcesses(string installDir) => Processes.ToList();

    public bool IsProcessAlive(int pid) => Processes.Any(process => process.Pid == pid);

    public bool HasAny(string processName) =>
        Processes.Any(process => string.Equals(process.Name, processName, StringComparison.OrdinalIgnoreCase));
}

internal sealed class FakeLockManager : ILockManager
{
    public bool Busy { get; set; }

    public LockStatus Inspect(DshInstallation installation) =>
        Busy
            ? new LockStatus { Busy = true, Reasons = { "lock" } }
            : new LockStatus();

    public Task<LockWaitResult> WaitForQuietAsync(
        DshInstallation installation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LockWaitResult { Quiet = !Busy, Status = Inspect(installation) });

    public Task<IDisposable?> AcquireManifestLockAsync(
        DshInstallation installation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IDisposable?>(new NoopDisposable());
}

internal sealed class FakeUpdaterMonitor : IUpdaterMonitor
{
    public PendingInstallerInfo? Pending { get; set; }

    public Task<PendingInstallerInfo?> FindPendingAsync(
        DshInstallation installation,
        string currentAppVersion,
        CancellationToken cancellationToken = default) => Task.FromResult(Pending);
}

internal sealed class FakeCheckService : ICheckService
{
    public CheckResult Result { get; set; } = new() { Summary = "ok" };

    public Task<CheckResult> CheckAsync(
        DshInstallation installation,
        SnapshotManifest? baseline,
        SnapshotRef? baselineRef,
        VersionDelta versions,
        LoomPaths paths,
        CancellationToken cancellationToken = default) => Task.FromResult(Result);
}

internal sealed class FakeDoctorService : IDoctorService
{
    public Task<DoctorResult> RunAsync(
        DshInstallation? installation,
        LoomPaths paths,
        LoomConfig config,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new DoctorResult { Healthy = true, Summary = "ok" });
}

internal sealed class FakeAutostartService : IAutostartService
{
    public bool Enabled { get; private set; }

    public bool IsEnabled() => Enabled;

    public string? GetRegisteredCommand() => Enabled ? "loom-dsh --tray" : null;

    public Task SetEnabledAsync(bool enabled, string command, CancellationToken cancellationToken = default)
    {
        Enabled = enabled;
        return Task.CompletedTask;
    }
}

/// <summary>进程内假闸门：不做跨进程互斥（避免测试并行时争用命名 mutex）。</summary>
internal sealed class FakeOperationGate : IOperationGate
{
    public int Acquired;

    public Task<IDisposable> AcquireAsync(string operationKind, CancellationToken cancellationToken = default)
    {
        Acquired++;
        return Task.FromResult<IDisposable>(new NoopDisposable());
    }
}

/// <summary>假 registry：记录查询次数，返回预置候选（默认兼容）。</summary>
internal sealed class FakeRegistry : IPackageRegistry
{
    public int Calls;
    public List<UpgradeCandidate> Candidates { get; } = new();
    public bool Ok { get; set; } = true;
    public bool Offline { get; set; }
    public UpgradeFailureClass Failure { get; set; } = UpgradeFailureClass.None;
    public string? Error { get; set; }

    public Task<PackageRegistryQueryResult> QueryAsync(
        PackageRegistryQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new PackageRegistryQueryResult
        {
            Ok = Ok,
            Offline = Offline,
            Failure = Failure,
            Error = Error,
            Registry = "mock",
            Candidates = Candidates.Select(candidate => candidate.Clone()).ToList(),
        });
    }

    /// <summary>构造一个“已评估为兼容”的候选（无需真实 evaluator）。</summary>
    public static UpgradeCandidate Compatible(string version, string package) => new()
    {
        Version = version,
        EvaluatorLevel = CompatibilityLevel.Compatible,
        Engine = CompatibilityEngine.Official,
        StaticResult = StaticCheckResult.Ok,
        Integrity = "sha512-" + version,
        DistTags = new List<string> { "latest" },
        ManifestJson = $"{{\"name\":\"{package}\",\"version\":\"{version}\",\"engines\":{{\"dsh\":\"^0.3.0\"}}}}",
    };
}

/// <summary>
/// 假 dsh.cmd：同时实现 Upgrade 侧与 Recovery 侧的同形 <c>IDshCli</c>（与组合根的适配器同构），
/// 只记录参数并按 handler 返回结果；测试里绝不会真的启动 dsh.cmd。
/// </summary>
internal sealed class FakeDshCli : Loom.Core.Upgrade.IDshCli, Loom.Core.Recovery.IDshCli
{
    public List<List<string>> Calls { get; } = new();

    public Func<IReadOnlyList<string>, CliCallResult> Handler { get; set; } =
        _ => new CliCallResult { ExitCode = 0 };

    public Task<CliCallResult> RunPluginAsync(
        DshInstallation installation,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(args.ToList());
        return Task.FromResult(Handler(args));
    }
}

/// <summary>
/// 极简假 <see cref="ILoomService"/>：CLI 退出码测试与设置页透传测试用。
/// 只实现被调用到的成员，其余返回中性值。
/// </summary>
internal sealed class FakeLoomService : ILoomService, IDisposable
{
    private readonly LoomPaths _paths;

    public FakeLoomService()
    {
        _paths = new LoomPaths(Path.Combine(Path.GetTempPath(), "loom-tests", "fake-loom-" + Guid.NewGuid().ToString("N")));
        _paths.EnsureDirectories();
        var logger = new NoopLogger();
        var registry = new FakeRegistry();
        var cli = new FakeDshCli();
        Upgrades = new UpgradeService(registry, cli, logger);
        Updates = new UpdateService(_paths, () => Config, config => Config = config, logger);
    }

    public LoomPaths Paths => _paths;
    public LoomConfig Config { get; set; } = new();
    public UpdateService Updates { get; }
    public UpgradeService Upgrades { get; }

    public LoomStatus Status { get; set; } = new();

    /// <summary>UpgradePluginAsync 的返回值；为 null 时返回一个失败结果。</summary>
    public Func<UpgradeRequest, UpgradeExecutionResult>? UpgradeHandler { get; set; }

    public PackageRestorePlan PreviewPlan { get; set; } = new();
    public PackageRestoreResult PackageRestoreResult { get; set; } = new();
    public OfflineRollbackResult RollbackResult { get; set; } = new();
    public bool CanRollback { get; set; }
    public CheckResult? LastCheck { get; set; }
    public LoomConfig? SavedConfig { get; private set; }
    public List<string> Calls { get; } = new();

    public event EventHandler<LoomStatus>? StatusChanged;
    public event EventHandler<LoomNotification>? NotificationRaised;

    public LoomStatus GetStatus() => Status;

    public Task RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("refresh-status");
        StatusChanged?.Invoke(this, Status);
        return Task.CompletedTask;
    }

    public Task TickAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("tick");
        return Task.CompletedTask;
    }

    public Task<DshInstallation?> LocateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<DshInstallation?>(null);

    public Task<SnapshotOutcome> TakeSnapshotAsync(
        SnapshotRequest? request = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.FromResult(new SnapshotOutcome());

    public Task<CheckOutcome> RunCheckAsync(
        CheckRequest? request = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CheckOutcome { Success = true, Result = LastCheck });

    public Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.FromResult(new RestoreResult());

    public Task<RestorePlan?> PreviewRestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default) => Task.FromResult<RestorePlan?>(null);

    public Task<UpgradeExecutionResult> UpgradePluginAsync(
        UpgradeRequest request,
        CancellationToken cancellationToken = default) =>
        UpgradePluginAsync(request, progress: null, cancellationToken);

    public Task<UpgradeExecutionResult> UpgradePluginAsync(
        UpgradeRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Calls.Add("upgrade:" + request.PackageName + (request.DryRun ? ":dry-run" : ""));
        return Task.FromResult(
            UpgradeHandler?.Invoke(request)
            ?? new UpgradeExecutionResult
            {
                OperationId = "fake-upgrade",
                Outcome = OperationOutcome.Failed,
                Messages = { "fake upgrade not configured" },
            });
    }

    public Task<PackageRestorePlan> PreviewPackageRestoreAsync(
        PackageRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("recovery-plan");
        return Task.FromResult(PreviewPlan);
    }

    public Task<PackageRestoreResult> PackageRestoreAsync(
        PackageRestoreRequest request,
        CancellationToken cancellationToken = default) =>
        PackageRestoreAsync(request, progress: null, cancellationToken);

    public Task<PackageRestoreResult> PackageRestoreAsync(
        PackageRestoreRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Calls.Add("recovery-apply" + (request.DryRun ? ":dry-run" : ""));
        return Task.FromResult(PackageRestoreResult);
    }

    public bool CanRollbackPackageOp(string opId)
    {
        Calls.Add("can-rollback:" + opId);
        return CanRollback;
    }

    public Task<OfflineRollbackResult> RollbackPackageOpAsync(
        string opId,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("rollback:" + opId);
        if (RollbackResult.OpId.Length == 0) RollbackResult.OpId = opId;
        return Task.FromResult(RollbackResult);
    }

    public Task<ReportView?> GetLatestReportAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ReportView?>(null);

    public Task<ReportView?> GetReportAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<ReportView?>(null);

    public Task<DoctorResult> RunDoctorAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DoctorResult { Healthy = true, Summary = "ok" });

    public Task SaveConfigAsync(LoomConfig config, CancellationToken cancellationToken = default)
    {
        SavedConfig = config;
        Config = config;
        Calls.Add("save-config");
        return Task.CompletedTask;
    }

    public void MutateConfig(Action<LoomConfig> mutate)
    {
        mutate(Config);
        SavedConfig = Config;
        Calls.Add("mutate-config");
    }

    public Task SetAutostartAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        Config.Autostart = enabled;
        return Task.CompletedTask;
    }

    public Task<CheckResult?> GetLastCheckAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(LastCheck);

    public bool IsBusy { get; set; }

    public void CancelCurrentOperation()
    {
    }

    /// <summary>触发一次通知（模拟服务内部事件，用于验证事件 id 去重契约）。</summary>
    public void Raise(LoomNotification notification) => NotificationRaised?.Invoke(this, notification);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_paths.DataRoot)) Directory.Delete(_paths.DataRoot, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}

internal sealed class NoopDisposable : IDisposable
{
    public void Dispose()
    {
    }
}
