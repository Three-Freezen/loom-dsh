using Loom.Core.Infrastructure;

namespace Loom.Core.Ipc;

/// <summary>Single-instance coordination for the tray + CLI pair.</summary>
public static class SingleInstance
{
    // 注意（P2-R2）：这两个名字**有意不按数据根派生**。
    //   * <c>Local\LoomForDsh-Instance-v1</c> 是“人机单实例”语义：安装器 installer\loom-dsh.iss 的
    //     AppMutex 必须与它同名，Inno 才能在升级/卸载前识别正在运行的托盘并等待退出；
    //     改名字会让安装器看不到已有实例，直接覆盖正在运行的程序文件。
    //   * CLI 也用它判断“是否有托盘在运行”（IsTrayRunning 无数据根参数），随后靠 tray-root.json
    //     握手判断是否同根。跨会话/跨用户的人机单实例不在这里解决（同一用户在不同会话各开一个
    //     托盘是允许的）；真正的写操作互斥由 OperationGate 的“数据根派生 mutex + 文件锁”保证。
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
    /// <summary>队列等待上限的环境变量覆盖（秒，10–7200）。</summary>
    public const string TimeoutEnvironmentVariable = "LOOM_QUEUE_TIMEOUT_SECONDS";

    /// <summary>
    /// 默认等待上限：**20 分钟**（原为 5 分钟）。慢盘/大 profile 上托盘执行一次快照或还原就可能超过
    /// 5 分钟，客户端会在托盘仍在正常工作时误报 Busy(4)。正常命令仍是秒级返回，20 分钟只影响
    /// “托盘卡住”时的最坏等待；可用 <see cref="TimeoutEnvironmentVariable"/> 覆盖。
    /// 预计可能超过该上限的 upgrade/recovery 不入队（直连 + 跨进程门控，见 TrayCommandRouting）。
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = ResolveDefaultTimeout();

    private static TimeSpan ResolveDefaultTimeout()
    {
        var raw = System.Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable);
        if (int.TryParse(raw, out var seconds) && seconds is >= 10 and <= 7200)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromMinutes(20);
    }

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
    /// <summary>
    /// P2-R2：队列泵锁文件名。托盘单实例互斥量（<see cref="SingleInstance.MutexName"/>）是
    /// <c>Local\</c> 会话级：便携版共享目录跨用户、或 session 0 计划任务与交互托盘同 data root 时，
    /// 两个托盘会同时泵同一队列。这里用数据根队列目录下的 <see cref="FileShare.None"/> 文件锁
    /// 补齐（跨会话有效、按数据根隔离、进程退出自动释放）；不同数据根天然不互相阻塞。
    /// </summary>
    public const string WorkerLockFileName = "queue.worker.lock";

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

    /// <summary>本服务器使用的队列泵锁文件（测试/诊断可见）。</summary>
    public string WorkerLockPath => WorkerLockPathFor(_paths);

    internal static string WorkerLockPathFor(LoomPaths paths) =>
        Path.Combine(paths.QueueDir, WorkerLockFileName);

    /// <summary>尝试独占打开队列泵锁；已被其它进程/会话持有时返回 null（本次 tick 直接让位）。</summary>
    internal FileStream? TryAcquireWorkerLock()
    {
        try
        {
            Directory.CreateDirectory(_paths.QueueDir);
            return new FileStream(
                WorkerLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch
        {
            return null;
        }
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

        // P2-R2：同一数据根只允许一个托盘进程泵队列；拿不到锁说明另一个会话已经在处理。
        FileStream? workerLock = TryAcquireWorkerLock();
        if (workerLock is null)
        {
            lock (_gate)
            {
                _processing = false;
            }

            return;
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
            try
            {
                workerLock.Dispose();
            }
            catch
            {
                // best effort
            }

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

                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - QueueClient.DefaultTimeout)
                {
                    // stale request from a killed CLI（等待上限已过，客户端早已放弃）
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
            // 与客户端等待上限对齐：否则客户端等 20 分钟、服务端 10 分钟就先取消了。
            using var cts = new CancellationTokenSource(QueueClient.DefaultTimeout);
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
