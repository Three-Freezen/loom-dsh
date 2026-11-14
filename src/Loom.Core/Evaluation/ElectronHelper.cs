using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Evaluation;

/// <summary>Request payload sent to <c>helper.mjs</c> on stdin (schemaVersion 1).</summary>
public sealed class HelperRequest
{
    public int SchemaVersion { get; set; } = 1;
    public string Mode { get; set; } = "evaluate";
    public string AppRoot { get; set; } = "";
    public string ProfileDir { get; set; } = "";
    public string? RuntimeVersion { get; set; }
    public Dictionary<string, List<string>> Exemptions { get; set; } = new();
    public List<HelperPluginInput> Plugins { get; set; } = new();
    public bool ForceFallback { get; set; }
}

public sealed class HelperPluginInput
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Dir { get; set; } = "";
    public string ManifestJson { get; set; } = "";
}

/// <summary>Response payload produced by <c>helper.mjs</c> on stdout (schemaVersion 1).</summary>
public sealed class HelperResponse
{
    public int SchemaVersion { get; set; }
    public string? Mode { get; set; }
    public string Engine { get; set; } = "unknown";
    public string? EngineDetail { get; set; }
    public string? RuntimeVersion { get; set; }
    public string? AppVersion { get; set; }
    public List<string> BaseBundles { get; set; } = new();
    public List<HelperPluginResult> Results { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class HelperPluginResult
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string Level { get; set; } = "unknown";
    public bool Compatible { get; set; }
    public bool Exempted { get; set; }
    public List<string> Requirements { get; set; } = new();
    public HelperStaticResult Static { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class HelperStaticResult
{
    public string Result { get; set; } = "notApplicable";
    public List<string> PatchFiles { get; set; } = new();
    public List<string> ClientInject { get; set; } = new();
    public List<string> MissingRefs { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

/// <summary>Result of a "probe" helper call: runtime/app versions and installation base bundles.</summary>
public sealed class HelperProbeResult
{
    public string Engine { get; set; } = "unknown";
    public string? EngineDetail { get; set; }
    public string? RuntimeVersion { get; set; }
    public string? AppVersion { get; set; }
    public List<string> BaseBundles { get; set; } = new();
}

/// <summary>
/// Runs the Electron Node helper (helper.mjs) as a child process:
/// <c>ELECTRON_RUN_AS_NODE=1 &lt;installDir&gt;\DeepSeek Harness.exe --expose-internals &lt;helper.mjs&gt;</c>.
/// stdin = one JSON request, stdout = one JSON response, stderr = diagnostics.
/// Never throws: an unavailable/crashing/timed-out helper returns null so callers can
/// degrade to <see cref="CompatibilityEngine.Unknown"/>.
/// </summary>
public static class ElectronHelper
{
    public const int DefaultTimeoutMs = 30_000;

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ElectronExecutablePath(string installDir) =>
        Path.Combine(installDir, DshDefaults.ExecutableName);

    public static bool IsAvailable(string helperPath, string installDir) =>
        !string.IsNullOrWhiteSpace(helperPath)
        && File.Exists(helperPath)
        && File.Exists(ElectronExecutablePath(installDir));

    /// <summary>Runs helper.mjs with the given request. Returns null when the bridge is unavailable or fails.</summary>
    public static async Task<HelperResponse?> RunAsync(
        string helperPath,
        string installDir,
        HelperRequest request,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(helperPath))
        {
            logger.Log(LogLevel.Debug, $"Electron helper missing: {helperPath}");
            return null;
        }

        var executable = ElectronExecutablePath(installDir);
        if (!File.Exists(executable))
        {
            logger.Log(LogLevel.Debug, $"Electron executable missing: {executable}");
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(installDir) ? installDir : System.Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("--expose-internals");
        startInfo.ArgumentList.Add(helperPath);
        startInfo.Environment["ELECTRON_RUN_AS_NODE"] = "1";

        var payload = JsonSerializer.Serialize(request, RequestOptions);
        var started = Stopwatch.StartNew();

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                logger.Log(LogLevel.Warn, "Electron helper failed to start.");
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            await process.StandardInput.WriteAsync(payload).ConfigureAwait(false);
            process.StandardInput.Close();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                logger.Log(
                    LogLevel.Warn,
                    cancellationToken.IsCancellationRequested
                        ? "Electron helper cancelled."
                        : $"Electron helper timed out after {timeout.TotalMilliseconds:0}ms.");
                return null;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            started.Stop();

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                var stderrText = stderr.Trim();
                if (stderrText.Length > 4000) stderrText = stderrText[..4000] + "…";
                logger.Log(LogLevel.Debug, "Electron helper stderr: " + stderrText);
            }

            if (process.ExitCode != 0)
            {
                logger.Log(LogLevel.Warn, $"Electron helper exited with {process.ExitCode} in {started.ElapsedMilliseconds}ms.");
                if (string.IsNullOrWhiteSpace(stdout)) return null;
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                logger.Log(LogLevel.Warn, "Electron helper produced no stdout JSON.");
                return null;
            }

            var response = JsonSerializer.Deserialize<HelperResponse>(stdout, JsonDefaults.Options);
            if (response is null)
            {
                logger.Log(LogLevel.Warn, "Electron helper stdout was not the expected JSON object.");
                return null;
            }

            logger.Log(
                LogLevel.Debug,
                $"Electron helper ok: engine={response.Engine} runtime={response.RuntimeVersion ?? "?"} results={response.Results.Count} in {started.ElapsedMilliseconds}ms");
            return response;
        }
        catch (Win32Exception exception)
        {
            logger.Log(LogLevel.Warn, $"Cannot start Electron helper: {exception.Message}");
            return null;
        }
        catch (Exception exception)
        {
            logger.LogException(exception, "Electron helper failed");
            TryKill(process);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>Probe call used by the locator/doctor: runtime version, app version and installation base bundles.</summary>
    public static async Task<HelperProbeResult?> ProbeAsync(
        string helperPath,
        string installDir,
        string appRoot,
        string profileDir,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var response = await RunAsync(
            helperPath,
            installDir,
            new HelperRequest
            {
                Mode = "probe",
                AppRoot = appRoot,
                ProfileDir = profileDir,
            },
            timeout,
            logger,
            cancellationToken).ConfigureAwait(false);
        if (response is null) return null;
        return new HelperProbeResult
        {
            Engine = response.Engine,
            EngineDetail = response.EngineDetail,
            RuntimeVersion = response.RuntimeVersion,
            AppVersion = response.AppVersion,
            BaseBundles = response.BaseBundles,
        };
    }

    private static void TryKill(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best effort
        }
    }
}
