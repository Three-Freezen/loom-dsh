using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Recovery;

/// <summary>
/// 包级恢复执行器（设计 12.5 / 12.6 / 9.4）。职责：
/// 1. 写前把 profile 核心文件备份到 <c>restore-backup\&lt;opId&gt;\</c> 并记录哈希与回滚点；
/// 2. 按 planner 的有序步骤原子写入 package.json / pnpm-lock.yaml；
/// 3. 用注入的 <see cref="IDshCli"/> 执行 <c>install --offline --frozen-lockfile</c>（默认绝不联网）；
/// 4. 成功后校验版本存在、bundles 齐全、lockfile 与快照一致；
/// 5. 任一步失败只回滚当前 op 的文件，并给人工入口。
/// 本类不结束任何 DSH 进程、不写安装目录 / app.asar；离线缺缓存时直接失败（不自动联网）。
/// </summary>
public sealed class PackageRestoreService
{
    /// <summary>包级恢复需要备份的 profile 核心文件（只备份本操作会写的文件）。</summary>
    private static readonly string[] BackupFileNames =
    {
        DshDefaults.ManifestFile,
        DshDefaults.LockfileFile,
    };

    private readonly IDshCli _cli;
    private readonly ILogger _logger;

    public PackageRestoreService(IDshCli cli, ILogger logger)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>按计划执行包级恢复（含备份、写入、离线安装、校验与失败回滚）。</summary>
    public async Task<PackageRestoreResult> RestoreAsync(
        PackageRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var installation = request.Installation ?? new DshInstallation();
        var plan = request.Plan ?? new PackageRestorePlan();
        var result = new PackageRestoreResult { Plan = plan, Outcome = OperationOutcome.Failed };
        var operationId = ResolveOperationId(request, plan);
        result.OperationId = operationId;

        if (request.Paths is null)
        {
            result.Failure = Failure(PackageRestoreFailureKind.Precondition, "未提供工具数据目录，无法写备份与 journal。");
            result.Messages.Add(result.Failure.Message);
            return result;
        }

        var journal = new JournalWriter(request.Paths, operationId);
        journal.Append(operationId, OperationStage.Start, "start",
            $"包级恢复开始：快照 {plan.SnapshotId}，步骤 {plan.Steps.Count} 项，profile {installation.ProfileDir}");

        if (string.IsNullOrWhiteSpace(installation.ProfileDir))
        {
            result.Failure = Failure(PackageRestoreFailureKind.Precondition, "未提供 profile 目录，包级恢复未执行。");
            result.Messages.Add(result.Failure.Message);
            journal.Append(operationId, OperationStage.Failed, "failed", result.Failure.Message);
            return result;
        }

        // ---------------------------------------------------------- 演练：不写任何文件、不调 CLI
        if (request.DryRun)
        {
            result.Outcome = OperationOutcome.DryRun;
            result.Failure = plan.IsBlocked ? plan.BlockingFailure : null;
            result.RequiresRestart = plan.HasWrites;
            result.Messages.Add($"演练：计划 {plan.Steps.Count(step => !step.Skip)} 个步骤" +
                                (plan.IsBlocked ? "，但前置条件不满足（" + plan.BlockingFailure?.Message + "）" : "") +
                                "；未写入任何文件、未执行安装。");
            journal.Append(operationId, OperationStage.Done, "done", "dry-run: no files written");
            return result;
        }

        // ---------------------------------------------------------- 前置校验：绝不自动结束 DSH 进程
        if (request.AppRunning && plan.RequiresFullExit)
        {
            result.Failure = Failure(
                PackageRestoreFailureKind.ProfileBusy,
                "DSH 仍在运行：包级恢复要求 App 完全退出，已停止且未写入任何文件（Loom 不会结束进程）。",
                manualAction: "请完全退出 DeepSeek Harness（含托盘/后台进程）后重试。");
            result.Messages.Add(result.Failure.Message);
            journal.Append(operationId, OperationStage.Failed, "failed", result.Failure.Message);
            return result;
        }

        if (plan.IsBlocked)
        {
            result.Failure = plan.BlockingFailure ?? Failure(
                PackageRestoreFailureKind.Precondition,
                "包级恢复计划的前置条件不满足，已停止且未写入任何文件。");
            result.Outcome = result.Failure.NeedsManual ? OperationOutcome.NeedsManual : OperationOutcome.Failed;
            result.Messages.Add("未写入任何文件：" + result.Failure.Message);
            if (!string.IsNullOrWhiteSpace(result.Failure.ManualAction))
            {
                result.Messages.Add("[人工入口] " + result.Failure.ManualAction);
            }

            journal.Append(operationId, OperationStage.Failed, "failed",
                "blocked: " + result.Failure.Kind + " " + result.Failure.Detail);
            return result;
        }

        OfflineRollbackPoint? point = null;
        try
        {
            // ------------------------------------------------------ 1) 备份 + 回滚点
            point = CreateRollbackPoint(request, plan, operationId, result);
            journal.Append(operationId, OperationStage.Backup, "done",
                $"备份 {result.BackupFiles.Count} 个文件到 {result.BackupDir}（含 sha256，回滚点 {point.Files.Count} 条）");

            // ------------------------------------------------------ 2) 顺序执行
            foreach (var step in plan.OrderedSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Skip)
                {
                    step.Status = PackageRestoreStepStatus.Skipped;
                    journal.Append(operationId, OperationStage.Apply, "skipped",
                        $"跳过 {step.Kind}：{step.SkipReason ?? "无需执行"}");
                    continue;
                }

                switch (step.Kind)
                {
                    case PackageRestoreStepKind.BackupProfile:
                        // 备份已在上方完成；这里只写回步骤状态。
                        step.Status = PackageRestoreStepStatus.Done;
                        break;

                    case PackageRestoreStepKind.WritePackageJson:
                    case PackageRestoreStepKind.WriteLockfile:
                        WriteFileStep(step, installation, result, journal, operationId);
                        break;

                    case PackageRestoreStepKind.InstallOffline:
                    case PackageRestoreStepKind.InstallOnline:
                        await RunInstallStepAsync(step, installation, request, point, result, journal, operationId, cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case PackageRestoreStepKind.Verify:
                        RunVerifyStep(step, plan, installation, journal, operationId);
                        break;

                    default:
                        step.Status = PackageRestoreStepStatus.Skipped;
                        journal.Append(operationId, OperationStage.Apply, "skipped", "未知步骤：" + step.Kind);
                        break;
                }
            }

            // ------------------------------------------------------ 3) 成功
            result.Verified = true;
            result.RequiresRestart = true;
            result.Outcome = OperationOutcome.Succeeded;
            result.Messages.Insert(0,
                "包级恢复完成：package.json / pnpm-lock.yaml 已恢复，" +
                "install --offline --frozen-lockfile 成功，版本与 bundles 校验通过；重启 DSH 后生效。");
            journal.Append(operationId, OperationStage.Done, "done",
                $"包级恢复完成：写 {result.WrittenFiles.Count} 个文件，备份在 {result.BackupDir}");
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.Log(LogLevel.Warn, "包级恢复已取消，回滚当前 op。", operationId);
            result.Failure = Failure(PackageRestoreFailureKind.Cancelled, "包级恢复已取消。");
            result.RolledBack = RollbackFiles(point, installation.ProfileDir, result, journal, operationId);
            result.Outcome = OperationOutcome.Cancelled;
            result.Messages.Insert(0, result.RolledBack
                ? "包级恢复已取消，已用 restore-backup 回滚本操作文件。"
                : "包级恢复已取消，但文件回滚未完成，请查看消息。");
            journal.Append(operationId, OperationStage.Failed, "cancelled", "cancelled; rollback " + result.RolledBack);
            return result;
        }
        catch (PackageRestoreStepException exception)
        {
            return await FailAsync(exception.Failure, point, installation, request, result, journal, operationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "包级恢复异常，回滚当前 op", operationId);
            var failure = Failure(
                PackageRestoreFailureKind.Unknown,
                "包级恢复异常：" + exception.Message,
                detail: exception.GetType().Name);
            return await FailAsync(failure, point, installation, request, result, journal, operationId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ steps

    private static void WriteFileStep(
        PackageRestoreStep step,
        DshInstallation installation,
        PackageRestoreResult result,
        JournalWriter journal,
        string operationId)
    {
        if (step.Content is null)
        {
            throw new PackageRestoreStepException(Failure(
                PackageRestoreFailureKind.SnapshotInputMissing,
                $"步骤 {step.Kind} 没有可写入内容（快照文件缺失）。"));
        }

        var target = Path.Combine(installation.ProfileDir, step.TargetFile);
        var before = Hashing.Sha256HexOrDefault(target);
        if (!string.IsNullOrEmpty(step.BeforeSha256)
            && !string.Equals(before, step.BeforeSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageRestoreStepException(Failure(
                PackageRestoreFailureKind.Precondition,
                $"profile\\{step.TargetFile} 在生成计划之后被修改，已停止写入；请重新生成计划后重试。",
                detail: $"before={ShortHash(before)} planned={ShortHash(step.BeforeSha256)}"));
        }

        var started = DateTimeOffset.UtcNow;
        AtomicFile.WriteAllText(target, step.Content);
        var after = Hashing.Sha256HexOrDefault(target);
        if (!string.IsNullOrEmpty(step.AfterSha256)
            && !string.Equals(after, step.AfterSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageRestoreStepException(Failure(
                PackageRestoreFailureKind.VerifyFailed,
                $"写入后哈希不一致：{step.TargetFile}",
                detail: $"expected={ShortHash(step.AfterSha256)} actual={ShortHash(after)}"));
        }

        step.Status = PackageRestoreStepStatus.Done;
        step.DurationSeconds = (DateTimeOffset.UtcNow - started).TotalSeconds;
        result.WrittenFiles.Add(target);
        journal.Append(operationId, OperationStage.Apply, "done",
            $"写入 {step.TargetFile}（sha256={ShortHash(after)}，备份在 restore-backup）");
    }

    private async Task RunInstallStepAsync(
        PackageRestoreStep step,
        DshInstallation installation,
        PackageRestoreRequest request,
        OfflineRollbackPoint point,
        PackageRestoreResult result,
        JournalWriter journal,
        string operationId,
        CancellationToken cancellationToken)
    {
        var timeout = ResolveTimeout(step, request);
        result.RequiresRestart = true;
        CliCallResult call;
        try
        {
            call = await _cli.RunPluginAsync(installation, step.Args.ToList(), timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PackageRestoreStepException(Failure(
                PackageRestoreFailureKind.Unknown,
                "调用 DSH CLI 失败：" + exception.Message,
                detail: step.DisplayCommand));
        }

        step.ExitCode = call.ExitCode;
        step.DurationSeconds = call.Duration.TotalSeconds;
        var ok = !call.TimedOut && call.ExitCode == 0;
        journal.Append(operationId, OperationStage.Apply, ok ? "done" : "failed",
            $"{step.DisplayCommand} exit={call.ExitCode} timedOut={call.TimedOut}");

        if (!ok)
        {
            var failure = PackageRestorePlanner.ClassifyCliFailure(call, operationId);
            failure.Detail = string.IsNullOrWhiteSpace(failure.Detail) ? step.DisplayCommand : failure.Detail;
            step.Status = PackageRestoreStepStatus.Failed;
            step.Error = failure.Message;
            throw new PackageRestoreStepException(failure);
        }

        step.Status = PackageRestoreStepStatus.Done;
        point.InstallExitCode = call.ExitCode;
    }

    private static void RunVerifyStep(
        PackageRestoreStep step,
        PackageRestorePlan plan,
        DshInstallation installation,
        JournalWriter journal,
        string operationId)
    {
        var expectedLockfileSha = plan.Find(PackageRestoreStepKind.WriteLockfile)?.AfterSha256;
        var problems = RecoveryVerification.VerifyProfile(
            installation.ProfileDir,
            plan.ExpectedBundles,
            plan.ExpectedDependencies,
            plan.ExpectedDependencyVersions,
            expectedLockfileSha);

        if (problems.Count > 0)
        {
            step.Status = PackageRestoreStepStatus.Failed;
            step.Error = string.Join("；", problems);
            throw new PackageRestoreStepException(Failure(
                PackageRestoreFailureKind.VerifyFailed,
                "安装后校验失败：" + string.Join("；", problems),
                detail: "版本存在 / bundles 齐全 / lockfile 一致 三项校验未全部通过。",
                manualAction: "可回滚本操作：loom-dsh recovery rollback " + operationId + "。"));
        }

        step.Status = PackageRestoreStepStatus.Done;
        journal.Append(operationId, OperationStage.Verify, "done",
            $"校验通过：{plan.ExpectedBundles.Count} 个 bundle、{plan.ExpectedDependencies.Count} 个依赖、lockfile 一致");
    }

    // ------------------------------------------------------------------ failure / rollback

    private async Task<PackageRestoreResult> FailAsync(
        PackageRestoreFailure failure,
        OfflineRollbackPoint? point,
        DshInstallation installation,
        PackageRestoreRequest request,
        PackageRestoreResult result,
        JournalWriter journal,
        string operationId,
        CancellationToken cancellationToken)
    {
        result.Failure = failure;
        result.RolledBack = RollbackFiles(point, installation.ProfileDir, result, journal, operationId);

        var repairAttempted = false;
        var repairOk = false;
        if (result.RolledBack && ShouldAttemptRepair(failure))
        {
            repairAttempted = true;
            (repairOk, var repairExit) = await TryRepairOfflineAsync(
                    installation, request, journal, operationId, cancellationToken)
                .ConfigureAwait(false);
            result.Messages.Add(repairOk
                ? "[已修复] 回滚后重新执行 install --offline --frozen-lockfile 成功。"
                : $"[待人工] 回滚后离线修复安装未成功（exit={repairExit}），node_modules 可能与恢复前不一致。");
        }

        var needsManual = failure.NeedsManual || !result.RolledBack || (repairAttempted && !repairOk);
        result.Outcome = needsManual ? OperationOutcome.NeedsManual : OperationOutcome.Failed;
        result.Messages.Insert(0, "包级恢复失败：" + failure.Message +
                                   (result.RolledBack ? "（已用 restore-backup 回滚本操作，仅影响当前 op）" : "（回滚未完成，请查看消息）"));
        if (!string.IsNullOrWhiteSpace(failure.ManualAction)) result.Messages.Add("[人工入口] " + failure.ManualAction);
        journal.Append(operationId, OperationStage.Failed, "failed",
            $"失败分类 {failure.Kind}：{failure.Message}；rolledBack={result.RolledBack} repairOk={(repairAttempted ? repairOk : (bool?)null)}");
        return result;
    }

    /// <summary>离线缺缓存/权限/磁盘等场景重跑离线 install 没有意义，直接给人工入口。</summary>
    private static bool ShouldAttemptRepair(PackageRestoreFailure failure) => failure.Kind is not (
        PackageRestoreFailureKind.OfflineCacheMissing
        or PackageRestoreFailureKind.Permission
        or PackageRestoreFailureKind.Disk
        or PackageRestoreFailureKind.CliMissing
        or PackageRestoreFailureKind.SnapshotInputMissing
        or PackageRestoreFailureKind.UnsupportedSpec
        or PackageRestoreFailureKind.Precondition);

    private async Task<(bool Ok, int ExitCode)> TryRepairOfflineAsync(
        DshInstallation installation,
        PackageRestoreRequest request,
        JournalWriter journal,
        string operationId,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(request.InstallTimeoutSeconds > 0 ? request.InstallTimeoutSeconds : 600);
        try
        {
            journal.Append(operationId, OperationStage.Rollback, "start", "离线修复安装（不联网）");
            var call = await _cli.RunPluginAsync(
                    installation,
                    PackageRestorePlanner.OfflineInstallArgs.ToList(),
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
            var ok = !call.TimedOut && call.ExitCode == 0;
            journal.Append(operationId, OperationStage.Rollback, ok ? "done" : "failed",
                $"离线修复安装 exit={call.ExitCode}");
            return (ok, call.ExitCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "回滚后的离线修复安装失败", operationId);
            journal.Append(operationId, OperationStage.Rollback, "failed", "离线修复安装异常：" + exception.Message);
            return (false, -1);
        }
    }

    private bool RollbackFiles(
        OfflineRollbackPoint? point,
        string profileDir,
        PackageRestoreResult result,
        JournalWriter journal,
        string operationId)
    {
        if (point is null)
        {
            journal.Append(operationId, OperationStage.Rollback, "failed", "没有回滚点，无法回滚");
            return false;
        }

        journal.Append(operationId, OperationStage.Rollback, "start",
            "回滚当前 op 的 profile 文件（restore-backup\\" + LoomPaths.SanitizeFileName(operationId) + "）");
        if (!RecoveryBackupStore.RestoreFiles(point, profileDir, out var restored, out var error))
        {
            _logger.Log(LogLevel.Error, "回滚失败：" + error, operationId);
            result.Messages.Add("[回滚失败] " + error);
            journal.Append(operationId, OperationStage.Rollback, "failed", "回滚失败：" + error);
            return false;
        }

        result.Messages.Add("[已回滚] 用 restore-backup 恢复 " + restored.Count + " 个文件（只影响当前 op）。");
        journal.Append(operationId, OperationStage.Rollback, "done",
            "回滚完成：" + string.Join("，", restored.Select(Path.GetFileName)));
        return true;
    }

    // ------------------------------------------------------------------ backup point

    private static OfflineRollbackPoint CreateRollbackPoint(
        PackageRestoreRequest request,
        PackageRestorePlan plan,
        string operationId,
        PackageRestoreResult result)
    {
        var profileDir = request.Installation.ProfileDir;
        var backupDir = request.Paths.RestoreBackupDirFor(operationId);
        Directory.CreateDirectory(backupDir);

        var point = new OfflineRollbackPoint
        {
            OpId = operationId,
            CreatedAt = DateTimeOffset.Now,
            ProfileDir = profileDir,
            SnapshotId = plan.SnapshotId,
            SnapshotDir = plan.SnapshotDir,
            Scope = OfflineRollbackScope.FilesAndPackages,
            BackupDir = backupDir,
            Bundles = plan.PreviousBundles.ToList(),
            Dependencies = plan.PreviousDependencies.ToList(),
            LockfileSha256 = plan.PreviousLockfileSha256,
        };

        foreach (var name in BackupFileNames)
        {
            var source = Path.Combine(profileDir, name);
            var record = new PackageRestoreFileBackup { Name = name };
            if (File.Exists(source))
            {
                var destination = Path.Combine(backupDir, name);
                File.Copy(source, destination, overwrite: true);
                record.Existed = true;
                record.Sha256 = Hashing.Sha256Hex(source);
                record.Size = new FileInfo(source).Length;
                result.BackupFiles.Add(destination);
            }

            point.Files.Add(record);
        }

        var pointPath = RecoveryBackupStore.PointPath(backupDir);
        AtomicFile.WriteJson(pointPath, point);
        result.BackupDir = backupDir;
        result.RollbackPointPath = pointPath;
        return point;
    }

    // ------------------------------------------------------------------ helpers

    private static string ResolveOperationId(PackageRestoreRequest request, PackageRestorePlan plan)
    {
        if (!string.IsNullOrWhiteSpace(request.OperationId)) return request.OperationId;
        if (!string.IsNullOrWhiteSpace(plan.OperationId)) return plan.OperationId;
        return LoomPaths.NewOperationId("recovery");
    }

    private static TimeSpan ResolveTimeout(PackageRestoreStep step, PackageRestoreRequest request)
    {
        var seconds = step.TimeoutSeconds > 0
            ? step.TimeoutSeconds
            : request.InstallTimeoutSeconds > 0
                ? request.InstallTimeoutSeconds
                : 600;
        return TimeSpan.FromSeconds(seconds);
    }

    private static PackageRestoreFailure Failure(
        PackageRestoreFailureKind kind,
        string message,
        string? detail = null,
        string? manualAction = null) => new()
    {
        Kind = kind,
        Message = message,
        Detail = detail,
        ManualAction = manualAction ?? (kind is PackageRestoreFailureKind.Precondition
            or PackageRestoreFailureKind.ProfileBusy
            or PackageRestoreFailureKind.SnapshotInputMissing
            ? "请修正前置条件后重新生成包级恢复计划。"
            : null),
    };

    private static string ShortHash(string? sha) =>
        string.IsNullOrEmpty(sha) ? "（无）" : sha.Length > 16 ? sha[..16] + "…" : sha;

    /// <summary>步骤级失败：把失败分类从执行器内部抛到统一的回滚路径。</summary>
    private sealed class PackageRestoreStepException : Exception
    {
        public PackageRestoreStepException(PackageRestoreFailure failure) : base(failure.Message)
        {
            Failure = failure;
        }

        public PackageRestoreFailure Failure { get; }
    }
}

/// <summary>
/// restore-backup\&lt;opId&gt;\ 的读写（执行器与离线回滚服务共用）。
/// 只触碰工具数据目录与 profile 的 package.json / pnpm-lock.yaml，绝不触碰安装目录。
/// </summary>
internal static class RecoveryBackupStore
{
    /// <summary>回滚点文件名（与 RestoreService 的 backup.json 区分）。</summary>
    internal const string PointFileName = "package-restore.json";

    internal static string PointPath(string backupDir) => Path.Combine(backupDir, PointFileName);

    /// <summary>读取回滚点；缺失或损坏返回 null（调用方按“不可回滚”处理）。</summary>
    internal static OfflineRollbackPoint? Read(string backupDir)
    {
        try
        {
            var path = PointPath(backupDir);
            if (!File.Exists(path)) return null;
            return AtomicFile.ReadJson<OfflineRollbackPoint>(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>校验备份文件存在且哈希与回滚点一致（防篡改/防半写）。</summary>
    internal static bool VerifyBackupFiles(OfflineRollbackPoint point, out string? error)
    {
        foreach (var record in point.Files.Where(record => record.Existed))
        {
            var path = Path.Combine(point.BackupDir, record.Name);
            if (!File.Exists(path))
            {
                error = "备份文件缺失：" + record.Name;
                return false;
            }

            var actual = Hashing.Sha256HexOrDefault(path);
            if (!string.IsNullOrEmpty(record.Sha256)
                && !string.Equals(actual, record.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "备份哈希不符（疑似被修改）：" + record.Name;
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// 按回滚点把文件恢复到操作前状态：Existed=true 复制回备份内容，Existed=false 删除目标。
    /// 返回 false 时 <paramref name="error"/> 说明失败文件；已恢复的文件保持恢复结果。
    /// </summary>
    internal static bool RestoreFiles(
        OfflineRollbackPoint point,
        string profileDir,
        out List<string> restored,
        out string? error)
    {
        restored = new List<string>();
        error = null;
        foreach (var record in point.Files)
        {
            var target = Path.Combine(profileDir, record.Name);
            try
            {
                if (record.Existed)
                {
                    var backup = Path.Combine(point.BackupDir, record.Name);
                    if (!File.Exists(backup))
                    {
                        error = "备份文件缺失：" + record.Name;
                        return false;
                    }

                    AtomicFile.WriteAllBytes(target, File.ReadAllBytes(backup));
                    restored.Add(target);
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                    restored.Add(target);
                }
            }
            catch (Exception exception)
            {
                error = record.Name + "：" + exception.Message;
                return false;
            }
        }

        return true;
    }
}

/// <summary>包级恢复 / 离线回滚共用的校验（纯文件读取，不执行 CLI）。</summary>
internal static class RecoveryVerification
{
    /// <summary>
    /// 校验 profile 状态：package.json 可解析、bundles 齐全、依赖 spec 存在、node_modules 版本存在、
    /// lockfile 与期望哈希一致。返回问题列表（空 = 通过）。
    /// </summary>
    internal static List<string> VerifyProfile(
        string profileDir,
        IReadOnlyList<string> expectedBundles,
        IReadOnlyList<string> expectedDependencies,
        IReadOnlyDictionary<string, string?> expectedVersions,
        string? expectedLockfileSha)
    {
        var problems = new List<string>();
        var manifestPath = Path.Combine(profileDir, DshDefaults.ManifestFile);
        if (!File.Exists(manifestPath))
        {
            problems.Add("package.json 缺失");
            return problems;
        }

        var root = ReadJsonObject(manifestPath);
        if (root is null)
        {
            problems.Add("package.json 不可解析");
            return problems;
        }

        var bundles = ReadBundles(root);
        foreach (var name in expectedBundles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!bundles.Contains(name, StringComparer.OrdinalIgnoreCase)) problems.Add("bundles 缺少 " + name);
        }

        var dependencies = root["dependencies"] as JsonObject;
        foreach (var name in expectedDependencies.Distinct(StringComparer.Ordinal))
        {
            if (dependencies is null || !dependencies.ContainsKey(name))
            {
                problems.Add("dependencies 缺少 " + name);
            }

            var packageDir = Path.Combine(profileDir, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
            var packageManifest = Path.Combine(packageDir, "package.json");
            if (!File.Exists(packageManifest))
            {
                problems.Add("node_modules 缺少 " + name + "（安装未完成）");
                continue;
            }

            if (expectedVersions.TryGetValue(name, out var expectedVersion)
                && !string.IsNullOrEmpty(expectedVersion))
            {
                var installedVersion = ReadManifestVersion(packageManifest);
                if (!string.IsNullOrEmpty(installedVersion)
                    && !string.Equals(installedVersion, expectedVersion, StringComparison.Ordinal))
                {
                    problems.Add($"版本不匹配 {name}：期望 {expectedVersion}，实际 {installedVersion}");
                }
            }
        }

        if (!string.IsNullOrEmpty(expectedLockfileSha))
        {
            var lockPath = Path.Combine(profileDir, DshDefaults.LockfileFile);
            var actual = Hashing.Sha256HexOrDefault(lockPath);
            if (!string.Equals(actual, expectedLockfileSha, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("pnpm-lock.yaml 与期望哈希不一致");
            }
        }

        return problems;
    }

    private static JsonObject? ReadJsonObject(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static List<string> ReadBundles(JsonObject root)
    {
        var bundles = new List<string>();
        if (root["dsh"] is not JsonObject dsh) return bundles;
        if (dsh["profile"] is not JsonObject profile) return bundles;
        if (profile["bundles"] is not JsonArray array) return bundles;
        foreach (var node in array)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                bundles.Add(text);
            }
        }

        return bundles;
    }

    private static string? ReadManifestVersion(string manifestPath)
    {
        var root = ReadJsonObject(manifestPath);
        return root?["version"] is JsonValue value && value.TryGetValue<string>(out var version) ? version : null;
    }
}
