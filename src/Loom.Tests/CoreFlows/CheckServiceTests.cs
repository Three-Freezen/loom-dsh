using Loom.Core.Abstractions;
using Loom.Core.Check;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Snapshot;
using Xunit;

namespace Loom.Tests.CoreFlows;

public sealed class CheckServiceTests
{
    private static readonly ILogger Logger = new NoopLogger();

    private static void SeedProfile(LoomSandbox sandbox, bool includeWhaleWidget = false)
    {
        var dependencies = new Dictionary<string, string>
        {
            ["dshmarket"] = "^1.73.19",
            ["dsh-plugin-wallpaper-engine"] = "1.4.2",
        };
        var bundles = new List<string>
        {
            "@deepseek-ai/dsh-base",
            "@deepseek-ai/dsh-web-app",
            "@deepseek-ai/dsh-experimental-agent-team-profile",
            "dshmarket",
            "dsh-plugin-wallpaper-engine",
        };
        var versions = new Dictionary<string, string>
        {
            ["dshmarket"] = "1.73.19",
            ["dsh-plugin-wallpaper-engine"] = "1.4.2",
        };
        if (includeWhaleWidget)
        {
            dependencies["dsh-whale-widget"] = "0.4.2";
            versions["dsh-whale-widget"] = "0.4.2";
            bundles.Add("dsh-whale-widget");
        }

        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(bundles, dependencies));
        sandbox.WriteProfileFile(DshDefaults.LockfileFile, TestData.Lockfile(versions, dependencies));
        sandbox.WritePatch(TestData.PatchYaml());
        sandbox.AddPlugin("dshmarket", "1.73.19");
        sandbox.AddPlugin("dsh-plugin-wallpaper-engine", "1.4.2");
        if (includeWhaleWidget) sandbox.AddPlugin("dsh-whale-widget", "0.4.2");
    }

    private static async Task<(SnapshotManifest Manifest, SnapshotRef Reference)> TakeBaseline(LoomSandbox sandbox)
    {
        var service = new SnapshotService(sandbox.Paths, Logger);
        var manifest = await service.CaptureAsync(sandbox.Installation, SnapshotTrigger.PreUpdate, false);
        var reference = await service.WriteAsync(manifest, sandbox.Paths, new LoomConfig());
        return (manifest, reference);
    }

    private static CheckService CreateCheck(
        FakeEvaluator evaluator,
        CheckEvidenceOptions? options = null) =>
        new(new ProfileReader(Logger), evaluator, Logger,
            options ?? new CheckEvidenceOptions { UseDefaultLocations = false });

    private static VersionDelta DshChanged() => new()
    {
        AppVersionBefore = "0.2.2-rc.1",
        AppVersionAfter = "0.3.0",
        DshVersionBefore = "0.2.2-rc.1",
        DshVersionAfter = "0.3.0",
        AppChanged = true,
        DshChanged = true,
    };

    [Fact]
    public async Task BaselineResetGivesStrongEvidenceAndOfficialBundleCandidate()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);

        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" },
            new Dictionary<string, string>
            {
                ["dshmarket"] = "^1.73.19",
                ["dsh-plugin-wallpaper-engine"] = "1.4.2",
            }));

        var result = await CreateCheck(new FakeEvaluator())
            .CheckAsync(sandbox.Installation, manifest, reference, DshChanged(), sandbox.Paths);

        Assert.Equal(EvidenceLevel.Strong, result.Evidence);
        Assert.True(result.RestorePossible);
        Assert.False(result.RequiresManualReview);
        Assert.Contains("@deepseek-ai/dsh-experimental-agent-team-profile", result.RemovedBundles);
        Assert.Contains("dshmarket", result.RemovedBundles);
        Assert.Contains("@deepseek-ai/dsh-experimental-agent-team-profile", result.RestoreCandidates);
        Assert.DoesNotContain("dshmarket", result.RestoreCandidates); // dependency + package still present = user disabled
        Assert.Contains(result.Issues, issue => issue.Code == "I_BUNDLE_DISABLED" && issue.Subject == "dshmarket");
        Assert.Contains(result.EvidenceReasons, reason => reason.Contains("基线", StringComparison.Ordinal));
        Assert.Contains("0.3.0", result.Summary);
    }

    [Fact]
    public async Task UserDisabledBundleIsRemovedButNeverACandidate()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);

        // wallpaper still installed but disabled by the user; bundles differ from baseline but not reset.
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(
            new[]
            {
                "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app",
                "dsh-plugin-wallpaper-engine",
            },
            new Dictionary<string, string>
            {
                ["dshmarket"] = "^1.73.19",
                ["dsh-plugin-wallpaper-engine"] = "1.4.2",
            }));

        var result = await CreateCheck(new FakeEvaluator())
            .CheckAsync(sandbox.Installation, manifest, reference, DshChanged(), sandbox.Paths);

        Assert.Equal(EvidenceLevel.Weak, result.Evidence);
        Assert.True(result.RequiresManualReview);
        Assert.False(result.RestorePossible);
        Assert.Empty(result.RestoreCandidates);
        Assert.Contains("dshmarket", result.RemovedBundles);
        Assert.Contains("@deepseek-ai/dsh-experimental-agent-team-profile", result.RemovedBundles);
        Assert.Contains(result.Issues, issue => issue.Code == "I_BUNDLE_DISABLED" && issue.Subject == "dshmarket");
        Assert.Contains(result.Issues, issue => issue.Code == "I_BUNDLE_OFFICIAL_MISSING");
    }

    [Fact]
    public async Task UninstalledBundleIsNotACandidate()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox, includeWhaleWidget: true);
        var (manifest, reference) = await TakeBaseline(sandbox);

        // whale widget removed completely (dependency + node_modules + bundle)
        Directory.Delete(Path.Combine(sandbox.ProfileDir, "node_modules", "dsh-whale-widget"), recursive: true);
        var dependencies = new Dictionary<string, string>
        {
            ["dshmarket"] = "^1.73.19",
            ["dsh-plugin-wallpaper-engine"] = "1.4.2",
        };
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(
            new[]
            {
                "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app",
                "@deepseek-ai/dsh-experimental-agent-team-profile", "dshmarket", "dsh-plugin-wallpaper-engine",
            },
            dependencies));

        var result = await CreateCheck(new FakeEvaluator())
            .CheckAsync(sandbox.Installation, manifest, reference, DshChanged(), sandbox.Paths);

        Assert.Contains("dsh-whale-widget", result.RemovedBundles);
        Assert.Contains("dsh-whale-widget", result.MissingDependencies);
        Assert.Contains(result.Issues, issue => issue.Code == "I_BUNDLE_UNINSTALLED" && issue.Subject == "dsh-whale-widget");
        Assert.DoesNotContain("dsh-whale-widget", result.RestoreCandidates);
        Assert.False(result.RestorePossible);
        Assert.True(result.RequiresManualReview);
    }

    [Fact]
    public async Task OperationsJsonlDisableRecordIsStrongEvidence()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);

        var operationsPath = Path.Combine(sandbox.Root, "evidence", "operations.jsonl");
        var opTime = manifest.TakenAt.AddMinutes(1);
        sandbox.WriteFile(operationsPath,
            "{\"runId\":\"r1\",\"operation\":{\"id\":\"o1\",\"time\":\"" + opTime.ToString("O") +
            "\",\"reason\":\"plugin\",\"target\":\"dshmarket\",\"stage\":\"enable\",\"enabled\":false,\"version\":\"1.73.19\",\"phase\":\"settled\",\"application\":\"applied\",\"changed\":true}}\n");

        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(
            new[]
            {
                "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app",
                "@deepseek-ai/dsh-experimental-agent-team-profile", "dsh-plugin-wallpaper-engine",
            },
            new Dictionary<string, string>
            {
                ["dshmarket"] = "^1.73.19",
                ["dsh-plugin-wallpaper-engine"] = "1.4.2",
            }));

        var options = new CheckEvidenceOptions
        {
            UseDefaultLocations = false,
            OperationsJsonlPath = operationsPath,
        };
        var result = await CreateCheck(new FakeEvaluator(), options)
            .CheckAsync(sandbox.Installation, manifest, reference, DshChanged(), sandbox.Paths);

        Assert.Equal(EvidenceLevel.Strong, result.Evidence);
        Assert.Contains(result.EvidenceReasons, reason => reason.Contains("operations.jsonl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartupCrashLogIsMediumEvidence()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);

        var crashDir = Path.Combine(sandbox.Root, "evidence", "logs");
        sandbox.WriteFile(Path.Combine(crashDir, "crash-2026-11-14T00-00-00-000Z-host.log"),
            "time: " + manifest.TakenAt.AddMinutes(1).ToString("O") + "\n" +
            "source: host\nphase: startup\n--- error ---\nError: host failed to start\n");

        // Drop the official optional bundle (weak on its own), keeping dependency state untouched.
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dshmarket", "dsh-plugin-wallpaper-engine" },
            new Dictionary<string, string>
            {
                ["dshmarket"] = "^1.73.19",
                ["dsh-plugin-wallpaper-engine"] = "1.4.2",
            }));

        var options = new CheckEvidenceOptions { UseDefaultLocations = false, CrashLogsDir = crashDir };
        var result = await CreateCheck(new FakeEvaluator(), options)
            .CheckAsync(sandbox.Installation, manifest, reference, DshChanged(), sandbox.Paths);

        Assert.Equal(EvidenceLevel.Medium, result.Evidence);
        Assert.False(result.RestorePossible);
        Assert.True(result.RequiresManualReview);
        Assert.Empty(result.RestoreCandidates);
        Assert.Contains(result.EvidenceReasons, reason => reason.Contains("崩溃日志", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DifferencesWithoutSignalsAreWeakEvidence()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);

        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dshmarket", "dsh-plugin-wallpaper-engine" },
            new Dictionary<string, string>
            {
                ["dshmarket"] = "^1.73.19",
                ["dsh-plugin-wallpaper-engine"] = "1.4.2",
            }));

        var result = await CreateCheck(new FakeEvaluator())
            .CheckAsync(sandbox.Installation, manifest, reference, DshChanged(), sandbox.Paths);

        Assert.Equal(EvidenceLevel.Weak, result.Evidence);
        Assert.True(result.RequiresManualReview);
        Assert.False(result.RestorePossible);
        Assert.Empty(result.RestoreCandidates);
        Assert.Contains(result.RemovedBundles, name => name == "@deepseek-ai/dsh-experimental-agent-team-profile");
    }

    [Fact]
    public async Task UnchangedProfileHasNoEvidenceAndNoIssues()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);

        var result = await CreateCheck(new FakeEvaluator())
            .CheckAsync(sandbox.Installation, manifest, reference, new VersionDelta(), sandbox.Paths);

        Assert.Equal(EvidenceLevel.None, result.Evidence);
        Assert.Empty(result.RemovedBundles);
        Assert.Empty(result.MissingDependencies);
        Assert.Empty(result.RestoreCandidates);
        Assert.Empty(result.Issues);
        Assert.False(result.RestorePossible);
        Assert.False(result.RequiresManualReview);
        Assert.Contains("一致", result.Summary);
    }

    [Fact]
    public async Task CleanVersionChangeKeepsIssuesEmptySoTheUpdateCanComplete()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);

        // Bundle/dependency state is untouched: only the DSH version changed (A06 path).
        var result = await CreateCheck(new FakeEvaluator { Engine = CompatibilityEngine.Official })
            .CheckAsync(sandbox.Installation, manifest, reference, DshChanged(), sandbox.Paths);

        Assert.Equal(EvidenceLevel.None, result.Evidence);
        Assert.Empty(result.Issues);
        Assert.False(result.RestorePossible);
        Assert.False(result.RequiresManualReview);
        Assert.Contains("运行时", result.Summary);
    }

    [Fact]
    public async Task CompatibilityLevelsAreCountedAndUnknownIsNeverCompatible()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox, includeWhaleWidget: true);
        var (manifest, reference) = await TakeBaseline(sandbox);

        var evaluator = new FakeEvaluator { Engine = CompatibilityEngine.Fallback };
        evaluator.Levels["dshmarket"] = CompatibilityLevel.Incompatible;
        evaluator.Requirements["dshmarket"] = new List<string> { "engines.dsh ^0.3.0" };
        evaluator.Levels["dsh-plugin-wallpaper-engine"] = CompatibilityLevel.Unverified;
        evaluator.Levels["dsh-whale-widget"] = CompatibilityLevel.Unknown;

        var result = await CreateCheck(evaluator)
            .CheckAsync(sandbox.Installation, manifest, reference, DshChanged(), sandbox.Paths);

        Assert.Equal(1, result.IncompatibleCount);
        Assert.Equal(1, result.UnverifiedCount);
        Assert.Equal(1, result.UnknownCount);
        Assert.Equal(CompatibilityEngine.Fallback, result.CompatibilityEngine);
        Assert.Contains(result.Issues, issue =>
            issue.Kind == IssueKind.CompatibilityIncompatible && issue.Severity == Severity.Error);
        Assert.Contains(result.Issues, issue =>
            issue.Kind == IssueKind.CompatibilityUnknown && issue.Severity == Severity.Warning);
        Assert.Contains(result.Issues, issue =>
            issue.Kind == IssueKind.CompatibilityUnverified && issue.Severity == Severity.Warning);
        var whale = Assert.Single(result.Plugins, plugin => plugin.Name == "dsh-whale-widget");
        Assert.Equal(CompatibilityLevel.Unknown, whale.Compatibility);
    }

    [Fact]
    public async Task StaticPrecheckUnverifiedDowngradesCompatiblePlugin()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);

        var evaluator = new FakeEvaluator();
        evaluator.StaticResults["dshmarket"] = StaticCheckResult.Unverified;

        var result = await CreateCheck(evaluator)
            .CheckAsync(sandbox.Installation, manifest, reference, new VersionDelta(), sandbox.Paths);

        Assert.Equal(1, result.UnverifiedCount);
        Assert.Contains(result.Issues, issue =>
            issue.Code == "W_UNVERIFIED" && issue.Subject == "dshmarket");
    }

    [Fact]
    public async Task EvaluatorFailureIsHandledAsUnknown()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);

        var result = await CreateCheck(new FakeEvaluator { Throw = true })
            .CheckAsync(sandbox.Installation, manifest, reference, new VersionDelta(), sandbox.Paths);

        Assert.Equal(CompatibilityEngine.Unknown, result.CompatibilityEngine);
        Assert.Equal(2, result.UnknownCount);
        Assert.Contains(result.Issues, issue => issue.Code == "W_COMPAT_ENGINE_UNKNOWN");
        Assert.All(result.Plugins, plugin => Assert.Equal(CompatibilityLevel.Unknown, plugin.Compatibility));
    }

    [Fact]
    public async Task MissingBaselineDoesNotClaimRestoreIsPossible()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);

        var result = await CreateCheck(new FakeEvaluator())
            .CheckAsync(sandbox.Installation, null, null, new VersionDelta(), sandbox.Paths);

        Assert.False(result.SnapshotAvailable);
        Assert.Equal(EvidenceLevel.None, result.Evidence);
        Assert.False(result.RestorePossible);
        Assert.False(result.RequiresManualReview);
        Assert.Contains(result.Issues, issue => issue.Code == "E_NO_SNAPSHOT");
        Assert.Contains("无基线快照", result.Summary);
    }

    [Fact]
    public async Task PatchResetFromNewerBackupIsStrongEvidenceAndRestorable()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);
        var originalPatch = sandbox.ReadProfileFile(DshDefaults.PatchFile);

        var backupName = DshDefaults.PatchFile + ".bak-" + (manifest.TakenAt.ToUnixTimeMilliseconds() + 5000);
        sandbox.WritePatchBackup(backupName, originalPatch);
        sandbox.WritePatch("# reset by recovery flow\n[]\n");

        var result = await CreateCheck(new FakeEvaluator())
            .CheckAsync(sandbox.Installation, manifest, reference, DshChanged(), sandbox.Paths);

        Assert.True(result.PatchReset);
        Assert.Equal(EvidenceLevel.Strong, result.Evidence);
        Assert.True(result.RestorePossible);
        Assert.Contains(DshDefaults.PatchFile, result.RestoreCandidates);
        Assert.Contains(result.Issues, issue => issue.Code == "W_PATCH_RESET");
    }
}
