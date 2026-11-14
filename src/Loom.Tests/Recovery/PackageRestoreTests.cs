using System.Text;
using System.Text.Json.Nodes;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Recovery;
using Loom.Tests.CoreFlows;
using Xunit;

namespace Loom.Tests.Recovery;

/// <summary>
/// 包级恢复（设计 12.5 / 12.6 / 9.4）的 planner 全分支与执行器测试。
/// 全部使用 %TEMP% 下的临时 sandbox 与假 <see cref="IDshCli"/>，绝不触碰真实 profile / %LOCALAPPDATA%。
/// </summary>
public sealed class PackageRestorePlannerTests
{
    [Fact]
    public async Task StepsFollowPackageRecoveryOrder()
    {
        using var sandbox = new LoomSandbox();
        RecoveryTestSupport.SeedProfile(sandbox);
        var (manifest, reference) = await RecoveryTestSupport.TakeBaselineAsync(sandbox);
        RecoveryTestSupport.ApplyDrift(sandbox);

        var plan = PackageRestorePlanner.Create(
            RecoveryTestSupport.BuildInput(sandbox, manifest, reference.Path));

        Assert.False(plan.IsBlocked);
        Assert.True(plan.RequiresFullExit);
        Assert.True(plan.OfflineOnly);
        Assert.Equal(
            new[]
            {
                PackageRestoreStepKind.BackupProfile,
                PackageRestoreStepKind.WritePackageJson,
                PackageRestoreStepKind.WriteLockfile,
                PackageRestoreStepKind.InstallOffline,
                PackageRestoreStepKind.Verify,
            },
            plan.OrderedSteps.Select(step => step.Kind));

        var install = plan.Find(PackageRestoreStepKind.InstallOffline);
        Assert.NotNull(install);
        Assert.Equal(new[] { "install", "--offline", "--frozen-lockfile" }, install!.Args);
        Assert.Contains("plugin --profile desktop install --offline --frozen-lockfile", install.DisplayCommand);
        Assert.True(install.RequiresFullExit);
        Assert.InRange(install.TimeoutSeconds, 1, 3600);

        // 二期绝不用 file:<tgz> 归档恢复，计划里不允许出现该形式。
        foreach (var step in plan.Steps)
        {
            Assert.DoesNotContain("file:", step.DisplayCommand ?? "");
            Assert.DoesNotContain("file:", step.Content ?? "");
            Assert.DoesNotContain("file:", string.Join(" ", step.Args));
        }

        var writePackage = plan.Find(PackageRestoreStepKind.WritePackageJson)!;
        var writeLock = plan.Find(PackageRestoreStepKind.WriteLockfile)!;
        Assert.False(writePackage.Skip);
        Assert.False(writeLock.Skip);
        Assert.Equal(Hashing.Sha256HexOfText(writePackage.Content!), writePackage.AfterSha256);
        Assert.Equal(Hashing.Sha256HexOfText(writeLock.Content!), writeLock.AfterSha256);
        Assert.NotEqual(writePackage.BeforeSha256, writePackage.AfterSha256);

        Assert.Contains("@deepseek-ai/dsh-base", plan.ExpectedBundles);
        Assert.Contains("dshmarket", plan.ExpectedDependencies);
        Assert.Equal("1.73.19", plan.ExpectedDependencyVersions["dshmarket"]);
        Assert.Contains(plan.Steps, step => step.Kind == PackageRestoreStepKind.Verify);
    }

    [Fact]
    public async Task PlannerRequiresFullExitAndBlocksWhileAppRuns()
    {
        using var sandbox = new LoomSandbox();
        RecoveryTestSupport.SeedProfile(sandbox);
        var (manifest, reference) = await RecoveryTestSupport.TakeBaselineAsync(sandbox);

        var plan = PackageRestorePlanner.Create(RecoveryTestSupport.BuildInput(
            sandbox,
            manifest,
            reference.Path,
            new PackageRestoreOptions { AppRunning = true }));

        Assert.True(plan.RequiresFullExit);
        Assert.True(plan.IsBlocked);
        Assert.NotNull(plan.BlockingFailure);
        Assert.Equal(PackageRestoreFailureKind.ProfileBusy, plan.BlockingFailure!.Kind);
        Assert.Contains("退出", plan.BlockingFailure.ManualAction);
        var precondition = plan.Preconditions.Single(item =>
            item.Code == PackageRestorePreconditionCode.DshFullyExited);
        Assert.False(precondition.Satisfied);
        Assert.Contains("不会结束任何 DSH 进程", precondition.ManualAction);
    }

    [Fact]
    public async Task PlannerBlocksWhenOfflineCacheMissingAndGivesManualEntry()
    {
        using var sandbox = new LoomSandbox();
        RecoveryTestSupport.SeedProfile(sandbox);
        var (manifest, reference) = await RecoveryTestSupport.TakeBaselineAsync(sandbox);

        var plan = PackageRestorePlanner.Create(RecoveryTestSupport.BuildInput(
            sandbox,
            manifest,
            reference.Path,
            new PackageRestoreOptions { OfflineCacheAvailable = false },
            "op-offline-miss"));

        Assert.True(plan.IsBlocked);
        Assert.Equal(PackageRestoreFailureKind.OfflineCacheMissing, plan.BlockingFailure!.Kind);
        Assert.Contains("install --frozen-lockfile", plan.BlockingFailure.ManualAction);
        Assert.Contains("recovery rollback op-offline-miss", plan.BlockingFailure.ManualAction);
        // 缺缓存时绝不在计划里放联网步骤（联网必须由用户重新授权）。
        Assert.DoesNotContain(plan.Steps, step => step.Kind == PackageRestoreStepKind.InstallOnline);
        Assert.True(plan.OfflineOnly);
    }

    [Fact]
    public async Task PlannerAddsOnlineRetryOnlyWithExplicitConsent()
    {
        using var sandbox = new LoomSandbox();
        RecoveryTestSupport.SeedProfile(sandbox);
        var (manifest, reference) = await RecoveryTestSupport.TakeBaselineAsync(sandbox);

        var offlinePlan = PackageRestorePlanner.Create(
            RecoveryTestSupport.BuildInput(sandbox, manifest, reference.Path));
        Assert.DoesNotContain(offlinePlan.Steps, step => step.Kind == PackageRestoreStepKind.InstallOnline);

        var onlinePlan = PackageRestorePlanner.Create(RecoveryTestSupport.BuildInput(
            sandbox,
            manifest,
            reference.Path,
            new PackageRestoreOptions { AllowNetworkFallback = true }));
        var online = Assert.Single(onlinePlan.Steps.Where(step => step.Kind == PackageRestoreStepKind.InstallOnline));
        Assert.Equal(new[] { "install", "--frozen-lockfile" }, online.Args);
        Assert.True(online.RequiresFullExit);
        Assert.False(onlinePlan.OfflineOnly);
        // 顺序：离线安装在前，联网重试在后。
        Assert.True(onlinePlan.Find(PackageRestoreStepKind.InstallOffline)!.Order < online.Order);
    }

    [Fact]
    public async Task PlannerSkipsWritesAfterOurOwnRestore()
    {
        using var sandbox = new LoomSandbox();
        RecoveryTestSupport.SeedProfile(sandbox);
        var (manifest, reference) = await RecoveryTestSupport.TakeBaselineAsync(sandbox);
        RecoveryTestSupport.ApplyDrift(sandbox);

        // 第一次计划 + 手工落盘（模拟执行器写入后的幂等状态）。
        var first = PackageRestorePlanner.Create(
            RecoveryTestSupport.BuildInput(sandbox, manifest, reference.Path, operationId: "op-idem-1"));
        Assert.True(first.HasWrites);
        AtomicFile.WriteAllText(
            sandbox.ProfilePath(DshDefaults.ManifestFile),
            first.Find(PackageRestoreStepKind.WritePackageJson)!.Content!);
        AtomicFile.WriteAllText(
            sandbox.ProfilePath(DshDefaults.LockfileFile),
            first.Find(PackageRestoreStepKind.WriteLockfile)!.Content!);

        var second = PackageRestorePlanner.Create(
            RecoveryTestSupport.BuildInput(sandbox, manifest, reference.Path, operationId: "op-idem-2"));
        Assert.False(second.HasWrites);
        Assert.True(second.Find(PackageRestoreStepKind.WritePackageJson)!.Skip);
        Assert.True(second.Find(PackageRestoreStepKind.WriteLockfile)!.Skip);
        // 安装与校验仍然保留（node_modules 可能需要修复）。
        Assert.False(second.Find(PackageRestoreStepKind.InstallOffline)!.Skip);
        Assert.Contains(second.Steps, step => step.Kind == PackageRestoreStepKind.Verify);
        Assert.False(second.IsBlocked);
    }

    [Theory]
    [InlineData(1, false, "ERR_PNPM_NO_OFFLINE_TARBALL dshmarket@1.73.19", (int)PackageRestoreFailureKind.OfflineCacheMissing)]
    [InlineData(1, false, "EACCES: permission denied, open profile/package.json", (int)PackageRestoreFailureKind.Permission)]
    [InlineData(1, false, "ENOSPC: no space left on device", (int)PackageRestoreFailureKind.Disk)]
    [InlineData(1, false, "ERR_PNPM_PEER_DEP_ISSUES unsatisfied peer dependency", (int)PackageRestoreFailureKind.PeerConflict)]
    [InlineData(1, false, "ERR_PNPM_FETCH_401 Unauthorized", (int)PackageRestoreFailureKind.Auth)]
    [InlineData(1, false, "ERR_PNPM_FETCH_404 404 Not Found - GET pkg", (int)PackageRestoreFailureKind.NotFound)]
    [InlineData(1, false, "getaddrinfo EAI_AGAIN registry.npmjs.org", (int)PackageRestoreFailureKind.Network)]
    [InlineData(1, true, "install --offline --frozen-lockfile", (int)PackageRestoreFailureKind.Timeout)]
    [InlineData(1, false, "something unexpected happened", (int)PackageRestoreFailureKind.InstallFailed)]
    public void PlannerClassifiesCliFailures(int exitCode, bool timedOut, string stderr, int expectedKind)
    {
        var failure = PackageRestorePlanner.ClassifyCliFailure(
            new CliCallResult { ExitCode = exitCode, TimedOut = timedOut, StdErr = stderr },
            "op-classify");

        Assert.Equal((PackageRestoreFailureKind)expectedKind, failure.Kind);
        Assert.NotNull(failure.ManualAction);
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
        if (failure.Kind == PackageRestoreFailureKind.Timeout)
        {
            Assert.True(failure.TimedOut);
            Assert.Contains("进程树", failure.ManualAction);
        }
    }

    [Fact]
    public async Task PlannerBlocksOnMissingSnapshotLockfile()
    {
        using var sandbox = new LoomSandbox();
        RecoveryTestSupport.SeedProfile(sandbox);
        var (manifest, reference) = await RecoveryTestSupport.TakeBaselineAsync(sandbox);
        var input = RecoveryTestSupport.BuildInput(sandbox, manifest, reference.Path);
        input.SnapshotLockfile = "";

        var plan = PackageRestorePlanner.Create(input);

        Assert.True(plan.IsBlocked);
        Assert.Equal(PackageRestoreFailureKind.SnapshotInputMissing, plan.BlockingFailure!.Kind);
        Assert.Contains("pnpm-lock.yaml", plan.BlockingFailure.Detail ?? plan.BlockingFailure.Message);
    }

    [Fact]
    public void PlannerRejectsFileArchiveSpecs()
    {
        var input = new PackageRestorePlanInput
        {
            Snapshot = new SnapshotManifest
            {
                Id = "snap-file",
                Profile = new ProfileSnapshot
                {
                    Dependencies = new Dictionary<string, DependencySnapshot>(StringComparer.Ordinal)
                    {
                        ["dsh-whale-widget"] = new() { Spec = "file:./archives/dsh-whale-widget.tgz" },
                    },
                },
            },
            SnapshotPackageJson = TestData.PackageJson(
                new[] { "@deepseek-ai/dsh-base" },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["dsh-whale-widget"] = "file:./archives/dsh-whale-widget.tgz",
                }),
            SnapshotLockfile = "lockfileVersion: '9.0'\n",
            Current = new ProfileState { ProfileDir = @"C:\fake\profile", HasManifest = true, PackageJsonText = "{}" },
            Installation = new DshInstallation
            {
                InstallDir = @"C:\fake",
                ProfileDir = @"C:\fake\profile",
                CliPath = @"C:\fake\dsh.cmd",
            },
            OperationId = "op-file-spec",
        };

        var plan = PackageRestorePlanner.Create(input);

        Assert.True(plan.IsBlocked);
        Assert.Equal(PackageRestoreFailureKind.UnsupportedSpec, plan.BlockingFailure!.Kind);
        Assert.Contains("file:", plan.BlockingFailure.Detail);
        Assert.Contains("file:<tgz>", plan.BlockingFailure.ManualAction);
    }

    [Fact]
    public void PlannerKeepsUserAddedTopLevelFieldsOnly()
    {
        var snapshotJson = TestData.PackageJson(
            RecoveryTestSupport.Bundles,
            RecoveryTestSupport.Specs);
        var currentRoot = new JsonObject
        {
            ["name"] = "dsh-profile-desktop",
            ["private"] = true,
            ["customUserField"] = new JsonObject { ["keep"] = true },
            ["dependencies"] = new JsonObject { ["user-added-package"] = "9.9.9" },
            ["dsh"] = new JsonObject
            {
                ["profile"] = new JsonObject
                {
                    ["bundles"] = new JsonArray((JsonNode?)JsonValue.Create("@deepseek-ai/dsh-base")),
                },
            },
        };

        var merged = PackageRestorePlanner.MergePackageJson(snapshotJson, currentRoot.ToJsonString());

        Assert.NotNull(merged);
        var root = JsonNode.Parse(merged!)!.AsObject();
        Assert.True(root["customUserField"]?["keep"]?.GetValue<bool>());
        // 依赖段与 dsh 段以快照为准：用户的 extra 依赖不得回灌（否则 --frozen-lockfile 必失败）。
        Assert.False(root["dependencies"]!.AsObject().ContainsKey("user-added-package"));
        Assert.Equal(
            RecoveryTestSupport.Specs["dshmarket"],
            root["dependencies"]!["dshmarket"]!.GetValue<string>());
        Assert.Equal(
            RecoveryTestSupport.Bundles.Length,
            root["dsh"]!["profile"]!["bundles"]!.AsArray().Count);
    }

    [Fact]
    public void FirstStartPassesWhenReadyAndComplete()
    {
        var verdict = PackageRestorePlanner.EvaluateFirstStart(new FirstStartObservation
        {
            OperationStartedAt = DateTimeOffset.Now.AddMinutes(-5),
            OperationFinishedAt = DateTimeOffset.Now.AddMinutes(-4),
            HostReady = true,
            ObservedSeconds = 8,
        });

        Assert.False(verdict.Failed);
        Assert.Equal(FirstStartFailureReason.None, verdict.Reason);
        Assert.False(verdict.AutoRollbackAllowed);
    }

    [Fact]
    public void FirstStartFailsWhenHostNotReadyAfterTimeout()
    {
        var verdict = PackageRestorePlanner.EvaluateFirstStart(new FirstStartObservation
        {
            OperationStartedAt = DateTimeOffset.Now.AddMinutes(-5),
            OperationFinishedAt = DateTimeOffset.Now.AddMinutes(-4),
            HostReady = false,
            ObservedSeconds = 25,
        });

        Assert.True(verdict.Failed);
        Assert.Equal(FirstStartFailureReason.NotReadyTimeout, verdict.Reason);
        Assert.True(verdict.AutoRollbackAllowed);
        Assert.Contains(verdict.Signals, signal => signal.Contains("25s"));
    }

    [Fact]
    public void FirstStartFailsWhenProcessExitedEarly()
    {
        var verdict = PackageRestorePlanner.EvaluateFirstStart(new FirstStartObservation
        {
            OperationStartedAt = DateTimeOffset.Now.AddMinutes(-5),
            OperationFinishedAt = DateTimeOffset.Now.AddMinutes(-4),
            HostReady = false,
            ObservedSeconds = 3,
            ProcessExited = true,
            ProcessExitedAt = DateTimeOffset.Now.AddMinutes(-3),
        });

        Assert.True(verdict.Failed);
        Assert.Equal(FirstStartFailureReason.ProcessExitedEarly, verdict.Reason);
    }

    [Fact]
    public void FirstStartFailsOnStartupCrashInsideOperationWindow()
    {
        var opStart = DateTimeOffset.Now.AddMinutes(-10);
        var verdict = PackageRestorePlanner.EvaluateFirstStart(new FirstStartObservation
        {
            OperationStartedAt = opStart,
            OperationFinishedAt = opStart.AddMinutes(1),
            HostReady = true,
            ObservedSeconds = 10,
            StartupCrash = true,
            StartupCrashAt = opStart.AddMinutes(2),
            CrashDetail = "host bootstrap failed",
        });

        Assert.True(verdict.Failed);
        Assert.Equal(FirstStartFailureReason.StartupCrash, verdict.Reason);
        Assert.Contains("host bootstrap failed", string.Join("；", verdict.Signals));
    }

    [Fact]
    public void FirstStartIgnoresCrashBeforeOperationWindow()
    {
        var opStart = DateTimeOffset.Now.AddMinutes(-2);
        var verdict = PackageRestorePlanner.EvaluateFirstStart(new FirstStartObservation
        {
            OperationStartedAt = opStart,
            OperationFinishedAt = opStart.AddSeconds(30),
            HostReady = true,
            ObservedSeconds = 5,
            StartupCrash = true,
            StartupCrashAt = opStart.AddHours(-2), // 用户手动崩溃，早于本次 op
        });

        Assert.False(verdict.Failed);
        Assert.Equal(FirstStartFailureReason.None, verdict.Reason);
        Assert.Contains(verdict.Signals, signal => signal.Contains("早于本次操作窗口"));
    }

    [Fact]
    public void FirstStartFailsWhenBundlesStillMissing()
    {
        var verdict = PackageRestorePlanner.EvaluateFirstStart(new FirstStartObservation
        {
            OperationStartedAt = DateTimeOffset.Now.AddMinutes(-5),
            OperationFinishedAt = DateTimeOffset.Now.AddMinutes(-4),
            HostReady = true,
            ObservedSeconds = 9,
            MissingBundles = new List<string> { "dshmarket" },
        });

        Assert.True(verdict.Failed);
        Assert.Equal(FirstStartFailureReason.BundlesMissing, verdict.Reason);
        Assert.Contains("dshmarket", string.Join("，", verdict.Signals));
    }
}

/// <summary>
/// 包级恢复执行器：备份/写入/离线安装/校验、失败只回滚当前 op、拒绝在 DSH 运行时执行。
/// </summary>
public sealed class PackageRestoreServiceTests
{
    [Fact]
    public async Task ExecutorBacksUpWritesInstallsAndVerifies()
    {
        using var sandbox = new LoomSandbox();
        RecoveryTestSupport.SeedProfile(sandbox);
        var (manifest, reference) = await RecoveryTestSupport.TakeBaselineAsync(sandbox);
        RecoveryTestSupport.ApplyDrift(sandbox);

        var prePackage = RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile);
        var preLockfile = RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.LockfileFile);
        var prePackageSha = Hashing.Sha256HexOfText(prePackage);

        var plan = PackageRestorePlanner.Create(
            RecoveryTestSupport.BuildInput(sandbox, manifest, reference.Path));
        var cli = new FakeDshCli();
        var result = await new PackageRestoreService(cli, RecoveryTestSupport.Logger)
            .RestoreAsync(RecoveryTestSupport.Request(sandbox, plan, "op-exec-1"));

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.True(result.Verified);
        Assert.True(result.RequiresRestart);
        Assert.False(result.RolledBack);
        Assert.NotNull(result.BackupDir);
        Assert.NotNull(result.RollbackPointPath);

        // 安装只用一次，且必须是离线 + frozen lockfile。
        var call = Assert.Single(cli.Calls);
        Assert.Contains("--offline", call.Args);
        Assert.Contains("--frozen-lockfile", call.Args);
        Assert.DoesNotContain("--no-frozen-lockfile", call.Args);
        Assert.Contains("plugin --profile desktop install --offline --frozen-lockfile",
            plan.Find(PackageRestoreStepKind.InstallOffline)!.DisplayCommand);

        // 备份：写入前的文件 + 哈希都记录在回滚点里。
        var point = RecoveryTestSupport.ReadPoint(sandbox, "op-exec-1");
        Assert.NotNull(point);
        Assert.Equal("op-exec-1", point!.OpId);
        Assert.Equal(prePackageSha, point.Files.Single(file => file.Name == DshDefaults.ManifestFile).Sha256);
        Assert.True(point.Files.Single(file => file.Name == DshDefaults.LockfileFile).Existed);
        Assert.Equal(
            prePackage,
            File.ReadAllText(Path.Combine(point.BackupDir, DshDefaults.ManifestFile), Encoding.UTF8));
        Assert.Equal(2, result.BackupFiles.Count);
        Assert.Contains(point.Bundles, bundle => bundle == "@deepseek-ai/dsh-base");

        // 步骤状态写回计划（含退出码）。
        Assert.Equal(PackageRestoreStepStatus.Done, plan.Find(PackageRestoreStepKind.WritePackageJson)!.Status);
        Assert.Equal(PackageRestoreStepStatus.Done, plan.Find(PackageRestoreStepKind.WriteLockfile)!.Status);
        Assert.Equal(0, plan.Find(PackageRestoreStepKind.InstallOffline)!.ExitCode);
        Assert.Equal(PackageRestoreStepStatus.Done, plan.Find(PackageRestoreStepKind.Verify)!.Status);

        // journal 逐阶段（JSONL，最后一行是终态）。
        var journal = RecoveryTestSupport.JournalText(sandbox, result.OperationId);
        Assert.Contains("\"stage\":\"Start\"", journal);
        Assert.Contains("\"stage\":\"Backup\"", journal);
        Assert.Contains("\"stage\":\"Apply\"", journal);
        Assert.Contains("\"stage\":\"Verify\"", journal);
        Assert.Contains("\"stage\":\"Done\"", journal);
        Assert.Contains("\"status\":\"done\"", journal);
        Assert.Contains("install --offline --frozen-lockfile", journal);

        // profile 已回到快照状态（bundles/依赖齐全，lockfile 与快照一致）。
        var restored = JsonNode.Parse(RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile))!.AsObject();
        var bundles = restored["dsh"]!["profile"]!["bundles"]!.AsArray()
            .Select(node => node!.GetValue<string>()).ToList();
        Assert.Equal(RecoveryTestSupport.Bundles, bundles);
        Assert.True(restored["dependencies"]!.AsObject().ContainsKey("dsh-whale-widget"));
        Assert.Equal(
            RecoveryTestSupport.ReadIfExists(Path.Combine(reference.Path, "profile", DshDefaults.LockfileFile)),
            RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.LockfileFile));
        Assert.NotEqual(preLockfile, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.LockfileFile));
    }

    [Fact]
    public async Task ExecutorFailureRollsBackOnlyCurrentOp()
    {
        using var sandbox = new LoomSandbox();
        RecoveryTestSupport.SeedProfile(sandbox);
        var (manifest, reference) = await RecoveryTestSupport.TakeBaselineAsync(sandbox);
        RecoveryTestSupport.ApplyDrift(sandbox, "before-failing-op");

        var prePackage = RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile);
        var preLockfile = RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.LockfileFile);

        // 另一个操作的备份目录必须完全不被触碰。
        var otherDir = sandbox.Paths.RestoreBackupDirFor("op-other");
        Directory.CreateDirectory(otherDir);
        var otherPointPath = Path.Combine(otherDir, "package-restore.json");
        File.WriteAllText(Path.Combine(otherDir, "package.json"), "{\"other\":true}");
        File.WriteAllText(otherPointPath, "{\"opId\":\"op-other\"}");
        var otherPointBefore = File.ReadAllText(otherPointPath);

        var plan = PackageRestorePlanner.Create(
            RecoveryTestSupport.BuildInput(sandbox, manifest, reference.Path));
        var cli = new FakeDshCli
        {
            Responder = _ => new CliCallResult
            {
                ExitCode = 1,
                StdErr = "ERR_PNPM_NO_OFFLINE_TARBALL dshmarket@1.73.19 not in store",
            },
        };

        var result = await new PackageRestoreService(cli, RecoveryTestSupport.Logger)
            .RestoreAsync(RecoveryTestSupport.Request(sandbox, plan, "op-fail-1"));

        Assert.Equal(OperationOutcome.NeedsManual, result.Outcome);
        Assert.NotNull(result.Failure);
        Assert.Equal(PackageRestoreFailureKind.OfflineCacheMissing, result.Failure!.Kind);
        Assert.True(result.RolledBack);
        Assert.False(result.Verified);
        Assert.Contains("install --frozen-lockfile", result.Failure.ManualAction);
        Assert.Contains("op-fail-1", result.Failure.ManualAction);
        Assert.Single(cli.Calls); // 离线缺缓存时不再重跑修复安装

        // 只回滚当前 op：文件恢复到操作前，另一个 op 的备份原封不动。
        Assert.Equal(prePackage, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile));
        Assert.Equal(preLockfile, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.LockfileFile));
        Assert.True(File.Exists(Path.Combine(otherDir, "package.json")));
        Assert.Equal(otherPointBefore, File.ReadAllText(otherPointPath));
        Assert.True(Directory.Exists(sandbox.Paths.RestoreBackupDirFor("op-fail-1")));

        var journal = RecoveryTestSupport.JournalText(sandbox, result.OperationId);
        Assert.Contains("\"stage\":\"Rollback\"", journal);
        Assert.Contains("\"stage\":\"Failed\"", journal);
        Assert.Contains("\"status\":\"failed\"", journal);
        Assert.Contains("OfflineCacheMissing", journal);
    }

    [Fact]
    public async Task ExecutorRefusesToRunWhileAppIsRunning()
    {
        using var sandbox = new LoomSandbox();
        RecoveryTestSupport.SeedProfile(sandbox);
        var (manifest, reference) = await RecoveryTestSupport.TakeBaselineAsync(sandbox);
        RecoveryTestSupport.ApplyDrift(sandbox);
        var before = RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile);

        var plan = PackageRestorePlanner.Create(
            RecoveryTestSupport.BuildInput(sandbox, manifest, reference.Path));
        Assert.False(plan.IsBlocked);
        var cli = new FakeDshCli();

        var result = await new PackageRestoreService(cli, RecoveryTestSupport.Logger)
            .RestoreAsync(RecoveryTestSupport.Request(sandbox, plan, "op-busy", appRunning: true));

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Equal(PackageRestoreFailureKind.ProfileBusy, result.Failure!.Kind);
        Assert.Contains("不会结束进程", result.Failure.Message);
        Assert.Empty(cli.Calls);
        Assert.False(Directory.Exists(sandbox.Paths.RestoreBackupDirFor("op-busy")));
        Assert.Equal(before, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile));
    }

    [Fact]
    public async Task ExecutorDryRunWritesNothing()
    {
        using var sandbox = new LoomSandbox();
        RecoveryTestSupport.SeedProfile(sandbox);
        var (manifest, reference) = await RecoveryTestSupport.TakeBaselineAsync(sandbox);
        RecoveryTestSupport.ApplyDrift(sandbox);
        var before = RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile);

        var plan = PackageRestorePlanner.Create(
            RecoveryTestSupport.BuildInput(sandbox, manifest, reference.Path));
        var cli = new FakeDshCli();

        var result = await new PackageRestoreService(cli, RecoveryTestSupport.Logger)
            .RestoreAsync(RecoveryTestSupport.Request(sandbox, plan, "op-dry", dryRun: true));

        Assert.Equal(OperationOutcome.DryRun, result.Outcome);
        Assert.Empty(cli.Calls);
        Assert.False(Directory.Exists(sandbox.Paths.RestoreBackupDirFor("op-dry")));
        Assert.Equal(before, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile));
        Assert.DoesNotContain("\"stage\":\"Backup\"", RecoveryTestSupport.JournalText(sandbox, "op-dry"));
    }
}
