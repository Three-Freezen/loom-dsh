using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Restore;
using Xunit;

namespace Loom.Tests.CoreFlows;

public sealed class RestorePlannerTests
{
    private const string ProfileDir = @"C:\loom-tests\profile";

    private static DshInstallation Installation() => new()
    {
        InstallDir = @"C:\loom-tests\install",
        ProfileDir = ProfileDir,
        DshHome = @"C:\loom-tests\dsh-home",
        AppVersion = "0.2.2-rc.1",
        DshVersion = "0.2.2-rc.1",
    };

    private static readonly DateTimeOffset SnapshotTime = DateTimeOffset.Now.AddMinutes(-10);

    private static SnapshotManifest Snapshot(
        IReadOnlyDictionary<string, string>? dependencies = null,
        IReadOnlyDictionary<string, List<string>>? exemptions = null,
        string? lockfileSha = "snap-lock",
        string? patchSha = "snap-patch") =>
        TestData.Snapshot(
            "snap-1",
            SnapshotTime,
            new[]
            {
                TestData.Bundle("@deepseek-ai/dsh-base"),
                TestData.Bundle("@deepseek-ai/dsh-web-app"),
                TestData.Bundle("@deepseek-ai/dsh-experimental-agent-team-profile"),
                TestData.Bundle("dshmarket"),
                TestData.Bundle("dsh-plugin-wallpaper-engine"),
                TestData.Bundle("dsh-whale-widget"),
            },
            dependencies?.ToDictionary(
                pair => pair.Key,
                pair => new DependencySnapshot { Spec = pair.Value, Resolved = pair.Value.TrimStart('^', '~') },
                StringComparer.Ordinal),
            patchSha,
            exemptions,
            lockfileSha);

    private static readonly Dictionary<string, string> SnapshotDependencies = new(StringComparer.Ordinal)
    {
        ["dshmarket"] = "^1.73.19",
        ["dsh-plugin-wallpaper-engine"] = "1.4.2",
        ["dsh-whale-widget"] = "0.4.2",
    };

    [Fact]
    public void MergesBundlesInSnapshotRelativeOrderAndKeepsUserInstalledAtEnd()
    {
        var snapshot = Snapshot(SnapshotDependencies);
        var current = TestData.Profile(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dshmarket", "user-new-bundle" },
            SnapshotDependencies,
            patchSha: "snap-patch",
            lockfileSha: "snap-lock",
            profileDir: ProfileDir);

        var plan = RestorePlanner.Create(snapshot, current, Installation(), new LoomConfig());

        var merge = Assert.Single(plan.Actions, action => action.Kind == RestoreActionKind.MergeBundles);
        Assert.Equal(
            new[]
            {
                "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app",
                "@deepseek-ai/dsh-experimental-agent-team-profile",
                "dshmarket", "dsh-plugin-wallpaper-engine", "dsh-whale-widget",
                "user-new-bundle",
            },
            merge.Items);
        Assert.Equal("@deepseek-ai/dsh-web-app", merge.Items[1]);

        var skip = Assert.Single(plan.Actions, action => action.Kind == RestoreActionKind.Skip);
        Assert.Contains("user-new-bundle", skip.Items);
        Assert.False(plan.RequiresManualReview);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void AddsOnlyMissingDependenciesAndSkipsLockfileWhenPackageJsonDiffers()
    {
        var snapshotDependencies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dep-a"] = "^1.0.0",
            ["dep-b"] = "2.0.0",
        };
        var snapshot = Snapshot(snapshotDependencies);
        var current = TestData.Profile(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dep-a" },
            new Dictionary<string, string> { ["dep-a"] = "^1.0.0", ["dep-c"] = "^3.0.0" },
            patchSha: "snap-patch",
            lockfileSha: "current-lock",
            profileDir: ProfileDir);

        var plan = RestorePlanner.Create(snapshot, current, Installation(), new LoomConfig());

        var dependencyMerge = Assert.Single(plan.Actions, action => action.Kind == RestoreActionKind.MergeDependencies);
        Assert.Equal(new[] { "dep-b" }, dependencyMerge.Items);

        var skippedLockfile = Assert.Single(plan.Actions,
            action => action.Kind == RestoreActionKind.Skip && action.TargetFile == DshDefaults.LockfileFile);
        Assert.Contains("不一致", skippedLockfile.Description);
        Assert.DoesNotContain(plan.Actions, action => action.Kind == RestoreActionKind.RestoreLockfile);
    }

    [Fact]
    public void RestoresLockfileWhenDependenciesMatchSnapshot()
    {
        var snapshot = Snapshot(SnapshotDependencies);
        var current = TestData.Profile(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dshmarket", "dsh-plugin-wallpaper-engine", "dsh-whale-widget" },
            SnapshotDependencies,
            patchSha: "snap-patch",
            lockfileSha: "current-lock",
            profileDir: ProfileDir);

        var plan = RestorePlanner.Create(snapshot, current, Installation(), new LoomConfig());

        Assert.Contains(plan.Actions, action => action.Kind == RestoreActionKind.RestoreLockfile);
    }

    [Fact]
    public void NewerPatchBackupWinsOverSnapshot()
    {
        var snapshot = Snapshot(SnapshotDependencies);
        var backupName = DshDefaults.PatchFile + ".bak-" + (SnapshotTime.ToUnixTimeMilliseconds() + 10000);
        var current = TestData.Profile(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dshmarket", "dsh-plugin-wallpaper-engine", "dsh-whale-widget" },
            SnapshotDependencies,
            patchSha: "current-patch",
            patchBackups: new[] { backupName },
            lockfileSha: "snap-lock",
            profileDir: ProfileDir);

        var plan = RestorePlanner.Create(snapshot, current, Installation(), new LoomConfig());

        var patchAction = Assert.Single(plan.Actions, action => action.Kind == RestoreActionKind.RestorePatch);
        Assert.Equal(Path.Combine(ProfileDir, backupName), patchAction.Detail);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void ConflictingPatchKeepsCurrentAndRequiresManualReview()
    {
        var snapshot = Snapshot(SnapshotDependencies);
        var current = TestData.Profile(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dshmarket", "dsh-plugin-wallpaper-engine", "dsh-whale-widget" },
            SnapshotDependencies,
            patchSha: "user-edited-patch",
            lockfileSha: "snap-lock",
            profileDir: ProfileDir);

        var plan = RestorePlanner.Create(snapshot, current, Installation(), new LoomConfig());

        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(DshDefaults.PatchFile, conflict.Subject);
        Assert.NotNull(conflict.CurrentPreview);
        Assert.Contains(plan.Actions, action => action.Kind == RestoreActionKind.KeepPatch);
        Assert.True(plan.RequiresManualReview);
    }

    [Fact]
    public void CompatibilityOnlyRestoresExactRuntimeMatches()
    {
        var exemptions = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["plugin-a@1.0.0"] = new() { "0.2.2-rc.1", "0.1.0" },
            ["plugin-b@2.0.0"] = new() { "0.1.0" },
        };
        var snapshot = Snapshot(SnapshotDependencies, exemptions);
        var current = TestData.Profile(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dshmarket", "dsh-plugin-wallpaper-engine", "dsh-whale-widget" },
            SnapshotDependencies,
            patchSha: "snap-patch",
            lockfileSha: "snap-lock",
            compatibilitySha: "other-compat",
            hasCompatibilityFile: true,
            profileDir: ProfileDir);

        var plan = RestorePlanner.Create(snapshot, current, Installation(), new LoomConfig());

        var compatibility = Assert.Single(plan.Actions, action => action.Kind == RestoreActionKind.RestoreCompatibility);
        Assert.Equal(new[] { "plugin-a@1.0.0" }, compatibility.Items);
        Assert.Contains("1 条旧版本豁免标记 inactive", compatibility.Description);
    }

    [Fact]
    public void DamagedCompatibilityFileIsNotOverwritten()
    {
        var exemptions = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["plugin-a@1.0.0"] = new() { "0.2.2-rc.1" },
        };
        var snapshot = Snapshot(SnapshotDependencies, exemptions);
        var current = TestData.Profile(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dsh-plugin-wallpaper-engine", "dsh-whale-widget" },
            SnapshotDependencies,
            patchSha: "snap-patch",
            lockfileSha: "snap-lock",
            compatibilitySha: null, // path present + sha null = unreadable / not rewritable
            hasCompatibilityFile: true,
            profileDir: ProfileDir);

        var plan = RestorePlanner.Create(snapshot, current, Installation(), new LoomConfig());

        var skip = Assert.Single(plan.Actions, action => action.Kind == RestoreActionKind.SkipCompatibility);
        Assert.Equal(DshDefaults.CompatibilityFile, skip.TargetFile);
        Assert.Contains("不覆盖", skip.Description);
        Assert.DoesNotContain(plan.Actions, action => action.Kind == RestoreActionKind.RestoreCompatibility);
    }

    [Fact]
    public void MarketStateIsNeverRestored()
    {
        var snapshot = Snapshot(SnapshotDependencies);
        snapshot.Profile.FileSha256[".dsh-market/state.json"] = "market-sha";
        var current = TestData.Profile(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dsh-plugin-wallpaper-engine", "dsh-whale-widget" },
            SnapshotDependencies,
            patchSha: "snap-patch",
            lockfileSha: "snap-lock",
            profileDir: ProfileDir);

        var plan = RestorePlanner.Create(snapshot, current, Installation(), new LoomConfig());

        var skip = Assert.Single(plan.Actions, action => action.TargetFile == ".dsh-market/state.json");
        Assert.Equal(RestoreActionKind.Skip, skip.Kind);
        Assert.DoesNotContain(plan.Actions, action =>
            action.Kind is RestoreActionKind.RestorePackageJson or RestoreActionKind.RestorePatch
                or RestoreActionKind.RestoreLockfile or RestoreActionKind.RestoreCompatibility);
    }

    [Fact]
    public void IdenticalProfileProducesEmptyPlan()
    {
        var snapshot = Snapshot(SnapshotDependencies);
        var current = TestData.Profile(
            new[]
            {
                "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app",
                "@deepseek-ai/dsh-experimental-agent-team-profile",
                "dshmarket", "dsh-plugin-wallpaper-engine", "dsh-whale-widget",
            },
            SnapshotDependencies,
            patchSha: "snap-patch",
            lockfileSha: "snap-lock",
            profileDir: ProfileDir);

        var plan = RestorePlanner.Create(snapshot, current, Installation(), new LoomConfig());

        Assert.Empty(plan.Actions);
        Assert.Empty(plan.Conflicts);
        Assert.False(plan.HasChanges);
        Assert.False(plan.RequiresManualReview);
        Assert.Contains("无需还原", plan.Summary);
    }
}
