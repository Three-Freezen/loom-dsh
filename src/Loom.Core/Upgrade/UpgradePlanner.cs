using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Upgrade;

/// <summary>
/// 策略 A 的纯函数决策表（设计 §11.1 / §11.4）。不读写文件、不调 CLI、不联网，输入输出都是模型，
/// 因此可以被完整单测覆盖。
///
/// 决策顺序（对应设计“候选筛选”步骤）：
///   1. 前置闸门：配置存在 → 离线 → 策略必须为 A → 目标 dshVersion 可解析 → 已装版本可解析；
///   2. 候选上限：按版本降序最多检查 20 个；
///   3. 逐个候选：版本可解析 → 高于已装 → 非 deprecated → 平台含 win32 →
///      evaluator 结论为 compatible（unknown/unverified/incompatible 全部拒绝）→
///      不依赖风险豁免 → static 预检 ok/不适用 → major 需 allowMajorUpgrade →
///      prerelease 需显式允许；
///   4. 排序：稳定版优先 → 版本号降序 → 同版本按 registry 返回顺序；
///   5. 命中即返回升级决策；未命中返回人工三选一，且 <see cref="UpgradeDecision.RiskExemptionGranted"/> 恒为 false。
/// </summary>
public static class UpgradePlanner
{
    /// <summary>设计 §11.1：单次决策最多检查 20 个候选版本。</summary>
    public const int DefaultMaxCandidates = 20;

    /// <summary>策略 A（自动升级到兼容版本）。B/C 一律不自动安装。</summary>
    public const string PolicyAutoUpgrade = "A";

    private const string NoCompatibleHint =
        "三选一：手动升级 / 保留旧版并申请风险豁免（需二次确认，由用户执行 allow-version --accept-risk，Loom 绝不自动授予）/ 跳过。";

    private const string MajorHint =
        "跨大版本默认不自动升级：可显式开启 allowMajorUpgrade 后重试，或手动升级（升级前请先备份插件数据）。";

    private const string PrereleaseHint =
        "如使用 nightly/预发布通道，请显式允许 prerelease 后重试；否则等待稳定兼容版本。";

    /// <summary>
    /// 只做“是否允许进入候选评估”的前置闸门。返回非 null 表示已经可以定论（不自动升级）；
    /// 返回 null 表示可以继续查询 registry 并评估候选。
    /// 执行器在查询 registry 之前先调用它，保证策略 B/C、离线、目标未知时不会发起任何查询。
    /// </summary>
    public static UpgradeDecision? EvaluateGates(UpgradePlannerInput? input)
    {
        if (input is null)
        {
            return Block(
                CreateBaseDecision(null),
                UpgradeBlockReason.InvalidInput,
                "缺少决策输入（UpgradePlannerInput），不自动升级。",
                "检查调用参数。",
                manual: true);
        }

        var decision = CreateBaseDecision(input);

        if (string.IsNullOrWhiteSpace(input.PackageName))
        {
            return Block(decision, UpgradeBlockReason.InvalidInput, "包名为空，不自动升级。", "检查调用参数。", manual: true);
        }

        if (input.Config is null)
        {
            return Block(
                decision,
                UpgradeBlockReason.InvalidInput,
                "缺少 config：无法确认自动升级策略与 allowMajorUpgrade，不自动升级。",
                "先把 config.json 读入再调用。",
                manual: true);
        }

        // 离线优先于策略：离线时既不能查询也不能安装，直接报告“无法查询/离线”。
        if (input.Offline)
        {
            return Block(
                decision,
                UpgradeBlockReason.Offline,
                "离线：不查询 registry、不自动安装，直接报告“无法查询/离线”。",
                "联网后重试。",
                manual: false);
        }

        if (!string.Equals(decision.Policy, PolicyAutoUpgrade, StringComparison.Ordinal))
        {
            return Block(
                decision,
                UpgradeBlockReason.PolicyNotAuto,
                $"自动升级策略为 {DisplayPolicy(decision.Policy)}（非 A），不自动安装。",
                "把 autoUpgradePolicy 设为 A 后才会自动安装；当前只生成人工提示。",
                manual: true);
        }

        if (!SemVersion.TryParse(input.TargetDshVersion, out _))
        {
            return Block(
                decision,
                UpgradeBlockReason.TargetUnknown,
                $"目标 dshVersion“{input.TargetDshVersion}”无法解析，不能判定兼容性，不自动升级。",
                "确认安装目录/运行时版本（或 update-info 目标版本）后重试。",
                manual: true);
        }

        if (!SemVersion.TryParse(input.InstalledVersion, out _))
        {
            return Block(
                decision,
                UpgradeBlockReason.InstalledVersionUnknown,
                $"已装版本“{input.InstalledVersion}”无法解析，无法安全比较版本，不自动升级。",
                "确认已装插件版本后重试。",
                manual: true);
        }

        return null;
    }

    /// <summary>纯函数决策表：输入包/版本/候选/配置/离线标记，输出升级决策。</summary>
    public static UpgradeDecision Plan(UpgradePlannerInput? input)
    {
        var gate = EvaluateGates(input);
        if (gate is not null) return gate;

        var source = input!;
        var decision = CreateBaseDecision(source);
        var installed = SemVersion.Parse(source.InstalledVersion);
        var maxCandidates = source.MaxCandidates > 0 ? source.MaxCandidates : DefaultMaxCandidates;

        // ---- 1. 版本解析 + registry 顺序 -------------------------------------
        var parsed = new List<CandidateVersion>();
        for (var index = 0; index < decision.Candidates.Count; index++)
        {
            var candidate = decision.Candidates[index];
            // 列表顺序即 registry 返回顺序；同版本排序时用它兜底。
            candidate.OriginalIndex = index;
            if (!SemVersion.TryParse(candidate.Version, out var version))
            {
                Reject(decision, candidate, UpgradeRejectionCodes.InvalidVersion, "版本号无法解析，按不可用处理。");
                continue;
            }

            parsed.Add(new CandidateVersion(candidate, version));
        }

        // ---- 2. 上限：按版本降序最多检查 N 个 ---------------------------------
        var ordered = parsed
            .OrderByDescending(item => item.Version)
            .ThenBy(item => item.Candidate.OriginalIndex)
            .ToList();
        var inScope = ordered.Take(maxCandidates).ToList();
        foreach (var extra in ordered.Skip(maxCandidates))
        {
            Reject(
                decision,
                extra.Candidate,
                UpgradeRejectionCodes.LimitExceeded,
                $"超出单次决策最多检查 {maxCandidates} 个候选的上限（设计 §11.1）。");
        }

        // ---- 3. 逐个候选筛选 -------------------------------------------------
        var eligible = new List<CandidateVersion>();
        var sawNewer = false;
        var majorBlocked = 0;
        var prereleaseBlocked = 0;

        foreach (var item in inScope)
        {
            var candidate = item.Candidate;
            var version = item.Version;

            if (version <= installed)
            {
                Reject(
                    decision,
                    candidate,
                    UpgradeRejectionCodes.NotNewer,
                    $"不高于已装版本 {source.InstalledVersion}，没有升级意义。");
                continue;
            }

            sawNewer = true;

            if (candidate.Deprecated)
            {
                Reject(
                    decision,
                    candidate,
                    UpgradeRejectionCodes.Deprecated,
                    "registry 已将版本标记为 deprecated；按设计不自动安装（如仍要用需用户确认）。");
                continue;
            }

            if (candidate.Platforms.Count > 0
                && !candidate.Platforms.Any(platform => string.Equals(platform?.Trim(), "win32", StringComparison.OrdinalIgnoreCase)))
            {
                Reject(
                    decision,
                    candidate,
                    UpgradeRejectionCodes.PlatformUnsupported,
                    "dsh.bundle.platforms 不包含 win32（" + string.Join("，", candidate.Platforms) + "）。");
                continue;
            }

            if (candidate.EvaluatorLevel is null)
            {
                Reject(
                    decision,
                    candidate,
                    UpgradeRejectionCodes.EvaluatorMissing,
                    "没有该候选在目标 dshVersion 上的 evaluator 结论，按未知处理，绝不视为兼容。");
                continue;
            }

            switch (candidate.EvaluatorLevel.Value)
            {
                case CompatibilityLevel.Unknown:
                    Reject(
                        decision,
                        candidate,
                        UpgradeRejectionCodes.EvaluatorUnknown,
                        DescribeEvaluator(candidate, "evaluator 判定“未知”，绝不视为兼容"));
                    continue;
                case CompatibilityLevel.Unverified:
                    Reject(
                        decision,
                        candidate,
                        UpgradeRejectionCodes.EvaluatorUnverified,
                        DescribeEvaluator(candidate, "evaluator 只能判定“未验证”，不自动升级"));
                    continue;
                case CompatibilityLevel.Incompatible:
                    Reject(
                        decision,
                        candidate,
                        UpgradeRejectionCodes.EvaluatorIncompatible,
                        DescribeEvaluator(candidate, "evaluator 判定不兼容"));
                    continue;
            }

            if (candidate.Exempted)
            {
                Reject(
                    decision,
                    candidate,
                    UpgradeRejectionCodes.EvaluatorExempted,
                    "该候选只有在风险豁免下才被判为兼容；绝不自动授予或依赖风险豁免，需用户二次确认。");
                continue;
            }

            if (candidate.StaticResult is StaticCheckResult.Unverified or StaticCheckResult.Missing)
            {
                var reason = "静态预检结果为 " + candidate.StaticResult + "，不进入自动升级（只进报告）";
                if (candidate.StaticMissingRefs.Count > 0)
                {
                    reason += "；缺少引用：" + string.Join("，", candidate.StaticMissingRefs);
                }

                Reject(decision, candidate, UpgradeRejectionCodes.StaticUnverified, reason + "。");
                continue;
            }

            if (version.Major > installed.Major && !source.Config!.AllowMajorUpgrade)
            {
                majorBlocked++;
                Reject(
                    decision,
                    candidate,
                    UpgradeRejectionCodes.MajorNotAllowed,
                    $"跨大版本 {installed.Major} → {version.Major}：allowMajorUpgrade=false，不自动升级（可能涉及插件数据格式迁移）。");
                continue;
            }

            if (version.Prerelease is not null && !source.AllowPrerelease)
            {
                prereleaseBlocked++;
                Reject(
                    decision,
                    candidate,
                    UpgradeRejectionCodes.PrereleaseNotAllowed,
                    "prerelease 版本需要显式允许并给出理由；当前未允许，稳定版优先。");
                continue;
            }

            eligible.Add(item);
        }

        // ---- 4. 无命中：给出人工三选一 ---------------------------------------
        if (eligible.Count == 0)
        {
            if (parsed.Count == 0)
            {
                return Block(
                    decision,
                    UpgradeBlockReason.NoCandidates,
                    "registry 没有返回可解析的候选版本；不安装、不授予风险豁免。",
                    "检查包名与 registry 配置（私服/镜像）后重试。",
                    manual: false);
            }

            if (!sawNewer)
            {
                return Block(
                    decision,
                    UpgradeBlockReason.UpToDate,
                    $"没有高于已装版本 {source.InstalledVersion} 的候选，视为已是最新；不安装。",
                    "无需操作。",
                    manual: false);
            }

            if (majorBlocked > 0)
            {
                return Block(
                    decision,
                    UpgradeBlockReason.MajorNotAllowed,
                    $"有 {majorBlocked} 个跨大版本候选通过兼容检查，但 allowMajorUpgrade=false；不自动升级、不授予豁免。",
                    MajorHint,
                    manual: true);
            }

            if (prereleaseBlocked > 0 && !source.AllowPrerelease)
            {
                return Block(
                    decision,
                    UpgradeBlockReason.PrereleaseNotAllowed,
                    $"只有 prerelease 候选通过兼容检查（{prereleaseBlocked} 个），当前未允许 prerelease；不自动升级。",
                    PrereleaseHint,
                    manual: true);
            }

            return Block(
                decision,
                UpgradeBlockReason.NoCompatibleCandidate,
                $"没有与目标 dshVersion {source.TargetDshVersion} 兼容的可自动升级候选（已拒绝 {decision.Rejected.Count} 个）；不安装、不授予任何风险豁免。",
                NoCompatibleHint,
                manual: true);
        }

        // ---- 5. 排序并选中 ---------------------------------------------------
        var ranked = eligible
            .OrderBy(item => item.Version.Prerelease is null ? 0 : 1)
            .ThenByDescending(item => item.Version)
            .ThenBy(item => item.Candidate.OriginalIndex)
            .ToList();
        var selected = ranked[0];
        var selectedVersion = selected.Version;
        foreach (var item in ranked)
        {
            item.Candidate.SortReason = BuildSortReason(item, selected);
        }

        decision.ShouldUpgrade = true;
        decision.Kind = UpgradeDecisionKind.Upgrade;
        decision.BlockReason = UpgradeBlockReason.None;
        decision.TargetVersion = selected.Candidate.Version;
        decision.Selected = selected.Candidate;
        decision.PrereleaseAccepted = selectedVersion.Prerelease is not null;
        decision.PrereleaseReason = decision.PrereleaseAccepted
            ? (!string.IsNullOrWhiteSpace(source.PrereleaseJustification)
                ? source.PrereleaseJustification!
                : "没有稳定的兼容版本，且配置显式允许 prerelease（nightly/预发布通道）")
            : null;
        decision.Reason = BuildUpgradeReason(selected.Candidate, selectedVersion, source.TargetDshVersion, decision.Policy);
        // 策略 A 的实现底线：没有兼容版本时只提示人工，任何情况下都不自动授予风险豁免。
        decision.RiskExemptionGranted = false;
        decision.ManualHint = null;
        return decision;
    }

    // ---------------------------------------------------------------- helpers

    private static UpgradeDecision CreateBaseDecision(UpgradePlannerInput? input)
    {
        return new UpgradeDecision
        {
            PackageName = input?.PackageName ?? "",
            InstalledVersion = input?.InstalledVersion ?? "",
            TargetDshVersion = input?.TargetDshVersion ?? "",
            Policy = NormalizePolicy(input?.Config?.AutoUpgradePolicy),
            AllowMajorUpgrade = input?.Config?.AllowMajorUpgrade ?? false,
            Candidates = CloneCandidates(input?.Candidates),
            RiskExemptionGranted = false,
        };
    }

    private static List<UpgradeCandidate> CloneCandidates(IEnumerable<UpgradeCandidate>? candidates) =>
        candidates?.Select(candidate => candidate.Clone()).ToList() ?? new List<UpgradeCandidate>();

    private static UpgradeDecision Block(
        UpgradeDecision decision,
        UpgradeBlockReason reason,
        string message,
        string? hint,
        bool manual)
    {
        decision.ShouldUpgrade = false;
        decision.Kind = manual ? UpgradeDecisionKind.ManualOnly : UpgradeDecisionKind.NoAction;
        decision.BlockReason = reason;
        decision.TargetVersion = null;
        decision.Selected = null;
        decision.PrereleaseAccepted = false;
        decision.PrereleaseReason = null;
        decision.Reason = message;
        decision.ManualHint = hint;
        decision.RiskExemptionGranted = false;
        return decision;
    }

    private static void Reject(UpgradeDecision decision, UpgradeCandidate candidate, string code, string reason)
    {
        decision.Rejected.Add(new UpgradeRejection { Version = candidate.Version, Code = code, Reason = reason });
    }

    private static string DescribeEvaluator(UpgradeCandidate candidate, string preamble)
    {
        var parts = new List<string> { preamble };
        if (candidate.Requirements.Count > 0)
        {
            parts.Add("未满足：" + string.Join("；", candidate.Requirements));
        }

        if (!string.IsNullOrWhiteSpace(candidate.EvaluatorError))
        {
            parts.Add("evaluator 错误：" + candidate.EvaluatorError);
        }

        if (candidate.Engine is { } engine)
        {
            parts.Add("engine=" + engine.ToString().ToLowerInvariant());
        }

        return string.Join("；", parts) + "。";
    }

    private static string BuildSortReason(CandidateVersion item, CandidateVersion selected)
    {
        if (ReferenceEquals(item, selected))
        {
            return selected.Version.Prerelease is null
                ? "选定：稳定版中版本最高，且 evaluator/static 均通过"
                : "选定：没有稳定的兼容版本，按允许 prerelease 的配置选择最高预发布版本";
        }

        return item.Version.Prerelease is null
            ? $"稳定版，但版本低于选定版本 {selected.Candidate.Version}"
            : $"prerelease 仅在无稳定兼容版本时考虑（已存在更优候选 {selected.Candidate.Version}）";
    }

    private static string BuildUpgradeReason(
        UpgradeCandidate candidate,
        SemVersion version,
        string targetDshVersion,
        string policy)
    {
        var parts = new List<string>
        {
            $"策略 {policy}：选中 {candidate.Version}（高于已装版本，且以目标 dshVersion {targetDshVersion} 评估为兼容）",
        };
        if (version.Prerelease is not null)
        {
            parts.Add("稳定版没有兼容候选，按配置允许 prerelease");
        }

        parts.Add($"evaluator={DescribeLevel(candidate.EvaluatorLevel)}，static={candidate.StaticResult}");
        parts.Add("未授予任何风险豁免");
        return string.Join("；", parts) + "。";
    }

    private static string DescribeLevel(CompatibilityLevel? level) => level switch
    {
        CompatibilityLevel.Compatible => "compatible",
        CompatibilityLevel.Incompatible => "incompatible",
        CompatibilityLevel.Unverified => "unverified",
        CompatibilityLevel.Unknown => "unknown",
        _ => "未评估",
    };

    private static string NormalizePolicy(string? policy) =>
        string.IsNullOrWhiteSpace(policy) ? "" : policy.Trim().ToUpperInvariant();

    private static string DisplayPolicy(string policy) => string.IsNullOrEmpty(policy) ? "未配置" : policy;

    /// <summary>候选 + 已解析版本（避免重复解析，保持排序稳定）。</summary>
    private sealed class CandidateVersion
    {
        public CandidateVersion(UpgradeCandidate candidate, SemVersion version)
        {
            Candidate = candidate;
            Version = version;
        }

        public UpgradeCandidate Candidate { get; }
        public SemVersion Version { get; }
    }
}
