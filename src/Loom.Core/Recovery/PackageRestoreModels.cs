using System.Text.Json.Serialization;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Recovery;

/// <summary>
/// 二期“包级恢复”的步骤类型。顺序固定对应设计 12.5：
/// 备份 → 恢复 package.json + pnpm-lock.yaml → install --offline --frozen-lockfile →（可选）联网重试 → 校验。
/// 本类型只描述“要做什么”，不包含任何进程/IO 行为。
/// </summary>
public enum PackageRestoreStepKind
{
    /// <summary>写前备份：把 profile 核心文件复制到 restore-backup\&lt;opId&gt;\ 并记录哈希（设计 9.4）。</summary>
    BackupProfile,

    /// <summary>写入快照的 package.json（保留用户新增的顶层字段）。</summary>
    WritePackageJson,

    /// <summary>写入快照的 pnpm-lock.yaml（保证 install --frozen-lockfile 可复现）。</summary>
    WriteLockfile,

    /// <summary>离线安装：dsh plugin install --offline --frozen-lockfile（默认路径，需要 store 有缓存）。</summary>
    InstallOffline,

    /// <summary>联网安装：仅在用户明确同意后由 planner 追加（设计 12.5 第 3 步）。</summary>
    InstallOnline,

    /// <summary>安装后校验：版本存在、bundles 齐全、lockfile 与快照一致。</summary>
    Verify,
}

/// <summary>步骤执行状态（写回计划，用于报告与测试断言）。</summary>
public enum PackageRestoreStepStatus
{
    Pending,
    Done,
    Skipped,
    Failed,
}

/// <summary>
/// 包级恢复的单个有序步骤。命令一律以参数数组（<see cref="Args"/>）表达，
/// 绝不拼 shell 字符串；<see cref="Content"/> 只存在于内存，不进入报告。
/// </summary>
public sealed class PackageRestoreStep
{
    /// <summary>从 1 开始的执行顺序；执行器按此排序，不依赖列表顺序。</summary>
    public int Order { get; set; }

    public PackageRestoreStepKind Kind { get; set; }

    /// <summary>面向用户的中文说明。</summary>
    public string Description { get; set; } = "";

    /// <summary>目标文件（相对 profile 的文件名）；安装/校验步骤为空。</summary>
    public string TargetFile { get; set; } = "";

    /// <summary>传给 <see cref="IDshCli.RunPluginAsync"/> 的参数（不含 plugin --profile &lt;name&gt; 前缀）。</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>用于报告/日志的完整命令展示串（只读展示，真正执行仍用参数数组）。</summary>
    public string? DisplayCommand { get; set; }

    /// <summary>写入前的哈希（文件步骤）；用于检测计划过期与生成回滚点。</summary>
    public string? BeforeSha256 { get; set; }

    /// <summary>写入后的期望哈希（文件步骤）；执行器写完必须复核一致。</summary>
    public string? AfterSha256 { get; set; }

    /// <summary>待写入内容（仅内存；planner 纯函数产出，执行器原子写入）。</summary>
    [JsonIgnore]
    public string? Content { get; set; }

    /// <summary>true = 该步骤无需执行（文件已经一致等）。</summary>
    public bool Skip { get; set; }

    /// <summary>跳过原因（中文）。</summary>
    public string? SkipReason { get; set; }

    /// <summary>true = 执行该步骤前必须完全退出 DSH（安装类步骤恒为 true）。</summary>
    public bool RequiresFullExit { get; set; }

    /// <summary>CLI 超时（秒）；0 表示使用执行器默认值。</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>实际退出码（仅安装步骤，执行后写回）。</summary>
    public int? ExitCode { get; set; }

    public PackageRestoreStepStatus Status { get; set; } = PackageRestoreStepStatus.Pending;

    /// <summary>耗时（秒），执行后写回。</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>该步骤的失败原因（中文）。</summary>
    public string? Error { get; set; }
}

/// <summary>前置条件代码（planner 只根据输入判断，不做 IO）。</summary>
public enum PackageRestorePreconditionCode
{
    /// <summary>profile 目录已知且可定位。</summary>
    ProfileReadable,

    /// <summary>快照中存在 profile\package.json。</summary>
    SnapshotPackageJson,

    /// <summary>快照中存在 profile\pnpm-lock.yaml（--frozen-lockfile 的前提）。</summary>
    SnapshotLockfile,

    /// <summary>能定位 DSH CLI / 安装目录。</summary>
    CliAvailable,

    /// <summary>快照依赖里不允许残留 file: 归档 spec（设计 12.5 明确禁止）。</summary>
    NoFileSpecs,

    /// <summary>DSH 必须完全退出（planner 不探测进程，只看调用方传入的事实）。</summary>
    DshFullyExited,

    /// <summary>没有并发的 profile 写锁 / 插件管理器写操作。</summary>
    ProfileNotLocked,

    /// <summary>pnpm store 已缓存 lockfile 所需包（离线安装的前提）。</summary>
    OfflineCache,

    /// <summary>磁盘空间足够（调用方提供可用字节数时才校验）。</summary>
    DiskSpace,
}

/// <summary>一条前置条件的判定结果。</summary>
public sealed class PackageRestorePrecondition
{
    public PackageRestorePreconditionCode Code { get; set; }

    public bool Satisfied { get; set; }

    /// <summary>判定依据（中文，含实际值）。</summary>
    public string Detail { get; set; } = "";

    /// <summary>不满足时给用户的人工入口（命令或操作说明）。</summary>
    public string? ManualAction { get; set; }
}

/// <summary>包级恢复的失败分类（设计 11.6 / 12.6）。</summary>
public enum PackageRestoreFailureKind
{
    None,

    /// <summary>前置条件不满足（计划未生成或不可执行）。</summary>
    Precondition,

    /// <summary>DSH / 插件管理器仍在运行或 profile 被锁：绝不自动结束进程。</summary>
    ProfileBusy,

    /// <summary>快照缺少 package.json / pnpm-lock.yaml。</summary>
    SnapshotInputMissing,

    /// <summary>依赖 spec 为 file:/link: 归档：二期拒绝自动恢复（不允许残留 file:）。</summary>
    UnsupportedSpec,

    /// <summary>找不到 DSH CLI。</summary>
    CliMissing,

    /// <summary>离线缓存缺包（pnpm store 没有 lockfile 所需 tarball）。</summary>
    OfflineCacheMissing,

    Network,
    Auth,
    NotFound,
    PeerConflict,
    Disk,
    Permission,

    /// <summary>CLI 超时（超时后只终止 CLI 进程树，不碰 DSH）。</summary>
    Timeout,

    /// <summary>安装失败（未被更具体分类覆盖）。</summary>
    InstallFailed,

    /// <summary>安装后校验失败（版本/bundles/lockfile 不一致）。</summary>
    VerifyFailed,

    /// <summary>备份阶段失败。</summary>
    BackupFailed,

    /// <summary>回滚点的备份文件缺失或哈希不符（疑似被篡改/损坏）。</summary>
    BackupCorrupt,

    /// <summary>回滚阶段失败。</summary>
    RollbackFailed,

    /// <summary>首启失败（升级/还原后按判据判定）。</summary>
    FirstStartFailed,

    Cancelled,
    Unknown,
}

/// <summary>失败详情 + 人工入口。执行器与 planner 共用同一分类结构。</summary>
public sealed class PackageRestoreFailure
{
    public PackageRestoreFailureKind Kind { get; set; }

    /// <summary>一行中文摘要。</summary>
    public string Message { get; set; } = "";

    /// <summary>补充细节（命令、路径、诊断）。</summary>
    public string? Detail { get; set; }

    /// <summary>人工入口：可照抄的命令或操作步骤。</summary>
    public string? ManualAction { get; set; }

    public int? ExitCode { get; set; }

    public bool TimedOut { get; set; }

    /// <summary>CLI 输出尾部（已由 logger 脱敏；仅供报告）。</summary>
    public string? OutputTail { get; set; }

    /// <summary>true = 存在“用户同意后联网重试”的可行路径（本版绝不自动联网）。</summary>
    public bool CanRetryWithNetwork { get; set; }

    /// <summary>true = 必须人工处理（离线缺缓存、权限、磁盘等），不自动联网。</summary>
    [JsonIgnore]
    public bool NeedsManual => Kind is PackageRestoreFailureKind.OfflineCacheMissing
        or PackageRestoreFailureKind.Permission
        or PackageRestoreFailureKind.Disk
        or PackageRestoreFailureKind.BackupCorrupt
        or PackageRestoreFailureKind.RollbackFailed
        or PackageRestoreFailureKind.SnapshotInputMissing
        or PackageRestoreFailureKind.UnsupportedSpec
        or PackageRestoreFailureKind.CliMissing;
}

/// <summary>
/// 包级恢复计划：有序步骤 + 是否需要 DSH 完全退出 + 前置条件 + 校验期望值。
/// 由 <see cref="PackageRestorePlanner.Create"/> 纯函数产出；执行器只按计划动作。
/// </summary>
public sealed class PackageRestorePlan
{
    public string OperationId { get; set; } = "";

    public string SnapshotId { get; set; } = "";

    public string SnapshotDir { get; set; } = "";

    public string ProfileDir { get; set; } = "";

    public List<PackageRestoreStep> Steps { get; set; } = new();

    public List<PackageRestorePrecondition> Preconditions { get; set; } = new();

    /// <summary>true = 整个流程要求 DSH 完全退出（包级恢复恒为 true）。</summary>
    public bool RequiresFullExit { get; set; }

    /// <summary>true = 计划中不含联网安装步骤（离线优先/离线强制）。</summary>
    public bool OfflineOnly { get; set; } = true;

    /// <summary>true = 前置条件不满足，执行器必须直接失败且不写任何文件。</summary>
    public bool IsBlocked { get; set; }

    /// <summary>阻塞原因（首个不满足的前置条件）。</summary>
    public PackageRestoreFailure? BlockingFailure { get; set; }

    /// <summary>计划生成过程中的说明（保留字段、跳过原因、联网开关等）。</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>恢复后必须出现在 dsh.profile.bundles 中的 bundle。</summary>
    public List<string> ExpectedBundles { get; set; } = new();

    /// <summary>恢复后必须出现在 dependencies 中的包名。</summary>
    public List<string> ExpectedDependencies { get; set; } = new();

    /// <summary>期望版本（name → lockfile 解析出的版本；未知为 null）。</summary>
    public Dictionary<string, string?> ExpectedDependencyVersions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>恢复前的 bundles（写入回滚点，用于回滚后校验）。</summary>
    public List<string> PreviousBundles { get; set; } = new();

    /// <summary>恢复前的依赖包名（写入回滚点）。</summary>
    public List<string> PreviousDependencies { get; set; } = new();

    public string? PreviousPackageJsonSha256 { get; set; }

    public string? PreviousLockfileSha256 { get; set; }

    public string Summary { get; set; } = "";

    /// <summary>true = 计划包含真正的文件写入（已一致时全部跳过）。</summary>
    [JsonIgnore]
    public bool HasWrites => Steps.Any(step => !step.Skip
        && step.Kind is PackageRestoreStepKind.WritePackageJson or PackageRestoreStepKind.WriteLockfile);

    /// <summary>按 Order 排序后的步骤（执行器必须用它，避免依赖列表顺序）。</summary>
    [JsonIgnore]
    public IReadOnlyList<PackageRestoreStep> OrderedSteps => Steps.OrderBy(step => step.Order).ToList();

    /// <summary>按类型查找第一个步骤（测试/集成方使用）。</summary>
    public PackageRestoreStep? Find(PackageRestoreStepKind kind) =>
        Steps.FirstOrDefault(step => step.Kind == kind);
}

/// <summary>
/// planner 的运行时开关。所有“事实”都由调用方探测后传入，planner 自己不读进程、不碰文件。
/// </summary>
public sealed class PackageRestoreOptions
{
    /// <summary>调用方（进程探针/锁管理器）观察到的 DSH 运行状态。</summary>
    public bool AppRunning { get; set; }

    /// <summary>profile\lock、package.json.lock、.plugin-manager\run.json 是否仍占用。</summary>
    public bool ProfileLocked { get; set; }

    /// <summary>
    /// pnpm store 是否已缓存 lockfile 所需包。默认 true（调用方未预检时保持乐观），
    /// 真实缺失由 CLI 输出在失败分类里兜底。
    /// </summary>
    public bool OfflineCacheAvailable { get; set; } = true;

    /// <summary>用户是否已明确同意离线失败后联网重试（设计 12.5 第 3 步）；默认 false。</summary>
    public bool AllowNetworkFallback { get; set; }

    /// <summary>首启 ready 判据的等待秒数 N（默认 20s，与配置 HealthCheckSeconds 对齐）。</summary>
    public int ReadyTimeoutSeconds { get; set; } = 20;

    /// <summary>单次 CLI 安装超时秒数。</summary>
    public int InstallTimeoutSeconds { get; set; } = 600;

    /// <summary>可用磁盘字节数（调用方探测；null = 不校验）。</summary>
    public long? AvailableBytes { get; set; }

    /// <summary>预计需要的磁盘字节数（0 = 不校验）。</summary>
    public long EstimatedBytes { get; set; }

    /// <summary>true = 即使文件已经一致也重新写入（人工强制重试）。</summary>
    public bool Force { get; set; }
}

/// <summary>
/// planner 的纯输入：快照元数据 + 快照文件文本 + 当前 profile 状态 + 安装信息 + 运行时开关。
/// 文件由调用方读取后传文本，planner 本身不做 IO（便于全分支单测）。
/// </summary>
public sealed class PackageRestorePlanInput
{
    public SnapshotManifest Snapshot { get; set; } = new();

    public string SnapshotDir { get; set; } = "";

    /// <summary>快照 profile\package.json 的文本内容（缺失为 null/空）。</summary>
    public string? SnapshotPackageJson { get; set; }

    /// <summary>快照 profile\pnpm-lock.yaml 的文本内容（缺失为 null/空）。</summary>
    public string? SnapshotLockfile { get; set; }

    public ProfileState Current { get; set; } = new();

    public DshInstallation Installation { get; set; } = new();

    public LoomConfig Config { get; set; } = new();

    public PackageRestoreOptions Options { get; set; } = new();

    public string OperationId { get; set; } = "";
}

/// <summary>执行器请求：安装信息 + 数据目录 + 已生成的计划。</summary>
public sealed class PackageRestoreRequest
{
    public DshInstallation Installation { get; set; } = new();

    public LoomPaths Paths { get; set; } = null!;

    public PackageRestorePlan Plan { get; set; } = new();

    public string OperationId { get; set; } = "";

    public bool DryRun { get; set; }

    /// <summary>执行前再次确认 DSH 未运行（true 则直接失败，绝不结束进程）。</summary>
    public bool AppRunning { get; set; }

    /// <summary>安装超时秒数（覆盖步骤内的值；0 = 用步骤值）。</summary>
    public int InstallTimeoutSeconds { get; set; }

    public SnapshotManifest? Snapshot { get; set; }

    /// <summary>
    /// 本次调用是否已获得“联网回退”的用户同意（默认 false = 只走离线）。
    /// 集成层还会再与 <c>LoomConfig.PackageRestoreAllowNetworkFallback</c> 求与：两者都成立才会加联网 install 步骤。
    /// </summary>
    public bool AllowNetworkFallback { get; set; }

    /// <summary>计划层强制重写（即使当前文件已与快照合并结果一致）。</summary>
    public bool Force { get; set; }
}

/// <summary>包级恢复结果（含步骤状态、失败分类、回滚点与人工入口）。</summary>
public sealed class PackageRestoreResult
{
    public string OperationId { get; set; } = "";

    public OperationOutcome Outcome { get; set; } = OperationOutcome.Failed;

    public PackageRestorePlan Plan { get; set; } = new();

    public PackageRestoreFailure? Failure { get; set; }

    public string? BackupDir { get; set; }

    public string? RollbackPointPath { get; set; }

    public List<string> BackupFiles { get; set; } = new();

    public List<string> WrittenFiles { get; set; } = new();

    /// <summary>true = 安装后校验通过。</summary>
    public bool Verified { get; set; }

    /// <summary>true = 本次失败已用 restore-backup\&lt;opId&gt;\ 回滚（只影响当前 op）。</summary>
    public bool RolledBack { get; set; }

    public bool RequiresRestart { get; set; }

    public List<string> Messages { get; set; } = new();

    /// <summary>人工入口（失败时可直接展示给用户）。</summary>
    [JsonIgnore]
    public string? ManualAction => Failure?.ManualAction;
}

/// <summary>首启失败判据（设计 12.6）：N 秒未 ready / 进程提前退出 / startup crash / check 仍缺 bundles。</summary>
public sealed class FirstStartCriteria
{
    /// <summary>N：启动后等待 host ready 的秒数（默认 20s）。</summary>
    public int ReadyTimeoutSeconds { get; set; } = 20;

    /// <summary>true = 把 startup 类新 crash 视为首启失败。</summary>
    public bool DetectStartupCrash { get; set; } = true;

    /// <summary>true = check 结果仍缺 bundles 视为首启失败。</summary>
    public bool RequireBundles { get; set; } = true;

    /// <summary>
    /// crash 时间窗容差：只有当 crash 时间不早于 op 开始时间减去该容差时才算本次失败，
    /// 避免把用户手动崩溃误判为恢复失败。
    /// </summary>
    public TimeSpan CrashWindowTolerance { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>首启观测输入（由进程探针 / check 结果填充，planner 只做纯判定）。</summary>
public sealed class FirstStartObservation
{
    public DateTimeOffset OperationStartedAt { get; set; }

    public DateTimeOffset OperationFinishedAt { get; set; }

    /// <summary>host 是否在 N 秒内 ready。</summary>
    public bool HostReady { get; set; }

    /// <summary>从启动到本次判定经过的秒数。</summary>
    public int ObservedSeconds { get; set; }

    /// <summary>host 进程是否提前退出。</summary>
    public bool ProcessExited { get; set; }

    public DateTimeOffset? ProcessExitedAt { get; set; }

    /// <summary>是否检测到新的 startup 类 crash。</summary>
    public bool StartupCrash { get; set; }

    public DateTimeOffset? StartupCrashAt { get; set; }

    public string? CrashDetail { get; set; }

    /// <summary>check 仍缺失的 bundle。</summary>
    public List<string> MissingBundles { get; set; } = new();

    /// <summary>check 仍缺失的依赖。</summary>
    public List<string> MissingDependencies { get; set; } = new();
}

/// <summary>首启失败原因（None = 通过）。</summary>
public enum FirstStartFailureReason
{
    None,
    NotReadyTimeout,
    ProcessExitedEarly,
    StartupCrash,
    BundlesMissing,
    DependenciesMissing,
}

/// <summary>首启判定结论：失败原因、是否允许自动回滚本次 op、报告信号。</summary>
public sealed class FirstStartVerdict
{
    public FirstStartFailureReason Reason { get; set; }

    public bool Failed { get; set; }

    /// <summary>true = 允许按 design 12.6 自动回滚本次升级/还原（restore-backup\&lt;opId&gt;\）。</summary>
    public bool AutoRollbackAllowed { get; set; }

    public string Summary { get; set; } = "";

    /// <summary>命中的判据信号（中文），用于报告。</summary>
    public List<string> Signals { get; set; } = new();
}

/// <summary>离线回滚范围：文件级 + 包级（重新离线 install），或只恢复文件。</summary>
public enum OfflineRollbackScope
{
    FilesAndPackages,
    FilesOnly,
}

/// <summary>回滚点里记录的单个文件备份（存在性 + 哈希 + 大小）。</summary>
public sealed class PackageRestoreFileBackup
{
    /// <summary>相对 profile 的文件名（包级恢复只涉及 package.json / pnpm-lock.yaml）。</summary>
    public string Name { get; set; } = "";

    /// <summary>操作前该文件是否存在；false = 回滚时删除。</summary>
    public bool Existed { get; set; }

    public string? Sha256 { get; set; }

    public long Size { get; set; }
}

/// <summary>
/// 回滚点（restore-backup\&lt;opId&gt;\package-restore.json）：只覆盖当前 op，
/// 记录写入前的文件哈希与恢复前的 bundles/依赖，供离线回滚后再次校验。
/// </summary>
public sealed class OfflineRollbackPoint
{
    public int SchemaVersion { get; set; } = 1;

    public string OpId { get; set; } = "";

    public string Kind { get; set; } = "package-restore";

    public DateTimeOffset CreatedAt { get; set; }

    public string ProfileDir { get; set; } = "";

    public string SnapshotId { get; set; } = "";

    public string? SnapshotDir { get; set; }

    public OfflineRollbackScope Scope { get; set; } = OfflineRollbackScope.FilesAndPackages;

    /// <summary>备份目录绝对路径。</summary>
    public string BackupDir { get; set; } = "";

    public List<PackageRestoreFileBackup> Files { get; set; } = new();

    /// <summary>回滚后应存在的 bundle（恢复前状态）。</summary>
    public List<string> Bundles { get; set; } = new();

    /// <summary>回滚后应存在的依赖包名（恢复前状态）。</summary>
    public List<string> Dependencies { get; set; } = new();

    /// <summary>回滚后的 pnpm-lock.yaml 期望哈希。</summary>
    public string? LockfileSha256 { get; set; }

    /// <summary>正向后 install 的退出码（便于审计；失败为 null）。</summary>
    public int? InstallExitCode { get; set; }
}

/// <summary>离线回滚请求。</summary>
public sealed class OfflineRollbackRequest
{
    public LoomPaths Paths { get; set; } = null!;

    public DshInstallation Installation { get; set; } = new();

    public string OpId { get; set; } = "";

    public bool DryRun { get; set; }

    public int InstallTimeoutSeconds { get; set; } = 600;
}

/// <summary>离线回滚结果。</summary>
public sealed class OfflineRollbackResult
{
    public string OpId { get; set; } = "";

    public OperationOutcome Outcome { get; set; } = OperationOutcome.Failed;

    public OfflineRollbackPoint? Point { get; set; }

    public PackageRestoreFailure? Failure { get; set; }

    public string? BackupDir { get; set; }

    public List<string> RestoredFiles { get; set; } = new();

    public int? InstallExitCode { get; set; }

    /// <summary>true = 回滚后再次校验通过。</summary>
    public bool Verified { get; set; }

    public List<string> Messages { get; set; } = new();

    [JsonIgnore]
    public string? ManualAction => Failure?.ManualAction;
}
