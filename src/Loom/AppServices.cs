using System.IO;
using Loom.Core;
using Loom.Core.Abstractions;
using Loom.Core.Cli;
using Loom.Core.Composition;
using Loom.Core.Infrastructure;
using Loom.Core.Ipc;
using Loom.Core.Models;

namespace Loom;

/// <summary>Composition root for the tray/CLI-hosted process: Loom service + command queue + UI hook.</summary>
public sealed class AppServices : IDisposable
{
    private readonly LoomService _loom;
    private bool _disposed;

    public AppServices(LoomOptions options)
    {
        _loom = LoomServiceFactory.Create(options);
        QueueServer = new QueueServer(_loom.Paths, ExecuteCliAsync);
    }

    public LoomService Loom => _loom;
    public LoomPaths Paths => _loom.Paths;
    public QueueServer QueueServer { get; }

    /// <summary>Set by the WPF host once the tray is created.</summary>
    public IUiCommandHandler? UiHandler { get; set; }

    public event EventHandler? ExitRequested;

    public void RequestExit()
    {
        try
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // best effort
        }
    }

    public void StartQueueServer() => QueueServer.Start();

    public async Task<CliProcessResult> ExecuteCliAsync(string[] args, CancellationToken cancellationToken)
    {
        var invocation = CliParser.Parse(args);
        if (invocation.Error is not null)
        {
            return new CliProcessResult { ExitCode = ExitCodes.Busy, StdErr = invocation.Error };
        }

        if (invocation.IsUi)
        {
            if (UiHandler is null)
            {
                return new CliProcessResult
                {
                    ExitCode = ExitCodes.Unavailable,
                    StdErr = "当前进程没有可用的界面宿主。",
                };
            }

            var code = await UiHandler.ShowAsync(invocation.UiTarget ?? "popup", cancellationToken)
                .ConfigureAwait(false);
            return new CliProcessResult { ExitCode = code };
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CliRunner(_loom, UiHandler);
        var exitCode = await runner.RunAsync(invocation, stdout, stderr, cancellationToken).ConfigureAwait(false);
        return new CliProcessResult
        {
            ExitCode = exitCode,
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
        };
    }

    public void TryLogException(Exception exception, string message)
    {
        try
        {
            _loom.Paths.EnsureDirectories();
            using var logger = new FileLogger(_loom.Paths, LoomLevel());
            logger.LogException(exception, message);
        }
        catch
        {
            // best effort
        }
    }

    private LogLevel LoomLevel() => LoomServiceFactory.ParseLogLevel(_loom.Config.LogLevel);

    public static Stream? OpenEmbedded(string logicalName)
    {
        var assembly = typeof(AppServices).Assembly;
        return assembly.GetManifestResourceStream(logicalName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            QueueServer.Dispose();
        }
        catch
        {
        }

        try
        {
            _loom.Dispose();
        }
        catch
        {
        }
    }
}
