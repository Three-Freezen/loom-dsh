using Loom.Core.Infrastructure;

namespace Loom.Core.Ipc;

/// <summary>Single-instance coordination for the tray + CLI pair.</summary>
public static class SingleInstance
{
    public const string MutexName = "Local\\LoomForDsh-Instance-v1";
    public const string ShowEventName = "Local\\LoomForDsh-Show-v1";

    public static bool IsTrayRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(MutexName);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Holds the tray's single-instance handles and signals an already running instance to show itself.</summary>
public sealed class InstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;

    public InstanceLock()
    {
        _mutex = new Mutex(true, SingleInstance.MutexName, out var createdNew);
        IsFirstInstance = createdNew;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstance.ShowEventName);
    }

    public bool IsFirstInstance { get; }

    public void SignalShow()
    {
        try
        {
            _showEvent.Set();
        }
        catch
        {
            // best effort
        }
    }

    public WaitHandle ShowWaitHandle => _showEvent;

    public void Dispose()
    {
        try
        {
            if (IsFirstInstance) _mutex.ReleaseMutex();
        }
        catch
        {
            // best effort
        }

        _mutex.Dispose();
        _showEvent.Dispose();
    }
}

public sealed class QueueRequest
{
    public string Id { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();
    public DateTimeOffset SubmittedAt { get; set; }
    public int ProcessId { get; set; }
}

public sealed class QueueResponse
{
    public string Id { get; set; } = "";
    public int ExitCode { get; set; }
    public string StdOut { get; set; } = "";
    public string StdErr { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>CLI side of the file-based command queue (design 7.4).</summary>
public sealed class QueueClient
{
    private readonly LoomPaths _paths;

    public QueueClient(LoomPaths paths)
    {
        _paths = paths;
    }

    public async Task<QueueResponse?> SubmitAsync(
        string[] args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.QueueRequestsDir);
        Directory.CreateDirectory(_paths.QueueResultsDir);
        var id = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(_paths.QueueRequestsDir, id + ".request.json");
        var responsePath = Path.Combine(_paths.QueueResultsDir, id + ".result.json");
        AtomicFile.WriteJson(requestPath, new QueueRequest
        {
            Id = id,
            Args = args,
            SubmittedAt = DateTimeOffset.Now,
            ProcessId = System.Environment.ProcessId,
        });

        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(responsePath))
                {
                    var response = AtomicFile.ReadJsonSafe<QueueResponse>(responsePath);
                    TryDelete(responsePath);
                    return response;
                }

                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            TryDelete(requestPath);
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }
}

/// <summary>Tray side of the file-based command queue: executes CLI requests on the UI process.</summary>
public sealed class QueueServer : IDisposable
{
    private readonly LoomPaths _paths;
    private readonly Func<string[], CancellationToken, Task<CliProcessResult>> _executor;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _processing;
    private bool _disposed;

    public QueueServer(LoomPaths paths, Func<string[], CancellationToken, Task<CliProcessResult>> executor)
    {
        _paths = paths;
        _executor = executor;
    }

    public void Start()
    {
        Directory.CreateDirectory(_paths.QueueRequestsDir);
        Directory.CreateDirectory(_paths.QueueResultsDir);
        lock (_gate)
        {
            if (_timer is not null) return;
            _timer = new Timer(_ => _ = PumpAsync(), null, 0, 250);
        }
    }

    private async Task PumpAsync()
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_processing) return;
            _processing = true;
        }

        try
        {
            while (!_disposed)
            {
                var request = TryTakeNextRequest(out var requestPath);
                if (request is null)
                {
                    if (requestPath is not null) TryDelete(requestPath);
                    break;
                }

                if (requestPath is not null) TryDelete(requestPath);
                var result = await ExecuteSafeAsync(request).ConfigureAwait(false);
                WriteResponse(request.Id, result);
            }
        }
        catch
        {
            // queue loop must survive
        }
        finally
        {
            lock (_gate)
            {
                _processing = false;
            }
        }
    }

    private QueueRequest? TryTakeNextRequest(out string? path)
    {
        path = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_paths.QueueRequestsDir, "*.request.json")
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                var request = AtomicFile.ReadJsonSafe<QueueRequest>(file);
                if (request is null)
                {
                    path = file;
                    return null;
                }

                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-5))
                {
                    // stale request from a killed CLI
                    path = file;
                    return null;
                }

                path = file;
                return request;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private async Task<CliProcessResult> ExecuteSafeAsync(QueueRequest request)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            return await _executor(request.Args, cts.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new CliProcessResult
            {
                ExitCode = Cli.ExitCodes.Failed,
                StdErr = "命令执行失败：" + exception.Message,
            };
        }
    }

    private void WriteResponse(string id, CliProcessResult result)
    {
        try
        {
            AtomicFile.WriteJson(Path.Combine(_paths.QueueResultsDir, id + ".result.json"), new QueueResponse
            {
                Id = id,
                ExitCode = result.ExitCode,
                StdOut = result.StdOut,
                StdErr = result.StdErr,
                CompletedAt = DateTimeOffset.Now,
            });
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        try
        {
            if (Directory.Exists(_paths.QueueRequestsDir))
            {
                foreach (var file in Directory.EnumerateFiles(_paths.QueueRequestsDir, "*.request.json"))
                {
                    TryDelete(file);
                }
            }
        }
        catch
        {
            // best effort
        }
    }
}

public sealed class CliProcessResult
{
    public int ExitCode { get; set; }
    public string StdOut { get; set; } = "";
    public string StdErr { get; set; } = "";
}
