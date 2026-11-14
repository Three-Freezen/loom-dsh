using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Loom.Core.Abstractions;
using Loom.Core.Environment;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Upgrade;

/// <summary>
/// 升级命令拼装（设计 §11.1 / §11.3）：一次只装一个包，转发给 dsh.cmd 的 pnpm，
/// 参数固定为 <c>add &lt;pkg&gt;@&lt;ver&gt; --save-exact</c>（prerelease 版本原样透传）。
/// 这里只负责“参数/展示命令”，实际进程由 <see cref="IDshCli"/> 执行。
/// </summary>
public static class UpgradeCommandBuilder
{
    /// <summary>精确版本落盘，避免 ^ 范围在下一次安装时漂移到不兼容版本。</summary>
    public const string SaveExactFlag = "--save-exact";

    /// <summary>DSH plugin 子命令（转发到 pnpm 的 add）。</summary>
    public const string AddVerb = "add";

    private const string RuntimeCliRelativePath = @"resources\runtime\cli\bin\dsh.cmd";
    private const string DesktopProfile = "desktop";

    /// <summary>构建 <c>plugin --profile &lt;p&gt; add &lt;pkg&gt;@&lt;ver&gt; --save-exact</c> 的参数（不含 plugin/profile）。</summary>
    public static IReadOnlyList<string> BuildAddArguments(string? packageName, string? version)
    {
        var name = (packageName ?? "").Trim();
        var value = (version ?? "").Trim();
        var spec = value.Length == 0 ? name : name + "@" + value;
        return new[] { AddVerb, spec, SaveExactFlag };
    }

    /// <summary>
    /// 审计用的完整展示命令：<c>"&lt;dsh.cmd&gt;" plugin --profile desktop add "pkg@ver" --save-exact</c>。
    /// 每个 token 都加双引号（package 路径/profile 可能含空格），只用于记录，不用于执行。
    /// </summary>
    public static string BuildDisplayCommand(DshInstallation? installation, IReadOnlyList<string>? args)
    {
        var builder = new StringBuilder();
        builder.Append(Quote(ResolveCliPath(installation)));
        builder.Append(" plugin --profile ").Append(Quote(ResolveProfileName(installation)));
        foreach (var arg in args ?? Array.Empty<string>())
        {
            builder.Append(' ').Append(Quote(arg));
        }

        return Redactor.Redact(builder.ToString());
    }

    /// <summary>dsh.cmd 的解析规则与 DshCli 保持一致（CliPath 优先，其次安装目录默认相对路径）。</summary>
    public static string ResolveCliPath(DshInstallation? installation)
    {
        if (installation is null) return RuntimeCliRelativePath;
        if (!string.IsNullOrWhiteSpace(installation.CliPath)) return installation.CliPath;
        return string.IsNullOrWhiteSpace(installation.InstallDir)
            ? RuntimeCliRelativePath
            : Path.Combine(installation.InstallDir, RuntimeCliRelativePath);
    }

    /// <summary>profile 名取 profile 目录名（默认 desktop），与 DshCli 的约定一致。</summary>
    public static string ResolveProfileName(DshInstallation? installation)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(installation?.ProfileDir))
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(installation!.ProfileDir));
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch
        {
            // 目录名不可解析时退回默认 profile
        }

        return DesktopProfile;
    }

    private static string Quote(string? value)
    {
        // cmd.exe 无法转义引号/换行：展示命令里直接剔除，避免审计文本被截断。
        var sanitized = (value ?? "").Replace("\"", "").Replace("\r", "").Replace("\n", "");
        return "\"" + sanitized + "\"";
    }
}

/// <summary>把既有 <see cref="Loom.Core.Environment.DshCli"/> 适配成可注入端口（默认组合用）。</summary>
public sealed class DshCliAdapter : IDshCli
{
    private readonly Loom.Core.Environment.DshCli _cli;

    public DshCliAdapter(Loom.Core.Environment.DshCli cli)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
    }

    public Task<CliCallResult> RunPluginAsync(
        DshInstallation installation,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => _cli.RunPluginAsync(installation, args, timeout, cancellationToken);
}

/// <summary>失败分类（设计 §11.6）：按 CLI 输出归类，保证“可重试/需人工/离线”判断一致。</summary>
public static class UpgradeFailureClassifier
{
    private static readonly (Regex Pattern, UpgradeFailureClass Class)[] Rules =
    {
        (new Regex(@"ETIMEDOUT|timed?\s*out|timeout", RegexOptions.IgnoreCase | RegexOptions.Compiled), UpgradeFailureClass.Timeout),
        (new Regex(@"E401|E403|\b40[13]\b|unauthor|forbidden|ENEEDAUTH|authenticat|need\s*auth|_auth", RegexOptions.IgnoreCase | RegexOptions.Compiled), UpgradeFailureClass.Auth),
        (new Regex(@"E404|\b404\b|not\s+found|ETARGET|no\s+matching\s+version|not\s+in\s+this\s+registry", RegexOptions.IgnoreCase | RegexOptions.Compiled), UpgradeFailureClass.NotFound),
        (new Regex(@"ERESOLVE|peer\s+depend|unmet\s+peer|conflicting\s+peer", RegexOptions.IgnoreCase | RegexOptions.Compiled), UpgradeFailureClass.PeerConflict),
        (new Regex(@"ENOSPC|no\s+space|disk\s+full", RegexOptions.IgnoreCase | RegexOptions.Compiled), UpgradeFailureClass.Disk),
        (new Regex(@"EPERM|EACCES|access\s+is\s+denied|permission\s+denied", RegexOptions.IgnoreCase | RegexOptions.Compiled), UpgradeFailureClass.Permission),
        (new Regex(@"ENOTFOUND|EAI_AGAIN|ECONNREFUSED|ECONNRESET|ENETUNREACH|getaddrinfo|network|offline|proxy", RegexOptions.IgnoreCase | RegexOptions.Compiled), UpgradeFailureClass.Network),
    };

    /// <summary>从一次 CLI 调用归类失败；<c>exit=0</c> 且无超时视为 None。</summary>
    public static UpgradeFailureClass FromCli(CliCallResult? result, string? extra = null)
    {
        if (result is null) return UpgradeFailureClass.Unknown;
        if (result.TimedOut) return UpgradeFailureClass.Timeout;

        var text = string.Join(
            "\n",
            new[] { result.StdErr, result.StdOut, extra }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        foreach (var (pattern, failureClass) in Rules)
        {
            if (pattern.IsMatch(text)) return failureClass;
        }

        return result.ExitCode == 0 ? UpgradeFailureClass.None : UpgradeFailureClass.InstallFailed;
    }

    /// <summary>分类的中文标签（报告/托盘文案）。</summary>
    public static string Describe(UpgradeFailureClass failure) => failure switch
    {
        UpgradeFailureClass.None => "无失败",
        UpgradeFailureClass.Offline => "离线/无法查询",
        UpgradeFailureClass.Network => "网络不可达/registry 无法访问",
        UpgradeFailureClass.Auth => "认证失败",
        UpgradeFailureClass.NotFound => "包或版本不存在",
        UpgradeFailureClass.Timeout => "超时",
        UpgradeFailureClass.PeerConflict => "依赖/peer 冲突",
        UpgradeFailureClass.Disk => "磁盘空间不足",
        UpgradeFailureClass.Permission => "权限不足",
        UpgradeFailureClass.VerifyFailed => "安装后校验失败",
        UpgradeFailureClass.InstallFailed => "安装命令失败",
        UpgradeFailureClass.BackupFailed => "备份失败",
        UpgradeFailureClass.RollbackFailed => "回滚失败",
        UpgradeFailureClass.AppRunning => "DSH 仍在运行",
        UpgradeFailureClass.Cancelled => "已取消",
        UpgradeFailureClass.InvalidResponse => "registry 返回无法解析",
        _ => "未知失败",
    };
}

/// <summary>
/// 默认 <see cref="IPackageRegistry"/>：走 <c>dsh plugin --profile &lt;p&gt; view &lt;pkg&gt; versions dist-tags --json</c>
/// （即 profile 自己的 pnpm / .npmrc / 私服 / 认证 / 代理），再用 <c>view &lt;pkg&gt;@&lt;ver&gt; --json</c>
/// 拉取最多 20 个候选的 manifest 交给 evaluator 复检。离线时由调用方在查询前短路。
/// 只解析 JSON、不写任何文件；单候选 manifest 失败不影响整体候选列表。
/// </summary>
public sealed class DshCliPackageRegistry : IPackageRegistry
{
    private readonly IDshCli _cli;
    private readonly ILogger _logger;

    public DshCliPackageRegistry(IDshCli cli, ILogger logger)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PackageRegistryQueryResult> QueryAsync(
        PackageRegistryQueryRequest? request,
        CancellationToken cancellationToken = default)
    {
        var result = new PackageRegistryQueryResult();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.PackageName))
            {
                result.Failure = UpgradeFailureClass.InvalidResponse;
                result.Error = "缺少包名，无法查询 registry。";
                return result;
            }

            var args = new[] { "view", request.PackageName, "versions", "dist-tags", "--json" };
            var call = await _cli
                .RunPluginAsync(request.Installation, args, request.Timeout, cancellationToken)
                .ConfigureAwait(false);
            result.Attempts++;
            result.Registry = request.Registry;

            if (call.TimedOut)
            {
                result.Failure = UpgradeFailureClass.Timeout;
                result.Error = $"registry 查询超时（{request.Timeout.TotalSeconds:0}s）。";
                _logger.Log(LogLevel.Warn, "registry 查询超时：" + request.PackageName);
                return result;
            }

            if (call.ExitCode != 0)
            {
                result.Failure = UpgradeFailureClassifier.FromCli(call);
                if (result.Failure == UpgradeFailureClass.None) result.Failure = UpgradeFailureClass.InstallFailed;
                result.Error = $"dsh.cmd view 失败（exit={call.ExitCode}）：{Summarize(call)}";
                _logger.Log(LogLevel.Warn, "registry 查询失败：" + Redactor.Redact(result.Error));
                return result;
            }

            var json = ExtractJson(call.StdOut);
            if (json is null)
            {
                result.Failure = UpgradeFailureClass.InvalidResponse;
                result.Error = "dsh.cmd view 的输出不是 JSON，无法解析候选版本。";
                return result;
            }

            try
            {
                result.Candidates = ParseVersions(json, request.Registry);
            }
            catch (JsonException exception)
            {
                result.Failure = UpgradeFailureClass.InvalidResponse;
                result.Error = "解析 registry JSON 失败：" + exception.Message;
                return result;
            }

            result.Ok = true;
            if (request.FetchManifests)
            {
                await FetchManifestsAsync(request, result, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            result.Ok = false;
            result.Failure = UpgradeFailureClass.Unknown;
            result.Error = "registry 查询异常：" + exception.Message;
            _logger.LogException(exception, "registry 查询异常");
            return result;
        }
        finally
        {
            result.Duration = stopwatch.Elapsed;
        }
    }

    // ------------------------------------------------------------ manifest

    private async Task FetchManifestsAsync(
        PackageRegistryQueryRequest request,
        PackageRegistryQueryResult result,
        CancellationToken cancellationToken)
    {
        var targets = result.Candidates
            .Where(candidate => string.IsNullOrWhiteSpace(candidate.ManifestJson))
            .Select(candidate => new
            {
                Candidate = candidate,
                Version = SemVersion.TryParse(candidate.Version, out var version) ? version : null,
            })
            .Where(item => item.Version is not null)
            .OrderByDescending(item => item.Version)
            .ThenBy(item => item.Candidate.OriginalIndex)
            .Take(Math.Max(0, request.MaxManifests))
            .ToList();

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = new[] { "view", $"{request.PackageName}@{target.Candidate.Version}", "--json" };
            CliCallResult call;
            try
            {
                call = await _cli
                    .RunPluginAsync(request.Installation, args, request.Timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "拉取候选 manifest 失败：" + target.Candidate.Version);
                continue;
            }

            result.Attempts++;
            if (call.TimedOut || call.ExitCode != 0)
            {
                // 单候选失败只意味着“该候选未评估”，绝不影响其它候选。
                continue;
            }

            var json = ExtractJson(call.StdOut);
            if (json is null) continue;

            target.Candidate.ManifestJson = json;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                var (deprecated, deprecationReason, integrity, platforms) = ReadDetail(document.RootElement);
                if (deprecated) target.Candidate.Deprecated = true;
                if (!string.IsNullOrWhiteSpace(deprecationReason)) target.Candidate.DeprecationReason = deprecationReason;
                if (!string.IsNullOrWhiteSpace(integrity)) target.Candidate.Integrity ??= integrity;
                if (platforms.Count > 0 && target.Candidate.Platforms.Count == 0)
                {
                    target.Candidate.Platforms.AddRange(platforms);
                }
            }
            catch (JsonException)
            {
                // JSON 可解析但结构陌生：仍保留 ManifestJson 供 evaluator 尝试。
            }
        }
    }

    // --------------------------------------------------------------- parsing

    private static List<UpgradeCandidate> ParseVersions(string json, string? registry)
    {
        var candidates = new List<UpgradeCandidate>();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            AddFromElement(root, candidates, registry);
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("versions", out var versions))
            {
                AddFromElement(versions, candidates, registry);
            }
            else if (root.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String)
            {
                AddVersion(versionElement.GetString(), root, candidates, registry);
            }
            else
            {
                // { "1.0.0": {...}, "1.1.0": {...} } 形式的版本→详情映射。
                AddFromElement(root, candidates, registry);
            }

            AttachDistTags(root, candidates);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return candidates.Where(candidate => seen.Add(candidate.Version)).ToList();
    }

    private static void AddFromElement(JsonElement element, List<UpgradeCandidate> target, string? registry)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        AddVersion(item.GetString(), null, target, registry);
                    }
                    else if (item.ValueKind == JsonValueKind.Object
                             && item.TryGetProperty("version", out var version)
                             && version.ValueKind == JsonValueKind.String)
                    {
                        AddVersion(version.GetString(), item, target, registry);
                    }
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AddVersion(property.Name, property.Value, target, registry);
                }

                break;
        }
    }

    private static void AddVersion(string? version, JsonElement? detail, List<UpgradeCandidate> target, string? registry)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        var candidate = new UpgradeCandidate
        {
            Version = version.Trim(),
            OriginalIndex = target.Count,
            Registry = registry,
        };
        if (detail is { ValueKind: JsonValueKind.Object } objectDetail)
        {
            var (deprecated, reason, integrity, platforms) = ReadDetail(objectDetail);
            candidate.Deprecated = deprecated;
            candidate.DeprecationReason = reason;
            candidate.Integrity = integrity;
            candidate.Platforms.AddRange(platforms);
            // 详情对象本身就是 manifest（packument 的 versions 映射）：直接留给 evaluator。
            if (objectDetail.TryGetProperty("name", out _) || objectDetail.TryGetProperty("engines", out _))
            {
                candidate.ManifestJson = objectDetail.GetRawText();
            }
        }

        target.Add(candidate);
    }

    private static (bool Deprecated, string? Reason, string? Integrity, List<string> Platforms) ReadDetail(JsonElement detail)
    {
        var deprecated = false;
        string? reason = null;
        string? integrity = null;
        var platforms = new List<string>();

        if (detail.TryGetProperty("deprecated", out var deprecatedElement))
        {
            switch (deprecatedElement.ValueKind)
            {
                case JsonValueKind.String:
                    reason = deprecatedElement.GetString();
                    deprecated = !string.IsNullOrWhiteSpace(reason);
                    break;
                case JsonValueKind.True:
                    deprecated = true;
                    break;
            }
        }

        if (detail.TryGetProperty("dist", out var dist)
            && dist.ValueKind == JsonValueKind.Object
            && dist.TryGetProperty("integrity", out var integrityElement)
            && integrityElement.ValueKind == JsonValueKind.String)
        {
            integrity = integrityElement.GetString();
        }

        if (detail.TryGetProperty("dsh", out var dsh)
            && dsh.ValueKind == JsonValueKind.Object
            && dsh.TryGetProperty("bundle", out var bundle)
            && bundle.ValueKind == JsonValueKind.Object
            && bundle.TryGetProperty("platforms", out var platformsElement)
            && platformsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var platform in platformsElement.EnumerateArray())
            {
                if (platform.ValueKind == JsonValueKind.String)
                {
                    var value = platform.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) platforms.Add(value!);
                }
            }
        }

        return (deprecated, reason, integrity, platforms);
    }

    private static void AttachDistTags(JsonElement root, List<UpgradeCandidate> candidates)
    {
        if (!root.TryGetProperty("dist-tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return;
        foreach (var tag in tags.EnumerateObject())
        {
            string? version = tag.Value.ValueKind switch
            {
                JsonValueKind.String => tag.Value.GetString(),
                JsonValueKind.Object when tag.Value.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String
                    => value.GetString(),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(version)) continue;
            foreach (var candidate in candidates)
            {
                if (!string.Equals(candidate.Version, version, StringComparison.Ordinal)) continue;
                if (!candidate.DistTags.Contains(tag.Name)) candidate.DistTags.Add(tag.Name);
            }
        }
    }

    /// <summary>从可能混有 pnpm 警告的 stdout 中截取第一个完整 JSON 值。</summary>
    private static string? ExtractJson(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var start = stdout.IndexOfAny(new[] { '{', '[' });
        if (start < 0) return null;
        var end = stdout.LastIndexOfAny(new[] { '}', ']' });
        if (end <= start) return null;
        return stdout.Substring(start, end - start + 1);
    }

    private static string Summarize(CliCallResult call)
    {
        var text = string.IsNullOrWhiteSpace(call.StdErr) ? call.StdOut : call.StdErr;
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(4);
        var summary = Redactor.Redact(string.Join(" | ", lines));
        return summary.Length > 400 ? summary[..400] : summary;
    }
}

/// <summary>
/// 策略 A 的执行器（设计 §9.4 / §11.3 / §11.5 / §11.6）：
/// 写前备份 profile 核心文件到 <c>restore-backup\&lt;opId&gt;\</c>（含 sha256）→ journal 逐阶段 →
/// <c>dsh plugin ... add &lt;pkg&gt;@&lt;ver&gt; --save-exact</c> → 回读验证版本/精确 spec →
/// 失败只按本次备份回滚。离线直接返回“无法查询/离线”；dry-run 不落盘。
///
/// 边界：不 kill DSH 进程、不写 DSH 安装目录/app.asar、绝不自动授予风险豁免；
/// 所有状态类磁盘写都走 <see cref="AtomicFile"/>（journal 复用既有 append-only 实现）。
/// </summary>
public sealed class UpgradeService
{
    /// <summary>回滚要覆盖的 profile 核心文件（与文件级还原保持一致）。</summary>
    private static readonly string[] CoreProfileFiles =
    {
        DshDefaults.ManifestFile,
        DshDefaults.LockfileFile,
        DshDefaults.PatchFile,
        DshDefaults.CompatibilityFile,
        DshDefaults.CordisFile,
    };

    private readonly IPackageRegistry _registry;
    private readonly IDshCli _cli;
    private readonly ICompatibilityEvaluator? _evaluator;
    private readonly IProcessProbe? _processProbe;
    private readonly ILogger _logger;

    /// <summary>完整组合：registry + CLI 必填；evaluator/probe 可选（不注入时更保守：候选未评估则拒绝）。</summary>
    public UpgradeService(
        IPackageRegistry registry,
        IDshCli cli,
        ILogger logger,
        ICompatibilityEvaluator? evaluator = null,
        IProcessProbe? processProbe = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _evaluator = evaluator;
        _processProbe = processProbe;
    }

    /// <summary>最简组合（无 evaluator/probe：候选必须由 registry 带回评估结论，否则一律不升级）。</summary>
    public UpgradeService(IPackageRegistry registry, IDshCli cli, ILogger logger)
        : this(registry, cli, logger, null, null)
    {
    }

    /// <summary>
    /// 执行策略 A。任何异常都会转成 <see cref="UpgradeExecutionResult"/>（不向外抛）；
    /// 调用方仍需自行持有全局操作锁并在 DSH 完全退出后调用。
    /// </summary>
    public async Task<UpgradeExecutionResult> UpgradeAsync(
        UpgradeRequest? request,
        CancellationToken cancellationToken = default)
    {
        var run = new RunState(request, _logger);
        try
        {
            return await ExecuteAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return run.Cancel();
        }
        catch (Exception exception)
        {
            return run.Unexpected(exception);
        }
    }

    // ------------------------------------------------------------ execution

    private async Task<UpgradeExecutionResult> ExecuteAsync(RunState run, CancellationToken cancellationToken)
    {
        var request = run.Request;
        if (request is null) return run.Invalid("缺少升级请求，未做任何操作。");
        if (request.Installation is null || string.IsNullOrWhiteSpace(request.Installation.ProfileDir))
        {
            return run.Invalid("未提供 DSH profile 目录，无法升级。");
        }

        if (request.Paths is null) return run.Invalid("未提供工具数据目录，无法升级。");
        if (string.IsNullOrWhiteSpace(request.PackageName)) return run.Invalid("未提供插件包名，无法升级。");

        run.Prepare();
        run.AppendStage(
            OperationStage.Start,
            "start",
            $"开始策略 A 升级：{request.PackageName} 已装 {request.InstalledVersion}，目标 dsh {request.TargetDshVersion}，离线={request.Offline}，演练={request.DryRun}");

        // ---- 1. 前置闸门：策略 A / 离线 / 目标与已装版本 ----------------------
        var gate = UpgradePlanner.EvaluateGates(BuildPlannerInput(request, Array.Empty<UpgradeCandidate>()));
        if (gate is not null)
        {
            return run.Complete(gate, MapGateOutcome(gate), MapGateFailure(gate), gate.Reason);
        }

        // ---- 2. App 必须完全退出（A18：只拒绝、不 kill） ----------------------
        if (_processProbe is not null)
        {
            var (running, count, probeError) = ProbeApp(request);
            if (running)
            {
                var decision = BlockedDecision(
                    request,
                    UpgradeBlockReason.AppRunning,
                    count > 0
                        ? $"DSH 仍在运行（{count} 个进程）：按约定不结束进程、不写安装目录；请完全退出 DSH 后重试。"
                        : $"无法确认 DSH 是否已完全退出（{probeError}）：为安全起见不执行包级升级，请退出 DSH 后重试。");
                return run.Complete(decision, OperationOutcome.NeedsManual, UpgradeFailureClass.AppRunning, decision.Reason);
            }
        }

        // ---- 3. registry 查询（离线已在闸门短路） ----------------------------
        var query = await QueryRegistryAsync(request, run.OperationId, cancellationToken).ConfigureAwait(false);
        run.Audit.QueryDuration = query.Duration;
        run.Audit.QueryAttempts = query.Attempts;
        if (!query.Ok)
        {
            var failure = query.Failure == UpgradeFailureClass.None ? UpgradeFailureClass.Unknown : query.Failure;
            var decision = BlockedDecision(
                request,
                UpgradeBlockReason.RegistryUnavailable,
                $"无法查询/查询失败（{UpgradeFailureClassifier.Describe(failure)}）：{query.Error}；未安装、未授予任何风险豁免。");
            run.Audit.FailureDetail = query.Error;
            return run.Complete(
                decision,
                query.Offline ? OperationOutcome.Skipped : OperationOutcome.NeedsManual,
                failure,
                decision.Reason);
        }

        // ---- 4. 以目标 dshVersion 补评估候选 ---------------------------------
        await AssessCandidatesAsync(request, query.Candidates, cancellationToken).ConfigureAwait(false);

        // ---- 5. 决策 ---------------------------------------------------------
        var plan = UpgradePlanner.Plan(BuildPlannerInput(request, query.Candidates));
        run.Audit.Policy = plan.Policy;
        run.Audit.AllowMajorUpgrade = plan.AllowMajorUpgrade;
        run.Audit.Candidates = plan.Candidates;
        run.Audit.Rejected = plan.Rejected;
        run.AppendStage(OperationStage.Plan, "done", plan.Reason);
        if (!plan.ShouldUpgrade)
        {
            var outcome = plan.Kind == UpgradeDecisionKind.ManualOnly
                ? OperationOutcome.NeedsManual
                : OperationOutcome.Skipped;
            return run.Complete(plan, outcome, UpgradeFailureClass.None, plan.Reason);
        }

        var targetVersion = plan.TargetVersion!;
        var args = UpgradeCommandBuilder.BuildAddArguments(request.PackageName, targetVersion);
        var commandLine = UpgradeCommandBuilder.BuildDisplayCommand(request.Installation, args);
        run.Audit.TargetVersion = targetVersion;
        run.Audit.CommandLine = commandLine;
        run.Audit.SelectedRegistry = plan.Selected?.Registry;
        run.Audit.SelectedIntegrity = plan.Selected?.Integrity;
        run.Result.CommandLine = commandLine;

        if (request.DryRun)
        {
            return run.Complete(
                plan,
                OperationOutcome.DryRun,
                UpgradeFailureClass.None,
                $"演练：将执行 {commandLine}；未备份、未安装、未写 journal/审计文件。");
        }

        // ---- 6. 写前备份 + 哈希 ----------------------------------------------
        var backup = new BackupSession(request.Installation.ProfileDir, request.Paths.RestoreBackupDirFor(run.OperationId), run.OperationId);
        run.Backup = backup;
        try
        {
            backup.Capture(CoreProfileFiles);
            backup.WriteManifest();
            run.Audit.BackupDir = backup.BackupDir;
            run.Audit.BackupFiles = backup.Records;
            run.Audit.RollbackPath = backup.BackupDir;
            run.Result.BackupDir = backup.BackupDir;
            run.LockfileExistedBefore = File.Exists(Path.Combine(request.Installation.ProfileDir, DshDefaults.LockfileFile));
            run.BundlesBefore = ReadBundles(request.Installation.ProfileDir);
            run.AppendStage(
                OperationStage.Backup,
                "done",
                $"已备份 {backup.Records.Count} 个 profile 核心文件到 {backup.BackupDir}（含 sha256）",
                new { backupDir = backup.BackupDir, files = backup.Records.Count });
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "升级备份失败，已中止（未执行安装）", run.OperationId);
            return run.Complete(
                plan,
                OperationOutcome.Failed,
                UpgradeFailureClass.BackupFailed,
                "备份 profile 核心文件失败，已中止且未执行安装：" + exception.Message);
        }

        // ---- 7. 执行安装（一次一个包，--save-exact） --------------------------
        run.AppendStage(OperationStage.Apply, "start", commandLine);
        CliCallResult call;
        try
        {
            run.InstallStarted = true;
            call = await _cli
                .RunPluginAsync(request.Installation, args, request.InstallTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "调用 dsh.cmd 安装失败", run.OperationId);
            return run.RollbackAndFail(plan, UpgradeFailureClass.InstallFailed, "调用 dsh.cmd 安装失败：" + exception.Message);
        }

        run.Audit.ExitCode = call.ExitCode;
        run.Audit.TimedOut = call.TimedOut;
        run.Audit.InstallDuration = call.Duration;
        if (call.TimedOut || call.ExitCode != 0)
        {
            var failure = call.TimedOut ? UpgradeFailureClass.Timeout : UpgradeFailureClassifier.FromCli(call);
            if (failure == UpgradeFailureClass.None) failure = UpgradeFailureClass.InstallFailed;
            var detail = SummarizeCliOutput(call);
            run.Audit.FailureDetail = detail;
            run.AppendStage(
                OperationStage.Apply,
                "failed",
                $"安装失败 exit={call.ExitCode} timedOut={call.TimedOut} class={failure}",
                new { exitCode = call.ExitCode, timedOut = call.TimedOut });
            return run.RollbackAndFail(plan, failure, $"安装失败（{UpgradeFailureClassifier.Describe(failure)}）：exit={call.ExitCode}；{detail}");
        }

        run.AppendStage(
            OperationStage.Apply,
            "done",
            $"安装命令完成 exit=0，耗时 {call.Duration.TotalSeconds:0.0}s",
            new { exitCode = 0 });

        // ---- 8. 回读验证 -----------------------------------------------------
        var problems = VerifyInstalled(request, targetVersion, run.BundlesBefore, run.LockfileExistedBefore, out var installedAfter);
        run.Audit.InstalledVersionAfter = installedAfter;
        run.Audit.VerifyDetail = problems.Count == 0
            ? $"回读版本 {installedAfter}，依赖 spec 为精确版本，bundles/lockfile 校验通过"
            : string.Join("；", problems);
        if (problems.Count > 0)
        {
            run.AppendStage(OperationStage.Verify, "failed", run.Audit.VerifyDetail);
            return run.RollbackAndFail(plan, UpgradeFailureClass.VerifyFailed, "安装后回读校验失败：" + run.Audit.VerifyDetail);
        }

        run.AppendStage(OperationStage.Verify, "done", run.Audit.VerifyDetail);
        run.Result.ProfileChanged = true;
        _logger.Log(
            LogLevel.Info,
            $"策略 A 升级成功：{request.PackageName} {request.InstalledVersion} → {installedAfter}（--save-exact，备份 {run.Audit.BackupDir}）",
            run.OperationId);
        return run.Complete(
            plan,
            OperationOutcome.Succeeded,
            UpgradeFailureClass.None,
            $"已升级 {request.PackageName}：{request.InstalledVersion} → {installedAfter}（--save-exact）；重启 DSH 后生效。");
    }

    // ------------------------------------------------------------- helpers

    private async Task<PackageRegistryQueryResult> QueryRegistryAsync(
        UpgradeRequest request,
        string operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = await _registry.QueryAsync(
                    new PackageRegistryQueryRequest
                    {
                        Installation = request.Installation,
                        PackageName = request.PackageName,
                        AllowPrerelease = request.AllowPrerelease,
                        MaxManifests = request.MaxCandidates,
                        Timeout = request.QueryTimeout,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return query ?? new PackageRegistryQueryResult
            {
                Ok = false,
                Failure = UpgradeFailureClass.InvalidResponse,
                Error = "registry 未返回结果。",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "registry 查询异常", operationId);
            return new PackageRegistryQueryResult
            {
                Ok = false,
                Failure = UpgradeFailureClass.Unknown,
                Error = "registry 查询异常：" + exception.Message,
            };
        }
    }

    /// <summary>只评估“有 manifest 且尚无结论”的候选；评估用目标运行时版本，绝不沿用当前版本。</summary>
    private async Task AssessCandidatesAsync(
        UpgradeRequest request,
        List<UpgradeCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (_evaluator is null || candidates.Count == 0) return;
        // 与 planner 的上限顺序保持一致：先按版本降序取最多 N 个，再评估。
        var pending = candidates
            .Where(candidate => candidate.EvaluatorLevel is null && !string.IsNullOrWhiteSpace(candidate.ManifestJson))
            .Select(candidate => new
            {
                Candidate = candidate,
                Version = SemVersion.TryParse(candidate.Version, out var version) ? version : null,
            })
            .Where(item => item.Version is not null)
            .OrderByDescending(item => item.Version)
            .ThenBy(item => item.Candidate.OriginalIndex)
            .Take(Math.Max(1, request.MaxCandidates))
            .Select(item => item.Candidate)
            .ToList();
        if (pending.Count == 0) return;

        var installation = CloneForTargetRuntime(request.Installation, request.TargetDshVersion);
        var inputs = pending
            .Select(candidate => new PluginManifestInput
            {
                Name = request.PackageName,
                Version = candidate.Version,
                Dir = "",
                ManifestJson = candidate.ManifestJson!,
            })
            .ToList();

        CompatibilityBatchResult? batch = null;
        try
        {
            batch = await _evaluator
                .EvaluateAsync(installation, inputs, new Dictionary<string, List<string>>(StringComparer.Ordinal), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "候选兼容性评估失败，按未知处理", null);
        }

        if (batch is null)
        {
            foreach (var candidate in pending)
            {
                candidate.EvaluatorLevel = CompatibilityLevel.Unknown;
                candidate.EvaluatorError = "evaluator 不可用（异常或无结果）。";
            }

            return;
        }

        var byVersion = new Dictionary<string, PluginCompatibilityResult>(StringComparer.Ordinal);
        foreach (var result in batch.Results)
        {
            if (!string.IsNullOrWhiteSpace(result.Version) && !byVersion.ContainsKey(result.Version))
            {
                byVersion[result.Version] = result;
            }
        }

        foreach (var candidate in pending)
        {
            if (!byVersion.TryGetValue(candidate.Version, out var result))
            {
                candidate.EvaluatorLevel = CompatibilityLevel.Unknown;
                candidate.EvaluatorError = "evaluator 未返回该候选的结果。";
                continue;
            }

            candidate.EvaluatorLevel = result.Level;
            candidate.Engine = batch.Engine;
            candidate.Exempted = result.Exempted;
            candidate.Requirements = result.Requirements?.ToList() ?? new List<string>();
            candidate.EvaluatorError = result.Error;
            candidate.StaticResult = result.Static?.Result ?? StaticCheckResult.NotApplicable;
            candidate.StaticMissingRefs = result.Static?.MissingRefs?.ToList() ?? new List<string>();
        }
    }

    private (bool Running, int Count, string? Error) ProbeApp(UpgradeRequest request)
    {
        try
        {
            var processes = _processProbe!.FindDshProcesses(request.Installation.InstallDir);
            var count = processes?.Count ?? 0;
            return (count > 0, count, null);
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "检查 DSH 进程失败：无法确认 App 已退出，按仍在运行处理（不 kill）");
            return (true, 0, exception.Message);
        }
    }

    private static UpgradePlannerInput BuildPlannerInput(UpgradeRequest request, IReadOnlyList<UpgradeCandidate> candidates) => new()
    {
        PackageName = request.PackageName,
        InstalledVersion = request.InstalledVersion,
        TargetDshVersion = request.TargetDshVersion,
        Candidates = candidates.ToList(),
        Config = request.Config,
        Offline = request.Offline,
        AllowPrerelease = request.AllowPrerelease,
        PrereleaseJustification = request.PrereleaseJustification,
        MaxCandidates = request.MaxCandidates,
    };

    private static UpgradeDecision BlockedDecision(UpgradeRequest request, UpgradeBlockReason reason, string message) => new()
    {
        PackageName = request.PackageName,
        InstalledVersion = request.InstalledVersion,
        TargetDshVersion = request.TargetDshVersion,
        Policy = NormalizePolicy(request.Config?.AutoUpgradePolicy),
        AllowMajorUpgrade = request.Config?.AllowMajorUpgrade ?? false,
        Kind = UpgradeDecisionKind.ManualOnly,
        BlockReason = reason,
        Reason = message,
        RiskExemptionGranted = false,
    };

    private static OperationOutcome MapGateOutcome(UpgradeDecision gate) => gate.BlockReason switch
    {
        UpgradeBlockReason.Offline => OperationOutcome.Skipped,
        UpgradeBlockReason.PolicyNotAuto => OperationOutcome.Skipped,
        _ => OperationOutcome.NeedsManual,
    };

    private static UpgradeFailureClass MapGateFailure(UpgradeDecision gate) => gate.BlockReason switch
    {
        UpgradeBlockReason.Offline => UpgradeFailureClass.Offline,
        _ => UpgradeFailureClass.None,
    };

    private static string NormalizePolicy(string? policy) =>
        string.IsNullOrWhiteSpace(policy) ? "" : policy.Trim().ToUpperInvariant();

    private static string JournalStatus(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Succeeded => "done",
        OperationOutcome.DryRun => "done",
        OperationOutcome.Skipped => "done",
        OperationOutcome.Cancelled => "cancelled",
        _ => "failed",
    };

    private static string? MapErrorCode(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Failed => LoomErrorCodes.Failed,
        OperationOutcome.NeedsManual => LoomErrorCodes.NeedsManual,
        OperationOutcome.Cancelled => LoomErrorCodes.Cancelled,
        _ => null,
    };

    private static DshInstallation CloneForTargetRuntime(DshInstallation source, string targetDshVersion) => new()
    {
        InstallDir = source.InstallDir,
        ProfileDir = source.ProfileDir,
        DshHome = source.DshHome,
        AppVersion = source.AppVersion,
        DshVersion = string.IsNullOrWhiteSpace(targetDshVersion) ? source.DshVersion : targetDshVersion,
        CliPath = source.CliPath,
        ElectronExe = source.ElectronExe,
        AppAsar = source.AppAsar,
        UpdaterRoot = source.UpdaterRoot,
        Source = source.Source,
        BaseBundles = source.BaseBundles?.ToList() ?? new List<string>(),
    };

    // ------------------------------------------------------- profile 读取

    private static string? ReadDependencySpec(string profileDir, string packageName)
    {
        var root = ReadManifest(profileDir);
        if (root?["dependencies"] is not JsonObject dependencies) return null;
        return dependencies[packageName] is JsonValue value && value.TryGetValue<string>(out var spec) ? spec : null;
    }

    private static List<string> ReadBundles(string profileDir)
    {
        var bundles = new List<string>();
        var root = ReadManifest(profileDir);
        if (root?["dsh"] is not JsonObject dsh) return bundles;
        if (dsh["profile"] is not JsonObject profile) return bundles;
        if (profile["bundles"] is not JsonArray array) return bundles;
        foreach (var node in array)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var name)) bundles.Add(name);
        }

        return bundles;
    }

    private static JsonObject? ReadManifest(string profileDir)
    {
        var path = Path.Combine(profileDir, DshDefaults.ManifestFile);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadInstalledVersion(string profileDir, string packageName)
    {
        var manifestPath = Path.Combine(
            profileDir,
            "node_modules",
            packageName.Replace('/', Path.DirectorySeparatorChar),
            "package.json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> VerifyInstalled(
        UpgradeRequest request,
        string targetVersion,
        IReadOnlyList<string> bundlesBefore,
        bool lockfileExistedBefore,
        out string installedAfter)
    {
        var problems = new List<string>();
        var profileDir = request.Installation.ProfileDir;

        installedAfter = ReadInstalledVersion(profileDir, request.PackageName) ?? "";
        if (!VersionsEqual(installedAfter, targetVersion))
        {
            problems.Add($"回读 node_modules 版本为“{(string.IsNullOrEmpty(installedAfter) ? "缺失" : installedAfter)}”，与目标 {targetVersion} 不一致");
        }

        var spec = ReadDependencySpec(profileDir, request.PackageName);
        if (spec is null)
        {
            problems.Add("package.json 中没有该依赖（--save-exact 应写入精确版本）");
        }
        else if (!IsExactSpec(spec, targetVersion))
        {
            problems.Add($"package.json 依赖 spec 为“{spec}”，不是精确版本（应等于 {targetVersion}）");
        }

        var bundlesAfter = ReadBundles(profileDir);
        if (bundlesBefore.Any(bundle => string.Equals(bundle, request.PackageName, StringComparison.OrdinalIgnoreCase))
            && !bundlesAfter.Any(bundle => string.Equals(bundle, request.PackageName, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("dsh.profile.bundles 中缺少该插件");
        }

        if (lockfileExistedBefore && !File.Exists(Path.Combine(profileDir, DshDefaults.LockfileFile)))
        {
            problems.Add("pnpm-lock.yaml 丢失");
        }

        return problems;
    }

    private static bool VersionsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (SemVersion.TryParse(left, out var leftVersion) && SemVersion.TryParse(right, out var rightVersion))
        {
            return leftVersion.Equals(rightVersion);
        }

        return string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);
    }

    private static bool IsExactSpec(string? spec, string targetVersion)
    {
        if (string.IsNullOrWhiteSpace(spec)) return false;
        // ^1.2.3 / ~1.2.3 / >=1.2.3 / 1.x / * 等范围写法都无法被 SemVersion 解析，直接判为非精确。
        return SemVersion.TryParse(spec, out var specVersion)
               && SemVersion.TryParse(targetVersion, out var target)
               && specVersion.Equals(target);
    }

    private static string SummarizeCliOutput(CliCallResult call)
    {
        var text = string.IsNullOrWhiteSpace(call.StdErr) ? call.StdOut : call.StdErr;
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(5);
        var summary = Redactor.Redact(string.Join(" | ", lines));
        return summary.Length > 600 ? summary[..600] : summary;
    }

    // ------------------------------------------------------------ 运行状态

    /// <summary>
    /// 一次升级的运行状态：负责 journal / 审计 / 结果收尾。集中在这里是为了让主流程可读，
    /// 同时保证任何分支（含取消）都会写最后一跳 journal 并持久化审计。
    /// </summary>
    private sealed class RunState
    {
        private readonly ILogger _logger;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public RunState(UpgradeRequest? request, ILogger logger)
        {
            _logger = logger;
            Request = request;
            OperationId = !string.IsNullOrWhiteSpace(request?.OperationId)
                ? request!.OperationId!
                : LoomPaths.NewOperationId("upgrade");
            Result = new UpgradeExecutionResult { OperationId = OperationId, Outcome = OperationOutcome.Failed };
            Audit = new UpgradeAuditRecord
            {
                OperationId = OperationId,
                StartedAt = DateTimeOffset.Now,
                PackageName = request?.PackageName ?? "",
                InstalledVersionBefore = request?.InstalledVersion ?? "",
                TargetDshVersion = request?.TargetDshVersion ?? "",
                Policy = NormalizePolicy(request?.Config?.AutoUpgradePolicy),
                AllowMajorUpgrade = request?.Config?.AllowMajorUpgrade ?? false,
                Offline = request?.Offline ?? false,
                DryRun = request?.DryRun ?? false,
                FailureClass = UpgradeFailureClass.None,
            };
        }

        public UpgradeRequest? Request { get; }
        public string OperationId { get; }
        public UpgradeExecutionResult Result { get; }
        public UpgradeAuditRecord Audit { get; }
        public JournalWriter? Journal { get; private set; }
        public BackupSession? Backup { get; set; }
        public bool InstallStarted { get; set; }
        public IReadOnlyList<string> BundlesBefore { get; set; } = Array.Empty<string>();
        public bool LockfileExistedBefore { get; set; }

        /// <summary>准备数据目录与 journal（dry-run 不写任何文件，因此不创建 journal）。</summary>
        public void Prepare()
        {
            if (Request?.Paths is null) return;
            Directory.CreateDirectory(Request.Paths.ReportsDir);
            if (Request.DryRun) return;
            Journal = new JournalWriter(Request.Paths, OperationId);
        }

        public void AppendStage(OperationStage stage, string status, string message, object? data = null)
        {
            Journal?.Append(OperationId, stage, status, message, data);
        }

        /// <summary>缺少请求/路径时的早退（不写磁盘）。</summary>
        public UpgradeExecutionResult Invalid(string message)
        {
            Result.Messages.Add(message);
            Result.Outcome = OperationOutcome.Failed;
            Result.ErrorCode = LoomErrorCodes.Unsupported;
            Audit.FailureClass = UpgradeFailureClass.None;
            Finalize(message);
            AppendStage(OperationStage.Done, "failed", message);
            return Result;
        }

        public UpgradeExecutionResult Complete(
            UpgradeDecision decision,
            OperationOutcome outcome,
            UpgradeFailureClass failureClass,
            string message)
        {
            Result.Decision = decision;
            Result.Outcome = outcome;
            Result.ErrorCode = MapErrorCode(outcome);
            if (!string.IsNullOrWhiteSpace(message)) Result.Messages.Add(message);

            Audit.Policy = decision.Policy;
            Audit.AllowMajorUpgrade = decision.AllowMajorUpgrade;
            Audit.TargetVersion = decision.TargetVersion ?? Audit.TargetVersion;
            Audit.SelectedRegistry = decision.Selected?.Registry ?? Audit.SelectedRegistry;
            Audit.SelectedIntegrity = decision.Selected?.Integrity ?? Audit.SelectedIntegrity;
            if (decision.Candidates.Count > 0) Audit.Candidates = decision.Candidates;
            if (decision.Rejected.Count > 0) Audit.Rejected = decision.Rejected;
            Audit.RollbackPath ??= Audit.BackupDir;
            Audit.FailureClass = failureClass;
            if (failureClass != UpgradeFailureClass.None && string.IsNullOrWhiteSpace(Audit.FailureDetail))
            {
                Audit.FailureDetail = message;
            }

            Finalize(message);
            AppendStage(
                OperationStage.Done,
                JournalStatus(outcome),
                message,
                new { outcome = outcome.ToString(), failureClass = failureClass.ToString() });
            PersistAudit();
            return Result;
        }

        /// <summary>失败后按本次备份回滚（只回滚该 op 备份过的文件）。</summary>
        public UpgradeExecutionResult RollbackAndFail(
            UpgradeDecision decision,
            UpgradeFailureClass failureClass,
            string message)
        {
            if (Backup is null)
            {
                return Complete(decision, OperationOutcome.Failed, failureClass, message);
            }

            AppendStage(OperationStage.Rollback, "start", "按 restore-backup 只回滚本次操作");
            List<string> errors;
            try
            {
                errors = Backup.Restore();
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "回滚异常", OperationId);
                errors = new List<string> { exception.Message };
            }

            Audit.RolledBack = errors.Count == 0;
            Audit.RollbackPath = Backup.BackupDir;
            if (errors.Count == 0)
            {
                AppendStage(OperationStage.Rollback, "done", $"已回滚 {Backup.Records.Count} 个 profile 文件");
                message += $"；已按 {Backup.BackupDir} 回滚本次写入。";
            }
            else
            {
                AppendStage(OperationStage.Rollback, "failed", string.Join("；", errors));
                failureClass = UpgradeFailureClass.RollbackFailed;
                message += "；回滚不完整：" + string.Join("；", errors);
            }

            return Complete(decision, OperationOutcome.Failed, failureClass, message);
        }

        /// <summary>取消：若已开始安装，同样只回滚本次备份。</summary>
        public UpgradeExecutionResult Cancel()
        {
            var errors = new List<string>();
            if (Backup is not null && InstallStarted)
            {
                AppendStage(OperationStage.Rollback, "start", "操作已取消：按 restore-backup 回滚本次写入");
                try
                {
                    errors = Backup.Restore();
                }
                catch (Exception exception)
                {
                    _logger.LogException(exception, "取消回滚异常", OperationId);
                    errors.Add(exception.Message);
                }

                Audit.RolledBack = errors.Count == 0;
                Audit.RollbackPath = Backup.BackupDir;
                AppendStage(OperationStage.Rollback, errors.Count == 0 ? "done" : "failed",
                    errors.Count == 0 ? "已回滚本次写入" : string.Join("；", errors));
            }

            var failureClass = errors.Count == 0 ? UpgradeFailureClass.Cancelled : UpgradeFailureClass.RollbackFailed;
            var message = errors.Count == 0
                ? "升级已取消（未完成的写入已回滚）。"
                : "升级已取消，但回滚不完整：" + string.Join("；", errors);
            return Complete(Result.Decision, OperationOutcome.Cancelled, failureClass, message);
        }

        /// <summary>兜底：任何未预期异常都以失败结果返回，必要时回滚。</summary>
        public UpgradeExecutionResult Unexpected(Exception exception)
        {
            _logger.LogException(exception, "升级出现未预期异常", OperationId);
            var message = "升级出现未预期异常，已中止：" + exception.Message;
            if (Backup is not null && InstallStarted)
            {
                return RollbackAndFail(Result.Decision, UpgradeFailureClass.Unknown, message);
            }

            return Complete(Result.Decision, OperationOutcome.Failed, UpgradeFailureClass.Unknown, message);
        }

        private void Finalize(string message)
        {
            Audit.FinishedAt = DateTimeOffset.Now;
            Audit.Duration = _stopwatch.Elapsed;
            Audit.Outcome = Result.Outcome.ToString();
            if (!string.IsNullOrWhiteSpace(message) && Audit.FailureClass != UpgradeFailureClass.None)
            {
                Audit.FailureDetail ??= message;
            }

            Result.Audit = Audit;
            Result.BackupDir = Audit.BackupDir;
            Result.CommandLine = Audit.CommandLine;
        }

        private void PersistAudit()
        {
            if (Request?.Paths is null || Request.DryRun) return;
            try
            {
                Directory.CreateDirectory(Request.Paths.ReportsDir);
                var path = Path.Combine(
                    Request.Paths.ReportsDir,
                    "upgrade-" + LoomPaths.SanitizeFileName(OperationId) + ".json");
                AtomicFile.WriteJson(path, Audit);
                Result.AuditPath = path;
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "写升级审计失败（不影响操作结果）", OperationId);
            }
        }
    }

    /// <summary>
    /// 写前备份：把 profile 核心文件复制到 <c>restore-backup\&lt;opId&gt;\</c>，记录 sha256/size，
    /// 并写 backup.json。回滚只使用本会话备份，绝不触碰其它 op。
    /// </summary>
    private sealed class BackupSession
    {
        private readonly string _profileDir;
        private readonly string _operationId;

        public BackupSession(string profileDir, string backupDir, string operationId)
        {
            _profileDir = profileDir;
            BackupDir = backupDir;
            _operationId = operationId;
        }

        public string BackupDir { get; }
        public List<UpgradeBackupFile> Records { get; } = new();

        public void Capture(IReadOnlyList<string> fileNames)
        {
            Directory.CreateDirectory(BackupDir);
            foreach (var fileName in fileNames)
            {
                var source = Path.Combine(_profileDir, fileName);
                if (!File.Exists(source))
                {
                    Records.Add(new UpgradeBackupFile { Name = fileName, Existed = false });
                    continue;
                }

                var bytes = File.ReadAllBytes(source);
                var destination = Path.Combine(BackupDir, fileName);
                AtomicFile.WriteAllBytes(destination, bytes);
                Records.Add(new UpgradeBackupFile
                {
                    Name = fileName,
                    Existed = true,
                    Sha256 = Hashing.Sha256Hex(bytes),
                    Size = bytes.LongLength,
                });
            }
        }

        public void WriteManifest()
        {
            Directory.CreateDirectory(BackupDir);
            AtomicFile.WriteJson(Path.Combine(BackupDir, "backup.json"), new
            {
                schemaVersion = 1,
                kind = "upgrade",
                opId = _operationId,
                takenAt = DateTimeOffset.Now,
                profileDir = _profileDir,
                files = Records,
            });
        }

        /// <summary>只回滚本会话记录的文件；返回错误列表（空 = 成功）。</summary>
        public List<string> Restore()
        {
            var errors = new List<string>();
            foreach (var record in Enumerable.Reverse(Records))
            {
                var target = Path.Combine(_profileDir, record.Name);
                try
                {
                    if (!record.Existed)
                    {
                        if (File.Exists(target)) File.Delete(target);
                        continue;
                    }

                    var backupPath = Path.Combine(BackupDir, record.Name);
                    if (!File.Exists(backupPath))
                    {
                        errors.Add($"备份文件缺失：{record.Name}");
                        continue;
                    }

                    var bytes = File.ReadAllBytes(backupPath);
                    if (!string.IsNullOrEmpty(record.Sha256)
                        && !string.Equals(Hashing.Sha256Hex(bytes), record.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"备份哈希不匹配：{record.Name}");
                        continue;
                    }

                    AtomicFile.WriteAllBytes(target, bytes);
                    if (!string.IsNullOrEmpty(record.Sha256)
                        && !string.Equals(Hashing.Sha256HexOrDefault(target), record.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"回滚后哈希不匹配：{record.Name}");
                    }
                }
                catch (Exception exception)
                {
                    errors.Add($"{record.Name}：{exception.Message}");
                }
            }

            return errors;
        }
    }
}
