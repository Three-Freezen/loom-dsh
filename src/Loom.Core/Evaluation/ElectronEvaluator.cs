using System.Text.Json;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Evaluation;

/// <summary>
/// ICompatibilityEvaluator implementation backed by the Electron Node helper (helper.mjs).
/// Engine selection: official @deepseek-ai/dsh-app-boot import -> fallback (vendored
/// semver in the helper) -> unknown. "unknown" is never treated as compatible.
/// </summary>
public sealed class ElectronEvaluator : ICompatibilityEvaluator
{
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(30);

    private readonly LoomPaths _paths;
    private readonly ILogger _logger;

    public ElectronEvaluator(LoomPaths paths, ILogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<CompatibilityBatchResult> EvaluateAsync(
        DshInstallation installation,
        IReadOnlyList<PluginManifestInput> plugins,
        IReadOnlyDictionary<string, List<string>> exemptions,
        CancellationToken cancellationToken = default)
    {
        if (plugins.Count == 0 || string.IsNullOrWhiteSpace(installation.InstallDir))
        {
            return new CompatibilityBatchResult
            {
                Engine = CompatibilityEngine.Unknown,
                EngineDetail = "no plugins or installation to evaluate",
                Results = plugins.Select(plugin => UnknownResult(plugin)).ToList(),
            };
        }

        var request = new HelperRequest
        {
            Mode = "evaluate",
            AppRoot = ResolveAppRoot(installation),
            ProfileDir = installation.ProfileDir ?? "",
            RuntimeVersion = IsKnownVersion(installation.DshVersion) ? installation.DshVersion : null,
            Exemptions = exemptions.ToDictionary(
                pair => pair.Key,
                pair => pair.Value ?? new List<string>(),
                StringComparer.Ordinal),
            Plugins = plugins.Select(plugin => new HelperPluginInput
            {
                Name = plugin.Name ?? "",
                Version = plugin.Version ?? "",
                Dir = plugin.Dir ?? "",
                ManifestJson = plugin.ManifestJson ?? "",
            }).ToList(),
        };

        var response = await ElectronHelper.RunAsync(
                _paths.HelperPath,
                installation.InstallDir,
                request,
                HelperTimeout,
                _logger,
                cancellationToken)
            .ConfigureAwait(false);

        if (response is null)
        {
            return new CompatibilityBatchResult
            {
                Engine = CompatibilityEngine.Unknown,
                EngineDetail = "Electron helper 不可用（helper.mjs 缺失、Electron 启动失败或超时）",
                Results = plugins.Select(plugin => UnknownResult(plugin)).ToList(),
            };
        }

        var engine = ParseEngine(response.Engine);
        var byKey = new Dictionary<string, HelperPluginResult>(StringComparer.Ordinal);
        foreach (var result in response.Results)
        {
            var key = BuildKey(result.Name, result.Version);
            if (!byKey.ContainsKey(key)) byKey[key] = result;
        }

        var mapped = new List<PluginCompatibilityResult>(plugins.Count);
        foreach (var plugin in plugins)
        {
            var key = BuildKey(plugin.Name, plugin.Version);
            if (!byKey.TryGetValue(key, out var result))
            {
                // Fall back to a name-only match (version may be empty in the input).
                result = response.Results.FirstOrDefault(r =>
                    string.Equals(r.Name, plugin.Name, StringComparison.Ordinal));
            }

            mapped.Add(result is null
                ? UnknownResult(plugin, "helper 未返回该插件的结果")
                : MapResult(result));
        }

        return new CompatibilityBatchResult
        {
            Engine = engine,
            EngineDetail = response.EngineDetail,
            Results = mapped,
        };
    }

    private static PluginCompatibilityResult MapResult(HelperPluginResult result)
    {
        var mapped = new PluginCompatibilityResult
        {
            Name = result.Name ?? "",
            Version = result.Version ?? "",
            Level = ParseLevel(result.Level, result.Error),
            Compatible = false,
            Exempted = result.Exempted,
            Requirements = result.Requirements ?? new List<string>(),
            Error = result.Error,
            Static = MapStatic(result.Static),
        };
        // Derive Compatible from the final level (static precheck can downgrade it).
        mapped.Compatible = mapped.Level == CompatibilityLevel.Compatible;
        return mapped;
    }

    private static StaticPrecheck MapStatic(HelperStaticResult? source)
    {
        if (source is null) return new StaticPrecheck { Result = StaticCheckResult.NotApplicable };
        return new StaticPrecheck
        {
            Result = source.Result?.ToLowerInvariant() switch
            {
                "ok" => StaticCheckResult.Ok,
                "unverified" => StaticCheckResult.Unverified,
                "missing" => StaticCheckResult.Missing,
                _ => StaticCheckResult.NotApplicable,
            },
            PatchFiles = source.PatchFiles ?? new List<string>(),
            ClientInject = source.ClientInject ?? new List<string>(),
            MissingRefs = source.MissingRefs ?? new List<string>(),
            Notes = source.Notes ?? new List<string>(),
        };
    }

    private static CompatibilityLevel ParseLevel(string? level, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error)) return CompatibilityLevel.Unknown;
        return level?.Trim().ToLowerInvariant() switch
        {
            "compatible" => CompatibilityLevel.Compatible,
            "incompatible" => CompatibilityLevel.Incompatible,
            "unverified" => CompatibilityLevel.Unverified,
            _ => CompatibilityLevel.Unknown,
        };
    }

    private static CompatibilityEngine ParseEngine(string? engine) => engine?.Trim().ToLowerInvariant() switch
    {
        "official" => CompatibilityEngine.Official,
        "fallback" => CompatibilityEngine.Fallback,
        _ => CompatibilityEngine.Unknown,
    };

    private static PluginCompatibilityResult UnknownResult(PluginManifestInput plugin, string? error = null) => new()
    {
        Name = plugin.Name,
        Version = plugin.Version,
        Level = CompatibilityLevel.Unknown,
        Compatible = false,
        Error = error ?? "evaluator unavailable",
        Static = new StaticPrecheck { Result = StaticCheckResult.NotApplicable },
    };

    private static string ResolveAppRoot(DshInstallation installation)
    {
        if (!string.IsNullOrWhiteSpace(installation.AppAsar)) return installation.AppAsar;
        return Path.Combine(installation.InstallDir, "resources", "app.asar");
    }

    private static bool IsKnownVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version)
        && !string.Equals(version, "unknown", StringComparison.OrdinalIgnoreCase);

    private static string BuildKey(string? name, string? version) => $"{name ?? ""}@{version ?? ""}";
}
