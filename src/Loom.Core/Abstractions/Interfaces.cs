using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Abstractions;

/// <summary>Abstraction over the system clock so core logic stays testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public enum LogLevel
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
}

/// <summary>Redacting, rotating file logger. Every line carries the current opId when present.</summary>
public interface ILogger
{
    void Log(LogLevel level, string message, string? operationId = null);
    void LogException(Exception exception, string message, string? operationId = null);
    string LogFilePath { get; }
    void Flush();
}

/// <summary>Finds and inspects the installed DeepSeek Harness desktop app.</summary>
public interface IInstallerLocator
{
    /// <summary>Returns null when the installation cannot be located / validated.</summary>
    Task<DshInstallation?> LocateAsync(LoomConfig config, CancellationToken cancellationToken = default);

    /// <summary>Forgets cached resolution (used by the "re-locate" UI action).</summary>
    void Invalidate();
}

/// <summary>Process discovery / liveness; never kills anything.</summary>
public interface IProcessProbe
{
    IReadOnlyList<DshProcess> FindDshProcesses(string installDir);
    bool IsProcessAlive(int pid);
    bool HasAny(string processName);
}

/// <summary>Serializes Loom against DSH profile locks and inspects why the profile is busy.</summary>
public interface ILockManager
{
    LockStatus Inspect(DshInstallation installation);

    /// <summary>
    /// Waits until profile\lock, package.json.lock and .plugin-manager\run.json are quiet.
    /// Returns Busy=true with reasons on timeout.
    /// </summary>
    Task<LockWaitResult> WaitForQuietAsync(
        DshInstallation installation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires the manifest lock (package.json.lock) for Loom's own writes.
    /// Callers must dispose before invoking dsh.cmd (it takes the same lock).
    /// </summary>
    Task<IDisposable?> AcquireManifestLockAsync(
        DshInstallation installation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads the DSH updater cache (read-only) to detect a downloaded update early.</summary>
public interface IUpdaterMonitor
{
    Task<PendingInstallerInfo?> FindPendingAsync(
        DshInstallation installation,
        string currentAppVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads the profile manifest/files with a stable (non-torn) read protocol.</summary>
public interface IProfileReader
{
    ProfileState Read(DshInstallation installation, bool strict = true);
}

/// <summary>Creates and persists snapshots, and applies retention.</summary>
public interface ISnapshotService
{
    /// <summary>Captures the current profile state (light, or full with lockfile-closure archive).</summary>
    Task<SnapshotManifest> CaptureAsync(
        DshInstallation installation,
        SnapshotTrigger trigger,
        bool full,
        CancellationToken cancellationToken = default);

    /// <summary>Writes the snapshot atomically and returns its reference (hash-verified).</summary>
    Task<SnapshotRef> WriteAsync(
        SnapshotManifest manifest,
        LoomPaths paths,
        LoomConfig config,
        CancellationToken cancellationToken = default);

    /// <summary>Loads and verifies a previously written snapshot manifest.</summary>
    Task<SnapshotManifest?> LoadAsync(SnapshotRef reference, CancellationToken cancellationToken = default);

    /// <summary>Deletes snapshots beyond the retention policy; returns number removed.</summary>
    Task<int> RetainAsync(
        LoomPaths paths,
        LoomState state,
        LoomConfig config,
        CancellationToken cancellationToken = default);

    /// <summary>Counts snapshots on disk and their total size.</summary>
    SnapshotStorageInfo InspectStorage(LoomPaths paths);

    bool Verify(SnapshotRef reference);
}

/// <summary>Post-update / on-demand compatibility and integrity check.</summary>
public interface ICheckService
{
    Task<CheckResult> CheckAsync(
        DshInstallation installation,
        SnapshotManifest? baseline,
        SnapshotRef? baselineRef,
        VersionDelta versions,
        LoomPaths paths,
        CancellationToken cancellationToken = default);
}

/// <summary>Applies a file-level restore plan (no package manager involved).</summary>
public interface IRestoreService
{
    Task<RestoreResult> ApplyAsync(
        RestoreApplyRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Writes machine-readable + human-readable operation reports.</summary>
public interface IReportService
{
    Task<ReportView> WriteAsync(OperationReport report, LoomPaths paths, CancellationToken cancellationToken = default);
    Task<ReportView?> ReadLatestAsync(LoomPaths paths, CancellationToken cancellationToken = default);
    Task<ReportView?> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Evaluates plugin compatibility with the official DSH evaluator when possible.</summary>
public interface ICompatibilityEvaluator
{
    Task<CompatibilityBatchResult> EvaluateAsync(
        DshInstallation installation,
        IReadOnlyList<PluginManifestInput> plugins,
        IReadOnlyDictionary<string, List<string>> exemptions,
        CancellationToken cancellationToken = default);
}

/// <summary>HKCU Run-key autostart (single mechanism, no scheduled task).</summary>
public interface IAutostartService
{
    bool IsEnabled();
    string? GetRegisteredCommand();
    Task SetEnabledAsync(bool enabled, string command, CancellationToken cancellationToken = default);
}

/// <summary>Environment self-check ("doctor").</summary>
public interface IDoctorService
{
    Task<DoctorResult> RunAsync(
        DshInstallation? installation,
        LoomPaths paths,
        LoomConfig config,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional host hook for the CLI <c>ui</c> command (tray windows). Implemented by the WPF host.</summary>
public interface IUiCommandHandler
{
    /// <summary>target: popup | settings | wizard | report | status</summary>
    Task<int> ShowAsync(string target, CancellationToken cancellationToken = default);
}

/// <summary>Serializes Loom write operations across processes (mutex + journal).</summary>
public interface IOperationGate
{
    /// <summary>Blocks until the global operation mutex is available.</summary>
    Task<IDisposable> AcquireAsync(string operationKind, CancellationToken cancellationToken = default);
}
