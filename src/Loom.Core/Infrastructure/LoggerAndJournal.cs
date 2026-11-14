using System.Text;
using Loom.Core.Abstractions;
using Loom.Core.Models;

namespace Loom.Core.Infrastructure;

/// <summary>Rotating text logger (loom.log, 5 MB x 3). Thread-safe, redacted, one line per entry.</summary>
public sealed class FileLogger : ILogger, IDisposable
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int MaxFiles = 3;
    private readonly object _gate = new();
    private readonly LoomPaths _paths;
    private readonly LogLevel _minimum;
    private StreamWriter? _writer;
    private long _written;
    private bool _disposed;

    public FileLogger(LoomPaths paths, LogLevel minimum = LogLevel.Info)
    {
        _paths = paths;
        _minimum = minimum;
        Directory.CreateDirectory(paths.LogsDir);
        Open();
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
                _writer ??= Open();
                _writer.WriteLine(line);
                _writer.Flush();
                _written += Encoding.UTF8.GetByteCount(line) + System.Environment.NewLine.Length;
                if (_written > MaxBytes) Rotate();
            }
            catch
            {
                // logging must never break the operation
            }
        }
    }

    private StreamWriter Open()
    {
        var stream = new FileStream(_paths.LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        try { _written = stream.Length; } catch { _written = 0; }
        return _writer;
    }

    private void Rotate()
    {
        try
        {
            _writer?.Dispose();
            _writer = null;
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
        }
        catch
        {
            // ignore rotation failures
        }
        finally
        {
            _written = 0;
            try { _writer = Open(); } catch { _writer = null; }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            try { _writer?.Flush(); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try { _writer?.Dispose(); } catch { }
            _writer = null;
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
