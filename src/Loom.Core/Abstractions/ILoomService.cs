using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Recovery;
using Loom.Core.Update;
using Loom.Core.Upgrade;

namespace Loom.Core.Abstractions;

/// <summary>Facade used by both the CLI and the tray UI.</summary>
public interface ILoomService
{
    LoomPaths Paths { get; }
    LoomConfig Config { get; }

    /// <summary>In-app update flow (GitHub Releases probe / download / silent upgrade).</summary>
    UpdateService Updates { get; }

    /// <summary>策略 A（插件自动升级到兼容版本）执行器；只读面，真正的入口是 <see cref="UpgradePluginAsync"/>。</summary>
    UpgradeService Upgrades { get; }

    LoomStatus GetStatus();

    event EventHandler<LoomStatus>? StatusChanged;
    event EventHandler<LoomNotification>? NotificationRaised;

    Task RefreshStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs the periodic maintenance logic (update detection, version change, auto snapshot).</summary>
    Task TickAsync(CancellationToken cancellationToken = default);

    Task<DshInstallation?> LocateAsync(CancellationToken cancellationToken = default);

    Task<SnapshotOutcome> TakeSnapshotAsync(
        SnapshotRequest? request = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<CheckOutcome> RunCheckAsync(
        CheckRequest? request = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RestorePlan?> PreviewRestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- 二期：插件升级 / 包级恢复

    /// <summary>
    /// 策略 A：查询 registry 并升级单个插件到与目标 dshVersion 兼容的版本（写前备份 + journal + 审计 + 失败回滚）。
    /// 调用方只需给包名/已装版本/目标运行时；安装目录、数据目录、配置与超时由服务补齐。
    /// 服务不结束 DSH 进程：App 未退出时返回 <see cref="OperationOutcome.NeedsManual"/>。
    /// </summary>
    Task<UpgradeExecutionResult> UpgradePluginAsync(
        UpgradeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>同 <see cref="UpgradePluginAsync(UpgradeRequest, CancellationToken)"/>，额外汇报阶段进度。</summary>
    Task<UpgradeExecutionResult> UpgradePluginAsync(
        UpgradeRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>只生成包级恢复计划（纯计算 + 读取快照文本），不写任何文件、不调用 CLI。</summary>
    Task<PackageRestorePlan> PreviewPackageRestoreAsync(
        PackageRestoreRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>执行包级恢复（备份 → 写两个核心文件 → 离线 install → 校验；失败只回滚本 op）。</summary>
    Task<PackageRestoreResult> PackageRestoreAsync(
        PackageRestoreRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>同 <see cref="PackageRestoreAsync(PackageRestoreRequest, CancellationToken)"/>，额外汇报阶段进度。</summary>
    Task<PackageRestoreResult> PackageRestoreAsync(
        PackageRestoreRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>该 opId 是否带回滚点且备份哈希校验通过（UI“回滚”按钮启用依据）。</summary>
    bool CanRollbackPackageOp(string opId);

    /// <summary>按 opId 离线完整回滚（恢复文件 + <c>install --offline --frozen-lockfile</c> + 再次校验）。</summary>
    Task<OfflineRollbackResult> RollbackPackageOpAsync(
        string opId,
        CancellationToken cancellationToken = default);

    Task<ReportView?> GetLatestReportAsync(CancellationToken cancellationToken = default);
    Task<ReportView?> GetReportAsync(string path, CancellationToken cancellationToken = default);
    Task<DoctorResult> RunDoctorAsync(CancellationToken cancellationToken = default);
    Task SaveConfigAsync(LoomConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// 配置的锁内“读最新 → 改 → 保存”入口（P2-4）：后台字段写回必须走这里，
    /// 不要拿整份旧 config 直接保存（会覆盖用户刚保存的设置）。
    /// </summary>
    void MutateConfig(Action<LoomConfig> mutate);

    Task SetAutostartAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<CheckResult?> GetLastCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>True when another Loom operation is running.</summary>
    bool IsBusy { get; }

    /// <summary>Cooperative cancellation of the running operation (safe points only).</summary>
    void CancelCurrentOperation();
}

public sealed class SnapshotRequest
{
    public SnapshotTrigger Trigger { get; set; } = SnapshotTrigger.Manual;
    public bool Full { get; set; }

    /// <summary>Overwrite the frozen pre-update pointer even when a snapshot already exists.</summary>
    public bool Force { get; set; }
}

public sealed class CheckRequest
{
    public bool Auto { get; set; }
}

public sealed class RestoreRequest
{
    public bool DryRun { get; set; }
    public bool Force { get; set; }
    public bool Auto { get; set; }
    public string? SnapshotId { get; set; }
    public EvidenceLevel? EvidenceOverride { get; set; }

    /// <summary>When set, only these bundles may be re-added (automatic restore uses check candidates).</summary>
    public IReadOnlyList<string>? AllowedBundles { get; set; }

    /// <summary>When set, only these dependencies may be re-added (automatic restore uses check candidates).</summary>
    public IReadOnlyList<string>? AllowedDependencies { get; set; }

    /// <summary>When false, patch changes are reported but not applied (automatic restore).</summary>
    public bool? AllowPatchRestore { get; set; }

    /// <summary>When false, compatibility exemptions are not merged (automatic restore).</summary>
    public bool? AllowCompatibilityRestore { get; set; }
}
