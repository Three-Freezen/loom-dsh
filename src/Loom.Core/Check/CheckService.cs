using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Snapshot;

namespace Loom.Core.Check;

/// <summary>
/// Optional evidence source overrides (tests point these into a sandbox; production keeps the defaults).
/// </summary>
public sealed class CheckEvidenceOptions
{
    public bool UseDefaultLocations { get; set; } = true;
    public string? OperationsJsonlPath { get; set; }
    public string? CrashLogsDir { get; set; }
}

/// <summary>
/// Post-update / on-demand check (design 8.5, 10, 12.2, 12.4):
/// diffs the current profile against the baseline snapshot, grades the recovery evidence,
/// evaluates plugin compatibility (unknown is never treated as compatible) and decides whether a
/// file-level restore is possible.
/// </summary>
public sealed class CheckService : ICheckService
{
    private static readonly Regex StartupPhasePattern = new(@"phase:\s*startup", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HostExitPattern = new(
        @"host\s+(?:exited|exit|failed)|启动失败|host startup",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IProfileReader _profileReader;
    private readonly ICompatibilityEvaluator _evaluator;
    private readonly ILogger _logger;
    private readonly CheckEvidenceOptions _evidenceOptions;

    public CheckService(IProfileReader profileReader, ICompatibilityEvaluator evaluator, ILogger logger)
        : this(profileReader, evaluator, logger, null)
    {
    }

    public CheckService(
        IProfileReader profileReader,
        ICompatibilityEvaluator evaluator,
        ILogger logger,
        CheckEvidenceOptions? evidenceOptions)
    {
        _profileReader = profileReader;
        _evaluator = evaluator;
        _logger = logger;
        _evidenceOptions = evidenceOptions ?? new CheckEvidenceOptions();
    }

    public async Task<CheckResult> CheckAsync(
        DshInstallation installation,
        SnapshotManifest? baseline,
        SnapshotRef? baselineRef,
        VersionDelta versions,
        LoomPaths paths,
        CancellationToken cancellationToken = default)
    {
        if (installation is null) throw new ArgumentNullException(nameof(installation));

        var result = new CheckResult
        {
            CheckedAt = DateTimeOffset.Now,
            Baseline = baselineRef,
            Versions = versions ?? new VersionDelta(),
        };
        result.SnapshotAvailable = baseline is not null;

        ProfileState current;
        try
        {
            current = _profileReader.Read(installation, strict: false);
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "Check: reading the current profile failed");
            result.Issues.Add(new CheckIssue
            {
                Kind = IssueKind.ToolError,
                Severity = Severity.Error,
                Code = "E_PROFILE_READ",
                Message = "读取当前 profile 失败：" + exception.Message,
            });
            await EvaluateCompatibilityAsync(installation, new ProfileState(), baseline, result, cancellationToken)
                .ConfigureAwait(false);
            result.Summary = "检查失败：无法读取当前 profile。";
            return result;
        }

        var hasDifference = false;
        var restorableBundles = new List<string>();
        var restorableDependencies = new List<string>();
        var restorablePatch = false;
        var patchConflict = false;
        var userDisabled = new List<string>();
        var userAdded = new List<string>();
        var evidenceReasons = new List<string>();

        if (baseline is null)
        {
            result.Issues.Add(new CheckIssue
            {
                Kind = IssueKind.SnapshotMissing,
                Severity = Severity.Warning,
                Code = "E_NO_SNAPSHOT",
                Message = "没有可用基线快照：无法判断插件是否被更新/恢复流程移除，仅完成兼容性检查。",
                Detail = "首次运行或快照损坏时属正常现象；建立基线快照后即可比较。",
            });
            result.Evidence = EvidenceLevel.None;
        }
        else
        {
            var installBaseline = new HashSet<string>(
                installation.BaseBundles is { Count: > 0 } ? installation.BaseBundles : DshDefaults.BaselineBundles,
                StringComparer.OrdinalIgnoreCase);

            var currentBundleNames = new List<string>(current.Bundles);
            var currentBundleSet = new HashSet<string>(currentBundleNames, StringComparer.OrdinalIgnoreCase);
            var snapshotBundleNames = new HashSet<string>(
                baseline.Profile.Bundles.Select(bundle => bundle.Name), StringComparer.OrdinalIgnoreCase);

            var snapshotNonBaseline = baseline.Profile.Bundles
                .Where(bundle => bundle.Class != BundleClass.Baseline && !installBaseline.Contains(bundle.Name))
                .ToList();

            // ---- bundles removed from the current list -------------------------------
            foreach (var bundle in snapshotNonBaseline)
            {
                if (currentBundleSet.Contains(bundle.Name)) continue;

                hasDifference = true;
                result.RemovedBundles.Add(bundle.Name);
                var dependencyPresent = current.Dependencies.ContainsKey(bundle.Name);
                var packagePresent = PackageDirectoryExists(installation.ProfileDir, bundle.Name);

                if (bundle.Class == BundleClass.Official)
                {
                    // Design 12.1: an official optional bundle removed by the recovery flow must be restored.
                    restorableBundles.Add(bundle.Name);
                    result.Issues.Add(new CheckIssue
                    {
                        Kind = IssueKind.BundleMissing,
                        Severity = Severity.Info,
                        Code = "I_BUNDLE_OFFICIAL_MISSING",
                        Subject = bundle.Name,
                        Message = $"官方可选 bundle {bundle.Name} 不在当前列表中（疑似被恢复流程移除）；强证据下可文件级还原。",
                    });
                }
                else if (dependencyPresent && packagePresent)
                {
                    // Design 12.4: dependency + package still present -> user disabled, never auto re-add.
                    userDisabled.Add(bundle.Name);
                    result.Issues.Add(new CheckIssue
                    {
                        Kind = IssueKind.BundleMissing,
                        Severity = Severity.Info,
                        Code = "I_BUNDLE_DISABLED",
                        Subject = bundle.Name,
                        Message = $"bundle {bundle.Name} 不在当前列表中，但依赖与包仍在：按用户禁用处理，不自动加回。",
                        Detail = "dependencies 与 node_modules 均存在",
                    });
                }
                else if (!dependencyPresent && !packagePresent)
                {
                    // Design 12.4: neither dependency nor package -> uninstalled; not a restore candidate.
                    result.Issues.Add(new CheckIssue
                    {
                        Kind = IssueKind.BundleMissing,
                        Severity = Severity.Info,
                        Code = "I_BUNDLE_UNINSTALLED",
                        Subject = bundle.Name,
                        Message = $"bundle {bundle.Name} 与依赖都不存在：视为未安装，不自动加回。",
                    });
                }
                else if (!dependencyPresent && packagePresent)
                {
                    // Package installed but the dependency spec is gone: file-level restore can fix this.
                    restorableBundles.Add(bundle.Name);
                    result.Issues.Add(new CheckIssue
                    {
                        Kind = IssueKind.BundleMissing,
                        Severity = Severity.Warning,
                        Code = "W_BUNDLE_DEP_MISSING",
                        Subject = bundle.Name,
                        Message = $"bundle {bundle.Name} 缺失且 package.json 无对应依赖（包仍在 node_modules）；强证据下可文件级还原。",
                    });
                }
                else
                {
                    result.Issues.Add(new CheckIssue
                    {
                        Kind = IssueKind.BundleMissing,
                        Severity = Severity.Warning,
                        Code = "W_BUNDLE_PACKAGE_MISSING",
                        Subject = bundle.Name,
                        Message = $"bundle {bundle.Name} 缺失且 node_modules 中没有该包：需要重新安装，不能仅靠文件级还原。",
                    });
                }
            }

            foreach (var name in currentBundleNames)
            {
                if (snapshotBundleNames.Contains(name) || installBaseline.Contains(name)) continue;
                userAdded.Add(name);
                result.Issues.Add(new CheckIssue
                {
                    Kind = IssueKind.BundleMissing,
                    Severity = Severity.Info,
                    Code = "I_BUNDLE_USER_ADDED",
                    Subject = name,
                    Message = $"bundle {name} 是更新后新增的：保留原位，不视为丢失。",
                });
            }

            // ---- dependencies -------------------------------------------------------
            foreach (var pair in baseline.Profile.Dependencies)
            {
                if (current.Dependencies.TryGetValue(pair.Key, out var currentDependency))
                {
                    if (!string.Equals(currentDependency.Spec, pair.Value.Spec, StringComparison.Ordinal)
                        || !string.Equals(currentDependency.Resolved ?? "", pair.Value.Resolved ?? "", StringComparison.Ordinal))
                    {
                        result.Issues.Add(new CheckIssue
                        {
                            Kind = IssueKind.DependencyMissing,
                            Severity = Severity.Info,
                            Code = "I_DEP_SPEC_CHANGED",
                            Subject = pair.Key,
                            Message = $"依赖 {pair.Key} 的 spec/版本与快照不同（当前 {currentDependency.Spec} / {currentDependency.Resolved ?? "?"}，快照 {pair.Value.Spec} / {pair.Value.Resolved ?? "?"}）：保留当前，不覆盖。",
                        });
                    }

                    continue;
                }

                hasDifference = true;
                result.MissingDependencies.Add(pair.Key);
                var packagePresent = PackageDirectoryExists(installation.ProfileDir, pair.Key);
                if (packagePresent) restorableDependencies.Add(pair.Key);
                result.Issues.Add(new CheckIssue
                {
                    Kind = IssueKind.DependencyMissing,
                    Severity = Severity.Warning,
                    Code = "W_DEP_MISSING",
                    Subject = pair.Key,
                    Message = packagePresent
                        ? $"依赖 {pair.Key} 不在 package.json 中（包仍在 node_modules）；强证据下可文件级还原。"
                        : $"依赖 {pair.Key} 不在 package.json 中且 node_modules 中没有该包：需要重新安装。",
                });
            }

            foreach (var name in current.Dependencies.Keys)
            {
                if (baseline.Profile.Dependencies.ContainsKey(name)) continue;
                result.Issues.Add(new CheckIssue
                {
                    Kind = IssueKind.DependencyMissing,
                    Severity = Severity.Info,
                    Code = "I_DEP_USER_ADDED",
                    Subject = name,
                    Message = $"依赖 {name} 是更新后新增的：保留，不删除。",
                });
            }

            // ---- cordis.patch.yml ---------------------------------------------------
            var baselinePatchSha = baseline.Profile.Patch.Sha256;
            var newestBackup = FindNewestBackupAfter(current.PatchBackups, baseline.TakenAt);
            if (newestBackup is not null)
            {
                hasDifference = true;
                restorablePatch = true;
                result.PatchReset = true;
                result.Issues.Add(new CheckIssue
                {
                    Kind = IssueKind.PatchReset,
                    Severity = Severity.Warning,
                    Code = "W_PATCH_RESET",
                    Subject = DshDefaults.PatchFile,
                    Message = $"检测到 {newestBackup}：cordis.patch.yml 在更新后被备份（恢复流程重置），可从备份或快照还原。",
                });
            }
            else if (!string.IsNullOrEmpty(baselinePatchSha))
            {
                if (string.IsNullOrEmpty(current.PatchSha256))
                {
                    hasDifference = true;
                    restorablePatch = true;
                    result.Issues.Add(new CheckIssue
                    {
                        Kind = IssueKind.PatchReset,
                        Severity = Severity.Warning,
                        Code = "W_PATCH_MISSING",
                        Subject = DshDefaults.PatchFile,
                        Message = "cordis.patch.yml 缺失（快照中存在）：可从快照还原。",
                    });
                }
                else if (!string.Equals(current.PatchSha256, baselinePatchSha, StringComparison.OrdinalIgnoreCase))
                {
                    hasDifference = true;
                    patchConflict = true;
                    result.Issues.Add(new CheckIssue
                    {
                        Kind = IssueKind.PatchReset,
                        Severity = Severity.Warning,
                        Code = "W_PATCH_CHANGED",
                        Subject = DshDefaults.PatchFile,
                        Message = "cordis.patch.yml 与快照不同且没有更新后的备份：可能是用户修改，还原时默认保留当前并提示冲突。",
                    });
                }
            }

            // A version change on its own is informational only: it must not turn a clean update into
            // "needs manual", so the issue is emitted only when the profile actually differs.
            if (result.Versions.AnyChanged && hasDifference)
            {
                result.Issues.Add(new CheckIssue
                {
                    Kind = IssueKind.VersionChanged,
                    Severity = Severity.Info,
                    Code = "I_VERSION_CHANGED",
                    Message = "检测到版本变化：" + result.Versions.Describe(),
                });
            }

            // ---- evidence grading (design 8.5 / 12.2) --------------------------------
            if (newestBackup is not null)
            {
                evidenceReasons.Add($"检测到 {newestBackup}（生成时间晚于更新前快照）");
            }

            var resetToBaseline = currentBundleSet.Count > 0
                                  && currentBundleSet.SetEquals(installBaseline)
                                  && snapshotNonBaseline.Count > 0
                                  && hasDifference;
            if (resetToBaseline)
            {
                evidenceReasons.Add("当前 bundles 与安装包基线完全一致（疑似恢复流程重置）");
            }

            foreach (var reason in ScanOperationLogs(installation, baseline))
            {
                evidenceReasons.Add(reason);
            }

            if (evidenceReasons.Count > 0)
            {
                result.Evidence = EvidenceLevel.Strong;
            }
            else
            {
                var mediumReasons = ScanCrashLogs(baseline).ToList();
                if (mediumReasons.Count > 0)
                {
                    result.Evidence = EvidenceLevel.Medium;
                    evidenceReasons.AddRange(mediumReasons);
                }
                else if (hasDifference)
                {
                    result.Evidence = EvidenceLevel.Weak;
                    evidenceReasons.Add("存在差异，但未找到 .bak-* 备份、基线重置或恢复流程操作记录；只能提示，不自动还原。");
                }
                else
                {
                    result.Evidence = EvidenceLevel.None;
                }
            }
        }

        await EvaluateCompatibilityAsync(installation, current, baseline, result, cancellationToken).ConfigureAwait(false);

        // ---- candidates / decision ------------------------------------------------
        var strong = result.Evidence == EvidenceLevel.Strong;
        var candidates = new List<string>();
        if (strong && baseline is not null)
        {
            candidates.AddRange(restorableBundles);
            candidates.AddRange(restorableDependencies);
            if (restorablePatch) candidates.Add(DshDefaults.PatchFile);
        }

        result.RestoreCandidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.EvidenceReasons = evidenceReasons.Distinct(StringComparer.Ordinal).ToList();
        result.RestorePossible = baseline is not null
                                 && result.Evidence == EvidenceLevel.Strong
                                 && (restorableBundles.Count > 0 || restorableDependencies.Count > 0 || restorablePatch);
        result.RequiresManualReview = (hasDifference && result.Evidence != EvidenceLevel.Strong) || patchConflict;
        result.Summary = BuildSummary(result, baseline, hasDifference, restorableBundles.Count + restorableDependencies.Count);

        _logger.Log(
            LogLevel.Info,
            $"Check done: evidence={result.Evidence} removed={result.RemovedBundles.Count} " +
            $"missingDeps={result.MissingDependencies.Count} restorePossible={result.RestorePossible} " +
            $"incompatible={result.IncompatibleCount} unverified={result.UnverifiedCount} unknown={result.UnknownCount}");

        return result;
    }

    private static bool PackageDirectoryExists(string profileDir, string packageName)
    {
        if (string.IsNullOrWhiteSpace(profileDir) || string.IsNullOrWhiteSpace(packageName)) return false;
        var directory = Path.Combine(profileDir, "node_modules", packageName.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(directory);
    }

    private static string? FindNewestBackupAfter(IEnumerable<string> backups, DateTimeOffset takenAt)
    {
        string? newest = null;
        DateTimeOffset newestTime = DateTimeOffset.MinValue;
        foreach (var name in backups)
        {
            if (!ProfileFiles.TryParseBackupTime(name, out var time)) continue;
            if (time <= takenAt) continue;
            if (time > newestTime)
            {
                newestTime = time;
                newest = name;
            }
        }

        return newest;
    }

    // ------------------------------------------------------------------ evidence

    private IEnumerable<string> ScanOperationLogs(DshInstallation installation, SnapshotManifest baseline)
    {
        foreach (var path in OperationLogCandidates(installation))
        {
            if (!File.Exists(path)) continue;
            IReadOnlyList<string> found;
            try
            {
                found = ScanOperationLogFile(path, baseline.TakenAt);
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "Check: reading operations.jsonl failed: " + path);
                continue;
            }

            foreach (var reason in found) yield return reason;
        }
    }

    private IEnumerable<string> OperationLogCandidates(DshInstallation installation)
    {
        if (_evidenceOptions.UseDefaultLocations)
        {
            var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                yield return Path.Combine(appData, "@deepseek-ai", "dsh-desktop", "plugin-operations", "operations.jsonl");
            }
        }

        if (!string.IsNullOrWhiteSpace(_evidenceOptions.OperationsJsonlPath))
        {
            yield return _evidenceOptions.OperationsJsonlPath!;
        }

        if (!string.IsNullOrWhiteSpace(installation.ProfileDir))
        {
            yield return Path.Combine(installation.ProfileDir, DshDefaults.PluginManagerDir, "operations.jsonl");
            yield return Path.Combine(installation.ProfileDir, "plugin-operations", "operations.jsonl");
        }
    }

    private static IReadOnlyList<string> ScanOperationLogFile(string path, DateTimeOffset baselineTime)
    {
        var reasons = new List<string>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? record;
            try
            {
                record = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (record?["operation"] is not JsonObject operation) continue;
            var time = ParseTime(operation["time"]);
            if (time is null || time <= baselineTime) continue;

            var stage = JsonText(operation["stage"]);
            var reason = JsonText(operation["reason"]);
            var phase = JsonText(operation["phase"]);
            var target = JsonText(operation["target"]);
            var enabled = operation["enabled"] is JsonValue enabledValue
                && enabledValue.TryGetValue<bool>(out var parsedEnabled)
                ? parsedEnabled
                : (bool?)null;

            var isDisableRecord = enabled == false
                                  && (string.Equals(stage, "enable", StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(reason, "remove", StringComparison.OrdinalIgnoreCase))
                                  && (string.IsNullOrEmpty(phase) || string.Equals(phase, "settled", StringComparison.OrdinalIgnoreCase));
            if (!isDisableRecord) continue;

            reasons.Add($"operations.jsonl 在 {time.Value:yyyy-MM-dd HH:mm:ss}Z 记录了 {target} 的禁用/移除操作");
            if (reasons.Count >= 2) break;
        }

        return reasons;
    }

    private IEnumerable<string> ScanCrashLogs(SnapshotManifest baseline)
    {
        foreach (var directory in CrashLogDirectories())
        {
            if (!Directory.Exists(directory)) continue;
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "crash-*.log");
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "Check: enumerating crash logs failed: " + directory);
                continue;
            }

            foreach (var file in files.OrderByDescending(File.GetLastWriteTimeUtc))
            {
                string text;
                try
                {
                    text = ReadTextShared(file);
                }
                catch (Exception exception)
                {
                    _logger.LogException(exception, "Check: reading crash log failed: " + file);
                    continue;
                }

                var time = ParseCrashTime(text) ?? SafeLastWrite(file);
                if (time <= baseline.TakenAt) continue;

                var startup = StartupPhasePattern.IsMatch(text);
                var hostExit = HostExitPattern.IsMatch(text);
                if (!startup && !hostExit) continue;

                yield return $"崩溃日志 {Path.GetFileName(file)}（{time:yyyy-MM-dd HH:mm:ss}Z）显示启动/宿主异常" +
                             (startup ? "（phase: startup）" : "（host exited）");
                break;
            }
        }
    }

    private IEnumerable<string> CrashLogDirectories()
    {
        if (_evidenceOptions.UseDefaultLocations)
        {
            var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                yield return Path.Combine(appData, "@deepseek-ai", "dsh-desktop", "logs");
            }
        }

        if (!string.IsNullOrWhiteSpace(_evidenceOptions.CrashLogsDir))
        {
            yield return _evidenceOptions.CrashLogsDir!;
        }
    }

    private static DateTimeOffset? ParseCrashTime(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("time:", StringComparison.OrdinalIgnoreCase)) continue;
            var value = trimmed[5..].Trim();
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static DateTimeOffset SafeLastWrite(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static string ReadTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static DateTimeOffset? ParseTime(JsonNode? node)
    {
        var text = JsonText(node);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string JsonText(JsonNode? node)
    {
        if (node is not JsonValue value) return "";
        if (value.TryGetValue<string>(out var text)) return text;
        return value.ToJsonString();
    }

    // ------------------------------------------------------- compatibility

    private async Task EvaluateCompatibilityAsync(
        DshInstallation installation,
        ProfileState current,
        SnapshotManifest? baseline,
        CheckResult result,
        CancellationToken cancellationToken)
    {
        var inputs = current.Plugins.Select(plugin => new PluginManifestInput
        {
            Name = plugin.Name,
            Version = plugin.Version,
            Dir = plugin.Dir,
            ManifestJson = BuildManifestJson(plugin),
        }).ToList();

        result.Plugins = current.Plugins;
        if (inputs.Count == 0)
        {
            result.CompatibilityEngine = CompatibilityEngine.Unknown;
            return;
        }

        var exemptions = ResolveExemptions(installation, baseline);
        CompatibilityBatchResult? batch = null;
        try
        {
            batch = await _evaluator
                .EvaluateAsync(installation, inputs, exemptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "Compatibility evaluation failed; treating all plugins as unknown");
        }

        if (batch is null)
        {
            result.CompatibilityEngine = CompatibilityEngine.Unknown;
            foreach (var plugin in current.Plugins)
            {
                ApplyEvaluation(plugin, null, CompatibilityEngine.Unknown, result);
            }

            result.Issues.Add(new CheckIssue
            {
                Kind = IssueKind.CompatibilityUnknown,
                Severity = Severity.Warning,
                Code = "W_COMPAT_ENGINE_UNKNOWN",
                Message = "兼容性评估器不可用：所有插件按“未知”处理，绝不视为兼容。",
            });
            return;
        }

        result.CompatibilityEngine = batch.Engine;
        var byName = new Dictionary<string, PluginCompatibilityResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var evaluation in batch.Results)
        {
            if (!string.IsNullOrWhiteSpace(evaluation.Name)) byName[evaluation.Name] = evaluation;
        }

        foreach (var plugin in current.Plugins)
        {
            byName.TryGetValue(plugin.Name, out var evaluation);
            ApplyEvaluation(plugin, evaluation, batch.Engine, result);
        }
    }

    private static void ApplyEvaluation(
        PluginSnapshot plugin,
        PluginCompatibilityResult? evaluation,
        CompatibilityEngine engine,
        CheckResult result)
    {
        plugin.Engine = engine;
        plugin.Compatibility = evaluation?.Level ?? CompatibilityLevel.Unknown;
        plugin.Requirements = evaluation?.Requirements?.ToList() ?? new List<string>();
        plugin.CompatibilityError = evaluation?.Error;
        if (evaluation?.Static is not null) plugin.Static = evaluation.Static;

        var level = plugin.Compatibility;
        if (level == CompatibilityLevel.Compatible
            && plugin.Static.Result is StaticCheckResult.Unverified or StaticCheckResult.Missing)
        {
            // Design 10.3: anything the static precheck cannot confirm is unverified, never compatible.
            level = CompatibilityLevel.Unverified;
            plugin.Compatibility = level;
        }

        switch (level)
        {
            case CompatibilityLevel.Compatible:
                break;
            case CompatibilityLevel.Incompatible:
                result.IncompatibleCount++;
                result.Issues.Add(new CheckIssue
                {
                    Kind = IssueKind.CompatibilityIncompatible,
                    Severity = Severity.Error,
                    Code = "E_INCOMPATIBLE",
                    Subject = plugin.Name,
                    Message = $"插件 {plugin.Name}@{plugin.Version} 与当前 DSH 运行时（{plugin.Engine}）不兼容。",
                    Detail = plugin.Requirements.Count > 0
                        ? "未满足：" + string.Join("；", plugin.Requirements)
                        : plugin.CompatibilityError,
                });
                break;
            case CompatibilityLevel.Unverified:
                result.UnverifiedCount++;
                result.Issues.Add(new CheckIssue
                {
                    Kind = IssueKind.CompatibilityUnverified,
                    Severity = Severity.Warning,
                    Code = "W_UNVERIFIED",
                    Subject = plugin.Name,
                    Message = $"插件 {plugin.Name}@{plugin.Version} 只能判定为“未验证”：静态预检无法确认，不自动升级、不宣告成功。",
                    Detail = BuildStaticDetail(plugin),
                });
                break;
            case CompatibilityLevel.Unknown:
            default:
                result.UnknownCount++;
                result.Issues.Add(new CheckIssue
                {
                    Kind = IssueKind.CompatibilityUnknown,
                    Severity = Severity.Warning,
                    Code = "W_UNKNOWN",
                    Subject = plugin.Name,
                    Message = $"插件 {plugin.Name}@{plugin.Version} 的兼容性无法判定（未知），绝不视为兼容。",
                    Detail = plugin.CompatibilityError ?? (engine == CompatibilityEngine.Unknown
                        ? "评估器整体不可用（engine=unknown）"
                        : BuildStaticDetail(plugin)),
                });
                break;
        }
    }

    private static string? BuildStaticDetail(PluginSnapshot plugin)
    {
        var parts = new List<string>();
        if (plugin.Static.MissingRefs.Count > 0) parts.Add("缺少静态引用：" + string.Join("，", plugin.Static.MissingRefs));
        if (plugin.Static.ClientInject.Count > 0) parts.Add("client 注入：" + string.Join("，", plugin.Static.ClientInject));
        if (plugin.Static.Notes.Count > 0) parts.AddRange(plugin.Static.Notes);
        return parts.Count == 0 ? null : string.Join("；", parts);
    }

    private static Dictionary<string, List<string>> ResolveExemptions(
        DshInstallation installation,
        SnapshotManifest? baseline)
    {
        var path = Path.Combine(installation.ProfileDir, DshDefaults.CompatibilityFile);
        if (File.Exists(path))
        {
            var current = ProfileFiles.ReadCompatibility(path);
            if (current.Rewritable && current.Exemptions.Count > 0) return current.Exemptions;
        }

        return baseline?.Profile.Compatibility.Exemptions is { Count: > 0 } snapshot
            ? snapshot.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal)
            : new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The evaluator expects the raw manifest JSON; when node_modules is unreadable we synthesise a
    /// minimal manifest from what the reader already captured so `unknown` is still handled gracefully.
    /// </summary>
    private static string BuildManifestJson(PluginSnapshot plugin)
    {
        if (!string.IsNullOrWhiteSpace(plugin.Dir))
        {
            var path = Path.Combine(plugin.Dir, "package.json");
            try
            {
                if (File.Exists(path)) return ReadTextShared(path);
            }
            catch
            {
                // fall through to a synthesised manifest
            }
        }

        var manifest = new JsonObject
        {
            ["name"] = plugin.Name,
            ["version"] = plugin.Version,
        };
        if (plugin.Engines is { Count: > 0 } engines)
        {
            var enginesNode = new JsonObject();
            foreach (var pair in engines) enginesNode[pair.Key] = pair.Value;
            manifest["engines"] = enginesNode;
        }

        if (plugin.PeerDependencies is { Count: > 0 } peers)
        {
            var peersNode = new JsonObject();
            foreach (var pair in peers) peersNode[pair.Key] = pair.Value;
            manifest["peerDependencies"] = peersNode;
        }

        if (plugin.Dsh is { } declaration)
        {
            var dshNode = new JsonObject();
            if (declaration.Bundle)
            {
                var bundleNode = new JsonObject();
                if (declaration.Patch is { Count: > 0 } patches)
                {
                    bundleNode["patch"] = declaration.Patch.Count == 1
                        ? JsonValue.Create(patches[0])
                        : new JsonArray(patches.Select(patch => (JsonNode?)JsonValue.Create(patch)).ToArray());
                }

                if (declaration.Platforms is { Count: > 0 } platforms)
                {
                    dshNode["bundle"] = bundleNode;
                    bundleNode["platforms"] = new JsonArray(platforms.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                }
                else
                {
                    dshNode["bundle"] = bundleNode;
                }
            }

            if (declaration.ClientInject is { Count: > 0 } inject)
            {
                dshNode["client"] = new JsonObject
                {
                    ["inject"] = new JsonArray(inject.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                };
            }

            if (dshNode.Count > 0) manifest["dsh"] = dshNode;
        }

        return manifest.ToJsonString();
    }

    // ------------------------------------------------------------------ summary

    private static string BuildSummary(
        CheckResult result,
        SnapshotManifest? baseline,
        bool hasDifference,
        int restorableCount)
    {
        var parts = new List<string>();
        if (result.Versions is { AnyChanged: true }) parts.Add(result.Versions.Describe());

        if (baseline is null)
        {
            parts.Add("无基线快照，仅完成兼容性检查");
        }
        else if (!hasDifference)
        {
            parts.Add("当前 profile 与基线一致，未发现插件丢失或配置重置");
        }
        else
        {
            var diffs = new List<string>();
            if (result.RemovedBundles.Count > 0) diffs.Add($"{result.RemovedBundles.Count} 个 bundle 缺失");
            if (result.MissingDependencies.Count > 0) diffs.Add($"{result.MissingDependencies.Count} 个依赖缺失");
            if (result.PatchReset) diffs.Add("cordis.patch.yml 被重置");
            if (diffs.Count > 0) parts.Add(string.Join("、", diffs));
            parts.Add("恢复证据：" + EvidenceLabel(result.Evidence));
        }

        if (result.CompatibilityEngine != CompatibilityEngine.Official)
        {
            parts.Add($"兼容性引擎：{result.CompatibilityEngine}");
        }

        if (result.IncompatibleCount > 0) parts.Add($"{result.IncompatibleCount} 个插件不兼容");
        if (result.UnverifiedCount > 0) parts.Add($"{result.UnverifiedCount} 个插件未验证");
        if (result.UnknownCount > 0) parts.Add($"{result.UnknownCount} 个插件兼容性未知");

        if (result.RestorePossible)
        {
            parts.Add($"可直接文件级还原 {restorableCount} 项配置改动（重启 DSH 后生效）");
        }
        else if (result.RequiresManualReview)
        {
            parts.Add("存在差异但证据不足或存在冲突，需要人工确认后再还原");
        }

        return string.Join("；", parts) + "。";
    }

    private static string EvidenceLabel(EvidenceLevel level) => level switch
    {
        EvidenceLevel.Strong => "强",
        EvidenceLevel.Medium => "中",
        EvidenceLevel.Weak => "弱",
        _ => "无",
    };
}
