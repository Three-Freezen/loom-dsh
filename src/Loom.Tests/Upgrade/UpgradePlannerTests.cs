using Loom.Core.Models;
using Loom.Core.Upgrade;
using Xunit;

namespace Loom.Tests.Upgrade;

/// <summary>
/// 策略 A 决策表全分支覆盖（设计 §11.1 / §11.4）：
/// 策略 A/B/C、major、prerelease、无兼容、离线、deprecated、evaluator/static 结论、
/// 风险豁免、候选上限与多候选排序。
/// </summary>
public sealed class UpgradePlannerTests
{
    private const string Package = "dsh-plugin-demo";
    private const string Installed = "1.0.0";
    private const string TargetDsh = "0.3.0";

    // ------------------------------------------------------------ 基本命中

    [Fact]
    public void PolicyASelectsHighestStableCompatibleCandidate()
    {
        var decision = UpgradePlanner.Plan(Input(Candidate("1.1.0"), Candidate("1.2.0"), Candidate("1.0.5")));

        Assert.True(decision.ShouldUpgrade);
        Assert.Equal(UpgradeDecisionKind.Upgrade, decision.Kind);
        Assert.Equal("1.2.0", decision.TargetVersion);
        Assert.NotNull(decision.Selected);
        Assert.Equal("1.2.0", decision.Selected!.Version);
        Assert.False(string.IsNullOrWhiteSpace(decision.Selected.SortReason));
        Assert.Contains("策略 A", decision.Reason);
        Assert.Equal(UpgradeBlockReason.None, decision.BlockReason);
        Assert.Empty(decision.Rejected);
        Assert.False(decision.RiskExemptionGranted);
    }

    [Fact]
    public void PolicyComparisonIsCaseInsensitive()
    {
        var decision = UpgradePlanner.Plan(InputWithConfig(new LoomConfig { AutoUpgradePolicy = "a" }, Candidate("1.2.0")));

        Assert.True(decision.ShouldUpgrade);
        Assert.Equal("A", decision.Policy);
    }

    [Theory]
    [InlineData("B")]
    [InlineData("C")]
    public void PolicyOtherThanANeverInstalls(string policy)
    {
        var decision = UpgradePlanner.Plan(InputWithConfig(new LoomConfig { AutoUpgradePolicy = policy }, Candidate("1.2.0")));

        Assert.False(decision.ShouldUpgrade);
        Assert.Equal(UpgradeDecisionKind.ManualOnly, decision.Kind);
        Assert.Equal(UpgradeBlockReason.PolicyNotAuto, decision.BlockReason);
        Assert.NotNull(decision.ManualHint);
        Assert.False(decision.RiskExemptionGranted);
    }

    [Fact]
    public void MissingConfigNeverInstalls()
    {
        var input = Input(Candidate("1.2.0"));
        input.Config = null;

        var decision = UpgradePlanner.Plan(input);

        Assert.False(decision.ShouldUpgrade);
        Assert.Equal(UpgradeBlockReason.InvalidInput, decision.BlockReason);
        Assert.Equal(UpgradeDecisionKind.ManualOnly, decision.Kind);
    }

    [Fact]
    public void OfflineNeverUpgrades()
    {
        var input = Input(Candidate("1.2.0"));
        input.Offline = true;

        var decision = UpgradePlanner.Plan(input);

        Assert.False(decision.ShouldUpgrade);
        Assert.Equal(UpgradeDecisionKind.NoAction, decision.Kind);
        Assert.Equal(UpgradeBlockReason.Offline, decision.BlockReason);
        Assert.Contains("离线", decision.Reason);
    }

    // ------------------------------------------------------------ major

    [Fact]
    public void MajorUpgradeIsBlockedUnlessAllowMajorUpgrade()
    {
        var blocked = UpgradePlanner.Plan(Input(Candidate("2.0.0")));

        Assert.False(blocked.ShouldUpgrade);
        Assert.Equal(UpgradeBlockReason.MajorNotAllowed, blocked.BlockReason);
        Assert.Contains(blocked.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.MajorNotAllowed);
        Assert.Contains("allowMajorUpgrade", blocked.Reason);

        var allowed = UpgradePlanner.Plan(InputWithConfig(
            new LoomConfig { AutoUpgradePolicy = "A", AllowMajorUpgrade = true },
            Candidate("2.0.0")));

        Assert.True(allowed.ShouldUpgrade);
        Assert.Equal("2.0.0", allowed.TargetVersion);
    }

    [Fact]
    public void MinorUpgradeDoesNotNeedMajorPermission()
    {
        var decision = UpgradePlanner.Plan(Input(Candidate("1.9.9")));

        Assert.True(decision.ShouldUpgrade);
        Assert.Equal("1.9.9", decision.TargetVersion);
    }

    // ------------------------------------------------------------ prerelease

    [Fact]
    public void PrereleaseIsRejectedUnlessExplicitlyAllowed()
    {
        var decision = UpgradePlanner.Plan(Input(Candidate("1.5.0-rc.1")));

        Assert.False(decision.ShouldUpgrade);
        Assert.Equal(UpgradeBlockReason.PrereleaseNotAllowed, decision.BlockReason);
        Assert.Contains(decision.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.PrereleaseNotAllowed);
        Assert.False(decision.PrereleaseAccepted);
    }

    [Fact]
    public void PrereleaseIsSelectedOnlyWhenNoStableCompatibleVersionExists()
    {
        var input = Input(Candidate("1.5.0-rc.1"));
        input.AllowPrerelease = true;

        var decision = UpgradePlanner.Plan(input);

        Assert.True(decision.ShouldUpgrade);
        Assert.Equal("1.5.0-rc.1", decision.TargetVersion);
        Assert.True(decision.PrereleaseAccepted);
        Assert.False(string.IsNullOrWhiteSpace(decision.PrereleaseReason));
        Assert.Contains("prerelease", decision.Reason);
    }

    [Fact]
    public void StableVersionWinsOverHigherPrerelease()
    {
        var input = Input(Candidate("1.5.0-rc.1"), Candidate("1.4.0"));
        input.AllowPrerelease = true;

        var decision = UpgradePlanner.Plan(input);

        Assert.True(decision.ShouldUpgrade);
        Assert.Equal("1.4.0", decision.TargetVersion);
        var prerelease = decision.Candidates.Single(candidate => candidate.Version == "1.5.0-rc.1");
        Assert.Contains("prerelease", prerelease.SortReason ?? "");
    }

    // ------------------------------------------------- 无兼容 / evaluator 结论

    [Fact]
    public void NoCompatibleCandidateYieldsManualChoiceAndKeepsEvaluatorRequirements()
    {
        var decision = UpgradePlanner.Plan(Input(
            Candidate("1.1.0", CompatibilityLevel.Incompatible, requirements: "engines.dsh: ^0.2.0"),
            Candidate("1.2.0", CompatibilityLevel.Unknown)));

        Assert.False(decision.ShouldUpgrade);
        Assert.Equal(UpgradeDecisionKind.ManualOnly, decision.Kind);
        Assert.Equal(UpgradeBlockReason.NoCompatibleCandidate, decision.BlockReason);
        Assert.NotNull(decision.ManualHint);
        Assert.Contains("三选一", decision.ManualHint!);
        Assert.Contains(decision.Rejected, rejection => rejection.Reason.Contains("engines.dsh: ^0.2.0"));
        Assert.Contains(decision.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.EvaluatorUnknown);
        Assert.False(decision.RiskExemptionGranted);
    }

    [Fact]
    public void UnverifiedAndUnknownEvaluatorResultsAreRejected()
    {
        var decision = UpgradePlanner.Plan(Input(
            Candidate("1.1.0", CompatibilityLevel.Unverified),
            Candidate("1.2.0", CompatibilityLevel.Unknown)));

        Assert.False(decision.ShouldUpgrade);
        Assert.Contains(decision.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.EvaluatorUnverified);
        Assert.Contains(decision.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.EvaluatorUnknown);
    }

    [Fact]
    public void MissingEvaluatorConclusionIsTreatedAsUnknown()
    {
        var candidate = Candidate("1.2.0");
        candidate.EvaluatorLevel = null;

        var decision = UpgradePlanner.Plan(Input(candidate));

        Assert.False(decision.ShouldUpgrade);
        Assert.Contains(decision.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.EvaluatorMissing);
    }

    [Fact]
    public void StaticUnverifiedCandidateIsRejectedWithMissingRefs()
    {
        var candidate = Candidate("1.2.0", staticResult: StaticCheckResult.Unverified);
        candidate.StaticMissingRefs.Add("client:@deepseek-ai/dsh-client-runtime");

        var decision = UpgradePlanner.Plan(Input(candidate));

        Assert.False(decision.ShouldUpgrade);
        Assert.Contains(decision.Rejected, rejection =>
            rejection.Code == UpgradeRejectionCodes.StaticUnverified
            && rejection.Reason.Contains("dsh-client-runtime"));
    }

    [Fact]
    public void StaticMissingCandidateIsRejected()
    {
        var decision = UpgradePlanner.Plan(Input(Candidate("1.2.0", staticResult: StaticCheckResult.Missing)));

        Assert.False(decision.ShouldUpgrade);
        Assert.Contains(decision.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.StaticUnverified);
    }

    // ------------------------------------------------------------ deprecated / 豁免

    [Fact]
    public void DeprecatedCandidateIsNeverAutoInstalled()
    {
        var decision = UpgradePlanner.Plan(Input(Candidate("1.2.0", deprecated: true)));

        Assert.False(decision.ShouldUpgrade);
        Assert.Contains(decision.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.Deprecated);
    }

    [Fact]
    public void NonDeprecatedCandidateWinsOverDeprecatedOne()
    {
        var decision = UpgradePlanner.Plan(Input(Candidate("1.3.0", deprecated: true), Candidate("1.2.0")));

        Assert.True(decision.ShouldUpgrade);
        Assert.Equal("1.2.0", decision.TargetVersion);
        Assert.Contains(decision.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.Deprecated);
    }

    [Fact]
    public void ExemptedCandidateIsNeverAutomaticallyInstalled()
    {
        var decision = UpgradePlanner.Plan(Input(Candidate("1.2.0", exempted: true)));

        Assert.False(decision.ShouldUpgrade);
        Assert.Equal(UpgradeDecisionKind.ManualOnly, decision.Kind);
        Assert.Contains(decision.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.EvaluatorExempted);
        Assert.False(decision.RiskExemptionGranted);
    }

    [Fact]
    public void PlatformWithoutWin32IsRejected()
    {
        var candidate = Candidate("1.2.0");
        candidate.Platforms.Add("darwin");

        var decision = UpgradePlanner.Plan(Input(candidate));

        Assert.False(decision.ShouldUpgrade);
        Assert.Contains(decision.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.PlatformUnsupported);
    }

    [Fact]
    public void Win32PlatformCandidateIsAccepted()
    {
        var candidate = Candidate("1.2.0");
        candidate.Platforms.Add("win32");

        var decision = UpgradePlanner.Plan(Input(candidate));

        Assert.True(decision.ShouldUpgrade);
    }

    // ------------------------------------------------------------ 版本比较 / 上限

    [Fact]
    public void OlderAndEqualVersionsAreRejectedAsUpToDate()
    {
        var decision = UpgradePlanner.Plan(Input(Candidate("0.9.0"), Candidate("1.0.0")));

        Assert.False(decision.ShouldUpgrade);
        Assert.Equal(UpgradeDecisionKind.NoAction, decision.Kind);
        Assert.Equal(UpgradeBlockReason.UpToDate, decision.BlockReason);
        Assert.Equal(2, decision.Rejected.Count);
        Assert.All(decision.Rejected, rejection => Assert.Equal(UpgradeRejectionCodes.NotNewer, rejection.Code));
    }

    [Fact]
    public void EmptyCandidateListIsNoCandidates()
    {
        var decision = UpgradePlanner.Plan(Input());

        Assert.False(decision.ShouldUpgrade);
        Assert.Equal(UpgradeBlockReason.NoCandidates, decision.BlockReason);
        Assert.Equal(UpgradeDecisionKind.NoAction, decision.Kind);
    }

    [Fact]
    public void UnparseableCandidateVersionIsRejected()
    {
        var decision = UpgradePlanner.Plan(Input(Candidate("nightly")));

        Assert.False(decision.ShouldUpgrade);
        Assert.Equal(UpgradeBlockReason.NoCandidates, decision.BlockReason);
        Assert.Contains(decision.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.InvalidVersion);
    }

    [Fact]
    public void CandidateLimitRejectsOverflowBeyondTwenty()
    {
        var candidates = Enumerable.Range(1, 25)
            .Select(index => Candidate($"1.0.{index}"))
            .ToArray();

        var decision = UpgradePlanner.Plan(Input(candidates));

        Assert.True(decision.ShouldUpgrade);
        Assert.Equal("1.0.25", decision.TargetVersion);
        Assert.Equal(5, decision.Rejected.Count(rejection => rejection.Code == UpgradeRejectionCodes.LimitExceeded));
    }

    [Fact]
    public void RegistryOrderBreaksVersionTies()
    {
        var first = Candidate("1.2.0");
        first.Integrity = "sha512-first";
        var second = Candidate("1.2.0");
        second.Integrity = "sha512-second";

        var decision = UpgradePlanner.Plan(Input(first, second));

        Assert.True(decision.ShouldUpgrade);
        Assert.Equal("sha512-first", decision.Selected!.Integrity);
        Assert.Equal(0, decision.Selected.OriginalIndex);
    }

    [Fact]
    public void InputCandidatesAreNotMutated()
    {
        var candidate = Candidate("1.2.0");
        var decision = UpgradePlanner.Plan(Input(candidate));

        Assert.True(decision.ShouldUpgrade);
        Assert.NotNull(decision.Selected!.SortReason);
        Assert.Null(candidate.SortReason); // planner 在副本上写排序理由
    }

    // ------------------------------------------------------------ 目标/已装未知

    [Fact]
    public void UnknownTargetDshVersionIsManualOnly()
    {
        var input = Input(Candidate("1.2.0"));
        input.TargetDshVersion = "unknown";

        var decision = UpgradePlanner.Plan(input);

        Assert.False(decision.ShouldUpgrade);
        Assert.Equal(UpgradeBlockReason.TargetUnknown, decision.BlockReason);
        Assert.Equal(UpgradeDecisionKind.ManualOnly, decision.Kind);
    }

    [Fact]
    public void UnparseableInstalledVersionIsManualOnly()
    {
        var input = Input(Candidate("1.2.0"));
        input.InstalledVersion = "latest";

        var decision = UpgradePlanner.Plan(input);

        Assert.False(decision.ShouldUpgrade);
        Assert.Equal(UpgradeBlockReason.InstalledVersionUnknown, decision.BlockReason);
    }

    [Fact]
    public void NullInputIsInvalidInput()
    {
        var decision = UpgradePlanner.Plan(null);

        Assert.False(decision.ShouldUpgrade);
        Assert.Equal(UpgradeBlockReason.InvalidInput, decision.BlockReason);
        Assert.False(decision.RiskExemptionGranted);
    }

    [Fact]
    public void EvaluateGatesShortCircuitsBeforeRegistryLookupReasons()
    {
        var offline = Input(Candidate("1.2.0"));
        offline.Offline = true;
        Assert.Equal(UpgradeBlockReason.Offline, UpgradePlanner.EvaluateGates(offline)?.BlockReason);

        var policy = InputWithConfig(new LoomConfig { AutoUpgradePolicy = "C" });
        Assert.Equal(UpgradeBlockReason.PolicyNotAuto, UpgradePlanner.EvaluateGates(policy)?.BlockReason);

        Assert.Null(UpgradePlanner.EvaluateGates(Input(Candidate("1.2.0"))));
    }

    // ------------------------------------------------------------ helpers

    private static UpgradeCandidate Candidate(
        string version,
        CompatibilityLevel level = CompatibilityLevel.Compatible,
        StaticCheckResult staticResult = StaticCheckResult.Ok,
        bool deprecated = false,
        bool exempted = false,
        params string[] requirements)
    {
        return new UpgradeCandidate
        {
            Version = version,
            Deprecated = deprecated,
            Exempted = exempted,
            EvaluatorLevel = level,
            Engine = CompatibilityEngine.Official,
            Requirements = requirements.ToList(),
            StaticResult = staticResult,
        };
    }

    private static UpgradePlannerInput Input(params UpgradeCandidate[] candidates) =>
        InputWithConfig(new LoomConfig { AutoUpgradePolicy = "A" }, candidates);

    private static UpgradePlannerInput InputWithConfig(LoomConfig config, params UpgradeCandidate[] candidates) => new()
    {
        PackageName = Package,
        InstalledVersion = Installed,
        TargetDshVersion = TargetDsh,
        Candidates = candidates.ToList(),
        Config = config,
    };
}
