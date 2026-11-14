using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Environment;

/// <summary>
/// Inspects and waits for the three DSH profile coordination files, and can hold the
/// manifest lock for Loom's own writes. Formats were verified against the shipped
/// sources in <c>app.asar</c> (DSH 0.2.2-rc.1):
/// <list type="bullet">
/// <item><c>&lt;profile&gt;\lock</c>: plain PID text written by the desktop side around
/// profile apply/disable operations.</item>
/// <item><c>&lt;profile&gt;\package.json.lock</c>: <c>&lt;pid&gt;\n</c> created with
/// <c>wx</c> by <c>@deepseek-ai/dsh-atomic-write</c> <c>withFileLock()</c>; the CLI and
/// plugin manager wait up to 120 s (<c>lockWaitMs: 12e4</c>) before giving up. A dead
/// holder's lock is taken over by DSH after a takeover-claim dance.</item>
/// <item><c>&lt;profile&gt;\.plugin-manager\run.json</c>: <c>{"pid":n,"grouped":bool}</c>
/// recording the pnpm child of a plugin-manager operation; a successor that inherits the
/// profile lock waits for that tree.</item>
/// </list>
/// Loom never kills processes and only deletes a lock it created itself.
/// </summary>
public sealed class LockManager : ILockManager
{
    /// <summary>How long a DSH CLI/plugin-manager contender waits for package.json.lock.</summary>
    public static readonly TimeSpan DshManifestLockWait = TimeSpan.FromSeconds(120);

    private const string ProfileLockFile = "lock";
    private const string ManifestLockFile = "package.json.lock";
    private const string PluginManagerDir = ".plugin-manager";
    private const string RunRecordFile = "run.json";

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(2);

    private readonly IProcessProbe _processes;
    private readonly ILogger _logger;

    public LockManager(IProcessProbe processes, ILogger logger)
    {
        _processes = processes;
        _logger = logger;
    }

    public LockStatus Inspect(DshInstallation installation)
    {
        var status = new LockStatus();
        var profileDir = installation.ProfileDir;
        if (string.IsNullOrWhiteSpace(profileDir) || !Directory.Exists(profileDir))
        {
            status.Detail = "profile 目录不存在";
            return status;
        }

        var notes = new List<string>();

        // 1. <profile>\lock — desktop-side profile operation lock (PID text).
        InspectPidFile(
            Path.Combine(profileDir, ProfileLockFile),
            @"profile\lock",
            "桌面端正在操作 profile",
            status,
            notes,
            treatUnparsableAsBusy: true);

        // 2. <profile>\package.json.lock — withFileLock sibling, "<pid>\n".
        var manifestLockPath = Path.Combine(profileDir, ManifestLockFile);
        var manifestInfo = ReadPidRecord(manifestLockPath);
        if (manifestInfo.Exists)
        {
            if (manifestInfo.Pid is int pid)
            {
                if (pid == System.Environment.ProcessId || _processes.IsProcessAlive(pid))
                {
                    status.Busy = true;
                    status.Pids.Add(pid);
                    status.Reasons.Add($"package.json.lock 被进程 {pid} 持有（DSH 正在写 profile manifest）");
                    var age = RecordAge(manifestLockPath);
                    if (age is not null && age > DshManifestLockWait)
                    {
                        notes.Add($"package.json.lock 已存在 {age.Value.TotalSeconds:0}s（超过 DSH CLI 约定的 120s 等待窗口，可能仍在执行长时间安装）");
                    }
                }
                else
                {
                    notes.Add($"package.json.lock 残留（PID {pid} 已退出，DSH 下次会接管该锁）");
                }
            }
            else
            {
                // DSH itself waits forever on a lock whose holder it cannot prove gone.
                status.Busy = true;
                status.Reasons.Add("package.json.lock 内容无法解析为 PID（按保守策略视为占用）");
            }
        }

        // 3. <profile>\.plugin-manager\run.json — orphan pnpm-tree record.
        var runRecordPath = Path.Combine(profileDir, PluginManagerDir, RunRecordFile);
        InspectRunRecord(runRecordPath, status, notes);

        status.Detail = notes.Count == 0 ? null : string.Join("；", notes);
        return status;
    }

    public async Task<LockWaitResult> WaitForQuietAsync(
        DshInstallation installation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var backoff = InitialBackoff;
        LockStatus status = Inspect(installation);

        while (status.Busy)
        {
            if (started.Elapsed >= timeout)
            {
                _logger.Log(
                    LogLevel.Warn,
                    $"Profile waits timed out after {started.Elapsed.TotalSeconds:0.0}s: {string.Join("；", status.Reasons)}");
                return new LockWaitResult { Quiet = false, Status = status, Waited = started.Elapsed };
            }

            cancellationToken.ThrowIfCancellationRequested();
            var remaining = timeout - started.Elapsed;
            var delay = backoff <= remaining ? backoff : remaining;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
            status = Inspect(installation);
        }

        return new LockWaitResult { Quiet = true, Status = status, Waited = started.Elapsed };
    }

    public async Task<IDisposable?> AcquireManifestLockAsync(
        DshInstallation installation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var profileDir = installation.ProfileDir;
        if (string.IsNullOrWhiteSpace(profileDir) || !Directory.Exists(profileDir))
        {
            _logger.Log(LogLevel.Warn, $"Cannot acquire manifest lock: profile dir missing ({profileDir}).");
            return null;
        }

        var lockPath = Path.Combine(profileDir, ManifestLockFile);
        var started = Stopwatch.StartNew();
        var backoff = InitialBackoff;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Mirror withFileLock(): exclusive-create sibling, contents "<pid>\n".
                // FileShare.ReadWrite keeps the record readable by DSH contenders and by
                // LockManager.ReadPidRecord (a reader's share mode must also allow this
                // handle's write access, so Read-only sharing would break .NET readers).
                var stream = new FileStream(
                    lockPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
                var record = Encoding.UTF8.GetBytes($"{System.Environment.ProcessId}\n");
                stream.Write(record, 0, record.Length);
                stream.Flush(flushToDisk: true);
                _logger.Log(LogLevel.Debug, $"Acquired manifest lock {lockPath} (PID {System.Environment.ProcessId}).");
                return new ManifestLock(lockPath, stream, _logger);
            }
            catch (IOException)
            {
                // Contention: an existing <profile>\package.json.lock.
                var existing = ReadPidRecord(lockPath);
                if (existing.Exists
                    && existing.Pid is int holder
                    && holder != System.Environment.ProcessId
                    && !_processes.IsProcessAlive(holder))
                {
                    // DSH takes over exited holders; do the same, then retry immediately.
                    try
                    {
                        File.Delete(lockPath);
                        _logger.Log(LogLevel.Info, $"Removed stale package.json.lock held by exited PID {holder}.");
                        continue;
                    }
                    catch (Exception exception)
                    {
                        _logger.Log(LogLevel.Debug, $"Stale lock takeover failed: {exception.Message}");
                    }
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                _logger.Log(LogLevel.Warn, $"Manifest lock access denied: {exception.Message}");
                return null;
            }

            if (started.Elapsed >= timeout)
            {
                _logger.Log(LogLevel.Warn, $"Manifest lock acquisition timed out after {timeout.TotalSeconds:0.0}s.");
                return null;
            }

            var remaining = timeout - started.Elapsed;
            var delay = backoff <= remaining ? backoff : remaining;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
        }
    }

    // ---------------------------------------------------------------- helpers

    private void InspectPidFile(
        string path,
        string label,
        string busyReason,
        LockStatus status,
        List<string> notes,
        bool treatUnparsableAsBusy)
    {
        var record = ReadPidRecord(path);
        if (!record.Exists) return;

        if (record.Pid is int pid)
        {
            if (pid == System.Environment.ProcessId || _processes.IsProcessAlive(pid))
            {
                status.Busy = true;
                status.Pids.Add(pid);
                status.Reasons.Add($"{label} 被进程 {pid} 持有（{busyReason}）");
            }
            else
            {
                notes.Add($"{label} 残留（PID {pid} 已退出）");
            }
        }
        else if (treatUnparsableAsBusy)
        {
            status.Busy = true;
            status.Reasons.Add($"{label} 内容无法解析为 PID（按保守策略视为占用）");
        }
        else
        {
            notes.Add($"{label} 内容无法解析");
        }
    }

    private void InspectRunRecord(string path, LockStatus status, List<string> notes)
    {
        if (!File.Exists(path)) return;
        try
        {
            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                text = reader.ReadToEnd();
            }

            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                status.Busy = true;
                status.Reasons.Add(".plugin-manager\\run.json 不是对象（按保守策略视为占用，需人工确认）");
                return;
            }

            int? pid = null;
            bool? grouped = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("pid") && property.Value.TryGetInt32(out var value)) pid = value;
                if (property.NameEquals("grouped") && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    grouped = property.Value.GetBoolean();
                }
            }

            if (pid is not int runPid || runPid <= 0)
            {
                status.Busy = true;
                status.Reasons.Add(".plugin-manager\\run.json 未记录有效 PID（按保守策略视为占用，需人工确认）");
                return;
            }

            if (_processes.IsProcessAlive(runPid))
            {
                status.Busy = true;
                status.Pids.Add(runPid);
                status.Reasons.Add($"上一次包管理器运行仍在进行（.plugin-manager\\run.json PID {runPid}）");
            }
            else
            {
                notes.Add($"run.json 残留（PID {runPid} 已退出，grouped={grouped?.ToString().ToLowerInvariant() ?? "?"}；DSH 会在下次操作时清理）");
            }
        }
        catch (Exception exception)
        {
            status.Busy = true;
            status.Reasons.Add($".plugin-manager\\run.json 无法解析（{exception.Message}；按保守策略视为占用）");
        }
    }

    private static (bool Exists, int? Pid) ReadPidRecord(string path)
    {
        try
        {
            if (!File.Exists(path)) return (false, null);
            // Open with FileShare.ReadWrite so the record stays readable while a holder
            // (Loom itself included) keeps its writer handle open.
            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                text = reader.ReadToEnd().Trim();
            }

            if (int.TryParse(text, out var pid) && pid > 0 && pid <= int.MaxValue) return (true, pid);
            return (true, null);
        }
        catch
        {
            return (true, null);
        }
    }

    private static TimeSpan? RecordAge(string path)
    {
        try
        {
            return DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Holds <c>package.json.lock</c>; Dispose releases it. Never call dsh.cmd while holding it.</summary>
    private sealed class ManifestLock : IDisposable
    {
        private readonly string _path;
        private readonly ILogger _logger;
        private FileStream? _stream;

        public ManifestLock(string path, FileStream stream, ILogger logger)
        {
            _path = path;
            _stream = stream;
            _logger = logger;
        }

        public void Dispose()
        {
            var stream = _stream;
            _stream = null;
            try
            {
                stream?.Dispose();
            }
            catch
            {
                // best effort
            }

            try
            {
                if (File.Exists(_path)) File.Delete(_path);
                _logger.Log(LogLevel.Debug, $"Released manifest lock {_path}.");
            }
            catch (Exception exception)
            {
                _logger.Log(LogLevel.Warn, $"Failed to delete manifest lock {_path}: {exception.Message}");
            }
        }
    }
}
