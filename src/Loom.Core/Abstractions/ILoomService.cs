using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Update;

namespace Loom.Core.Abstractions;

/// <summary>Facade used by both the CLI and the tray UI.</summary>
public interface ILoomService
{
    LoomPaths Paths { get; }
    LoomConfig Config { get; }

    /// <summary>In-app update flow (GitHub Releases probe / download / silent upgrade).</summary>
    UpdateService Updates { get; }

    LoomStatus GetStatus();

    event EventHandler<LoomStatus>? StatusChanged;
    event EventHandler<LoomNotification>? NotificationRaised;

    Task RefreshStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs the periodic maintenance logic (update detection, version change, auto snapshot).</summary>
    Task TickAsync(CancellationToken cancellationToken = default);

    Task<DshInstallation?> LocateAsync(CancellationToken cancellationToken = default);

    Task<SnapshotOutcome> TakeSnapshotAsync(
        SnapshotRequest? request = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<CheckOutcome> RunCheckAsync(
        CheckRequest? request = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RestorePlan?> PreviewRestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default);

    Task<ReportView?> GetLatestReportAsync(CancellationToken cancellationToken = default);
    Task<ReportView?> GetReportAsync(string path, CancellationToken cancellationToken = default);
    Task<DoctorResult> RunDoctorAsync(CancellationToken cancellationToken = default);
    Task SaveConfigAsync(LoomConfig config, CancellationToken cancellationToken = default);
    Task SetAutostartAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<CheckResult?> GetLastCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>True when another Loom operation is running.</summary>
    bool IsBusy { get; }

    /// <summary>Cooperative cancellation of the running operation (safe points only).</summary>
    void CancelCurrentOperation();
}

public sealed class SnapshotRequest
{
    public SnapshotTrigger Trigger { get; set; } = SnapshotTrigger.Manual;
    public bool Full { get; set; }

    /// <summary>Overwrite the frozen pre-update pointer even when a snapshot already exists.</summary>
    public bool Force { get; set; }
}

public sealed class CheckRequest
{
    public bool Auto { get; set; }
}

public sealed class RestoreRequest
{
    public bool DryRun { get; set; }
    public bool Force { get; set; }
    public bool Auto { get; set; }
    public string? SnapshotId { get; set; }
    public EvidenceLevel? EvidenceOverride { get; set; }

    /// <summary>When set, only these bundles may be re-added (automatic restore uses check candidates).</summary>
    public IReadOnlyList<string>? AllowedBundles { get; set; }

    /// <summary>When set, only these dependencies may be re-added (automatic restore uses check candidates).</summary>
    public IReadOnlyList<string>? AllowedDependencies { get; set; }

    /// <summary>When false, patch changes are reported but not applied (automatic restore).</summary>
    public bool? AllowPatchRestore { get; set; }

    /// <summary>When false, compatibility exemptions are not merged (automatic restore).</summary>
    public bool? AllowCompatibilityRestore { get; set; }
}
