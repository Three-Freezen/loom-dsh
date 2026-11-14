using System.Text;
using Loom.Core.Abstractions;
using Loom.Core.Models;

namespace Loom.Core.Infrastructure;

/// <summary>Rotating text logger (loom.log, 5 MB x 3). Thread-safe, redacted, one line per entry.</summary>
public sealed class FileLogger : ILogger, IDisposable
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int MaxFiles = 3;

    /// <summary>
    /// 跨进程日志互斥量（P2-2 / P2-R2）：名字由数据根派生，不同数据根互不阻塞；同一数据根在
    /// 同一会话内共用一把锁。跨会话仍由“打开即 EOF + FileMode.Append”兜底（宁可不丢日志）。
    /// </summary>
    public static string LogMutexName => LogMutexNameFor(null);

    /// <summary>按数据根派生日志互斥量名（与 <see cref="OperationGate"/> 同一套哈希规则）。</summary>
    public static string LogMutexNameFor(string? dataRoot) => ProcessNames.LogMutex(dataRoot);

    /// <summary>等待日志互斥量的上限；超时后仍然写入（FileMode.Append 本身也会定位 EOF，宁可不丢日志）。</summary>
    private static readonly TimeSpan LogMutexTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly LoomPaths _paths;
    private readonly LogLevel _minimum;
    private readonly string _logMutexName;
    private long _written;
    private bool _disposed;

    public FileLogger(LoomPaths paths, LogLevel minimum = LogLevel.Info)
    {
        _paths = paths;
        _minimum = minimum;
        _logMutexName = LogMutexNameFor(paths.DataRoot);
        Directory.CreateDirectory(paths.LogsDir);
        _written = TryGetLength();
    }

    public string LogFilePath => _paths.LogFilePath;

    public void Log(LogLevel level, string message, string? operationId = null)
    {
        if (level > _minimum) return;
        WriteLine(level, message, operationId);
    }

    public void LogException(Exception exception, string message, string? operationId = null)
    {
        WriteLine(LogLevel.Error, $"{message} :: {exception.GetType().Name}: {exception.Message}", operationId);
        if (_minimum >= LogLevel.Debug)
        {
            WriteLine(LogLevel.Debug, exception.ToString(), operationId);
        }
    }

    private void WriteLine(LogLevel level, string message, string? operationId)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level.ToString().ToUpperInvariant()}]"
                   + (string.IsNullOrEmpty(operationId) ? "" : $" [{operationId}]")
                   + $" {Redactor.Redact(message)}";
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                using var logMutex = TryAcquireLogMutex(_logMutexName);
                AppendLine(line);
                if (_written > MaxBytes) RotateLocked();
            }
            catch
            {
                // logging must never break the operation
            }
        }
    }

    /// <summary>
    /// 每次写入都 open/close：<see cref="FileMode.Append"/> 在打开时定位到 EOF，因此不存在“陈旧偏移”；
    /// 关闭句柄也避免长期占用文件导致别的进程轮转失败。
    /// </summary>
    private void AppendLine(string line)
    {
        using var stream = new FileStream(
            _paths.LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine(line);
        writer.Flush();
        _written = stream.Length;
    }

    private long TryGetLength()
    {
        try
        {
            return File.Exists(_paths.LogFilePath) ? new FileInfo(_paths.LogFilePath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>尝试获取跨进程日志互斥量；超时返回 null（调用方仍然写入，绝不因为抢锁失败丢日志）。</summary>
    private static LogMutexLease? TryAcquireLogMutex(string mutexName)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
        }
        catch
        {
            return null;
        }

        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(LogMutexTimeout);
            }
            catch (AbandonedMutexException)
            {
                // 上一个持锁进程崩溃：本线程接管所有权，之后仍可正常 ReleaseMutex。
                acquired = true;
            }

            if (acquired) return new LogMutexLease(mutex);
        }
        catch
        {
            // 命名互斥量不可用（受限会话等）：退化为无锁追加。
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>日志互斥量的释放句柄：先 ReleaseMutex 再 Dispose（两者都在同一线程上完成）。</summary>
    private sealed class LogMutexLease : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _released;

        public LogMutexLease(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 理论上不会发生（在同一线程释放）；保留兜底。
            }
            finally
            {
                _mutex.Dispose();
            }
        }
    }

    /// <summary>轮转（调用方已持锁）：log → log.1 → log.2 → log.3，超出的最旧文件丢弃。</summary>
    private void RotateLocked()
    {
        try
        {
            for (var index = MaxFiles - 1; index >= 1; index--)
            {
                var older = $"{_paths.LogFilePath}.{index}";
                var newer = $"{_paths.LogFilePath}.{index + 1}";
                if (File.Exists(older))
                {
                    File.Move(older, newer, overwrite: true);
                }
            }

            if (File.Exists(_paths.LogFilePath))
            {
                File.Move(_paths.LogFilePath, _paths.LogFilePath + ".1", overwrite: true);
            }

            // 与旧实现一致：轮转后立刻有一个空的新日志文件，避免“日志文件消失”被误判为故障。
            using (File.Create(_paths.LogFilePath))
            {
            }

            _written = 0;
        }
        catch
        {
            // 另一个进程/杀软仍占用文件：本次跳过，写到阈值一半后再重试，避免每条日志都尝试轮转。
            _written = MaxBytes / 2;
        }
    }

    public void Flush()
    {
        // 每次写入都已立即 Flush 并关闭句柄，无需额外动作（保留接口语义）。
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }
}

/// <summary>Best-effort journal: one JSON line per stage; used to resume/repair after a crash.</summary>
public sealed class JournalWriter
{
    private readonly object _gate = new();
    private readonly string _path;

    public JournalWriter(LoomPaths paths, string operationId)
    {
        _path = paths.JournalFile(operationId);
        Directory.CreateDirectory(paths.JournalDir);
    }

    public string FilePath => _path;

    public void Append(string operationId, OperationStage stage, string status, string message, object? data = null)
    {
        var entry = new
        {
            schemaVersion = 1,
            ts = DateTimeOffset.Now,
            opId = operationId,
            stage = stage.ToString(),
            status,
            message = Redactor.Redact(message),
            data,
        };
        lock (_gate)
        {
            try
            {
                // One compact JSON object per line is required by HasUnfinishedOperation.
                File.AppendAllText(
                    _path,
                    System.Text.Json.JsonSerializer.Serialize(entry) + System.Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // journal is best effort; the operation continues but is reported
            }
        }
    }

    public static IReadOnlyList<string> ReadLines(LoomPaths paths, string operationId)
    {
        var path = paths.JournalFile(operationId);
        return File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
    }

    public static bool HasUnfinishedOperation(LoomPaths paths, out string? operationId)
    {
        operationId = null;
        if (!Directory.Exists(paths.JournalDir)) return false;
        foreach (var file in Directory.EnumerateFiles(paths.JournalDir, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var lines = File.ReadAllLines(file);
            if (lines.Length == 0) continue;
            var last = lines[^1];
            string? status = null;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(last);
                if (document.RootElement.TryGetProperty("status", out var statusElement))
                {
                    status = statusElement.GetString();
                }
            }
            catch
            {
                // fall back to the substring check below
            }

            if (status is "done" or "failed" or "cancelled") continue;
            if (status is null)
            {
                // Legacy/broken line boundaries: scan the whole file for the last status value.
                var text = string.Join("\n", lines);
                var match = System.Text.RegularExpressions.Regex.Matches(
                    text,
                    "\"status\"\\s*:\\s*\"([a-zA-Z]+)\"");
                if (match.Count > 0)
                {
                    status = match[^1].Groups[1].Value;
                }
            }

            if (status is "done" or "failed" or "cancelled") continue;

            operationId = Path.GetFileNameWithoutExtension(file);
            return true;
        }

        return false;
    }
}
