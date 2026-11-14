using System.Text.Json.Serialization;
using Loom.Core.Infrastructure;

namespace Loom.Core.Models;

public enum SnapshotTrigger
{
    Auto,
    PreUpdate,
    Manual,
    PostUpdate,
}

public enum BundleClass
{
    Baseline,
    Official,
    ThirdParty,
}

public enum CompatibilityLevel
{
    Compatible,
    Incompatible,
    Unverified,
    Unknown,
}

public enum CompatibilityEngine
{
    Official,
    Fallback,
    Unknown,
}

public enum EvidenceLevel
{
    None,
    Weak,
    Medium,
    Strong,
}

public enum IssueKind
{
    SnapshotMissing,
    VersionChanged,
    BundleMissing,
    DependencyMissing,
    PatchReset,
    CompatibilityIncompatible,
    CompatibilityUnverified,
    CompatibilityUnknown,
    LockBusy,
    ToolError,
}

public enum OperationStage
{
    Start,
    Locate,
    LockWait,
    ReadProfile,
    Capture,
    WriteSnapshot,
    Check,
    Plan,
    Backup,
    Apply,
    Verify,
    Report,
    Done,
    Failed,
    Rollback,
}

public enum OperationOutcome
{
    Succeeded,
    Failed,
    NeedsManual,
    Skipped,
    Cancelled,
    DryRun,
}

public enum TrayStatus
{
    Normal,
    Busy,
    AwaitingExit,
    NeedsManual,
    Failed,
    NotFound,
}

public enum UpdatePhase
{
    Idle,
    SnapshotReady,
    UpdateDetected,
    NewVersionRunning,
    AwaitingAppExit,
    Restoring,
    VerifyFailed,
    Blocked,
    Failed,
    Done,
}

public enum Severity
{
    Info,
    Warning,
    Error,
}

public enum NotificationKind
{
    Info,
    SnapshotTaken,
    UpdateDetected,
    RestoreCompleted,
    NeedsManual,
    Failed,
}

public sealed class LoomConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Explicit install dir override (null = auto locate).</summary>
    public string? InstallDir { get; set; }

    /// <summary>Explicit DSH_HOME override (null = env / %USERPROFILE%\.dsh).</summary>
    public string? DshHome { get; set; }

    /// <summary>Explicit profile dir override (default: &lt;dsh-home&gt;\profiles\desktop).</summary>
    public string? ProfileDir { get; set; }

    /// <summary>Tool data root (null/empty = %LOCALAPPDATA%\Loom).</summary>
    public string? DataRoot { get; set; }

    /// <summary>Compatibility policy: A = auto upgrade to compatible version (phase 2).</summary>
    public string AutoUpgradePolicy { get; set; } = "A";

    /// <summary>
    /// 策略 A 自动升级总开关。**默认关闭**（安全默认）：关闭时 Tick 绝不查询 registry、绝不执行安装；
    /// 打开且 <see cref="AutoUpgradePolicy"/> = "A" 时才允许 Tick 在 DSH 完全退出后自动升级
    /// “与目标 dshVersion 兼容”的插件（没有兼容版本时只提示人工，绝不自动授予风险豁免）。
    /// </summary>
    public bool AutoUpgradeEnabled { get; set; }

    public bool AllowMajorUpgrade { get; set; }

    /// <summary>是否允许把 prerelease（nightly/next）候选纳入自动升级（仅在没有稳定兼容版本时才会选中）。</summary>
    public bool AllowPrereleaseUpgrade { get; set; }

    /// <summary>策略 A 的 registry 查询超时（秒，默认 60）。</summary>
    public int UpgradeQueryTimeoutSeconds { get; set; } = 60;

    /// <summary>策略 A 的单包安装超时（秒，默认 900 = 15min，设计 §8.4）。</summary>
    public int UpgradeInstallTimeoutSeconds { get; set; } = 900;

    /// <summary>升级前必须确认 DSH 已完全退出（默认 true；false 时不注入进程探针）。</summary>
    public bool RequireAppExitForUpgrade { get; set; } = true;

    /// <summary>包级恢复（二期实验性）总开关。默认关闭：关闭时 CLI/UI 的包级恢复直接拒绝，不写 profile。</summary>
    public bool PackageRestoreEnabled { get; set; }

    /// <summary>
    /// 包级恢复离线失败后是否允许**询问**用户联网重试（仍需逐次同意；默认 false = 只走离线）。
    /// </summary>
    public bool PackageRestoreAllowNetworkFallback { get; set; }

    /// <summary>包级恢复的单次 install 超时（秒，默认 600）。</summary>
    public int PackageRestoreInstallTimeoutSeconds { get; set; } = 600;

    /// <summary>包级恢复后首启就绪判定窗口（秒，默认 20，与 <see cref="HealthCheckSeconds"/> 默认一致）。</summary>
    public int PackageRestoreReadyTimeoutSeconds { get; set; } = 20;

    /// <summary><c>restore-backup\*</c> 回滚点保留份数（默认 5；清理只删最旧的，绝不删当前 op）。</summary>
    public int KeepRollbackBackups { get; set; } = 5;

    public int KeepSnapshots { get; set; } = 5;

    /// <summary>Total snapshot quota in bytes (default 1 GiB).</summary>
    public long MaxSnapshotBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Max bytes for a full (lockfile closure) archive (default 500 MiB).</summary>
    public long MaxArchiveBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>Take a full snapshot when a new installer download is detected.</summary>
    public bool FullSnapshotOnUpdate { get; set; } = true;

    public bool AutoSnapshotOnStartup { get; set; } = true;

    /// <summary>Automatically apply file-level restore after a recovery event with strong evidence.</summary>
    public bool AutoRestoreAfterRecovery { get; set; } = true;

    public bool Notifications { get; set; } = true;

    public string QuietHoursStart { get; set; } = "23:00";

    public string QuietHoursEnd { get; set; } = "08:00";

    public bool QuietHoursEnabled { get; set; } = true;

    public bool Autostart { get; set; }

    public string LogLevel { get; set; } = "info";

    /// <summary>system | dark | light</summary>
    public string Theme { get; set; } = "system";

    /// <summary>Automatically check GitHub Releases for a Loom update once per 24 hours.</summary>
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>UTC timestamp of the last update check (success or failure); used to throttle auto-checks.</summary>
    public DateTimeOffset LastUpdateCheckUtc { get; set; }

    /// <summary>Delete the downloaded setup installer after a successful in-app upgrade.</summary>
    public bool DeleteSetupAfterUpdate { get; set; } = true;

    public bool FirstRunCompleted { get; set; }

    public int HealthCheckSeconds { get; set; } = 20;

    /// <summary>Seconds after a stable app start before an automatic snapshot is taken.</summary>
    public int StartupSnapshotDelaySeconds { get; set; } = 60;

    /// <summary>Debounce for profile-change driven auto snapshots.</summary>
    public int ProfileChangeDebounceSeconds { get; set; } = 45;

    public int OperationTimeoutSeconds { get; set; } = 60;
}

public sealed class SnapshotRef
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTimeOffset TakenAt { get; set; }
    public string AppVersion { get; set; } = "unknown";
    public string DshVersion { get; set; } = "unknown";
    public string Sha256 { get; set; } = "";
    public SnapshotTrigger Trigger { get; set; }
    public long SizeBytes { get; set; }
    public bool Frozen { get; set; }
    public int PluginCount { get; set; }
    public int BundleCount { get; set; }
}

public sealed class PendingUpdate
{
    public string TargetVersion { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? Sha512 { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
    public string Source { get; set; } = "updater-cache";
    public bool SnapshotTaken { get; set; }
}

public sealed class HandledUpdate
{
    public string TargetVersion { get; set; } = "";
    public DateTimeOffset HandledAt { get; set; }
    public string Result { get; set; } = "";
}

public sealed class LastOperation
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public OperationOutcome Outcome { get; set; }
    public DateTimeOffset At { get; set; }
    public string Summary { get; set; } = "";
    public string? ReportPath { get; set; }
}

public sealed class LastCheckSummary
{
    public DateTimeOffset At { get; set; }
    public string AppVersion { get; set; } = "unknown";
    public string DshVersion { get; set; } = "unknown";
    public int Issues { get; set; }
    public int Incompatible { get; set; }
    public int Unverified { get; set; }
    public int Unknown { get; set; }
    public EvidenceLevel Evidence { get; set; }
    public bool RestorePossible { get; set; }
    public string Summary { get; set; } = "";
    public string? ReportPath { get; set; }
}

public sealed class LoomState
{
    public int SchemaVersion { get; set; } = 1;
    public SnapshotRef? LastSnapshot { get; set; }
    public SnapshotRef? PreUpdateSnapshot { get; set; }
    public PendingUpdate? PendingUpdate { get; set; }
    public HandledUpdate? HandledUpdate { get; set; }
    public LastOperation? LastOperation { get; set; }
    public LastCheckSummary? LastCheck { get; set; }
    public string? ToolVersion { get; set; }
    public string? LastNotifiedEventId { get; set; }
    public DateTimeOffset? LastNotifiedAt { get; set; }

    /// <summary>
    /// 最近通知过的 eventId（滚动窗口，最多 <see cref="Infrastructure.NotificationLedger.MaxRememberedEventIds"/> 个）。
    /// 用于跨进程重启去重；旧版 state.json 只有 lastNotifiedEventId，加载时会自动并入本列表。
    /// </summary>
    public List<string> RecentNotifiedEventIds { get; set; } = new();
    public UpdatePhase Phase { get; set; } = UpdatePhase.Idle;
    public bool FirstRunCompleted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>App version observed when the app was last seen running stably (for update detection).</summary>
    public string? LastSeenAppVersion { get; set; }

    /// <summary>Dsh version observed when the app was last seen running stably.</summary>
    public string? LastSeenDshVersion { get; set; }

    public DateTimeOffset? LastAutoSnapshotAt { get; set; }
    public string? LastAutoSnapshotFingerprint { get; set; }
    public bool AwaitingAppExit { get; set; }
    public string? PendingRestoreSnapshotId { get; set; }
}

public sealed class DshInstallation
{
    public string InstallDir { get; set; } = "";
    public string ProfileDir { get; set; } = "";
    public string DshHome { get; set; } = "";
    public string AppVersion { get; set; } = "unknown";
    public string DshVersion { get; set; } = "unknown";
    public string CliPath { get; set; } = "";
    public string ElectronExe { get; set; } = "";
    public string AppAsar { get; set; } = "";
    public string? UpdaterRoot { get; set; }
    public string Source { get; set; } = "";
    public bool Found => InstallDir.Length > 0 && Directory.Exists(ProfileDir);

    /// <summary>Installation-owned bundles (from the current install template when available).</summary>
    public List<string> BaseBundles { get; set; } = new(DshDefaults.BaselineBundles);

    public DshInstallationInfo ToInfo() => new()
    {
        InstallDir = InstallDir,
        ProfileDir = ProfileDir,
        DshHome = DshHome,
        AppVersion = AppVersion,
        DshVersion = DshVersion,
        Found = Found,
    };
}

public sealed class DshInstallationInfo
{
    public string InstallDir { get; set; } = "";
    public string ProfileDir { get; set; } = "";
    public string DshHome { get; set; } = "";
    public string AppVersion { get; set; } = "unknown";
    public string DshVersion { get; set; } = "unknown";
    public bool Found { get; set; }
}

public sealed class DshProcess
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public string? ExecutablePath { get; set; }
}

public sealed class LockStatus
{
    public bool Busy { get; set; }
    public List<string> Reasons { get; set; } = new();
    public List<int> Pids { get; set; } = new();
    public string? Detail { get; set; }
}

public sealed class LockWaitResult
{
    public bool Quiet { get; set; }
    public LockStatus Status { get; set; } = new();
    public TimeSpan Waited { get; set; }
}

public sealed class PendingInstallerInfo
{
    public string TargetVersion { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? Sha512 { get; set; }
    public string? Path { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
    /// <summary>download | ready</summary>
    public string Stage { get; set; } = "ready";
}

public sealed class SnapshotStorageInfo
{
    public int Count { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class LoomStatus
{
    public TrayStatus TrayState { get; set; } = TrayStatus.NotFound;
    public UpdatePhase Phase { get; set; } = UpdatePhase.Idle;
    public string Headline { get; set; } = "";
    public string Detail { get; set; } = "";
    public DshInstallationInfo? Installation { get; set; }
    public string? AppVersion { get; set; }
    public string? DshVersion { get; set; }
    public DateTimeOffset? LastSnapshotAt { get; set; }
    public SnapshotTrigger? LastSnapshotTrigger { get; set; }
    public int ProtectedPlugins { get; set; }
    public int SnapshotCount { get; set; }
    public long SnapshotBytes { get; set; }
    public int PendingIssues { get; set; }
    public int IncompatiblePlugins { get; set; }
    public int UnverifiedPlugins { get; set; }
    public bool AppRunning { get; set; }
    public bool Busy { get; set; }
    public string? BusyLabel { get; set; }
    public string? LastReportPath { get; set; }
    public bool FirstRun { get; set; }
    public List<ActivityItem> RecentActivity { get; set; } = new();

    /// <summary>
    /// 最近一个“可离线回滚”的包级操作 opId（restore-backup\&lt;opId&gt;\ 带回滚点且哈希校验通过）。
    /// 为空表示当前没有可回滚的包级操作。**可选字段**：旧版 UI/JSON 消费方忽略即可。
    /// </summary>
    public string? RollbackOperationId { get; set; }

    /// <summary>
    /// 包级恢复当前是否可用（开关打开且已有快照）。**可选字段**，用于 UI 入口启用状态；
    /// 真正的执行仍会再校验一次开关与前置条件。
    /// </summary>
    public bool PackageRestoreAvailable { get; set; }
}

public sealed class ActivityItem
{
    public DateTimeOffset At { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Kind { get; set; } = "info";
}

public sealed class LoomNotification
{
    public string EventId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public NotificationKind Kind { get; set; }
    public string? ReportPath { get; set; }
    public DateTimeOffset At { get; set; }
}
