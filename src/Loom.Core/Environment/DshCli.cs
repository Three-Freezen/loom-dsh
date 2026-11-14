using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Environment;

/// <summary>
/// Wraps the DSH CLI. Always invokes the installation's own
/// <c>resources\runtime\cli\bin\dsh.cmd</c> with an argument array (never system pnpm
/// or a string-concatenated shell command), cwd = install dir, scrubbed environment,
/// bounded timeout and per-line logging (the logger already redacts credentials).
/// Falls back to <c>DeepSeek Harness.exe --expose-internals ...\dsh-desktop-host\lib\cli.js</c>
/// only when dsh.cmd is missing. This class does not run installs itself in this phase.
/// </summary>
public sealed class DshCli
{
    /// <summary>Names the DSH subprocess seam strips from child environments.</summary>
    private static readonly Regex SensitiveEnvironmentName =
        new("KEY|PASSWORD|SECRET|TOKEN|AUTH", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string RuntimeCliRelativePath = @"resources\runtime\cli\bin\dsh.cmd";
    private const string HostCliRelativePath =
        @"resources\app.asar\dsh\node_modules\@deepseek-ai\dsh-desktop-host\lib\cli.js";

    private readonly ILogger _logger;

    public DshCli(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Runs <c>dsh plugin --profile &lt;profile&gt; &lt;args...&gt;</c>.
    /// The caller must not hold <c>package.json.lock</c> (the CLI takes it itself).
    /// </summary>
    public Task<CliCallResult> RunPluginAsync(
        DshInstallation installation,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var profileName = ResolveProfileName(installation);
        var fullArgs = new List<string> { "plugin", "--profile", profileName };
        fullArgs.AddRange(args ?? Array.Empty<string>());
        return ExecuteAsync(installation, fullArgs, timeout, cancellationToken);
    }

    /// <summary>Runs an arbitrary DSH CLI argument vector (test/advanced use; no shell interpolation).</summary>
    public Task<CliCallResult> RunAsync(
        DshInstallation installation,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(installation, args ?? Array.Empty<string>(), timeout, cancellationToken);

    private async Task<CliCallResult> ExecuteAsync(
        DshInstallation installation,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = new CliCallResult();
        var started = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(installation.InstallDir))
        {
            result.ExitCode = -1;
            result.StdErr = "DSH installation dir is unknown";
            return result;
        }

        var dshCmd = !string.IsNullOrWhiteSpace(installation.CliPath)
            ? installation.CliPath
            : Path.Combine(installation.InstallDir, RuntimeCliRelativePath);
        var electronExe = !string.IsNullOrWhiteSpace(installation.ElectronExe)
            ? installation.ElectronExe
            : Path.Combine(installation.InstallDir, DshDefaults.ExecutableName);

        ProcessStartInfo startInfo;
        string displayCommandLine;
        var useShellScript = File.Exists(dshCmd);
        if (useShellScript)
        {
            // .cmd files cannot be started directly by CreateProcess; route through cmd.exe.
            var commandLine = BuildBatchCommandLine(dshCmd, args);
            displayCommandLine = commandLine;
            startInfo = new ProcessStartInfo
            {
                FileName = ResolveCommandInterpreter(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = installation.InstallDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                // cmd.exe must receive a raw argument string: .NET's ArgumentList
                // quoting (\" escapes) is not understood by cmd.exe.
                Arguments = "/d /s /c \"" + commandLine + "\"",
            };
        }
        else
        {
            var hostCli = Path.Combine(installation.InstallDir, HostCliRelativePath);
            if (!File.Exists(electronExe))
            {
                result.ExitCode = -1;
                result.StdErr = $"DSH CLI not found: {dshCmd} (and no Electron executable at {electronExe})";
                _logger.Log(LogLevel.Warn, result.StdErr);
                return result;
            }

            displayCommandLine = Quote(electronExe) + " --expose-internals " + Quote(hostCli) + " " + string.Join(" ", args.Select(Quote));
            startInfo = new ProcessStartInfo
            {
                FileName = electronExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = installation.InstallDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("--expose-internals");
            startInfo.ArgumentList.Add(hostCli);
            foreach (var arg in args) startInfo.ArgumentList.Add(arg);
            _logger.Log(LogLevel.Info, "dsh.cmd missing; falling back to Electron host CLI.");
        }

        ApplyScrubbedEnvironment(startInfo, electronExe);
        result.CommandLine = Redactor.Redact(displayCommandLine);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null) return;
                lock (stdout) stdout.AppendLine(eventArgs.Data);
                _logger.Log(LogLevel.Debug, "dsh stdout: " + eventArgs.Data);
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null) return;
                lock (stderr) stderr.AppendLine(eventArgs.Data);
                _logger.Log(LogLevel.Debug, "dsh stderr: " + eventArgs.Data);
            };

            _logger.Log(LogLevel.Info, "Running: " + result.CommandLine);
            if (!process.Start())
            {
                result.ExitCode = -1;
                result.StdErr = "failed to start DSH CLI";
                return result;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                result.ExitCode = process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                result.TimedOut = true;
                result.ExitCode = -1;
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch
                {
                    // best effort
                }

                _logger.Log(
                    LogLevel.Warn,
                    cancellationToken.IsCancellationRequested
                        ? "dsh CLI cancelled."
                        : $"dsh CLI timed out after {timeout.TotalSeconds:0}s.");
            }

            // Flush asynchronous output handlers.
            try
            {
                process.WaitForExit();
            }
            catch
            {
                // best effort
            }

            lock (stdout) result.StdOut = stdout.ToString();
            lock (stderr) result.StdErr = stderr.ToString();
            result.Duration = started.Elapsed;
            _logger.Log(
                LogLevel.Info,
                $"dsh CLI finished: exit={result.ExitCode} timedOut={result.TimedOut} duration={started.Elapsed.TotalSeconds:0.0}s command={result.CommandLine}");
            return result;
        }
        catch (Exception exception)
        {
            started.Stop();
            result.Duration = started.Elapsed;
            result.ExitCode = -1;
            result.StdErr = exception.Message;
            _logger.LogException(exception, "dsh CLI failed");
            return result;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string ResolveProfileName(DshInstallation installation)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(installation.ProfileDir))
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(installation.ProfileDir));
                if (!string.IsNullOrWhiteSpace(name)) return name!;
            }
        }
        catch
        {
            // fall through
        }

        return DshDefaults.DesktopProfile;
    }

    private static string ResolveCommandInterpreter()
    {
        var comSpec = System.Environment.GetEnvironmentVariable("ComSpec");
        return string.IsNullOrWhiteSpace(comSpec) ? "cmd.exe" : comSpec;
    }

    /// <summary>
    /// Builds <c>"&lt;dsh.cmd&gt;" plugin --profile desktop ...</c> for <c>cmd.exe /d /s /c</c>.
    /// The executable is always quoted; plain argument tokens are kept as written (so they
    /// reach the batch file verbatim through <c>%*</c>), and only tokens containing spaces
    /// or cmd metacharacters are quoted. Quotes/newlines are stripped because cmd.exe
    /// cannot escape them.
    /// </summary>
    private static string BuildBatchCommandLine(string dshCmd, IReadOnlyList<string> args)
    {
        var builder = new StringBuilder();
        builder.Append(Quote(dshCmd));
        foreach (var arg in args)
        {
            builder.Append(' ').Append(QuoteArgument(arg));
        }

        return builder.ToString();
    }

    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var sanitized = Sanitize(value);
        return "\"" + sanitized + "\"";
    }

    private static readonly char[] CmdUnsafeCharacters = { ' ', '\t', '&', '|', '<', '>', '^', '(', ')', '"', '%', ',' };

    private static string QuoteArgument(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var sanitized = Sanitize(value);
        return sanitized.IndexOfAny(CmdUnsafeCharacters) >= 0 ? "\"" + sanitized + "\"" : sanitized;
    }

    private static string Sanitize(string value) =>
        value
            .Replace("\"", "")
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace("%", "");

    /// <summary>
    /// Starts the child from the DSH "scrubbed parent env": credential-shaped names and
    /// all DSH_* facts are removed, then the explicit DSH/Electron facts are added back.
    /// </summary>
    private static void ApplyScrubbedEnvironment(ProcessStartInfo startInfo, string electronExe)
    {
        startInfo.Environment.Clear();
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || entry.Value is not string value) continue;
            if (string.IsNullOrEmpty(name)) continue;
            if (SensitiveEnvironmentName.IsMatch(name)) continue;
            if (name.StartsWith("DSH_", StringComparison.OrdinalIgnoreCase)) continue;
            startInfo.Environment[name] = value;
        }

        startInfo.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        startInfo.Environment["DSH_DESKTOP_NODE_EXECUTABLE"] = electronExe;
    }
}
