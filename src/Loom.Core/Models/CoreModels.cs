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

    public bool AllowMajorUpgrade { get; set; }

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
