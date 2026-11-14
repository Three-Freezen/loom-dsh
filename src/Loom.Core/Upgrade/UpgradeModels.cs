using System.Text.Json.Serialization;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Upgrade;

// ---------------------------------------------------------------------------
// 二期「策略 A」的纯数据模型与可注入端口。
//
// 设计依据：docs\实现方案-v2.md §11（候选排序 / prerelease / 私服 / 离线 /
// --save-exact / 回滚 / 审计）、§8.4（upgrading 状态）、§9.4（restore-backup）、§15（安全）。
//
// 约束（实现纪律）：
//   * 决策层（UpgradePlanner）是纯函数：不碰文件、不调 CLI、不联网。
//   * 执行层（UpgradeService）只通过 IPackageRegistry / IDshCli 两个端口访问外部世界，
//     默认实现分别走 dsh.cmd 的 view / add（pnpm 转发）；测试注入假实现即可完全离线。
//   * 绝不自动授予风险豁免（不存在任何自动 allow-version 调用路径）。
// ---------------------------------------------------------------------------

/// <summary>决策结果种类。</summary>
public enum UpgradeDecisionKind
{
    /// <summary>策略 A 命中兼容候选，应当执行安装。</summary>
    Upgrade,

    /// <summary>不自动安装，需要人工介入（无兼容版本 / 目标未知 / 跨大版本被拦等）。</summary>
    ManualOnly,

    /// <summary>不执行任何动作（策略非 A、离线、已是最新等）。</summary>
    NoAction,
}

/// <summary>不升级的原因分类（用于报告与 UI 文案，不参与安装）。</summary>
public enum UpgradeBlockReason
{
    None,
    InvalidInput,
    PolicyNotAuto,
    Offline,
    TargetUnknown,
    InstalledVersionUnknown,
    NoCandidates,
    UpToDate,
    NoCompatibleCandidate,
    MajorNotAllowed,
    PrereleaseNotAllowed,
    AppRunning,
    RegistryUnavailable,
}

/// <summary>失败分类（设计 §11.6）：审计与重试提示都依赖它。</summary>
public enum UpgradeFailureClass
{
    None,
    Offline,
    Network,
    Auth,
    NotFound,
    Timeout,
    PeerConflict,
    Disk,
    Permission,
    VerifyFailed,
    InstallFailed,
    BackupFailed,
    RollbackFailed,
    AppRunning,
    Cancelled,
    InvalidResponse,
    Unknown,
}

/// <summary>拒绝原因的稳定代码（测试与报告按代码断言/展示）。</summary>
public static class UpgradeRejectionCodes
{
    public const string InvalidVersion = "E_CANDIDATE_VERSION_INVALID";
    public const string LimitExceeded = "E_CANDIDATE_LIMIT";
    public const string NotNewer = "E_CANDIDATE_NOT_NEWER";
    public const string Deprecated = "E_CANDIDATE_DEPRECATED";
    public const string PlatformUnsupported = "E_CANDIDATE_PLATFORM";
    public const string EvaluatorMissing = "E_CANDIDATE_EVALUATOR_MISSING";
    public const string EvaluatorUnknown = "E_CANDIDATE_EVALUATOR_UNKNOWN";
    public const string EvaluatorUnverified = "E_CANDIDATE_EVALUATOR_UNVERIFIED";
    public const string EvaluatorIncompatible = "E_CANDIDATE_EVALUATOR_INCOMPATIBLE";
    public const string EvaluatorExempted = "E_CANDIDATE_EXEMPTED";
    public const string StaticUnverified = "E_CANDIDATE_STATIC_UNVERIFIED";
    public const string MajorNotAllowed = "E_CANDIDATE_MAJOR";
    public const string PrereleaseNotAllowed = "E_CANDIDATE_PRERELEASE";
}

/// <summary>
/// 一个 registry 候选版本及其兼容性评估结论。
/// evaluator 结论（<see cref="EvaluatorLevel"/> 等）与 static 结论（<see cref="StaticResult"/>）
/// 由调用方/执行器在 <b>目标 dshVersion</b> 上评估后填入；未评估时保持 null，绝不当成兼容。
/// </summary>
public sealed class UpgradeCandidate
{
    /// <summary>候选版本号（semver 原文，例如 1.2.0 / 2.0.0-rc.1）。</summary>
    public string Version { get; set; } = "";

    /// <summary>registry 是否标记 deprecated（deprecated 绝不自安装）。</summary>
    public bool Deprecated { get; set; }

    /// <summary>deprecated 的原因文案（只用于报告）。</summary>
    public string? DeprecationReason { get; set; }

    /// <summary>该版本命中的 dist-tags（latest / next / nightly ...）。</summary>
    public List<string> DistTags { get; set; } = new();

    /// <summary>registry 记录的 tarball integrity（sha512-...）。</summary>
    public string? Integrity { get; set; }

    /// <summary>查询命中的 registry（私服/镜像），未知时为空。</summary>
    public string? Registry { get; set; }

    /// <summary>以目标 dshVersion 评估的兼容级别；null = 未评估（按未知处理）。</summary>
    public CompatibilityLevel? EvaluatorLevel { get; set; }

    /// <summary>评估引擎（official / fallback / unknown）。</summary>
    public CompatibilityEngine? Engine { get; set; }

    /// <summary>评估时是否依赖了风险豁免（依赖豁免即拒绝自动升级）。</summary>
    public bool Exempted { get; set; }

    /// <summary>evaluator 列出的未满足项，作为拒绝原因原样保留到审计。</summary>
    public List<string> Requirements { get; set; } = new();

    /// <summary>evaluator 的错误/未知原因。</summary>
    public string? EvaluatorError { get; set; }

    /// <summary>静态预检结论（manifest 判不到的部分）。</summary>
    public StaticCheckResult StaticResult { get; set; } = StaticCheckResult.NotApplicable;

    /// <summary>静态预检缺失的引用（client inject / patch 等）。</summary>
    public List<string> StaticMissingRefs { get; set; } = new();

    /// <summary>dsh.bundle.platforms（manifest 声明）；为空表示不限制。</summary>
    public List<string> Platforms { get; set; } = new();

    /// <summary>registry 返回顺序（同版本排序的最终依据）。</summary>
    public int OriginalIndex { get; set; }

    /// <summary>排序/选中理由：由 UpgradePlanner 写入，供报告解释“为什么是它”。</summary>
    public string? SortReason { get; set; }

    /// <summary>
    /// 候选的 package.json 原文（若 registry 能提供）：执行器用它在目标 dshVersion 上复跑 evaluator。
    /// 体积可能较大，不写入审计 JSON。
    /// </summary>
    [JsonIgnore]
    public string? ManifestJson { get; set; }

    public UpgradeCandidate Clone()
    {
        return new UpgradeCandidate
        {
            Version = Version,
            Deprecated = Deprecated,
            DeprecationReason = DeprecationReason,
            DistTags = DistTags.ToList(),
            Integrity = Integrity,
            Registry = Registry,
            EvaluatorLevel = EvaluatorLevel,
            Engine = Engine,
            Exempted = Exempted,
            Requirements = Requirements.ToList(),
            EvaluatorError = EvaluatorError,
            StaticResult = StaticResult,
            StaticMissingRefs = StaticMissingRefs.ToList(),
            Platforms = Platforms.ToList(),
            OriginalIndex = OriginalIndex,
            SortReason = SortReason,
            ManifestJson = ManifestJson,
        };
    }
}

/// <summary>一条候选拒绝记录（版本 + 稳定代码 + 人话原因；evaluator 未满足项会拼进 Reason）。</summary>
public sealed class UpgradeRejection
{
    public string Version { get; set; } = "";
    public string Code { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>决策输入：完全由调用方提供，planner 不读任何外部状态。</summary>
public sealed class UpgradePlannerInput
{
    public string PackageName { get; set; } = "";
    public string InstalledVersion { get; set; } = "";

    /// <summary>目标运行时版本（更新后的 dshVersion；下载期可用 update-info 目标版本）。</summary>
    public string TargetDshVersion { get; set; } = "";

    /// <summary>候选列表（可含各自评估结论）；顺序即 registry 返回顺序。</summary>
    public List<UpgradeCandidate> Candidates { get; set; } = new();

    /// <summary>从 config.json 读出的配置；为空则不自动升级。</summary>
    public LoomConfig? Config { get; set; }

    /// <summary>是否离线：离线时不做查询、不安装。</summary>
    public bool Offline { get; set; }

    /// <summary>是否允许考虑 prerelease（nightly 默认允许；仍稳定版优先）。</summary>
    public bool AllowPrerelease { get; set; }

    /// <summary>允许 prerelease 时的“明确理由”（写进决策，便于审计）。</summary>
    public string? PrereleaseJustification { get; set; }

    /// <summary>单次决策最多检查的候选数量（设计 §11.1：20）。</summary>
    public int MaxCandidates { get; set; } = UpgradePlanner.DefaultMaxCandidates;
}

/// <summary>策略 A 决策：升不升、升到哪个版本、为什么、以及每个候选被拒绝的原因。</summary>
public sealed class UpgradeDecision
{
    public string PackageName { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string TargetDshVersion { get; set; } = "";

    /// <summary>决策时生效的策略（A/B/C，已归一化为大写）。</summary>
    public string Policy { get; set; } = "A";

    public bool AllowMajorUpgrade { get; set; }

    public UpgradeDecisionKind Kind { get; set; } = UpgradeDecisionKind.NoAction;

    public bool ShouldUpgrade { get; set; }

    /// <summary>选中的目标版本（仅 ShouldUpgrade=true 时有值）。</summary>
    public string? TargetVersion { get; set; }

    /// <summary>选中的候选（含评估结论与排序理由）。</summary>
    public UpgradeCandidate? Selected { get; set; }

    /// <summary>本次参与决策的候选（已填 SortReason）。</summary>
    public List<UpgradeCandidate> Candidates { get; set; } = new();

    /// <summary>被拒绝的候选及原因（evaluator 未满足项保留）。</summary>
    public List<UpgradeRejection> Rejected { get; set; } = new();

    public UpgradeBlockReason BlockReason { get; set; } = UpgradeBlockReason.None;

    /// <summary>人话理由（报告/托盘展示）。</summary>
    public string Reason { get; set; } = "";

    /// <summary>需要人工处理时的三选一提示（手动升级 / 保留旧版并申请豁免 / 跳过）。</summary>
    public string? ManualHint { get; set; }

    /// <summary>是否选择了 prerelease（稳定版优先；仅在无稳定兼容版本时）。</summary>
    public bool PrereleaseAccepted { get; set; }

    /// <summary>选择 prerelease 的明确理由。</summary>
    public string? PrereleaseReason { get; set; }

    /// <summary>
    /// 是否自动授予了风险豁免。策略 A 恒为 false：没有兼容版本时只提示人工，
    /// 绝不自动写 allow-version / compatibility.json。
    /// </summary>
    public bool RiskExemptionGranted { get; set; }
}

/// <summary>备份到 restore-backup\&lt;opId&gt;\ 的单个文件记录（含哈希）。</summary>
public sealed class UpgradeBackupFile
{
    public string Name { get; set; } = "";

    /// <summary>操作前该文件是否存在（false 表示回滚时应删除）。</summary>
    public bool Existed { get; set; }

    public string? Sha256 { get; set; }
    public long Size { get; set; }
}

/// <summary>
/// 审计记录（设计 §11.5）。写入 reports\upgrade-&lt;opId&gt;.json，同时每个阶段写 journal。
/// 只记录路径/哈希/版本/命令/结果，不写文件内容、环境变量或凭据。
/// </summary>
public sealed class UpgradeAuditRecord
{
    public int SchemaVersion { get; set; } = 1;
    public string OperationId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public TimeSpan Duration { get; set; }

    public string PackageName { get; set; } = "";
    public string InstalledVersionBefore { get; set; } = "";
    public string? InstalledVersionAfter { get; set; }
    public string? TargetVersion { get; set; }
    public string TargetDshVersion { get; set; } = "";

    public string Policy { get; set; } = "";
    public bool AllowMajorUpgrade { get; set; }
    public bool Offline { get; set; }
    public bool DryRun { get; set; }

    /// <summary>候选列表（含 deprecated / distTags / integrity / registry / evaluator / static / 排序理由）。</summary>
    public List<UpgradeCandidate> Candidates { get; set; } = new();

    public List<UpgradeRejection> Rejected { get; set; } = new();

    public string? SelectedRegistry { get; set; }
    public string? SelectedIntegrity { get; set; }

    /// <summary>实际执行的命令（已按 cmd 规则加引号；日志侧另有脱敏）。</summary>
    public string? CommandLine { get; set; }

    public int? ExitCode { get; set; }
    public bool TimedOut { get; set; }

    /// <summary>安装命令耗时（与查询耗时分开记录）。</summary>
    public TimeSpan InstallDuration { get; set; }

    public string? BackupDir { get; set; }
    public List<UpgradeBackupFile> BackupFiles { get; set; } = new();

    public bool RolledBack { get; set; }

    /// <summary>回滚路径：restore-backup\&lt;opId&gt;\（可用它人工复查/恢复）。</summary>
    public string? RollbackPath { get; set; }

    public UpgradeFailureClass FailureClass { get; set; } = UpgradeFailureClass.None;
    public string? FailureDetail { get; set; }
    public string? VerifyDetail { get; set; }

    /// <summary>registry 查询耗时与次数（私服/离线排障用）。</summary>
    public TimeSpan QueryDuration { get; set; }
    public int QueryAttempts { get; set; }

    public string Outcome { get; set; } = "";
}

/// <summary>执行一次策略 A 升级的请求。调用方负责锁与“App 已退出”，服务只做防御性检查。</summary>
public sealed class UpgradeRequest
{
    public DshInstallation Installation { get; set; } = new();

    /// <summary>Loom 数据目录（备份 / journal / 审计都写在这里，绝不写 DSH 安装目录）。</summary>
    public LoomPaths Paths { get; set; } = null!;

    /// <summary>当前 config.json（策略 A、allowMajorUpgrade 等）。</summary>
    public LoomConfig Config { get; set; } = new();

    public string PackageName { get; set; } = "";

    /// <summary>已装版本（调用方从 profile 读取；无法确定时留空会拒绝自动升级）。</summary>
    public string InstalledVersion { get; set; } = "";

    /// <summary>目标 dshVersion（更新后的运行时版本）。</summary>
    public string TargetDshVersion { get; set; } = "";

    /// <summary>离线：不查询 registry、不安装，直接返回“无法查询/离线”。</summary>
    public bool Offline { get; set; }

    /// <summary>演练：只生成决策与命令预览，不备份、不执行、不写 journal/审计文件。</summary>
    public bool DryRun { get; set; }

    /// <summary>是否允许考虑 prerelease（仅在没有稳定兼容版本时才会选中）。</summary>
    public bool AllowPrerelease { get; set; }

    /// <summary>允许 prerelease 的理由（审计用；为空时使用默认说明）。</summary>
    public string? PrereleaseJustification { get; set; }

    /// <summary>指定 opId（测试/重试）；为空则自动生成。</summary>
    public string? OperationId { get; set; }

    /// <summary>单次决策最多检查的候选数量（默认 20）。</summary>
    public int MaxCandidates { get; set; } = UpgradePlanner.DefaultMaxCandidates;

    /// <summary>registry 查询超时（默认 60s）。</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>单包安装超时（设计 §8.4：单包 15min）。</summary>
    public TimeSpan InstallTimeout { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>执行结果：决策 + 审计 + 人话消息；失败已分类，服务不向外抛异常。</summary>
public sealed class UpgradeExecutionResult
{
    public string OperationId { get; set; } = "";
    public OperationOutcome Outcome { get; set; } = OperationOutcome.Failed;
    public UpgradeDecision Decision { get; set; } = new();
    public UpgradeAuditRecord? Audit { get; set; }

    /// <summary>审计 JSON 路径（dry-run / 早退无写入时为空）。</summary>
    public string? AuditPath { get; set; }

    public string? BackupDir { get; set; }
    public string? CommandLine { get; set; }
    public string? ErrorCode { get; set; }

    /// <summary>是否改动了 profile（成功升级后为 true）。</summary>
    public bool ProfileChanged { get; set; }

    public List<string> Messages { get; set; } = new();
}

// ---------------------------------------------------------------------------
// 可注入端口（执行器只依赖这两个抽象，测试与离线场景均可替换）
// ---------------------------------------------------------------------------

/// <summary>查询某个包的候选版本（默认实现走 dsh.cmd 的 pnpm view 视角）。</summary>
public interface IPackageRegistry
{
    Task<PackageRegistryQueryResult> QueryAsync(
        PackageRegistryQueryRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>执行 dsh.cmd plugin 命令的最小端口（默认实现包装既有 DshCli）。</summary>
public interface IDshCli
{
    Task<CliCallResult> RunPluginAsync(
        DshInstallation installation,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>registry 查询参数。</summary>
public sealed class PackageRegistryQueryRequest
{
    public DshInstallation Installation { get; set; } = new();
    public string PackageName { get; set; } = "";

    /// <summary>是否允许查询/拉取 prerelease 版本的 manifest（排序仍由 planner 稳定版优先）。</summary>
    public bool AllowPrerelease { get; set; }

    /// <summary>是否逐个拉取候选 manifest 供 evaluator 复检（最多 <see cref="MaxManifests"/> 个）。</summary>
    public bool FetchManifests { get; set; } = true;

    /// <summary>最多拉取多少个候选 manifest（设计 §11.1 的上限 20）。</summary>
    public int MaxManifests { get; set; } = UpgradePlanner.DefaultMaxCandidates;

    /// <summary>单次查询超时（设计 §11.1：60s）。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>可选 registry 标记（仅用于审计/报告；默认实现不解析 .npmrc）。</summary>
    public string? Registry { get; set; }
}

/// <summary>registry 查询结果；失败已分类，绝不抛给调用方。</summary>
public sealed class PackageRegistryQueryResult
{
    public bool Ok { get; set; }

    /// <summary>是否是“离线/网络不可达”这类“无法查询”的情况。</summary>
    public bool Offline { get; set; }

    public UpgradeFailureClass Failure { get; set; } = UpgradeFailureClass.None;
    public string? Error { get; set; }
    public string? Registry { get; set; }
    public TimeSpan Duration { get; set; }
    public int Attempts { get; set; }
    public List<UpgradeCandidate> Candidates { get; set; } = new();
}
