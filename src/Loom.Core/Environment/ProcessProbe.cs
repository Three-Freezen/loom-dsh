using System.Diagnostics;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Environment;

/// <summary>
/// Read-only process discovery. Never kills anything. DSH runs several processes
/// named "DeepSeek Harness" (browser, renderer, GPU, utility), so the whole set of
/// names with the "DeepSeek Harness" prefix is inspected, filtered by executable
/// path ownership when the path is readable.
/// </summary>
public sealed class ProcessProbe : IProcessProbe
{
    private readonly ILogger _logger;

    public ProcessProbe(ILogger logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<DshProcess> FindDshProcesses(string installDir)
    {
        var normalizedInstallDir = NormalizeDirectory(installDir);
        var found = new List<DshProcess>();
        var seen = new HashSet<int>();

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "Process enumeration failed");
            return Array.Empty<DshProcess>();
        }

        foreach (var process in processes)
        {
            try
            {
                if (!IsDshProcessName(process.ProcessName)) continue;
                if (!seen.Add(process.Id)) continue;

                var path = TryGetExecutablePath(process);
                var name = process.ProcessName;

                if (normalizedInstallDir.Length > 0 && path is not null)
                {
                    // A readable path that belongs to another installation must not count.
                    var directory = NormalizeDirectory(Path.GetDirectoryName(path) ?? "");
                    if (!string.Equals(directory, normalizedInstallDir, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                found.Add(new DshProcess
                {
                    Pid = process.Id,
                    Name = name,
                    ExecutablePath = path,
                });
            }
            finally
            {
                process.Dispose();
            }
        }

        return found;
    }

    public bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public bool HasAny(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        try
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        catch (Exception exception)
        {
            _logger.Log(LogLevel.Debug, $"Process lookup failed for {processName}: {exception.Message}");
            return false;
        }
    }

    private static bool IsDshProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        return processName.StartsWith(DshDefaults.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            // Access denied (another user / elevated process): the name is all we have.
            return null;
        }
    }

    private static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var trimmed = path.Trim().Trim('"');
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch
        {
            return path.Trim().Trim('"');
        }
    }
}
