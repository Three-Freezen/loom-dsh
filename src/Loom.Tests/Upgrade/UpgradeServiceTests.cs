using System.Text.Json.Nodes;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Upgrade;
using Loom.Tests.CoreFlows;
using Loom.Tests.Env;
using Xunit;

namespace Loom.Tests.Upgrade;

/// <summary>
/// 策略 A 执行器覆盖（设计 §11.5 / §11.6）：
/// 成功（备份 → 安装 → 回读校验 → 审计/journal）、失败回滚、校验失败回滚、
/// 离线/策略 B/C 不查询、App 运行中拒绝、无兼容版本不安装不豁免、
/// registry 默认实现的 JSON 解析与失败分类、--save-exact 命令拼装（含引号与 prerelease）。
/// </summary>
public sealed class UpgradeServiceTests
{
    private const string Package = "dsh-plugin-demo";
    private const string TargetDsh = "0.3.0";

    // ------------------------------------------------------------ 成功路径

    [Fact]
    public async Task SuccessfulUpgradeBacksUpInstallsWithSaveExactAndVerifies()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var opId = "up-success";
        var backupDir = sandbox.Paths.RestoreBackupDirFor(opId);
        var originalPackageJson = sandbox.ReadProfileFile("package.json");

        var cli = new FakeCli();
        var sawBackupBeforeInstall = false;
        var packageJsonAtInstall = "";
        cli.Handler = _ =>
        {
            sawBackupBeforeInstall = File.Exists(Path.Combine(backupDir, "backup.json"));
            packageJsonAtInstall = sandbox.ReadProfileFile("package.json");
            ApplyInstalledVersion(sandbox, "1.2.0");
            return new CliCallResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(5) };
        };

        var registry = new FakeRegistry();
        registry.Candidates.Add(CompatibleCandidate("1.2.0"));
        var evaluator = new RecordingEvaluator();
        var logger = new RecordingLogger();
        var service = new UpgradeService(registry, cli, logger, evaluator);

        var result = await service.UpgradeAsync(Request(sandbox, opId, "1.0.0"));

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.True(result.Decision.ShouldUpgrade);
        Assert.Equal("1.2.0", result.Decision.TargetVersion);
        Assert.False(result.Decision.RiskExemptionGranted);

        // 命令：plugin add <pkg>@<ver> --save-exact（一次只装一个包）
        var call = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "add", Package + "@1.2.0", "--save-exact" }, call);
        Assert.NotNull(result.CommandLine);
        Assert.Contains(Package + "@1.2.0", result.CommandLine!);
        Assert.Contains("--save-exact", result.CommandLine!);
        Assert.Contains("plugin --profile", result.CommandLine!);

        // 备份发生在安装之前，且内容就是安装前的 profile
        Assert.True(sawBackupBeforeInstall);
        Assert.Equal(originalPackageJson, packageJsonAtInstall);

        // 备份清单记录了哈希
        var backupManifestPath = Path.Combine(backupDir, "backup.json");
        Assert.True(File.Exists(backupManifestPath));
        var backupRoot = JsonNode.Parse(File.ReadAllText(backupManifestPath))!.AsObject();
        var packageRecord = backupRoot["files"]!.AsArray()
            .Single(node => node!["name"]!.GetValue<string>() == "package.json")!;
        Assert.True(packageRecord["existed"]!.GetValue<bool>());
        Assert.Equal(Hashing.Sha256HexOfText(originalPackageJson), packageRecord["sha256"]!.GetValue<string>());
        Assert.Equal(originalPackageJson, File.ReadAllText(Path.Combine(backupDir, "package.json")));

        // 审计文件
        Assert.NotNull(result.AuditPath);
        Assert.True(File.Exists(result.AuditPath!));
        var audit = JsonNode.Parse(File.ReadAllText(result.AuditPath!))!.AsObject();
        Assert.Equal(opId, audit["operationId"]!.GetValue<string>());
        Assert.Equal(Package, audit["packageName"]!.GetValue<string>());
        Assert.Equal("1.0.0", audit["installedVersionBefore"]!.GetValue<string>());
        Assert.Equal("1.2.0", audit["installedVersionAfter"]!.GetValue<string>());
        Assert.Equal(0, audit["exitCode"]!.GetValue<int>());
        Assert.Contains("--save-exact", audit["commandLine"]!.GetValue<string>());
        Assert.True(audit["backupFiles"]!.AsArray().Count > 0);
        Assert.False(audit["rolledBack"]!.GetValue<bool>());
        Assert.Equal("none", audit["failureClass"]!.GetValue<string>());
        Assert.True(audit["candidates"]!.AsArray().Count > 0);
        Assert.NotNull(audit["selectedIntegrity"]);

        // journal 逐阶段
        var lines = JournalWriter.ReadLines(sandbox.Paths, opId);
        Assert.Contains(lines, line => line.Contains("\"stage\":\"Start\""));
        Assert.Contains(lines, line => line.Contains("\"stage\":\"Plan\""));
        Assert.Contains(lines, line => line.Contains("\"stage\":\"Backup\""));
        Assert.Contains(lines, line => line.Contains("\"stage\":\"Apply\""));
        Assert.Contains(lines, line => line.Contains("\"stage\":\"Verify\""));
        Assert.Contains(lines, line => line.Contains("\"stage\":\"Done\"") && line.Contains("\"status\":\"done\""));

        // 候选以目标 dshVersion 评估（不是当前 0.2.2-rc.1）
        Assert.NotEmpty(evaluator.RuntimeVersions);
        Assert.All(evaluator.RuntimeVersions, version => Assert.Equal(TargetDsh, version));
        Assert.Contains("1.2.0", evaluator.Versions);
        Assert.NotEmpty(logger.Lines);
    }

    [Fact]
    public async Task PrereleaseVersionIsPassedVerbatimAndQuotedInAuditCommand()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var cli = new FakeCli();
        cli.Handler = _ =>
        {
            ApplyInstalledVersion(sandbox, "2.0.0-rc.1");
            return new CliCallResult { ExitCode = 0 };
        };

        var registry = new FakeRegistry();
        registry.Candidates.Add(CompatibleCandidate("2.0.0-rc.1"));
        var service = new UpgradeService(registry, cli, new RecordingLogger(), new RecordingEvaluator());

        var result = await service.UpgradeAsync(Request(sandbox, "up-pre", "1.0.0", request =>
        {
            request.AllowPrerelease = true;
            request.Config.AllowMajorUpgrade = true;
            // 带空格的安装路径用于验证展示命令的引号
            request.Installation.CliPath = @"C:\Program Files\DeepSeek\resources\runtime\cli\bin\dsh.cmd";
        }));

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.True(result.Decision.PrereleaseAccepted);
        Assert.False(string.IsNullOrWhiteSpace(result.Decision.PrereleaseReason));
        var call = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "add", Package + "@2.0.0-rc.1", "--save-exact" }, call);
        Assert.NotNull(result.CommandLine);
        Assert.Contains("\"C:\\Program Files\\DeepSeek\\resources\\runtime\\cli\\bin\\dsh.cmd\"", result.CommandLine!);
        Assert.Contains("\"" + Package + "@2.0.0-rc.1\"", result.CommandLine!);
    }

    [Fact]
    public async Task DryRunBuildsPlanWithoutWritingOrInstalling()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var cli = new FakeCli();
        var registry = new FakeRegistry();
        registry.Candidates.Add(CompatibleCandidate("1.2.0"));
        var service = new UpgradeService(registry, cli, new RecordingLogger(), new RecordingEvaluator());

        var result = await service.UpgradeAsync(Request(sandbox, "up-dry", "1.0.0", request => request.DryRun = true));

        Assert.Equal(OperationOutcome.DryRun, result.Outcome);
        Assert.True(result.Decision.ShouldUpgrade);
        Assert.Empty(cli.Calls);
        Assert.Null(result.AuditPath);
        Assert.False(Directory.Exists(sandbox.Paths.RestoreBackupDirFor("up-dry")));
        Assert.False(File.Exists(sandbox.Paths.JournalFile("up-dry")));
        Assert.Contains("--save-exact", result.CommandLine!);
    }

    // ------------------------------------------------------------ 失败与回滚

    [Fact]
    public async Task FailedInstallRollsBackOnlyThisOperationAndRecordsFailureClass()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var opId = "up-fail";
        var backupDir = sandbox.Paths.RestoreBackupDirFor(opId);
        var originalPackageJson = sandbox.ReadProfileFile("package.json");
        var originalLockfile = sandbox.ReadProfileFile("pnpm-lock.yaml");

        var cli = new FakeCli
        {
            Handler = _ => new CliCallResult
            {
                ExitCode = 1,
                StdErr = "npm ERR! code E404\n404 Not Found - GET https://registry.example/dsh-plugin-demo",
                Duration = TimeSpan.FromMilliseconds(3),
            },
        };
        var registry = new FakeRegistry();
        registry.Candidates.Add(CompatibleCandidate("1.2.0"));
        var service = new UpgradeService(registry, cli, new RecordingLogger(), new RecordingEvaluator());

        var result = await service.UpgradeAsync(Request(sandbox, opId, "1.0.0"));

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Audit);
        Assert.Equal(UpgradeFailureClass.NotFound, result.Audit!.FailureClass);
        Assert.True(result.Audit.RolledBack);
        Assert.Equal(backupDir, result.Audit.RollbackPath);
        Assert.Equal(1, result.Audit.ExitCode);
        Assert.Equal(originalPackageJson, sandbox.ReadProfileFile("package.json"));
        Assert.Equal(originalLockfile, sandbox.ReadProfileFile("pnpm-lock.yaml"));
        Assert.Contains("回滚", string.Join("；", result.Messages));

        var lines = JournalWriter.ReadLines(sandbox.Paths, opId);
        Assert.Contains(lines, line => line.Contains("\"stage\":\"Rollback\""));
        Assert.Contains(lines, line => line.Contains("\"stage\":\"Apply\"") && line.Contains("\"status\":\"failed\""));
        Assert.Contains(lines, line => line.Contains("\"stage\":\"Done\"") && line.Contains("\"status\":\"failed\""));

        // 审计保留候选与拒绝原因（此处候选是兼容的，安装阶段才失败）
        Assert.True(result.Audit!.Candidates.Count > 0);
        Assert.Equal("1.2.0", result.Audit.TargetVersion);
    }

    [Fact]
    public async Task InstallThatWritesCaretSpecFailsVerificationAndRollsBackProfile()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var originalPackageJson = sandbox.ReadProfileFile("package.json");

        var cli = new FakeCli();
        cli.Handler = _ =>
        {
            // 模拟 CLI 没有 --save-exact（写回 ^1.2.0）：必须被回读校验发现并回滚。
            ApplyInstalledVersion(sandbox, "1.2.0", "^1.2.0");
            return new CliCallResult { ExitCode = 0 };
        };
        var registry = new FakeRegistry();
        registry.Candidates.Add(CompatibleCandidate("1.2.0"));
        var service = new UpgradeService(registry, cli, new RecordingLogger(), new RecordingEvaluator());

        var result = await service.UpgradeAsync(Request(sandbox, "up-verify", "1.0.0"));

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Equal(UpgradeFailureClass.VerifyFailed, result.Audit!.FailureClass);
        Assert.True(result.Audit.RolledBack);
        Assert.Contains("不是精确版本", result.Audit.VerifyDetail ?? "");
        Assert.Equal(originalPackageJson, sandbox.ReadProfileFile("package.json"));
    }

    [Fact]
    public async Task CancelledInstallRollsBackAndMarksJournalCancelled()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var opId = "up-cancel";
        var originalPackageJson = sandbox.ReadProfileFile("package.json");

        var cli = new FakeCli { Handler = _ => throw new OperationCanceledException() };
        var registry = new FakeRegistry();
        registry.Candidates.Add(CompatibleCandidate("1.2.0"));
        var service = new UpgradeService(registry, cli, new RecordingLogger(), new RecordingEvaluator());

        var result = await service.UpgradeAsync(Request(sandbox, opId, "1.0.0"));

        Assert.Equal(OperationOutcome.Cancelled, result.Outcome);
        Assert.Equal(UpgradeFailureClass.Cancelled, result.Audit!.FailureClass);
        Assert.True(result.Audit.RolledBack);
        Assert.Equal(originalPackageJson, sandbox.ReadProfileFile("package.json"));
        var lines = JournalWriter.ReadLines(sandbox.Paths, opId);
        Assert.Contains(lines, line => line.Contains("\"stage\":\"Done\"") && line.Contains("\"status\":\"cancelled\""));
    }

    // ------------------------------------------------------------ 不查询/不安装

    [Fact]
    public async Task OfflineReturnsUnableToQueryWithoutRegistryOrCliCalls()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var registry = new FakeRegistry();
        var cli = new FakeCli();
        var service = new UpgradeService(registry, cli, new RecordingLogger(), new RecordingEvaluator());

        var result = await service.UpgradeAsync(Request(sandbox, "up-offline", "1.0.0", request => request.Offline = true));

        Assert.Equal(OperationOutcome.Skipped, result.Outcome);
        Assert.Equal(UpgradeBlockReason.Offline, result.Decision.BlockReason);
        Assert.Equal(0, registry.Calls);
        Assert.Empty(cli.Calls);
        Assert.Contains(result.Messages, message => message.Contains("离线"));
        Assert.False(Directory.Exists(sandbox.Paths.RestoreBackupDirFor("up-offline")));
        Assert.Equal(UpgradeFailureClass.Offline, result.Audit!.FailureClass);
    }

    [Theory]
    [InlineData("B")]
    [InlineData("C")]
    public async Task PolicyOtherThanADoesNotQueryOrInstall(string policy)
    {
        using var sandbox = CreateSandbox("1.0.0");
        var registry = new FakeRegistry();
        var cli = new FakeCli();
        var service = new UpgradeService(registry, cli, new RecordingLogger(), new RecordingEvaluator());

        var result = await service.UpgradeAsync(Request(sandbox, "up-policy", "1.0.0", request => request.Config.AutoUpgradePolicy = policy));

        Assert.Equal(OperationOutcome.Skipped, result.Outcome);
        Assert.Equal(UpgradeBlockReason.PolicyNotAuto, result.Decision.BlockReason);
        Assert.Equal(0, registry.Calls);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task AppStillRunningRefusesBeforeRegistryQuery()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var registry = new FakeRegistry();
        var cli = new FakeCli();
        var probe = new FakeProcessProbe();
        probe.Processes.Add(new DshProcess { Pid = 4242, Name = "DeepSeek Harness" });
        var service = new UpgradeService(registry, cli, new RecordingLogger(), new RecordingEvaluator(), probe);

        var result = await service.UpgradeAsync(Request(sandbox, "up-running", "1.0.0"));

        Assert.Equal(OperationOutcome.NeedsManual, result.Outcome);
        Assert.Equal(UpgradeBlockReason.AppRunning, result.Decision.BlockReason);
        Assert.Equal(UpgradeFailureClass.AppRunning, result.Audit!.FailureClass);
        Assert.Equal(0, registry.Calls);
        Assert.Empty(cli.Calls);
        Assert.Equal(sandbox.InstallDir, Assert.Single(probe.RequestedInstallDirs));
    }

    [Fact]
    public async Task NoCompatibleCandidateNeedsManualAndNeverGrantsExemption()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var registry = new FakeRegistry();
        var rejected = CompatibleCandidate("1.5.0");
        rejected.EvaluatorLevel = CompatibilityLevel.Incompatible;
        rejected.Requirements = new List<string> { "engines.dsh: ^0.2.0" };
        registry.Candidates.Add(rejected);
        var cli = new FakeCli();
        var service = new UpgradeService(registry, cli, new RecordingLogger());

        var result = await service.UpgradeAsync(Request(sandbox, "up-none", "1.0.0"));

        Assert.Equal(OperationOutcome.NeedsManual, result.Outcome);
        Assert.Equal(UpgradeBlockReason.NoCompatibleCandidate, result.Decision.BlockReason);
        Assert.Empty(cli.Calls);
        Assert.False(result.Decision.RiskExemptionGranted);
        Assert.Contains(result.Decision.Rejected, rejection => rejection.Reason.Contains("engines.dsh: ^0.2.0"));
        Assert.NotNull(result.Decision.ManualHint);
        // 绝不自动授予风险豁免：任何命令都不会是 allow-version
        Assert.DoesNotContain(cli.Calls, args => args.Any(arg => arg.Contains("allow-version", StringComparison.OrdinalIgnoreCase)));
        Assert.False(Directory.Exists(sandbox.Paths.RestoreBackupDirFor("up-none")));
        Assert.True(result.Audit!.Rejected.Count > 0);
    }

    [Fact]
    public async Task CandidateWithoutManifestOrConclusionIsRejectedConservatively()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var registry = new FakeRegistry();
        registry.Candidates.Add(new UpgradeCandidate { Version = "1.3.0", StaticResult = StaticCheckResult.Ok });
        var service = new UpgradeService(registry, new FakeCli(), new RecordingLogger(), new RecordingEvaluator());

        var result = await service.UpgradeAsync(Request(sandbox, "up-nomanifest", "1.0.0"));

        Assert.Equal(OperationOutcome.NeedsManual, result.Outcome);
        Assert.Contains(result.Decision.Rejected, rejection => rejection.Code == UpgradeRejectionCodes.EvaluatorMissing);
    }

    [Fact]
    public async Task RegistryFailureIsClassifiedWithoutInstalling()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var registry = new FakeRegistry
        {
            Ok = false,
            Failure = UpgradeFailureClass.Network,
            Error = "getaddrinfo ENOTFOUND registry.example",
        };
        var cli = new FakeCli();
        var service = new UpgradeService(registry, cli, new RecordingLogger(), new RecordingEvaluator());

        var result = await service.UpgradeAsync(Request(sandbox, "up-regfail", "1.0.0"));

        Assert.Equal(OperationOutcome.NeedsManual, result.Outcome);
        Assert.Equal(UpgradeBlockReason.RegistryUnavailable, result.Decision.BlockReason);
        Assert.Equal(UpgradeFailureClass.Network, result.Audit!.FailureClass);
        Assert.Empty(cli.Calls);
        Assert.Contains("无法查询", string.Join("；", result.Messages));
    }

    [Fact]
    public async Task EmptyRegistryResultIsNoCandidatesWithoutInstall()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var registry = new FakeRegistry();
        var cli = new FakeCli();
        var service = new UpgradeService(registry, cli, new RecordingLogger());

        var result = await service.UpgradeAsync(Request(sandbox, "up-empty", "1.0.0"));

        Assert.Equal(OperationOutcome.Skipped, result.Outcome);
        Assert.Equal(UpgradeBlockReason.NoCandidates, result.Decision.BlockReason);
        Assert.Empty(cli.Calls);
    }

    // ------------------------------------------------------ 默认 registry 实现

    [Fact]
    public async Task DshCliRegistryParsesVersionsDistTagsAndManifests()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var versionsJson = """
            {"name":"dsh-plugin-demo",
             "versions":[
               {"version":"1.0.0","deprecated":"use 1.1.0","dist":{"integrity":"sha512-1.0.0"}},
               {"version":"1.1.0","dist":{"integrity":"sha512-1.1.0"}},
               {"version":"1.1.1-beta.1"}],
             "dist-tags":{"latest":"1.1.0","next":"1.1.1-beta.1"}}
            """;
        var cli = new FakeCli();
        cli.Handler = args =>
        {
            if (args.Count == 3 && args[1].StartsWith(Package + "@", StringComparison.Ordinal))
            {
                var version = args[1][(Package.Length + 1)..];
                return new CliCallResult
                {
                    ExitCode = 0,
                    StdOut = $"{{\"name\":\"{Package}\",\"version\":\"{version}\",\"dsh\":{{\"bundle\":{{\"platforms\":[\"win32\"]}}}}}}",
                };
            }

            return new CliCallResult { ExitCode = 0, StdOut = versionsJson };
        };
        var registry = new DshCliPackageRegistry(cli, new RecordingLogger());

        var result = await registry.QueryAsync(new PackageRegistryQueryRequest
        {
            Installation = sandbox.Installation,
            PackageName = Package,
            FetchManifests = true,
            MaxManifests = 20,
            Timeout = TimeSpan.FromSeconds(5),
        });

        Assert.True(result.Ok);
        Assert.Equal(UpgradeFailureClass.None, result.Failure);
        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal(4, result.Attempts); // 1 次 versions + 3 次 manifest
        var latest = result.Candidates.Single(candidate => candidate.Version == "1.1.0");
        Assert.Contains("latest", latest.DistTags);
        Assert.Equal("sha512-1.1.0", latest.Integrity);
        Assert.NotNull(latest.ManifestJson);
        Assert.Contains("win32", latest.Platforms);
        var deprecated = result.Candidates.Single(candidate => candidate.Version == "1.0.0");
        Assert.True(deprecated.Deprecated);
        Assert.Equal("use 1.1.0", deprecated.DeprecationReason);
        Assert.Contains("next", result.Candidates.Single(candidate => candidate.Version == "1.1.1-beta.1").DistTags);
    }

    [Fact]
    public async Task DshCliRegistryParsesVersionMapShapeWithoutManifestFetch()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var cli = new FakeCli
        {
            Handler = _ => new CliCallResult
            {
                ExitCode = 0,
                StdOut = """
                    {"versions":{"1.0.0":{},"1.2.0":{"deprecated":"broken","dist":{"integrity":"sha512-z"}}},
                     "dist-tags":{"latest":"1.2.0"}}
                    """,
            },
        };
        var registry = new DshCliPackageRegistry(cli, new RecordingLogger());

        var result = await registry.QueryAsync(new PackageRegistryQueryRequest
        {
            Installation = sandbox.Installation,
            PackageName = Package,
            FetchManifests = false,
            Timeout = TimeSpan.FromSeconds(5),
        });

        Assert.True(result.Ok);
        Assert.Equal(2, result.Candidates.Count);
        var candidate = result.Candidates.Single(item => item.Version == "1.2.0");
        Assert.True(candidate.Deprecated);
        Assert.Equal("sha512-z", candidate.Integrity);
        Assert.Contains("latest", candidate.DistTags);
        Assert.Equal(1, result.Attempts);
    }

    [Theory]
    [InlineData("npm ERR! code E401\n401 Unauthorized", "Auth")]
    [InlineData("npm ERR! code E404\n404 Not Found", "NotFound")]
    [InlineData("npm ERR! code ETIMEDOUT", "Timeout")]
    [InlineData("getaddrinfo ENOTFOUND registry.example", "Network")]
    [InlineData("npm ERR! ERESOLVE unable to resolve dependency tree", "PeerConflict")]
    [InlineData("npm ERR! code ENOSPC: no space left on device", "Disk")]
    [InlineData("npm ERR! code EACCES permission denied", "Permission")]
    public async Task DshCliRegistryClassifiesQueryFailures(string stderr, string expected)
    {
        using var sandbox = CreateSandbox("1.0.0");
        var cli = new FakeCli { Handler = _ => new CliCallResult { ExitCode = 1, StdErr = stderr } };
        var registry = new DshCliPackageRegistry(cli, new RecordingLogger());

        var result = await registry.QueryAsync(new PackageRegistryQueryRequest
        {
            Installation = sandbox.Installation,
            PackageName = Package,
            Timeout = TimeSpan.FromSeconds(5),
        });

        Assert.False(result.Ok);
        Assert.Equal(expected, result.Failure.ToString());
        Assert.False(result.Offline);
    }

    [Fact]
    public async Task DshCliRegistryReportsTimeoutAndInvalidJson()
    {
        using var sandbox = CreateSandbox("1.0.0");
        var registry = new DshCliPackageRegistry(
            new FakeCli { Handler = _ => new CliCallResult { TimedOut = true, ExitCode = -1 } },
            new RecordingLogger());

        var timedOut = await registry.QueryAsync(new PackageRegistryQueryRequest
        {
            Installation = sandbox.Installation,
            PackageName = Package,
            Timeout = TimeSpan.FromSeconds(1),
        });
        Assert.False(timedOut.Ok);
        Assert.Equal(UpgradeFailureClass.Timeout, timedOut.Failure);

        var broken = new DshCliPackageRegistry(
            new FakeCli { Handler = _ => new CliCallResult { ExitCode = 0, StdOut = "this is not json" } },
            new RecordingLogger());
        var invalid = await broken.QueryAsync(new PackageRegistryQueryRequest
        {
            Installation = sandbox.Installation,
            PackageName = Package,
        });
        Assert.False(invalid.Ok);
        Assert.Equal(UpgradeFailureClass.InvalidResponse, invalid.Failure);
    }

    // ------------------------------------------------------------ 命令拼装

    [Fact]
    public void BuildAddArgumentsUsesSaveExactAndKeepsPrerelease()
    {
        var args = UpgradeCommandBuilder.BuildAddArguments(Package, "2.0.0-rc.1");

        Assert.Equal(new[] { "add", Package + "@2.0.0-rc.1", "--save-exact" }, args);
    }

    [Fact]
    public void DisplayCommandQuotesExecutablePackageAndProfile()
    {
        var installation = new DshInstallation
        {
            InstallDir = @"C:\Program Files\DeepSeek",
            ProfileDir = @"D:\dsh-home\profiles\desktop",
        };
        var args = UpgradeCommandBuilder.BuildAddArguments(Package, "2.0.0-rc.1");

        var display = UpgradeCommandBuilder.BuildDisplayCommand(installation, args);

        Assert.Contains("\"C:\\Program Files\\DeepSeek\\resources\\runtime\\cli\\bin\\dsh.cmd\"", display);
        Assert.Contains("plugin --profile \"desktop\"", display);
        Assert.Contains("\"" + Package + "@2.0.0-rc.1\"", display);
        Assert.Contains("--save-exact", display);
    }

    [Fact]
    public void FailureClassifierMapsCommonOutputs()
    {
        Assert.Equal(
            UpgradeFailureClass.Timeout,
            UpgradeFailureClassifier.FromCli(new CliCallResult { TimedOut = true }));
        Assert.Equal(
            UpgradeFailureClass.None,
            UpgradeFailureClassifier.FromCli(new CliCallResult { ExitCode = 0 }));
        Assert.Equal(
            UpgradeFailureClass.InstallFailed,
            UpgradeFailureClassifier.FromCli(new CliCallResult { ExitCode = 1, StdErr = "something odd" }));
    }

    // ------------------------------------------------------------ helpers

    private static LoomSandbox CreateSandbox(string installedVersion)
    {
        var sandbox = new LoomSandbox();
        sandbox.WriteProfileFile(
            "package.json",
            TestData.PackageJson(
                new[] { "@deepseek-ai/dsh-base", Package },
                new Dictionary<string, string> { [Package] = installedVersion }));
        sandbox.WriteProfileFile(
            "pnpm-lock.yaml",
            TestData.Lockfile(new Dictionary<string, string> { [Package] = installedVersion }));
        sandbox.WriteProfileFile("cordis.patch.yml", TestData.PatchYaml());
        sandbox.AddPlugin(Package, installedVersion, enginesDsh: "^0.2.0");
        return sandbox;
    }

    /// <summary>模拟 dsh.cmd add 成功后的 profile 写回（node_modules 版本 + 依赖 spec）。</summary>
    private static void ApplyInstalledVersion(LoomSandbox sandbox, string version, string? dependencySpec = null)
    {
        var root = JsonNode.Parse(sandbox.ReadProfileFile("package.json"))!.AsObject();
        var dependencies = root["dependencies"] as JsonObject ?? new JsonObject();
        dependencies[Package] = dependencySpec ?? version;
        root["dependencies"] = dependencies;
        sandbox.WriteProfileFile("package.json", root.ToJsonString());
        sandbox.AddPlugin(Package, version, enginesDsh: "^0.3.0");
    }

    private static UpgradeRequest Request(LoomSandbox sandbox, string opId, string installedVersion, Action<UpgradeRequest>? configure = null)
    {
        var request = new UpgradeRequest
        {
            Installation = sandbox.Installation,
            Paths = sandbox.Paths,
            Config = new LoomConfig { AutoUpgradePolicy = "A" },
            PackageName = Package,
            InstalledVersion = installedVersion,
            TargetDshVersion = TargetDsh,
            OperationId = opId,
            QueryTimeout = TimeSpan.FromSeconds(5),
            InstallTimeout = TimeSpan.FromSeconds(10),
        };
        configure?.Invoke(request);
        return request;
    }

    private static UpgradeCandidate CompatibleCandidate(string version) => new()
    {
        Version = version,
        Engine = CompatibilityEngine.Official,
        StaticResult = StaticCheckResult.Ok,
        Integrity = "sha512-" + version,
        DistTags = new List<string> { "latest" },
        ManifestJson = $"{{\"name\":\"{Package}\",\"version\":\"{version}\",\"engines\":{{\"dsh\":\"^0.3.0\"}}}}",
    };

    /// <summary>假 registry：记录调用次数，可返回成功/失败/候选。</summary>
    private sealed class FakeRegistry : IPackageRegistry
    {
        public int Calls;
        public List<UpgradeCandidate> Candidates { get; } = new();
        public bool Ok { get; set; } = true;
        public bool Offline { get; set; }
        public UpgradeFailureClass Failure { get; set; } = UpgradeFailureClass.None;
        public string? Error { get; set; }

        public Task<PackageRegistryQueryResult> QueryAsync(
            PackageRegistryQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PackageRegistryQueryResult
            {
                Ok = Ok,
                Offline = Offline,
                Failure = Failure,
                Error = Error,
                Registry = "mock",
                Candidates = Candidates.Select(candidate => candidate.Clone()).ToList(),
            });
        }
    }

    /// <summary>假 dsh.cmd：记录参数并按 handler 返回结果（可模拟写回/失败/取消）。</summary>
    private sealed class FakeCli : IDshCli
    {
        public List<List<string>> Calls { get; } = new();

        public Func<IReadOnlyList<string>, CliCallResult> Handler { get; set; } = _ => new CliCallResult { ExitCode = 0 };

        public Task<CliCallResult> RunPluginAsync(
            DshInstallation installation,
            IReadOnlyList<string> args,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(args.ToList());
            return Task.FromResult(Handler(args));
        }
    }

    /// <summary>记录评估时使用的运行时版本，并默认返回 compatible/ok。</summary>
    private sealed class RecordingEvaluator : ICompatibilityEvaluator
    {
        public List<string> RuntimeVersions { get; } = new();
        public List<string> Versions { get; } = new();

        public Task<CompatibilityBatchResult> EvaluateAsync(
            DshInstallation installation,
            IReadOnlyList<PluginManifestInput> plugins,
            IReadOnlyDictionary<string, List<string>> exemptions,
            CancellationToken cancellationToken = default)
        {
            RuntimeVersions.Add(installation.DshVersion);
            var batch = new CompatibilityBatchResult { Engine = CompatibilityEngine.Official, EngineDetail = "test" };
            foreach (var plugin in plugins)
            {
                Versions.Add(plugin.Version);
                batch.Results.Add(new PluginCompatibilityResult
                {
                    Name = plugin.Name,
                    Version = plugin.Version,
                    Level = CompatibilityLevel.Compatible,
                    Compatible = true,
                    Static = new StaticPrecheck { Result = StaticCheckResult.Ok },
                });
            }

            return Task.FromResult(batch);
        }
    }
}
