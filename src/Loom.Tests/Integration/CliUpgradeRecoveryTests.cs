using System.Text.Json.Nodes;
using Loom.Core.Cli;
using Loom.Core.Models;
using Loom.Core.Recovery;
using Loom.Core.Upgrade;
using Xunit;

namespace Loom.Tests.Integration;

/// <summary>
/// 二期 CLI 接线：<c>upgrade</c> 与 <c>recovery</c> 的参数解析、退出码语义
/// （0 成功 / 1 失败 / 2 待人工 / 3 不可用 / 4 忙）、JSON 输出与 help 文案。
/// 全部走假 <see cref="Loom.Core.Abstractions.ILoomService"/>，不触碰真实 DSH。
/// </summary>
public sealed class CliUpgradeRecoveryTests
{
    private const string Package = "dsh-plugin-demo";

    // ------------------------------------------------------------ 参数解析

    [Fact]
    public void ParsesUpgradeFlags()
    {
        var invocation = CliParser.Parse(new[] { "upgrade", "--pkg", Package, "--dry-run", "--json", "--allow-prerelease" });

        Assert.Null(invocation.Error);
        Assert.Equal("upgrade", invocation.Command);
        Assert.Equal(Package, invocation.PackageName);
        Assert.True(invocation.DryRun);
        Assert.True(invocation.Json);
        Assert.True(invocation.AllowPrerelease);
    }

    [Fact]
    public void ParsesRecoverySubcommandsAndSnapshot()
    {
        var plan = CliParser.Parse(new[] { "recovery", "plan", "--snapshot", "20260101-000000", "--json" });
        Assert.Null(plan.Error);
        Assert.Equal("recovery", plan.Command);
        Assert.Equal(new[] { "plan" }, plan.Positionals);
        Assert.Equal("20260101-000000", plan.SnapshotId);
        Assert.True(plan.Json);

        var rollback = CliParser.Parse(new[] { "recovery", "rollback", "20260101-000000-recovery-abc123" });
        Assert.Equal("rollback", rollback.Positionals[0]);
        Assert.Equal("20260101-000000-recovery-abc123", rollback.Positionals[1]);
    }

    [Fact]
    public async Task MissingPkgValueIsArgumentError()
    {
        var (code, stderr) = await RunAsync(new FakeLoomService(), "upgrade", "--pkg");
        Assert.Equal(ExitCodes.Failed, code);
        Assert.Contains("--pkg", stderr);
    }

    [Fact]
    public async Task RecoveryWithoutSubcommandIsArgumentError()
    {
        var (code, stderr) = await RunAsync(new FakeLoomService(), "recovery");
        Assert.Equal(ExitCodes.Failed, code);
        Assert.Contains("plan|apply|rollback", stderr);
    }

    [Fact]
    public async Task RecoveryRollbackWithoutOpIdIsArgumentError()
    {
        var (code, stderr) = await RunAsync(new FakeLoomService(), "recovery", "rollback");
        Assert.Equal(ExitCodes.Failed, code);
        Assert.Contains("opId", stderr);
    }

    // ------------------------------------------------------------ upgrade 退出码

    [Fact]
    public async Task UpgradeSuccessExitsZeroAndPrintsJson()
    {
        using var loom = new FakeLoomService();
        loom.UpgradeHandler = request => new UpgradeExecutionResult
        {
            OperationId = "op-1",
            Outcome = OperationOutcome.Succeeded,
            ProfileChanged = true,
            CommandLine = "dsh.cmd plugin --profile desktop add " + request.PackageName + "@1.2.0 --save-exact",
            BackupDir = @"C:\data\restore-backup\op-1",
            Messages = { "已升级到 1.2.0" },
            Decision = new UpgradeDecision
            {
                PackageName = request.PackageName,
                InstalledVersion = "1.0.0",
                TargetVersion = "1.2.0",
                TargetDshVersion = "0.3.0",
                Kind = UpgradeDecisionKind.Upgrade,
                ShouldUpgrade = true,
            },
        };

        var (code, _) = await RunAsync(loom, "upgrade", "--pkg", Package, "--json");

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains(loom.Calls, call => call == "upgrade:" + Package);
    }

    [Fact]
    public async Task UpgradeNeedsManualExitsTwo()
    {
        using var loom = new FakeLoomService();
        loom.UpgradeHandler = request => new UpgradeExecutionResult
        {
            OperationId = "op-2",
            Outcome = OperationOutcome.NeedsManual,
            Decision = new UpgradeDecision
            {
                PackageName = request.PackageName,
                Kind = UpgradeDecisionKind.ManualOnly,
                BlockReason = UpgradeBlockReason.NoCompatibleCandidate,
                Reason = "没有与目标运行时兼容的版本",
            },
            Messages = { "没有与目标运行时兼容的版本；未安装、未授予风险豁免。" },
        };

        var (code, _) = await RunAsync(loom, "upgrade", "--pkg", Package);
        Assert.Equal(ExitCodes.NeedsManual, code);
    }

    [Fact]
    public async Task UpgradeAppRunningExitsBusy()
    {
        using var loom = new FakeLoomService();
        loom.UpgradeHandler = request => new UpgradeExecutionResult
        {
            OperationId = "op-3",
            Outcome = OperationOutcome.NeedsManual,
            Decision = new UpgradeDecision
            {
                PackageName = request.PackageName,
                Kind = UpgradeDecisionKind.ManualOnly,
                BlockReason = UpgradeBlockReason.AppRunning,
                Reason = "DSH 仍在运行",
            },
            Audit = new UpgradeAuditRecord { FailureClass = UpgradeFailureClass.AppRunning },
            Messages = { "DSH 仍在运行：请完全退出后重试。" },
        };

        var (code, _) = await RunAsync(loom, "upgrade", "--pkg", Package);
        Assert.Equal(ExitCodes.Busy, code);
    }

    [Fact]
    public async Task UpgradeFailureExitsOne()
    {
        using var loom = new FakeLoomService();
        loom.UpgradeHandler = _ => new UpgradeExecutionResult
        {
            OperationId = "op-4",
            Outcome = OperationOutcome.Failed,
            Audit = new UpgradeAuditRecord { FailureClass = UpgradeFailureClass.InstallFailed },
            Messages = { "安装失败" },
        };

        var (code, _) = await RunAsync(loom, "upgrade", "--pkg", Package);
        Assert.Equal(ExitCodes.Failed, code);
    }

    [Fact]
    public async Task UpgradeOfflineSkipExitsUnavailable()
    {
        using var loom = new FakeLoomService();
        loom.UpgradeHandler = request => new UpgradeExecutionResult
        {
            OperationId = "op-5",
            Outcome = OperationOutcome.Skipped,
            Decision = new UpgradeDecision
            {
                PackageName = request.PackageName,
                Kind = UpgradeDecisionKind.NoAction,
                BlockReason = UpgradeBlockReason.Offline,
                Reason = "离线：无法查询",
            },
            Messages = { "离线：无法查询候选版本。" },
        };

        var (code, _) = await RunAsync(loom, "upgrade", "--pkg", Package);
        Assert.Equal(ExitCodes.Unavailable, code);
    }

    [Fact]
    public async Task BatchUpgradeWithoutCheckResultExitsUnavailable()
    {
        using var loom = new FakeLoomService { LastCheck = null };

        var (code, stderr) = await RunAsync(loom, "upgrade");
        Assert.Equal(ExitCodes.Unavailable, code);
        Assert.Contains("loom-dsh check", stderr);
    }

    [Fact]
    public async Task BatchUpgradeProcessesIncompatiblePluginsAndSkipsUnverified()
    {
        using var loom = new FakeLoomService
        {
            LastCheck = new CheckResult
            {
                Summary = "2 个插件不兼容",
                Plugins =
                {
                    new PluginSnapshot { Name = "plugin-a", Version = "1.0.0", Compatibility = CompatibilityLevel.Incompatible },
                    new PluginSnapshot { Name = "plugin-b", Version = "1.0.0", Compatibility = CompatibilityLevel.Unverified },
                    new PluginSnapshot { Name = "plugin-c", Version = "1.0.0", Compatibility = CompatibilityLevel.Incompatible },
                },
            },
        };
        loom.UpgradeHandler = request => new UpgradeExecutionResult
        {
            OperationId = "op-" + request.PackageName,
            Outcome = OperationOutcome.DryRun,
            Decision = new UpgradeDecision { PackageName = request.PackageName, ShouldUpgrade = true, TargetVersion = "1.1.0" },
            Messages = { "演练" },
        };

        var (code, _) = await RunAsync(loom, "upgrade", "--dry-run");

        Assert.Equal(ExitCodes.Success, code);
        // 只处理 incompatible（unverified 一律不自动装）。
        Assert.Contains(loom.Calls, call => call == "upgrade:plugin-a:dry-run");
        Assert.Contains(loom.Calls, call => call == "upgrade:plugin-c:dry-run");
        Assert.DoesNotContain(loom.Calls, call => call.Contains("plugin-b", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ recovery 退出码

    [Fact]
    public async Task RecoveryPlanBlockedByMissingSnapshotExitsUnavailable()
    {
        using var loom = new FakeLoomService
        {
            PreviewPlan = new PackageRestorePlan
            {
                OperationId = "plan-1",
                IsBlocked = true,
                Summary = "没有可用快照",
                BlockingFailure = new PackageRestoreFailure
                {
                    Kind = PackageRestoreFailureKind.SnapshotInputMissing,
                    Message = "没有可用的快照",
                },
            },
        };

        var (code, _) = await RunAsync(loom, "recovery", "plan");
        Assert.Equal(ExitCodes.Unavailable, code);
    }

    [Fact]
    public async Task RecoveryPlanNotBlockedExitsZeroAndPrintsJson()
    {
        using var loom = new FakeLoomService
        {
            PreviewPlan = new PackageRestorePlan
            {
                OperationId = "plan-2",
                SnapshotId = "snap-1",
                Summary = "计划 5 步",
                Steps = { new PackageRestoreStep { Order = 1, Kind = PackageRestoreStepKind.BackupProfile, Description = "备份" } },
            },
        };

        var (code, stdout) = await RunAsync(loom, "recovery", "plan", "--json");

        Assert.Equal(ExitCodes.Success, code);
        var json = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal("plan-2", json["operationId"]!.GetValue<string>());
        Assert.False(json["isBlocked"]!.GetValue<bool>());
        Assert.Single(json["steps"]!.AsArray());
        // 计划 JSON 不包含文件内容（package.json / lockfile 全文），避免把大文件塞进日志。
        Assert.Null(json["steps"]![0]!["content"]);
    }

    [Fact]
    public async Task RecoveryApplyDisabledExitsNeedsManual()
    {
        using var loom = new FakeLoomService
        {
            PackageRestoreResult = new PackageRestoreResult
            {
                OperationId = "op-r1",
                Outcome = OperationOutcome.Failed,
                Failure = new PackageRestoreFailure
                {
                    Kind = PackageRestoreFailureKind.Precondition,
                    Message = "包级恢复未启用",
                },
                Messages = { "包级恢复未启用" },
            },
        };

        var (code, _) = await RunAsync(loom, "recovery", "apply");
        Assert.Equal(ExitCodes.NeedsManual, code);
    }

    [Fact]
    public async Task RecoveryApplyProfileBusyExitsBusy()
    {
        using var loom = new FakeLoomService
        {
            PackageRestoreResult = new PackageRestoreResult
            {
                OperationId = "op-r2",
                Outcome = OperationOutcome.NeedsManual,
                Failure = new PackageRestoreFailure
                {
                    Kind = PackageRestoreFailureKind.ProfileBusy,
                    Message = "DSH 仍在运行",
                },
                Messages = { "DSH 仍在运行" },
            },
        };

        var (code, _) = await RunAsync(loom, "recovery", "apply");
        Assert.Equal(ExitCodes.Busy, code);
    }

    [Fact]
    public async Task RecoveryRollbackDryRunReportsAvailability()
    {
        using var loom = new FakeLoomService { CanRollback = true };
        var (code, _) = await RunAsync(loom, "recovery", "rollback", "op-9", "--dry-run");
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains(loom.Calls, call => call == "can-rollback:op-9");

        loom.CanRollback = false;
        var (blocked, _) = await RunAsync(loom, "recovery", "rollback", "op-9", "--dry-run");
        Assert.Equal(ExitCodes.NeedsManual, blocked);
    }

    [Fact]
    public async Task RecoveryRollbackExecutesOfflineRollback()
    {
        using var loom = new FakeLoomService
        {
            RollbackResult = new OfflineRollbackResult
            {
                Outcome = OperationOutcome.Succeeded,
                Verified = true,
                RestoredFiles = { "package.json", "pnpm-lock.yaml" },
            },
        };

        var (code, stdout) = await RunAsync(loom, "recovery", "rollback", "op-10");

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains(loom.Calls, call => call == "rollback:op-10");
        Assert.Contains("离线回滚：成功", stdout);
    }

    // ------------------------------------------------------------ help

    [Fact]
    public void HelpDocumentsUpgradeAndRecovery()
    {
        var writer = new StringWriter();
        CliRunner.WriteHelp(writer);
        var text = writer.ToString();

        Assert.Contains("loom-dsh upgrade", text);
        Assert.Contains("--allow-prerelease", text);
        Assert.Contains("loom-dsh recovery", text);
        Assert.Contains("rollback <opId>", text);
    }

    // ------------------------------------------------------------ helpers

    private static async Task<(int Code, string Output)> RunAsync(FakeLoomService loom, params string[] args)
    {
        var invocation = CliParser.Parse(args);
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await new CliRunner(loom).RunAsync(invocation, output, error);
        // 断言用的合并文本：参数错误/失败原因走 stderr，正常结果走 stdout。
        var combined = output.ToString() + error.ToString();
        return (code, combined);
    }
}
