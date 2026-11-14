using System.Text.Json.Nodes;
using Loom.Core.Infrastructure;

namespace Loom.Core.Models;

/// <summary>Parsed snapshot of the desktop profile's manifest files (used by snapshot / check / restore).</summary>
public sealed class ProfileState
{
    public string ProfileDir { get; set; } = "";
    public bool HasManifest { get; set; }
    public string? PackageJsonPath { get; set; }
    public string? PackageJsonText { get; set; }
    public List<string> Bundles { get; set; } = new();
    public Dictionary<string, DependencySnapshot> Dependencies { get; set; } = new();
    public string? LockfilePath { get; set; }
    public string? LockfileSha256 { get; set; }
    public string? PatchPath { get; set; }
    public string? PatchSha256 { get; set; }
    public List<string> PatchBackups { get; set; } = new();
    public string? CompatibilityPath { get; set; }
    public string? CompatibilitySha256 { get; set; }
    public string? CordisYmlPath { get; set; }
    public string? CordisYmlSha256 { get; set; }
    public Dictionary<string, string> FileSha256 { get; set; } = new();
    public List<PluginSnapshot> Plugins { get; set; } = new();

    /// <summary>Stable fingerprint of the parts that matter for change detection.</summary>
    public string Fingerprint()
    {
        var parts = new List<string>
        {
            PackageJsonText ?? "<none>",
            LockfileSha256 ?? "<none>",
            PatchSha256 ?? "<none>",
            string.Join("|", Bundles),
        };
        return string.Join("\n", parts);
    }
}

public sealed class VersionDelta
{
    public string? AppVersionBefore { get; set; }
    public string? AppVersionAfter { get; set; }
    public string? DshVersionBefore { get; set; }
    public string? DshVersionAfter { get; set; }
    public bool AppChanged { get; set; }
    public bool DshChanged { get; set; }
    public bool AnyChanged => AppChanged || DshChanged;

    public string Describe()
    {
        var parts = new List<string>();
        if (AppChanged) parts.Add($"应用 {AppVersionBefore ?? "unknown"} → {AppVersionAfter ?? "unknown"}");
        if (DshChanged) parts.Add($"运行时 {DshVersionBefore ?? "unknown"} → {DshVersionAfter ?? "unknown"}");
        return parts.Count == 0 ? "版本未变化" : string.Join("；", parts);
    }
}

public sealed class CheckIssue
{
    public IssueKind Kind { get; set; }
    public Severity Severity { get; set; } = Severity.Warning;
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Subject { get; set; }
    public string? Detail { get; set; }
}

public sealed class CheckResult
{
    public string OperationId { get; set; } = "";
    public DateTimeOffset CheckedAt { get; set; }
    public VersionDelta Versions { get; set; } = new();
    public SnapshotRef? Baseline { get; set; }
    public EvidenceLevel Evidence { get; set; }
    public List<string> EvidenceReasons { get; set; } = new();
    public List<CheckIssue> Issues { get; set; } = new();
    public int IncompatibleCount { get; set; }
    public int UnverifiedCount { get; set; }
    public int UnknownCount { get; set; }
    public List<string> RemovedBundles { get; set; } = new();
    public List<string> MissingDependencies { get; set; } = new();
    public List<string> RestoreCandidates { get; set; } = new();
    public bool PatchReset { get; set; }
    public bool SnapshotAvailable { get; set; }
    public bool RestorePossible { get; set; }
    public bool RequiresManualReview { get; set; }
    public string Summary { get; set; } = "";
    public List<PluginSnapshot> Plugins { get; set; } = new();
    public CompatibilityEngine CompatibilityEngine { get; set; } = CompatibilityEngine.Unknown;
}

public enum RestoreActionKind
{
    RestorePackageJson,
    RestoreLockfile,
    MergeBundles,
    MergeDependencies,
    RestorePatch,
    KeepPatch,
    RestoreCompatibility,
    SkipCompatibility,
    Skip,
}

public sealed class RestoreAction
{
    public RestoreActionKind Kind { get; set; }
    public string TargetFile { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Safe { get; set; } = true;
    public List<string> Items { get; set; } = new();
    public string? Detail { get; set; }
}

public sealed class RestoreConflict
{
    public string Subject { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? CurrentPreview { get; set; }
    public string? SnapshotPreview { get; set; }
}

public sealed class RestorePlan
{
    public string SnapshotId { get; set; } = "";
    public string SnapshotDir { get; set; } = "";
    public List<RestoreAction> Actions { get; set; } = new();
    public List<RestoreConflict> Conflicts { get; set; } = new();
    public bool RequiresManualReview { get; set; }
    public bool HasChanges => Actions.Count > 0;
    public string Summary { get; set; } = "";
}

public sealed class RestoreApplyRequest
{
    public DshInstallation Installation { get; set; } = new();
    public LoomPaths Paths { get; set; } = null!;
    public RestorePlan Plan { get; set; } = new();
    public string OperationId { get; set; } = "";
    public bool DryRun { get; set; }
    public EvidenceLevel Evidence { get; set; }
    public SnapshotManifest? Snapshot { get; set; }
}

public sealed class RestoreResult
{
    public string OperationId { get; set; } = "";
    public OperationOutcome Outcome { get; set; } = OperationOutcome.Failed;
    public string? ErrorCode { get; set; }
    public int RestoredCount { get; set; }
    public int SkippedCount { get; set; }
    public int ConflictCount { get; set; }
    public bool RestartRequired { get; set; }
    public List<string> Messages { get; set; } = new();
    public List<string> BackupFiles { get; set; } = new();
    public string? ReportPath { get; set; }
    public string? BackupDir { get; set; }
    public RestorePlan Plan { get; set; } = new();
}

public sealed class OperationProgress
{
    public OperationStage Stage { get; set; }
    public string Message { get; set; } = "";
    public int? Percent { get; set; }
}

public sealed class OperationContext
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
}

public sealed class SnapshotOutcome
{
    public string OperationId { get; set; } = "";
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public string? ErrorCode { get; set; }
    public SnapshotRef? Snapshot { get; set; }
    public string Message { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
    public ReportView? Report { get; set; }
}

public sealed class CheckOutcome
{
    public string OperationId { get; set; } = "";
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public CheckResult? Result { get; set; }
    public string Message { get; set; } = "";
    public ReportView? Report { get; set; }
}

public sealed class ReportProblem
{
    public Severity Severity { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Subject { get; set; }
    public string? Detail { get; set; }
}

public sealed class ReportAction
{
    public string Stage { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Target { get; set; }
    public bool Applied { get; set; }
}

public sealed class OperationReport
{
    public int SchemaVersion { get; set; } = 1;
    public string OperationId { get; set; } = "";
    public string Kind { get; set; } = "snapshot";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public OperationOutcome Outcome { get; set; }
    public string Summary { get; set; } = "";
    public string? AppVersion { get; set; }
    public string? DshVersion { get; set; }
    public SnapshotRef? Snapshot { get; set; }
    public EvidenceLevel Evidence { get; set; }
    public List<ReportProblem> Problems { get; set; } = new();
    public List<ReportAction> Actions { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> RollbackCommands { get; set; } = new();
    public JsonObject? Extra { get; set; }
}

public sealed class ReportView
{
    public string? JsonPath { get; set; }
    public string? MarkdownPath { get; set; }
    public string Json { get; set; } = "";
    public string Markdown { get; set; } = "";
}

public sealed class DoctorCheck
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class DoctorResult
{
    public bool Healthy { get; set; }
    public List<DoctorCheck> Checks { get; set; } = new();
    public string Summary { get; set; } = "";
}

public sealed class PluginManifestInput
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Dir { get; set; } = "";
    public string ManifestJson { get; set; } = "";
}

public sealed class PluginCompatibilityResult
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Compatible { get; set; }
    public bool Exempted { get; set; }
    public List<string> Requirements { get; set; } = new();
    public StaticPrecheck Static { get; set; } = new();
    public string? Error { get; set; }
    public CompatibilityLevel Level { get; set; } = CompatibilityLevel.Unknown;
}

public sealed class CompatibilityBatchResult
{
    public CompatibilityEngine Engine { get; set; } = CompatibilityEngine.Unknown;
    public string? EngineDetail { get; set; }
    public List<PluginCompatibilityResult> Results { get; set; } = new();
}

public sealed class CliCallResult
{
    public int ExitCode { get; set; }
    public string StdOut { get; set; } = "";
    public string StdErr { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public bool TimedOut { get; set; }
    public TimeSpan Duration { get; set; }
}
