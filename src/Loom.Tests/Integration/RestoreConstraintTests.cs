using Loom.Tests.Infrastructure;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Restore;
using Xunit;

namespace Loom.Tests.Integration;

/// <summary>
/// Regression tests for the QA findings P1-1 (automatic restore must not re-add user-disabled
/// bundles) and P1-3/P1-4 (journal durability, corrupt compatibility.json does not block restore).
/// </summary>
public sealed class RestoreConstraintTests
{
    private static SnapshotManifest Snapshot(params string[] bundles) => new()
    {
        Id = "snap-1",
        TakenAt = DateTimeOffset.Now.AddHours(-1),
        Trigger = SnapshotTrigger.PreUpdate,
        AppVersion = "0.2.2-rc.1",
        DshVersion = "0.2.2-rc.1",
        InstallDir = @"C:\fake",
        ProfileDir = @"C:\fake\profile",
        DshHome = @"C:\fake",
        Profile = new ProfileSnapshot
        {
            Bundles = bundles
                .Select(name => new BundleSnapshot { Name = name, Class = DshDefaults.ClassifyBundle(name) })
                .ToList(),
            Dependencies = new Dictionary<string, DependencySnapshot>
            {
                ["dshmarket"] = new() { Spec = "^1.73.19" },
                ["dsh-whale-widget"] = new() { Spec = "^0.4.2" },
            },
            FileSha256 = new Dictionary<string, string> { [DshDefaults.ManifestFile] = "abc" },
            LockfileSha256 = "lock",
            Patch = new PatchSnapshot { Sha256 = "patch" },
        },
    };

    private static ProfileState Current(params string[] bundles) => new()
    {
        ProfileDir = @"C:\fake\profile",
        HasManifest = true,
        Bundles = bundles.ToList(),
        Dependencies = new Dictionary<string, DependencySnapshot>
        {
            ["dshmarket"] = new() { Spec = "^1.73.19" },
            ["dsh-whale-widget"] = new() { Spec = "^0.4.2" },
        },
    };

    private static DshInstallation Installation() => new()
    {
        InstallDir = @"C:\fake",
        ProfileDir = @"C:\fake\profile",
        DshHome = @"C:\fake",
        BaseBundles = new List<string> { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" },
    };

    private static SnapshotManifest StandardSnapshot() => Snapshot(
        "@deepseek-ai/dsh-base",
        "@deepseek-ai/dsh-web-app",
        "@deepseek-ai/dsh-experimental-agent-team-profile",
        "dshmarket",
        "dsh-whale-widget");

    [Fact]
    public void AutomaticConstraintsOnlyReAddCandidates()
    {
        var plan = RestorePlanner.Create(
            StandardSnapshot(),
            Current("@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app"),
            Installation(),
            new LoomConfig(),
            new RestoreConstraints
            {
                AllowedBundles = new[] { "@deepseek-ai/dsh-experimental-agent-team-profile" },
                AllowedDependencies = Array.Empty<string>(),
                AllowPatchRestore = false,
                AllowCompatibilityRestore = false,
            });

        var merge = Assert.Single(plan.Actions.Where(action => action.Kind == RestoreActionKind.MergeBundles));
        var expected = merge.Detail ?? "";
        Assert.Contains("@deepseek-ai/dsh-experimental-agent-team-profile", expected);
        Assert.DoesNotContain("dshmarket", expected);
        Assert.DoesNotContain("dsh-whale-widget", expected);
        Assert.Contains(merge.Items, name => name == "@deepseek-ai/dsh-experimental-agent-team-profile");
        Assert.DoesNotContain(merge.Items, name => name == "dshmarket");
        Assert.Contains(plan.Actions, action => action.Kind == RestoreActionKind.Skip
                                               && action.Subject == "未自动加回的 bundle");
    }

    [Fact]
    public void PermissivePlannerStillRestoresEverything()
    {
        var plan = RestorePlanner.Create(
            StandardSnapshot(),
            Current("@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app"),
            Installation(),
            new LoomConfig());

        var merge = Assert.Single(plan.Actions.Where(action => action.Kind == RestoreActionKind.MergeBundles));
        Assert.Contains("dshmarket", merge.Detail);
        Assert.Contains("dsh-whale-widget", merge.Detail);
    }

    [Fact]
    public void AutomaticConstraintsDoNotTouchPatchOrCompatibility()
    {
        var plan = RestorePlanner.Create(
            StandardSnapshot(),
            new ProfileState
            {
                ProfileDir = @"C:\fake\profile",
                HasManifest = true,
                Bundles = new List<string> { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" },
                PatchSha256 = "different",
            },
            Installation(),
            new LoomConfig(),
            new RestoreConstraints
            {
                AllowedBundles = new[] { "@deepseek-ai/dsh-experimental-agent-team-profile" },
                AllowedDependencies = Array.Empty<string>(),
                AllowPatchRestore = false,
                AllowCompatibilityRestore = false,
            });

        Assert.DoesNotContain(plan.Actions, action => action.Kind == RestoreActionKind.RestorePatch);
        Assert.DoesNotContain(plan.Actions, action => action.Kind == RestoreActionKind.RestoreCompatibility);
        Assert.Contains(plan.Actions, action => action.Kind == RestoreActionKind.KeepPatch);
        Assert.Contains(plan.Actions, action => action.Kind == RestoreActionKind.SkipCompatibility);
    }
}

public sealed class JournalDurabilityTests
{
    [Fact]
    public void TerminalLinesAreDetected()
    {
        using var temp = new TempDir();
        var paths = temp.Paths;
        paths.EnsureDirectories();
        const string opId = "20261114-120000-test";

        var journal = new JournalWriter(paths, opId);
        journal.Append(opId, OperationStage.Start, "start", "begin");
        Assert.True(JournalWriter.HasUnfinishedOperation(paths, out var unfinished));
        Assert.Equal(opId, unfinished);

        journal.Append(opId, OperationStage.Done, "done", "finished");
        Assert.False(JournalWriter.HasUnfinishedOperation(paths, out _));
    }

    [Fact]
    public void LegacyPrettyPrintedJournalIsTreatedAsFinished()
    {
        using var temp = new TempDir();
        var paths = temp.Paths;
        paths.EnsureDirectories();
        const string opId = "20261114-120000-legacy";
        File.WriteAllText(paths.JournalFile(opId),
            "{\n  \"stage\": \"Start\",\n  \"status\": \"start\"\n}\n{\n  \"stage\": \"Done\",\n  \"status\": \"done\"\n}\n");
        Assert.False(JournalWriter.HasUnfinishedOperation(paths, out _));
    }
}
