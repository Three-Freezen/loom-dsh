using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Recovery;

/// <summary>
/// 离线完整回滚（设计 9.4 / 12.6）：从 <c>restore-backup\&lt;opId&gt;\</c> 恢复文件级内容，
/// 并重新执行 <c>install --offline --frozen-lockfile</c> 修复 node_modules；全程不联网、
/// 不使用 <c>file:&lt;tgz&gt;</c>、不结束任何 DSH 进程、只影响指定 op。
/// 回滚后再次校验 package.json / bundles / 依赖 / lockfile。
/// </summary>
public sealed class OfflineRollbackService
{
    private readonly LoomPaths _paths;
    private readonly IDshCli _cli;
    private readonly ILogger _logger;

    public OfflineRollbackService(LoomPaths paths, IDshCli cli, ILogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>该 opId 是否有可用的回滚点（备份目录 + package-restore.json）。</summary>
    public bool CanRollback(string opId) => CanRollback(opId, out _);

    /// <summary>同 <see cref="CanRollback(string)"/>，额外给出不可回滚的中文原因。</summary>
    public bool CanRollback(string opId, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(opId))
        {
            reason = "未指定 opId";
            return false;
        }

        var backupDir = _paths.RestoreBackupDirFor(opId);
        if (!Directory.Exists(backupDir))
        {
            reason = "备份目录不存在：" + backupDir;
            return false;
        }

        var point = RecoveryBackupStore.Read(backupDir);
        if (point is null)
        {
            reason = "缺少或无法解析回滚点：" + RecoveryBackupStore.PointPath(backupDir);
            return false;
        }

        if (!RecoveryBackupStore.VerifyBackupFiles(point, out var error))
        {
            reason = error ?? "备份文件不可用";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>读取回滚点（缺失/损坏返回 null）。</summary>
    public OfflineRollbackPoint? ReadRollbackPoint(string opId)
    {
        if (string.IsNullOrWhiteSpace(opId)) return null;
        return RecoveryBackupStore.Read(_paths.RestoreBackupDirFor(opId));
    }

    /// <summary>列出所有带回滚点的 opId（按创建时间倒序）。</summary>
    public IReadOnlyList<string> ListRollbackPointIds()
    {
        if (!Directory.Exists(_paths.RestoreBackupDir)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateDirectories(_paths.RestoreBackupDir)
                .Select(directory => (directory, point: RecoveryBackupStore.Read(directory)))
                .Where(item => item.point is not null)
                .OrderByDescending(item => item.point!.CreatedAt)
                // 同一毫秒创建的多个 op 用目录名兜底排序，保证 FindLatest 稳定。
                .ThenByDescending(item => Path.GetFileName(item.directory), StringComparer.Ordinal)
                .Select(item => Path.GetFileName(item.directory))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>最近一个可回滚的操作（UI“回滚上一次恢复”入口）。</summary>
    public OfflineRollbackPoint? FindLatest()
    {
        foreach (var opId in ListRollbackPointIds())
        {
            var point = ReadRollbackPoint(opId);
            if (point is not null) return point;
        }

        return null;
    }

    /// <summary>按 opId 执行离线回滚（便捷重载）。</summary>
    public Task<OfflineRollbackResult> RollbackAsync(
        string opId,
        DshInstallation installation,
        CancellationToken cancellationToken = default) =>
        RollbackAsync(
            new OfflineRollbackRequest
            {
                Paths = _paths,
                Installation = installation,
                OpId = opId,
            },
            cancellationToken);

    /// <summary>离线回滚：恢复文件 → 离线 install → 再次校验；任一步失败给出人工入口。</summary>
    public async Task<OfflineRollbackResult> RollbackAsync(
        OfflineRollbackRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var opId = request.OpId ?? "";
        var installation = request.Installation ?? new DshInstallation();
        var result = new OfflineRollbackResult { OpId = opId, Outcome = OperationOutcome.Failed };

        var paths = request.Paths ?? _paths;
        if (paths is null || string.IsNullOrWhiteSpace(opId))
        {
            result.Failure = Failure(PackageRestoreFailureKind.Precondition, "离线回滚缺少 opId 或工具数据目录。");
            result.Messages.Add(result.Failure.Message);
            return result;
        }

        var journal = new JournalWriter(paths, opId);
        journal.Append(opId, OperationStage.Rollback, "start", "离线回滚请求（restore-backup\\" + opId + "）");

        if (string.IsNullOrWhiteSpace(installation.ProfileDir))
        {
            result.Failure = Failure(PackageRestoreFailureKind.Precondition, "未提供 profile 目录，离线回滚未执行。");
            result.Messages.Add(result.Failure.Message);
            journal.Append(opId, OperationStage.Failed, "failed", result.Failure.Message);
            return result;
        }

        // ---------------------------------------------------------- 读取并校验回滚点
        var backupDir = paths.RestoreBackupDirFor(opId);
        var point = RecoveryBackupStore.Read(backupDir);
        if (point is null)
        {
            result.Failure = Failure(
                PackageRestoreFailureKind.Precondition,
                "没有可用的回滚点（restore-backup\\" + opId + "\\package-restore.json 缺失或损坏）。");
            result.Messages.Add(result.Failure.Message);
            journal.Append(opId, OperationStage.Failed, "failed", result.Failure.Message);
            return result;
        }

        result.Point = point;
        result.BackupDir = backupDir;

        if (!RecoveryBackupStore.VerifyBackupFiles(point, out var backupError))
        {
            result.Failure = Failure(
                PackageRestoreFailureKind.BackupCorrupt,
                "回滚点的备份文件不可用：" + backupError,
                manualAction: "不要继续回滚；请从快照重新生成恢复计划，或人工检查 " + backupDir + "。");
            result.Messages.Add(result.Failure.Message);
            journal.Append(opId, OperationStage.Failed, "failed", result.Failure.Message);
            return result;
        }

        if (request.DryRun)
        {
            result.Outcome = OperationOutcome.DryRun;
            result.Messages.Add(
                $"演练：将用 {point.Files.Count} 条备份记录恢复文件" +
                (point.Scope == OfflineRollbackScope.FilesOnly ? "（仅文件级）" : "，并离线执行 install --offline --frozen-lockfile") +
                "；未写入任何文件。");
            journal.Append(opId, OperationStage.Done, "done", "dry-run: no files restored");
            return result;
        }

        // ---------------------------------------------------------- 1) 文件级恢复
        if (!RecoveryBackupStore.RestoreFiles(point, installation.ProfileDir, out var restored, out var restoreError))
        {
            result.Failure = Failure(
                PackageRestoreFailureKind.RollbackFailed,
                "恢复文件失败：" + restoreError,
                manualAction: "检查 profile 文件权限与 " + backupDir + " 的备份内容后人工恢复。");
            result.Messages.Add(result.Failure.Message);
            journal.Append(opId, OperationStage.Rollback, "failed", result.Failure.Message);
            result.Outcome = OperationOutcome.NeedsManual;
            return result;
        }

        result.RestoredFiles = restored;
        result.Messages.Add("[已恢复] 用 restore-backup 恢复 " + restored.Count + " 个文件（仅当前 op）。");
        journal.Append(opId, OperationStage.Rollback, "done",
            "文件级恢复完成：" + string.Join("，", restored.Select(Path.GetFileName)));

        // ---------------------------------------------------------- 2) 离线重装（只影响包级状态）
        if (point.Scope != OfflineRollbackScope.FilesOnly)
        {
            var timeout = TimeSpan.FromSeconds(request.InstallTimeoutSeconds > 0 ? request.InstallTimeoutSeconds : 600);
            CliCallResult call;
            try
            {
                call = await _cli.RunPluginAsync(
                        installation,
                        PackageRestorePlanner.OfflineInstallArgs.ToList(),
                        timeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result.Failure = Failure(PackageRestoreFailureKind.Cancelled, "离线回滚已取消（文件已恢复）。");
                result.Outcome = OperationOutcome.Cancelled;
                result.Messages.Insert(0, "离线回滚已取消：文件已恢复，但 node_modules 未重装。");
                journal.Append(opId, OperationStage.Failed, "cancelled", "cancelled after file restore");
                return result;
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "离线回滚安装异常", opId);
                call = new CliCallResult { ExitCode = -1, StdErr = exception.Message };
            }

            result.InstallExitCode = call.ExitCode;
            if (call.TimedOut || call.ExitCode != 0)
            {
                var failure = PackageRestorePlanner.ClassifyCliFailure(call, opId);
                failure.ManualAction = "离线回滚时 install --offline --frozen-lockfile 失败（未联网、未使用 file:<tgz>）。" +
                                       "人工入口：确认 pnpm store 缓存后重跑 dsh.cmd plugin --profile " +
                                       PackageRestorePlanner.ResolveProfileName(installation) +
                                       " install --offline --frozen-lockfile；必要时人工核对 " + backupDir + " 中的备份文件。";
                result.Failure = failure;
                result.Outcome = failure.NeedsManual ? OperationOutcome.NeedsManual : OperationOutcome.Failed;
                result.Messages.Insert(0, "离线回滚失败：" + failure.Message + "（文件已恢复，node_modules 可能不完整）");
                journal.Append(opId, OperationStage.Failed, "failed",
                    $"离线回滚安装失败（{failure.Kind}）：{failure.Message}");
                return result;
            }

            journal.Append(opId, OperationStage.Rollback, "done", "离线重装 exit=0");
        }

        // ---------------------------------------------------------- 3) 回滚后再次校验
        var problems = RecoveryVerification.VerifyProfile(
            installation.ProfileDir,
            point.Bundles,
            point.Dependencies,
            new Dictionary<string, string?>(StringComparer.Ordinal),
            point.LockfileSha256);

        if (problems.Count > 0)
        {
            result.Failure = Failure(
                PackageRestoreFailureKind.VerifyFailed,
                "回滚后校验失败：" + string.Join("；", problems),
                manualAction: "文件已按回滚点恢复；请检查 " + backupDir + " 或使用文件级还原（快照）恢复。");
            result.Messages.Insert(0, result.Failure.Message);
            result.Outcome = OperationOutcome.Failed;
            journal.Append(opId, OperationStage.Failed, "failed", result.Failure.Message);
            return result;
        }

        result.Verified = true;
        result.Outcome = OperationOutcome.Succeeded;
        result.Messages.Insert(0, "离线回滚完成：文件已恢复、node_modules 已按 lockfile 离线重装、校验通过。");
        journal.Append(opId, OperationStage.Done, "done", "离线回滚完成并校验通过");
        return result;
    }

    private static PackageRestoreFailure Failure(
        PackageRestoreFailureKind kind,
        string message,
        string? manualAction = null) => new()
    {
        Kind = kind,
        Message = message,
        ManualAction = manualAction,
        CanRetryWithNetwork = false,
    };
}
