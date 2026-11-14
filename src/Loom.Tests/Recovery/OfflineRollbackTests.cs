using System.Text;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Recovery;
using Loom.Tests.CoreFlows;
using Xunit;

namespace Loom.Tests.Recovery;

/// <summary>
/// 离线完整回滚（设计 9.4 / 12.6）：CanRollback、文件级恢复 + 离线 install、回滚后校验、
/// 只影响指定 op、以及“不联网”的失败路径。全部使用临时 sandbox 与假 <see cref="IDshCli"/>。
/// </summary>
public sealed class OfflineRollbackTests
{
    private static async Task<(SnapshotManifest Manifest, SnapshotRef Reference)> PrepareAsync(LoomSandbox sandbox)
    {
        RecoveryTestSupport.SeedProfile(sandbox);
        return await RecoveryTestSupport.TakeBaselineAsync(sandbox);
    }

    /// <summary>跑一次会漂移 + 成功包级恢复的操作，返回操作前的文件内容。</summary>
    private static async Task<(string PrePackage, string PreLockfile, PackageRestoreResult Result)> RestoreOnceAsync(
        LoomSandbox sandbox,
        SnapshotManifest manifest,
        SnapshotRef reference,
        string opId,
        FakeDshCli cli)
    {
        RecoveryTestSupport.ApplyDrift(sandbox, opId + "-pre");
        var prePackage = RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile);
        var preLockfile = RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.LockfileFile);

        var plan = PackageRestorePlanner.Create(
            RecoveryTestSupport.BuildInput(sandbox, manifest, reference.Path, operationId: opId));
        Assert.False(plan.IsBlocked);
        var result = await new PackageRestoreService(cli, RecoveryTestSupport.Logger)
            .RestoreAsync(RecoveryTestSupport.Request(sandbox, plan, opId));

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.True(File.Exists(Path.Combine(sandbox.Paths.RestoreBackupDirFor(opId), "package-restore.json")));
        return (prePackage, preLockfile, result);
    }

    [Fact]
    public void CanRollbackRequiresRollbackPoint()
    {
        using var sandbox = new LoomSandbox();
        var service = new OfflineRollbackService(sandbox.Paths, new FakeDshCli(), RecoveryTestSupport.Logger);

        Assert.False(service.CanRollback("op-missing"));
        Assert.False(service.CanRollback("op-missing", out var reason));
        Assert.Contains("备份目录不存在", reason);
        Assert.False(service.CanRollback(""));
        Assert.Empty(service.ListRollbackPointIds());
        Assert.Null(service.FindLatest());
        Assert.Null(service.ReadRollbackPoint("op-missing"));
    }

    [Fact]
    public async Task RollbackRestoresFilesReinstallsOfflineAndVerifies()
    {
        using var sandbox = new LoomSandbox();
        var (manifest, reference) = await PrepareAsync(sandbox);
        var cli = new FakeDshCli();
        var (prePackage, preLockfile, _) = await RestoreOnceAsync(
            sandbox, manifest, reference, "op-rb-1", cli);

        // 恢复后又出现新的损坏：回滚必须把文件恢复到 op 之前。
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, "{\"name\":\"broken-after-op\"}");
        sandbox.WriteProfileFile(DshDefaults.LockfileFile, "lockfileVersion: '9.0'\n# broken\n");

        var rollback = new OfflineRollbackService(sandbox.Paths, cli, RecoveryTestSupport.Logger);
        Assert.True(rollback.CanRollback("op-rb-1"));
        var result = await rollback.RollbackAsync("op-rb-1", sandbox.Installation);

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.True(result.Verified);
        Assert.False(string.IsNullOrWhiteSpace(result.BackupDir));
        Assert.Equal(2, result.RestoredFiles.Count);
        Assert.Equal(0, result.InstallExitCode);
        Assert.Equal(prePackage, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile));
        Assert.Equal(preLockfile, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.LockfileFile));

        // 回滚的重新安装必须仍是离线（绝不联网、绝不用 file:<tgz>）。
        var lastCall = cli.Calls[^1];
        Assert.Contains("install", lastCall.Args);
        Assert.Contains("--offline", lastCall.Args);
        Assert.Contains("--frozen-lockfile", lastCall.Args);
        Assert.All(cli.Calls, call => Assert.Contains("--offline", call.Args));

        var journal = RecoveryTestSupport.JournalText(sandbox, "op-rb-1");
        Assert.Contains("\"stage\":\"Rollback\"", journal);
        Assert.Contains("\"stage\":\"Done\"", journal);
        Assert.Contains("\"status\":\"done\"", journal);
    }

    [Fact]
    public async Task RollbackFailsWithoutNetworkWhenOfflineInstallFails()
    {
        using var sandbox = new LoomSandbox();
        var (manifest, reference) = await PrepareAsync(sandbox);
        var offlineFails = false;
        var cli = new FakeDshCli
        {
            Responder = _ => offlineFails
                ? new CliCallResult { ExitCode = 1, StdErr = "ERR_PNPM_NO_OFFLINE_TARBALL dsh-whale-widget" }
                : new CliCallResult { ExitCode = 0 },
        };
        var (prePackage, _, _) = await RestoreOnceAsync(sandbox, manifest, reference, "op-rb-fail", cli);

        sandbox.WriteProfileFile(DshDefaults.ManifestFile, "{\"name\":\"broken\"}");
        offlineFails = true;

        var rollback = new OfflineRollbackService(sandbox.Paths, cli, RecoveryTestSupport.Logger);
        var result = await rollback.RollbackAsync("op-rb-fail", sandbox.Installation);

        Assert.Equal(OperationOutcome.NeedsManual, result.Outcome);
        Assert.NotNull(result.Failure);
        Assert.Equal(PackageRestoreFailureKind.OfflineCacheMissing, result.Failure!.Kind);
        Assert.False(result.Verified);
        Assert.NotEmpty(result.RestoredFiles);
        // 文件已恢复（丢 node_modules 修复），但绝不偷偷联网。
        Assert.Equal(prePackage, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile));
        Assert.All(cli.Calls, call => Assert.Contains("--offline", call.Args));
        Assert.Contains("未联网", result.Failure.ManualAction);
        Assert.Contains("\"status\":\"failed\"", RecoveryTestSupport.JournalText(sandbox, "op-rb-fail"));
    }

    [Fact]
    public async Task RollbackRefusesTamperedBackup()
    {
        using var sandbox = new LoomSandbox();
        var (manifest, reference) = await PrepareAsync(sandbox);
        var cli = new FakeDshCli();
        var (_, _, restoreResult) = await RestoreOnceAsync(sandbox, manifest, reference, "op-rb-tamper", cli);

        var postOpPackage = RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile);
        var backupDir = sandbox.Paths.RestoreBackupDirFor("op-rb-tamper");
        var backupPackage = Path.Combine(backupDir, DshDefaults.ManifestFile);
        Assert.True(File.Exists(backupPackage));
        File.WriteAllText(backupPackage, "{\"tampered\":true}");

        var rollback = new OfflineRollbackService(sandbox.Paths, cli, RecoveryTestSupport.Logger);
        Assert.False(rollback.CanRollback("op-rb-tamper", out var reason));
        Assert.Contains("哈希不符", reason);

        var result = await rollback.RollbackAsync("op-rb-tamper", sandbox.Installation);
        Assert.Equal(PackageRestoreFailureKind.BackupCorrupt, result.Failure!.Kind);
        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Empty(result.RestoredFiles);
        // 备份不可信时绝不写 profile。
        Assert.Equal(postOpPackage, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile));
        Assert.Contains("\"status\":\"failed\"", RecoveryTestSupport.JournalText(sandbox, restoreResult.OperationId));
    }

    [Fact]
    public async Task RollbackDryRunWritesNothing()
    {
        using var sandbox = new LoomSandbox();
        var (manifest, reference) = await PrepareAsync(sandbox);
        var cli = new FakeDshCli();
        await RestoreOnceAsync(sandbox, manifest, reference, "op-rb-dry", cli);
        var callsAfterRestore = cli.Calls.Count;

        sandbox.WriteProfileFile(DshDefaults.ManifestFile, "{\"name\":\"dry-run-target\"}");
        var before = RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile);

        var rollback = new OfflineRollbackService(sandbox.Paths, cli, RecoveryTestSupport.Logger);
        var result = await rollback.RollbackAsync(new OfflineRollbackRequest
        {
            Paths = sandbox.Paths,
            Installation = sandbox.Installation,
            OpId = "op-rb-dry",
            DryRun = true,
        });

        Assert.Equal(OperationOutcome.DryRun, result.Outcome);
        Assert.Equal(before, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile));
        Assert.Equal(callsAfterRestore, cli.Calls.Count);
        Assert.Contains("未写入任何文件", string.Join("；", result.Messages));
    }

    [Fact]
    public async Task RollbackOnlyTouchesRequestedOp()
    {
        using var sandbox = new LoomSandbox();
        var (manifest, reference) = await PrepareAsync(sandbox);
        var cli = new FakeDshCli();

        var (prePackageA, preLockfileA, _) = await RestoreOnceAsync(
            sandbox, manifest, reference, "op-a", cli);
        var (prePackageB, preLockfileB, _) = await RestoreOnceAsync(
            sandbox, manifest, reference, "op-b", cli);

        var backupB = sandbox.Paths.RestoreBackupDirFor("op-b");
        var backupBPackage = File.ReadAllText(Path.Combine(backupB, DshDefaults.ManifestFile), Encoding.UTF8);
        var backupBPoint = File.ReadAllText(
            Path.Combine(backupB, "package-restore.json"), Encoding.UTF8);

        var rollback = new OfflineRollbackService(sandbox.Paths, cli, RecoveryTestSupport.Logger);
        Assert.True(rollback.CanRollback("op-a"));
        Assert.True(rollback.CanRollback("op-b"));
        var ids = rollback.ListRollbackPointIds();
        Assert.Contains("op-a", ids);
        Assert.Contains("op-b", ids);
        Assert.Equal("op-b", rollback.FindLatest()!.OpId);

        var result = await rollback.RollbackAsync("op-a", sandbox.Installation);
        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(prePackageA, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile));
        Assert.Equal(preLockfileA, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.LockfileFile));

        // op-b 的备份与回滚点完全不受影响（只回滚当前 op）。
        Assert.Equal(backupBPackage,
            File.ReadAllText(Path.Combine(backupB, DshDefaults.ManifestFile), Encoding.UTF8));
        Assert.Equal(backupBPoint,
            File.ReadAllText(Path.Combine(backupB, "package-restore.json"), Encoding.UTF8));
        Assert.Equal("op-b", RecoveryTestSupport.ReadPoint(sandbox, "op-b")!.OpId);

        // op-b 自己的回滚仍然可用，且恢复的是 op-b 操作前的内容。
        var rolloutB = await rollback.RollbackAsync("op-b", sandbox.Installation);
        Assert.Equal(OperationOutcome.Succeeded, rolloutB.Outcome);
        Assert.Equal(prePackageB, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.ManifestFile));
        Assert.Equal(preLockfileB, RecoveryTestSupport.ReadProfile(sandbox, DshDefaults.LockfileFile));
    }
}
