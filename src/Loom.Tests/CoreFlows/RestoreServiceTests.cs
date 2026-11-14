using System.Text;
using System.Text.Json.Nodes;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Restore;
using Loom.Core.Snapshot;
using Xunit;

namespace Loom.Tests.CoreFlows;

public sealed class RestoreServiceTests
{
    private static readonly ILogger Logger = new NoopLogger();

    private static void SeedProfile(LoomSandbox sandbox, bool withCompatibility = false)
    {
        var dependencies = new Dictionary<string, string>
        {
            ["dshmarket"] = "^1.73.19",
            ["dsh-plugin-wallpaper-engine"] = "1.4.2",
        };
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(
            new[]
            {
                "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app",
                "@deepseek-ai/dsh-experimental-agent-team-profile",
                "dshmarket", "dsh-plugin-wallpaper-engine",
            },
            dependencies));
        sandbox.WriteProfileFile(DshDefaults.LockfileFile,
            TestData.Lockfile(new Dictionary<string, string>
            {
                ["dshmarket"] = "1.73.19",
                ["dsh-plugin-wallpaper-engine"] = "1.4.2",
            }, dependencies));
        sandbox.WritePatch(TestData.PatchYaml());
        sandbox.AddPlugin("dshmarket", "1.73.19");
        sandbox.AddPlugin("dsh-plugin-wallpaper-engine", "1.4.2");
        if (withCompatibility)
        {
            sandbox.WriteCompatibility(TestData.CompatibilityJson(("dshmarket@1.73.19", new[] { "0.2.2-rc.1" })));
        }
    }

    private static async Task<(SnapshotManifest Manifest, SnapshotRef Reference)> TakeBaseline(LoomSandbox sandbox)
    {
        var service = new SnapshotService(sandbox.Paths, Logger);
        var manifest = await service.CaptureAsync(sandbox.Installation, SnapshotTrigger.PreUpdate, false);
        var reference = await service.WriteAsync(manifest, sandbox.Paths, new LoomConfig());
        return (manifest, reference);
    }

    private static RestorePlan BuildPlan(LoomSandbox sandbox, SnapshotManifest manifest, SnapshotRef reference)
    {
        var current = new ProfileReader(Logger).Read(sandbox.Installation, strict: false);
        var plan = RestorePlanner.Create(manifest, current, sandbox.Installation, new LoomConfig());
        plan.SnapshotDir = reference.Path;
        plan.SnapshotId = reference.Id;
        return plan;
    }

    private static RestoreApplyRequest Request(
        LoomSandbox sandbox,
        RestorePlan plan,
        SnapshotManifest manifest,
        string operationId = "op-restore-1",
        bool dryRun = false) => new()
    {
        Installation = sandbox.Installation,
        Paths = sandbox.Paths,
        Plan = plan,
        OperationId = operationId,
        DryRun = dryRun,
        Evidence = EvidenceLevel.Strong,
        Snapshot = manifest,
    };

    [Fact]
    public async Task AppliesMergeWithBackupJournalAndVerification()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);

        // Simulate a recovery-flow profile: bundles reset, dependency spec removed, lockfile touched,
        // plus a user-specific field that must survive.
        var currentPackage = new JsonObject
        {
            ["name"] = "dsh-profile-desktop",
            ["private"] = true,
            ["customUserField"] = new JsonObject { ["keep"] = true },
            ["dependencies"] = new JsonObject { ["dshmarket"] = "^1.73.19" },
            ["dsh"] = new JsonObject
            {
                ["profile"] = new JsonObject
                {
                    ["bundles"] = new JsonArray(
                        (JsonNode?)JsonValue.Create("@deepseek-ai/dsh-base"),
                        JsonValue.Create("@deepseek-ai/dsh-web-app")),
                },
            },
        };
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, currentPackage.ToJsonString());
        sandbox.WriteProfileFile(DshDefaults.LockfileFile, "lockfileVersion: '9.0'\n# user touched\n");

        var plan = BuildPlan(sandbox, manifest, reference);
        Assert.Contains(plan.Actions, action => action.Kind == RestoreActionKind.MergeBundles);
        Assert.Contains(plan.Actions, action => action.Kind == RestoreActionKind.MergeDependencies);

        var service = new RestoreService(Logger);
        var result = await service.ApplyAsync(Request(sandbox, plan, manifest));

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.True(result.RestoredCount >= 2);
        Assert.True(result.RestartRequired);
        Assert.NotNull(result.BackupDir);
        Assert.Contains(result.BackupFiles, path => path.EndsWith(DshDefaults.ManifestFile, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(result.BackupDir!, "backup.json")));

        var journal = string.Join("\n", JournalWriter.ReadLines(sandbox.Paths, result.OperationId));
        // Journal lines are compact JSON (JSONL) so HasUnfinishedOperation can parse the last line.
        Assert.Contains("\"stage\":\"Backup\"", journal);
        Assert.Contains("\"stage\":\"Apply\"", journal);
        Assert.Contains("\"stage\":\"Verify\"", journal);
        Assert.Contains("\"stage\":\"Done\"", journal);
        Assert.Contains("\"status\":\"done\"", journal);

        var restored = ReadPackageJson(sandbox);
        var bundles = ReadBundles(restored);
        Assert.Equal(
            new[]
            {
                "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app",
                "@deepseek-ai/dsh-experimental-agent-team-profile",
                "dshmarket", "dsh-plugin-wallpaper-engine",
            },
            bundles);
        Assert.True(restored["customUserField"]?["keep"]?.GetValue<bool>());
        Assert.True(restored["dependencies"]!.AsObject().ContainsKey("dsh-plugin-wallpaper-engine"));
        Assert.Equal(
            File.ReadAllText(Path.Combine(reference.Path, "profile", DshDefaults.LockfileFile)),
            sandbox.ReadProfileFile(DshDefaults.LockfileFile));
    }

    [Fact]
    public async Task ReapplyingTheSamePlanIsIdempotent()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" },
            new Dictionary<string, string> { ["dshmarket"] = "^1.73.19" }));

        var plan = BuildPlan(sandbox, manifest, reference);
        var service = new RestoreService(Logger);
        var first = await service.ApplyAsync(Request(sandbox, plan, manifest, "op-restore-idem-1"));
        Assert.Equal(OperationOutcome.Succeeded, first.Outcome);
        var afterFirst = sandbox.ReadProfileFile(DshDefaults.ManifestFile);
        var bundlesAfterFirst = ReadBundles(ReadPackageJson(sandbox));

        var second = await service.ApplyAsync(Request(sandbox, plan, manifest, "op-restore-idem-2"));
        Assert.Equal(OperationOutcome.Succeeded, second.Outcome);
        Assert.Equal(0, second.RestoredCount);
        Assert.False(second.RestartRequired);
        Assert.Equal(bundlesAfterFirst, ReadBundles(ReadPackageJson(sandbox)));
        Assert.Equal(bundlesAfterFirst.Distinct().Count(), bundlesAfterFirst.Count);
        Assert.Equal(
            JsonNode.Parse(afterFirst)!.ToJsonString(),
            JsonNode.Parse(sandbox.ReadProfileFile(DshDefaults.ManifestFile))!.ToJsonString());

        // A plan recomputed after the restore has no write actions left.
        var replanned = BuildPlan(sandbox, manifest, reference);
        Assert.DoesNotContain(replanned.Actions, action => action.Kind is not (RestoreActionKind.Skip
            or RestoreActionKind.KeepPatch
            or RestoreActionKind.SkipCompatibility));
    }

    [Fact]
    public async Task DryRunWritesNothing()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" },
            new Dictionary<string, string> { ["dshmarket"] = "^1.73.19" }));
        var before = sandbox.ReadProfileFile(DshDefaults.ManifestFile);
        var plan = BuildPlan(sandbox, manifest, reference);

        var result = await new RestoreService(Logger).ApplyAsync(Request(sandbox, plan, manifest, "op-dry", dryRun: true));

        Assert.Equal(OperationOutcome.DryRun, result.Outcome);
        Assert.Equal(before, sandbox.ReadProfileFile(DshDefaults.ManifestFile));
        Assert.False(Directory.Exists(sandbox.Paths.RestoreBackupDirFor("op-dry")));
    }

    [Fact]
    public async Task WriteFailureRollsBackFilesAlreadyWritten()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" },
            new Dictionary<string, string> { ["dshmarket"] = "^1.73.19" }));

        // A directory where pnpm-lock.yaml is expected makes the lockfile write fail mid-transaction.
        var lockPath = sandbox.ProfilePath(DshDefaults.LockfileFile);
        File.Delete(lockPath);
        Directory.CreateDirectory(lockPath);
        var before = sandbox.ReadProfileFile(DshDefaults.ManifestFile);

        var plan = BuildPlan(sandbox, manifest, reference);
        var result = await new RestoreService(Logger).ApplyAsync(Request(sandbox, plan, manifest, "op-rollback"));

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Contains(result.Messages, message => message.Contains("回滚", StringComparison.Ordinal));
        Assert.Equal(
            JsonNode.Parse(before)!.ToJsonString(),
            JsonNode.Parse(sandbox.ReadProfileFile(DshDefaults.ManifestFile))!.ToJsonString());
        Assert.Equal(
            new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" },
            ReadBundles(ReadPackageJson(sandbox)));
    }

    [Fact]
    public async Task SkippedCountMatchesSkipMessagesExactlyOnce()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);

        // 构造最小计划：一个真实写入动作（lockfile）+ 两个 Skip，避免 RestorePlanner 额外产生跳过项。
        sandbox.WriteProfileFile(DshDefaults.LockfileFile, "lockfileVersion: '9.0'\n# user touched\n");
        var plan = new RestorePlan
        {
            SnapshotDir = reference.Path,
            SnapshotId = reference.Id,
        };
        plan.Actions.Add(new RestoreAction
        {
            Kind = RestoreActionKind.RestoreLockfile,
            TargetFile = DshDefaults.LockfileFile,
            Description = "还原 lockfile",
            Detail = "snapshot",
        });
        plan.Actions.Add(new RestoreAction
        {
            Kind = RestoreActionKind.Skip,
            TargetFile = DshDefaults.ManifestFile,
            Description = "skip-one",
        });
        plan.Actions.Add(new RestoreAction
        {
            Kind = RestoreActionKind.Skip,
            TargetFile = DshDefaults.ManifestFile,
            Description = "skip-two",
        });

        var result = await new RestoreService(Logger).ApplyAsync(Request(sandbox, plan, manifest, "op-skip-count"));

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        var skipMessages = result.Messages
            .Where(message => message.StartsWith("[已跳过]", StringComparison.Ordinal))
            .ToList();
        // 每个 plan Skip 只产生一条消息，也只计数一次（回归：预循环与 ApplyAction 重复 +1）。
        Assert.Equal(2, skipMessages.Count);
        Assert.Equal(2, result.SkippedCount);
        Assert.Single(skipMessages, message => message.EndsWith("skip-one", StringComparison.Ordinal));
        Assert.Single(skipMessages, message => message.EndsWith("skip-two", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoresMissingPackageJsonAtomically()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var (manifest, reference) = await TakeBaseline(sandbox);
        var packagePath = sandbox.ProfilePath(DshDefaults.ManifestFile);
        var expected = File.ReadAllText(packagePath);
        File.Delete(packagePath);

        var plan = new RestorePlan
        {
            SnapshotDir = reference.Path,
            SnapshotId = reference.Id,
        };
        plan.Actions.Add(new RestoreAction
        {
            Kind = RestoreActionKind.RestorePackageJson,
            TargetFile = DshDefaults.ManifestFile,
            Description = "恢复缺失的 package.json",
            Detail = "snapshot",
        });

        var result = await new RestoreService(Logger).ApplyAsync(Request(sandbox, plan, manifest, "op-pkg-json"));

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, File.ReadAllText(packagePath));
        // 原实现是 File.Copy(overwrite:false)；原子写不应留下临时文件。
        Assert.Empty(Directory.GetFiles(sandbox.ProfileDir, "package.json.tmp-*"));
    }

    [Fact]
    public async Task RestoresOnlyExactRuntimeCompatibilityExemptions()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox, withCompatibility: true);
        var (manifest, reference) = await TakeBaseline(sandbox);

        // The user later edited compatibility.json (different content), plus a new record we must keep.
        sandbox.WriteCompatibility(TestData.CompatibilityJson(
            ("other-plugin@2.0.0", new[] { "0.2.2-rc.1" }),
            ("dshmarket@1.73.19", new[] { "0.1.0" })));

        var plan = BuildPlan(sandbox, manifest, reference);
        Assert.Contains(plan.Actions, action => action.Kind == RestoreActionKind.RestoreCompatibility);

        var result = await new RestoreService(Logger).ApplyAsync(Request(sandbox, plan, manifest, "op-compat"));
        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);

        var compatibility = JsonNode.Parse(sandbox.ReadProfileFile(DshDefaults.CompatibilityFile))!.AsObject();
        Assert.True(compatibility.ContainsKey("other-plugin@2.0.0"));
        var versions = compatibility["dshmarket@1.73.19"]!.AsArray()
            .Select(node => node!.GetValue<string>()).ToList();
        Assert.Contains("0.2.2-rc.1", versions);
        Assert.Contains("0.1.0", versions);
    }

    private static JsonObject ReadPackageJson(LoomSandbox sandbox) =>
        JsonNode.Parse(sandbox.ReadProfileFile(DshDefaults.ManifestFile))!.AsObject();

    private static List<string> ReadBundles(JsonObject root) =>
        (root["dsh"]?["profile"]?["bundles"] as JsonArray ?? new JsonArray())
        .Select(node => node!.GetValue<string>()).ToList();
}
