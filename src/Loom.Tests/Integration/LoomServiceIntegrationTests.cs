using System.Text.Json.Nodes;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Recovery;
using Loom.Core.Upgrade;
using Loom.Tests.CoreFlows;
using Xunit;

namespace Loom.Tests.Integration;

/// <summary>
/// 二期服务接线：<c>LoomService.UpgradePluginAsync</c> / <c>PreviewPackageRestoreAsync</c> /
/// <c>PackageRestoreAsync</c> / <c>CanRollbackPackageOp</c> / <c>RollbackPackageOpAsync</c>，
/// 以及 <c>TickAsync</c> 的策略 A 自动升级（开关关闭 = 零动作、DSH 运行中 = 零动作、每次最多 3 个）。
/// 全部使用假 registry / 假 dsh.cmd，绝不启动真实 CLI、不联网。
///
/// 其中“长操作持有真实跨进程门”的用例会使用命名互斥量，因此整类与 <c>OperationGateTests</c>
/// 放到同一个 xunit collection，避免并行测试互相抢门。
/// </summary>
[Collection("OperationGate")]
public sealed class LoomServiceIntegrationTests
{
    private const string Package = "dsh-plugin-demo";

    // ------------------------------------------------------------ 单插件升级入口

    [Fact]
    public async Task UpgradePluginAsyncBacksUpInstallsAndReports()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        harness.Registry.Candidates.Add(FakeRegistry.Compatible("1.2.0", Package));

        var sawBackupBeforeInstall = false;
        var service = harness.Service;
        harness.Cli.Handler = args =>
        {
            sawBackupBeforeInstall = Directory.EnumerateDirectories(harness.Paths.RestoreBackupDir).Any();
            return SimulateInstall(harness.Sandbox, args);
        };

        var result = await service.UpgradePluginAsync(new UpgradeRequest
        {
            Installation = harness.Sandbox.Installation,
            PackageName = Package,
            InstalledVersion = "1.0.0",
            TargetDshVersion = "0.3.0",
        });

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.True(result.ProfileChanged);
        Assert.Equal("1.2.0", result.Decision.TargetVersion);
        Assert.False(result.Decision.RiskExemptionGranted);
        Assert.True(sawBackupBeforeInstall);

        // 安装命令固定 add + --save-exact，且一次只装一个包。
        var call = Assert.Single(harness.Cli.Calls);
        Assert.Equal(new[] { "add", Package + "@1.2.0", "--save-exact" }, call);

        // 审计文件（reports\upgrade-<opId>.json）与 operation 状态
        Assert.NotNull(result.AuditPath);
        Assert.True(File.Exists(result.AuditPath!));
        Assert.Equal(Package, result.Audit!.PackageName);
        Assert.Equal("upgrade", harness.State.LastOperation?.Kind);
        Assert.Equal(OperationOutcome.Succeeded, harness.State.LastOperation!.Outcome);

        // 通知事件 id 带 opId（幂等去重契约）
        var notification = Assert.Single(harness.Notifications);
        Assert.Equal("upgrade-" + result.OperationId, notification.EventId);

        // 服务自己写的 markdown 报告（ReportService）
        var reports = Directory.GetFiles(harness.Paths.ReportsDir, "report-*.md");
        Assert.Contains(reports, path => path.Contains(result.OperationId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpgradePluginAsyncDryRunWritesNothing()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        harness.Registry.Candidates.Add(FakeRegistry.Compatible("1.2.0", Package));
        var service = harness.Service;

        var result = await service.UpgradePluginAsync(new UpgradeRequest
        {
            Installation = harness.Sandbox.Installation,
            PackageName = Package,
            InstalledVersion = "1.0.0",
            TargetDshVersion = "0.3.0",
            DryRun = true,
        });

        Assert.Equal(OperationOutcome.DryRun, result.Outcome);
        Assert.Empty(harness.Cli.Calls);
        Assert.Empty(Directory.GetDirectories(harness.Paths.RestoreBackupDir));
        Assert.Contains(Package + "@1.2.0", result.CommandLine!);
        // P3-R8：dry-run 只读演练不进入跨进程操作门（否则托盘长安装会挡住只读命令）。
        Assert.Equal(0, harness.FakeGate.Acquired);

        // 演练不写备份/安装阶段（外层 op journal 只有 Start/Done 与决策记录）。
        var journal = Directory.GetFiles(harness.Paths.JournalDir, "*.jsonl");
        if (journal.Length > 0)
        {
            var text = string.Concat(journal.Select(File.ReadAllText));
            Assert.DoesNotContain("\"stage\":\"Backup\"", text);
            Assert.DoesNotContain("\"stage\":\"Apply\"", text);
        }
    }

    [Fact]
    public async Task UpgradePluginAsyncFailsClosedWhenAppStillRunning()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        harness.Registry.Candidates.Add(FakeRegistry.Compatible("1.2.0", Package));
        harness.Processes.Processes.Add(new DshProcess { Pid = 4242, Name = "DeepSeek Harness" });
        var service = harness.Service;

        var result = await service.UpgradePluginAsync(new UpgradeRequest
        {
            Installation = harness.Sandbox.Installation,
            PackageName = Package,
            InstalledVersion = "1.0.0",
            TargetDshVersion = "0.3.0",
        });

        Assert.Equal(OperationOutcome.NeedsManual, result.Outcome);
        Assert.Equal(UpgradeBlockReason.AppRunning, result.Decision.BlockReason);
        Assert.Empty(harness.Cli.Calls);
        Assert.Equal(0, harness.Registry.Calls); // 闸门在查询 registry 之前就命中
    }

    // ------------------------------------------------------------ Tick 自动升级

    [Fact]
    public async Task TickDoesNothingWhenAutoUpgradeDisabled()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        await SeedIncompatibleCheckAsync(harness);
        harness.Registry.Candidates.Add(FakeRegistry.Compatible("1.2.0", Package));
        harness.Registry.Calls = 0;
        harness.Cli.Calls.Clear();

        // 默认 AutoUpgradeEnabled=false（安全默认）
        Assert.False(harness.Config.AutoUpgradeEnabled);
        await harness.Service.TickAsync();

        Assert.Equal(0, harness.Registry.Calls);
        Assert.Empty(harness.Cli.Calls);
        Assert.NotEqual("upgrade", harness.State.LastOperation?.Kind);
        Assert.DoesNotContain(harness.Notifications, item => item.EventId.StartsWith("upgrade-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TickDoesNothingWhenPolicyIsNotA()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        await SeedIncompatibleCheckAsync(harness);
        harness.Config.AutoUpgradeEnabled = true;
        harness.Config.AutoUpgradePolicy = "B"; // 仅提示
        harness.Registry.Candidates.Add(FakeRegistry.Compatible("1.2.0", Package));
        harness.Registry.Calls = 0;
        harness.Cli.Calls.Clear();

        await harness.Service.TickAsync();

        Assert.Equal(0, harness.Registry.Calls);
        Assert.Empty(harness.Cli.Calls);
    }

    [Fact]
    public async Task TickDoesNothingWhileAppIsRunning()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        await SeedIncompatibleCheckAsync(harness);
        harness.Config.AutoUpgradeEnabled = true;
        harness.Registry.Candidates.Add(FakeRegistry.Compatible("1.2.0", Package));
        harness.Registry.Calls = 0;
        harness.Cli.Calls.Clear();
        harness.Processes.Processes.Add(new DshProcess { Pid = 99, Name = "DeepSeek Harness" });

        await harness.Service.TickAsync();

        // Tick 在“应用运行中”分支只做更新检测/自动快照，绝不写 profile。
        Assert.Equal(0, harness.Registry.Calls);
        Assert.Empty(harness.Cli.Calls);
    }

    [Fact]
    public async Task TickDoesNothingWhenProfileIsLocked()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        await SeedIncompatibleCheckAsync(harness);
        harness.Config.AutoUpgradeEnabled = true;
        harness.Registry.Candidates.Add(FakeRegistry.Compatible("1.2.0", Package));
        harness.Registry.Calls = 0;
        harness.Cli.Calls.Clear();
        harness.Locks.Busy = true;

        await harness.Service.TickAsync();

        Assert.Equal(0, harness.Registry.Calls);
        Assert.Empty(harness.Cli.Calls);
    }

    [Fact]
    public async Task TickUpgradesIncompatiblePluginWhenEnabledAndAppExited()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        await SeedIncompatibleCheckAsync(harness);
        harness.Config.AutoUpgradeEnabled = true;
        harness.Registry.Candidates.Add(FakeRegistry.Compatible("1.2.0", Package));
        harness.Registry.Calls = 0;
        harness.Cli.Calls.Clear();
        harness.Cli.Handler = args => SimulateInstall(harness.Sandbox, args);

        await harness.Service.TickAsync();

        Assert.Equal(1, harness.Registry.Calls);
        var call = Assert.Single(harness.Cli.Calls);
        Assert.Equal(new[] { "add", Package + "@1.2.0", "--save-exact" }, call);
        Assert.Equal("upgrade", harness.State.LastOperation?.Kind);
        Assert.Equal(OperationOutcome.Succeeded, harness.State.LastOperation!.Outcome);
        Assert.Contains(harness.Notifications, item => item.EventId.StartsWith("upgrade-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TickProcessesAtMostThreePluginsPerTick()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        var plugins = Enumerable.Range(0, 5)
            .Select(index => new PluginSnapshot
            {
                Name = "plugin-" + index,
                Version = "1.0.0",
                Compatibility = CompatibilityLevel.Incompatible,
            })
            .ToArray();
        harness.Checks.Result = new CheckResult
        {
            Summary = "5 个插件不兼容",
            IncompatibleCount = 5,
            Plugins = plugins.ToList(),
        };
        await harness.Service.RunCheckAsync();
        harness.Config.AutoUpgradeEnabled = true;
        harness.Registry.Candidates.Add(FakeRegistry.Compatible("1.2.0", Package));
        harness.Registry.Calls = 0;
        harness.Cli.Calls.Clear();
        harness.Cli.Handler = args => SimulateInstall(harness.Sandbox, args);

        await harness.Service.TickAsync();

        // 每次最多 3 个（串行），其余留到下一个 Tick。
        Assert.Equal(3, harness.Registry.Calls);
        Assert.Equal(3, harness.Cli.Calls.Count);
    }

    [Fact]
    public async Task TickStopsAfterHardFailure()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        var plugins = Enumerable.Range(0, 3)
            .Select(index => new PluginSnapshot
            {
                Name = "plugin-" + index,
                Version = "1.0.0",
                Compatibility = CompatibilityLevel.Incompatible,
            })
            .ToArray();
        harness.Checks.Result = new CheckResult { Summary = "3 个不兼容", IncompatibleCount = 3, Plugins = plugins.ToList() };
        await harness.Service.RunCheckAsync();
        harness.Config.AutoUpgradeEnabled = true;
        harness.Registry.Candidates.Add(FakeRegistry.Compatible("1.2.0", Package));
        harness.Registry.Calls = 0;
        harness.Cli.Calls.Clear();
        harness.Cli.Handler = _ => new CliCallResult { ExitCode = 1, StdErr = "ERR_PNPM_FETCH_404 404 Not Found" };

        await harness.Service.TickAsync();

        // 第一个失败即停止本 Tick（不再尝试后续插件）。
        Assert.Single(harness.Cli.Calls);
        Assert.Equal(OperationOutcome.Failed, harness.State.LastOperation?.Outcome);
    }

    // ------------------------------------------------------------ 包级恢复

    [Fact]
    public async Task PackageRestoreRejectedWhenDisabled()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        Assert.False(harness.Config.PackageRestoreEnabled);

        var result = await harness.Service.PackageRestoreAsync(new PackageRestoreRequest
        {
            Installation = harness.Sandbox.Installation,
        });

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Equal(PackageRestoreFailureKind.Precondition, result.Failure?.Kind);
        Assert.Contains("未启用", result.Messages[0]);
        // 开关关闭 → 不建 op、不写 journal、不动 profile。
        Assert.Empty(Directory.GetDirectories(harness.Paths.RestoreBackupDir));
        Assert.False(Directory.Exists(harness.Paths.JournalDir)
                     && Directory.GetFiles(harness.Paths.JournalDir, "*.jsonl").Length > 0);
        Assert.Empty(Directory.GetFiles(harness.Paths.ReportsDir, "report-*.md"));
        Assert.Empty(harness.Cli.Calls);
    }

    [Fact]
    public async Task PreviewPackageRestoreBlockedWithoutSnapshot()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");

        var plan = await harness.Service.PreviewPackageRestoreAsync(new PackageRestoreRequest
        {
            Installation = harness.Sandbox.Installation,
        });

        Assert.True(plan.IsBlocked);
        Assert.Equal(PackageRestoreFailureKind.SnapshotInputMissing, plan.BlockingFailure?.Kind);
        Assert.NotEmpty(plan.OperationId);
    }

    [Fact]
    public async Task PreviewPackageRestoreBuildsPlanFromSnapshot()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        var snapshot = await harness.Service.TakeSnapshotAsync(new SnapshotRequest { Trigger = SnapshotTrigger.Manual });
        Assert.True(snapshot.Success);
        harness.Cli.Calls.Clear();

        var plan = await harness.Service.PreviewPackageRestoreAsync(new PackageRestoreRequest
        {
            Installation = harness.Sandbox.Installation,
        });

        Assert.False(plan.IsBlocked);
        Assert.Equal(snapshot.Snapshot!.Id, plan.SnapshotId);
        Assert.Contains(plan.Steps, step => step.Kind == PackageRestoreStepKind.BackupProfile);
        Assert.Contains(plan.Steps, step => step.Kind == PackageRestoreStepKind.InstallOffline);
        Assert.Contains(plan.Steps, step => step.Kind == PackageRestoreStepKind.Verify);
        Assert.True(plan.RequiresFullExit);
        Assert.True(plan.OfflineOnly);
        // 预览必须只读：不能创建备份目录、不能调用 CLI。
        Assert.Empty(Directory.GetDirectories(harness.Paths.RestoreBackupDir));
        Assert.Empty(harness.Cli.Calls);
    }

    [Fact]
    public async Task PackageRestoreDryRunDoesNotTouchProfile()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        var snapshot = await harness.Service.TakeSnapshotAsync(new SnapshotRequest { Trigger = SnapshotTrigger.Manual });
        harness.Config.PackageRestoreEnabled = true;
        var before = harness.Sandbox.ReadProfileFile(DshDefaults.ManifestFile);
        harness.Cli.Calls.Clear();

        var result = await harness.Service.PackageRestoreAsync(new PackageRestoreRequest
        {
            Installation = harness.Sandbox.Installation,
            DryRun = true,
            Plan = new PackageRestorePlan { SnapshotId = snapshot.Snapshot!.Id },
        });

        Assert.Equal(OperationOutcome.DryRun, result.Outcome);
        Assert.Equal(before, harness.Sandbox.ReadProfileFile(DshDefaults.ManifestFile));
        Assert.Empty(harness.Cli.Calls);
        Assert.Empty(Directory.GetDirectories(harness.Paths.RestoreBackupDir));
    }

    [Fact]
    public async Task PackageRestoreAppliesOfflineInstallAndKeepsRollbackPoint()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        var snapshot = await harness.Service.TakeSnapshotAsync(new SnapshotRequest { Trigger = SnapshotTrigger.Manual });
        harness.Config.PackageRestoreEnabled = true;
        harness.Cli.Calls.Clear();
        harness.Cli.Handler = _ => new CliCallResult { ExitCode = 0 };

        var result = await harness.Service.PackageRestoreAsync(new PackageRestoreRequest
        {
            Installation = harness.Sandbox.Installation,
            Plan = new PackageRestorePlan { SnapshotId = snapshot.Snapshot!.Id },
        });

        // 离线安装必须带 --offline --frozen-lockfile，且回滚点写入 restore-backup\<opId>\。
        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.Contains(harness.Cli.Calls, call => call.SequenceEqual(new[] { "install", "--offline", "--frozen-lockfile" }));
        Assert.True(result.Verified);
        Assert.NotNull(result.RollbackPointPath);
        Assert.True(File.Exists(result.RollbackPointPath!));
        Assert.Equal("recovery", harness.State.LastOperation?.Kind);
        Assert.Contains(harness.Notifications, item => item.EventId == "recovery-" + result.OperationId);
    }

    [Fact]
    public void CanRollbackIsFalseForUnknownOperation()
    {
        using var harness = new LoomServiceHarness();
        Assert.False(harness.Service.CanRollbackPackageOp("no-such-op"));
        Assert.False(harness.Service.CanRollbackPackageOp(""));
    }

    [Fact]
    public async Task RollbackWithoutOpIdFailsClosed()
    {
        using var harness = new LoomServiceHarness();

        var result = await harness.Service.RollbackPackageOpAsync("");

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Equal(PackageRestoreFailureKind.Precondition, result.Failure?.Kind);
        Assert.Contains("opId", result.Failure!.Message);
    }

    [Fact]
    public async Task RollbackWithCorruptBackupPointNeedsManual()
    {
        using var harness = new LoomServiceHarness();
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        // 目录存在但没有回滚点文件（模拟半写/被删）
        var opId = "20260101-000000-recovery-deadbeef";
        Directory.CreateDirectory(harness.Paths.RestoreBackupDirFor(opId));

        var result = await harness.Service.RollbackPackageOpAsync(opId);

        Assert.Equal(OperationOutcome.NeedsManual, result.Outcome);
        Assert.Equal(opId, result.OpId);
        Assert.NotNull(result.Failure?.ManualAction);
        Assert.Empty(harness.Cli.Calls);
    }

    // ------------------------------------------------------------ P1-2 / P2-4 / P2-5

    /// <summary>P1-2：upgrade 在真正安装（写 profile）期间必须持有跨进程门，结束后释放。</summary>
    [Fact]
    public async Task UpgradeHoldsCrossProcessGateForTheWholeInstall()
    {
        using var harness = new LoomServiceHarness(useRealOperationGate: true);
        Assert.NotNull(harness.RealGate);
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        harness.Registry.Candidates.Add(FakeRegistry.Compatible("1.2.0", Package));

        using var installStarted = new ManualResetEventSlim(false);
        using var releaseInstall = new ManualResetEventSlim(false);
        harness.Cli.Handler = args =>
        {
            installStarted.Set();
            releaseInstall.Wait(TimeSpan.FromSeconds(30));
            return SimulateInstall(harness.Sandbox, args);
        };

        var upgrade = harness.Service.UpgradePluginAsync(new UpgradeRequest
        {
            Installation = harness.Sandbox.Installation,
            PackageName = Package,
            InstalledVersion = "1.0.0",
            TargetDshVersion = "0.3.0",
        });

        Assert.True(installStarted.Wait(TimeSpan.FromSeconds(10)), "升级应进入安装阶段");
        Assert.False(TryTakeNamedMutexOnOtherThread(harness.RealGate!.LeaseMutexName), "升级安装期间跨进程门必须被持有");

        releaseInstall.Set();
        var result = await upgrade;
        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.True(TryTakeNamedMutexOnOtherThread(harness.RealGate!.LeaseMutexName), "升级结束后跨进程门必须释放");
    }

    /// <summary>P1-2：包级恢复的离线 install 期间同样必须持有跨进程门。</summary>
    [Fact]
    public async Task PackageRestoreHoldsCrossProcessGateForTheWholeInstall()
    {
        using var harness = new LoomServiceHarness(useRealOperationGate: true);
        Assert.NotNull(harness.RealGate);
        SeedProfile(harness.Sandbox, Package, installedVersion: "1.0.0");
        var snapshot = await harness.Service.TakeSnapshotAsync(new SnapshotRequest { Trigger = SnapshotTrigger.Manual });
        Assert.True(snapshot.Success);
        harness.Config.PackageRestoreEnabled = true;
        harness.Cli.Calls.Clear();

        using var installStarted = new ManualResetEventSlim(false);
        using var releaseInstall = new ManualResetEventSlim(false);
        harness.Cli.Handler = _ =>
        {
            installStarted.Set();
            releaseInstall.Wait(TimeSpan.FromSeconds(30));
            return new CliCallResult { ExitCode = 0 };
        };

        var recovery = harness.Service.PackageRestoreAsync(new PackageRestoreRequest
        {
            Installation = harness.Sandbox.Installation,
            Plan = new PackageRestorePlan { SnapshotId = snapshot.Snapshot!.Id },
        });

        Assert.True(installStarted.Wait(TimeSpan.FromSeconds(10)), "包级恢复应进入离线安装阶段");
        Assert.False(TryTakeNamedMutexOnOtherThread(harness.RealGate!.LeaseMutexName), "包级恢复安装期间跨进程门必须被持有");

        releaseInstall.Set();
        var result = await recovery;
        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.True(TryTakeNamedMutexOnOtherThread(harness.RealGate!.LeaseMutexName), "包级恢复结束后跨进程门必须释放");
    }

    /// <summary>
    /// P2-4：设置页拿着“旧快照”整份保存时，后台刚写的时间戳不能被覆盖回旧值。
    /// </summary>
    [Fact]
    public async Task SettingsSaveDoesNotOverwriteNewerBackgroundTimestamp()
    {
        using var harness = new LoomServiceHarness();
        var stamp = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);
        harness.Service.MutateConfig(config => config.LastUpdateCheckUtc = stamp);
        Assert.Equal(stamp, ConfigStore.Load(harness.Paths).LastUpdateCheckUtc);

        // 模拟设置页：BuildUpdatedConfig 用的是 Load() 时的旧配置（时间戳仍是 default）。
        var stale = JsonDefaults.Deserialize<LoomConfig>(JsonDefaults.Serialize(harness.Config))!;
        stale.LastUpdateCheckUtc = default;
        stale.KeepSnapshots = 9;

        await harness.Service.SaveConfigAsync(stale);

        var saved = ConfigStore.Load(harness.Paths);
        Assert.Equal(9, saved.KeepSnapshots);          // 用户改动生效
        Assert.Equal(stamp, saved.LastUpdateCheckUtc); // 后台字段没有被旧值覆盖
    }

    /// <summary>P2-4：自动更新检查的时间戳写回走 MutateConfig（只改该字段，不整份覆盖）。</summary>
    [Fact]
    public async Task UpdateCheckPersistsTimestampThroughMutateConfig()
    {
        using var harness = new LoomServiceHarness();
        harness.Config.KeepSnapshots = 11;
        await harness.Service.SaveConfigAsync(harness.Config);

        harness.Service.Updates.LatestTagOverride = "v99.0.0";
        await harness.Service.Updates.CheckAsync();

        var saved = ConfigStore.Load(harness.Paths);
        Assert.True(DateTimeOffset.UtcNow - saved.LastUpdateCheckUtc < TimeSpan.FromMinutes(1));
        Assert.Equal(11, saved.KeepSnapshots); // MutateConfig 没有把其它字段重置成默认值
    }

    /// <summary>P2-5：doctor 必须输出一条 tick 状态（不新增 UI）。</summary>
    [Fact]
    public async Task DoctorReportsTickWatchdogStatus()
    {
        using var harness = new LoomServiceHarness();
        await harness.Service.TickAsync();

        var doctor = await harness.Service.RunDoctorAsync();

        var tick = Assert.Single(doctor.Checks, check => check.Name == "tick");
        Assert.True(tick.Ok);
        Assert.Contains("空闲", tick.Detail);
    }

    /// <summary>
    /// P3-R2：用一个“可控卡住”的定位依赖验证：超时 → 取消 → 标记 abandoned → 下一 Tick 能继续，
    /// 并且复位会写 Failed 通知（eventId 带旧 Tick 的 opId）。
    /// </summary>
    [Fact]
    public async Task StalledTickIsCancelledAndTheNextTickStillRuns()
    {
        using var harness = new LoomServiceHarness();
        harness.Service.TickGate.StallThreshold = TimeSpan.FromMilliseconds(100);
        harness.Service.TickGate.ResetWait = TimeSpan.FromMilliseconds(250);

        using var firstStarted = new ManualResetEventSlim(false);
        var releaseFirst = new TaskCompletionSource<DshInstallation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        harness.Locator.Handler = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.Set();
                // 故意不响应取消：模拟一个真正卡死的依赖，逼出 abandoned 路径。
                return releaseFirst.Task;
            }

            return Task.FromResult<DshInstallation?>(harness.Sandbox.Installation);
        };

        var firstTick = harness.Service.TickAsync();
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)), "第一轮 Tick 应进入定位阶段");

        await Task.Delay(300); // 超过 100ms 阈值
        var secondTick = harness.Service.TickAsync();
        await secondTick.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(calls >= 2, "复位后下一轮 Tick 必须真正执行");
        Assert.Contains(
            harness.Notifications,
            notification => notification.Kind == NotificationKind.Failed
                            && notification.EventId.StartsWith("tick-stalled-", StringComparison.Ordinal));

        var snapshot = harness.Service.TickGate.Snapshot();
        Assert.True(snapshot.StalledResets >= 1);
        Assert.NotNull(snapshot.LastResetAt);

        // 释放旧 Tick：它可以慢慢退出，但绝不能把新一轮/闸门状态搞乱。
        releaseFirst.TrySetResult(harness.Sandbox.Installation);
        await firstTick.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(harness.Service.TickGate.IsRunning);
    }

    /// <summary>在独立线程上尝试获取指定命名互斥量（等价于另一个进程）；成功立即释放。</summary>
    private static bool TryTakeNamedMutexOnOtherThread(string mutexName)
    {
        var acquired = false;
        var thread = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(initiallyOwned: false, mutexName);
                acquired = mutex.WaitOne(0);
                if (acquired) mutex.ReleaseMutex();
            }
            catch
            {
                acquired = false;
            }
        });
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(5));
        return acquired;
    }

    // ------------------------------------------------------------ helpers

    private static void SeedProfile(LoomSandbox sandbox, string package, string installedVersion)
    {
        sandbox.WriteProfileFile(
            DshDefaults.ManifestFile,
            TestData.PackageJson(
                new[] { "@deepseek-ai/dsh-base", package },
                new Dictionary<string, string> { [package] = installedVersion }));
        sandbox.WriteProfileFile(
            DshDefaults.LockfileFile,
            TestData.Lockfile(new Dictionary<string, string> { [package] = installedVersion }));
        sandbox.WriteProfileFile(DshDefaults.PatchFile, TestData.PatchYaml());
        sandbox.AddPlugin(package, installedVersion, enginesDsh: "^0.2.0");
    }

    /// <summary>
    /// 模拟 <c>dsh.cmd plugin add &lt;pkg&gt;@&lt;ver&gt; --save-exact</c> 成功后的写回：
    /// 依赖 spec 变精确版本 + node_modules\&lt;pkg&gt;\package.json 版本更新。
    /// </summary>
    private static CliCallResult SimulateInstall(LoomSandbox sandbox, IReadOnlyList<string> args)
    {
        if (args.Count >= 2 && string.Equals(args[0], "add", StringComparison.Ordinal))
        {
            var spec = args[1];
            var separator = spec.LastIndexOf('@');
            if (separator > 0)
            {
                var name = spec[..separator];
                var version = spec[(separator + 1)..];
                var root = JsonNode.Parse(sandbox.ReadProfileFile(DshDefaults.ManifestFile))!.AsObject();
                var dependencies = root["dependencies"] as JsonObject ?? new JsonObject();
                dependencies[name] = version;
                root["dependencies"] = dependencies;
                sandbox.WriteProfileFile(DshDefaults.ManifestFile, root.ToJsonString());
                sandbox.AddPlugin(name, version, enginesDsh: "^0.3.0");
            }
        }

        return new CliCallResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(5) };
    }

    /// <summary>跑一次真实检查流程，把“不兼容插件”的检查结果放进服务内存（Tick 自动升级的输入）。</summary>
    private static async Task SeedIncompatibleCheckAsync(LoomServiceHarness harness)
    {
        harness.Checks.Result = new CheckResult
        {
            Summary = "1 个插件不兼容",
            IncompatibleCount = 1,
            Plugins =
            {
                new PluginSnapshot { Name = Package, Version = "1.0.0", Compatibility = CompatibilityLevel.Incompatible },
                new PluginSnapshot { Name = "plugin-unverified", Version = "1.0.0", Compatibility = CompatibilityLevel.Unverified },
            },
        };
        await harness.Service.RunCheckAsync();
    }
}
