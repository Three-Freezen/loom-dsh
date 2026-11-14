using System.Text;
using Loom.Core.Abstractions;
using Loom.Core.Models;

namespace Loom.Core.Infrastructure;

/// <summary>
/// 迁移/归一化前的原文件备份（设计 7.3/§14）：&lt;file&gt;.bak-&lt;yyyyMMdd-HHmmss&gt;。
/// 备份是 best effort：任何失败都不能影响启动或保存。
/// </summary>
internal static class StoreBackup
{
    public static void BackupIfExists(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var backup = $"{path}.bak-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: true);
        }
        catch
        {
            // 备份失败不阻断启动/保存；下一次启动仍会尝试。
        }
    }
}

/// <summary>Loads/saves config.json with schema validation and corrupt-file quarantine.</summary>
public static class ConfigStore
{
    public static LoomConfig Load(LoomPaths paths)
    {
        var path = paths.ConfigFile;
        string text;
        try
        {
            if (!File.Exists(path)) return new LoomConfig();
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch
        {
            return new LoomConfig();
        }

        LoomConfig config;
        try
        {
            config = JsonDefaults.Deserialize<LoomConfig>(text) ?? new LoomConfig();
        }
        catch
        {
            // 损坏文件：先留一份 .bak，再走 quarantine（原行为不变）。
            StoreBackup.BackupIfExists(path);
            AtomicFile.TryQuarantine(path);
            return new LoomConfig();
        }

        // 旧 schema 或字段需要归一化时，先备份原始内容再在内存中升级。
        if (config.SchemaVersion != 1 || RequiresNormalization(config))
        {
            StoreBackup.BackupIfExists(path);
        }

        Normalize(config);
        return config;
    }

    public static void Save(LoomPaths paths, LoomConfig config)
    {
        var path = paths.ConfigFile;
        var incomingNeedsNormalization = RequiresNormalization(config);
        var existingNeedsMigration = false;
        try
        {
            if (File.Exists(path))
            {
                var existing = JsonDefaults.Deserialize<LoomConfig>(File.ReadAllText(path, Encoding.UTF8));
                existingNeedsMigration = existing is not null
                                        && (existing.SchemaVersion != 1 || RequiresNormalization(existing));
            }
        }
        catch
        {
            // 现有文件不可解析：保存前也保留一份原始字节，避免静默覆盖。
            existingNeedsMigration = true;
        }

        if (incomingNeedsNormalization || existingNeedsMigration)
        {
            StoreBackup.BackupIfExists(path);
        }

        Normalize(config);
        AtomicFile.WriteJson(path, config);
    }

    public static void Normalize(LoomConfig config)
    {
        config.SchemaVersion = Math.Max(config.SchemaVersion, 1);
        if (config.KeepSnapshots < 1) config.KeepSnapshots = 1;
        if (config.KeepSnapshots > 50) config.KeepSnapshots = 50;
        if (config.MaxSnapshotBytes < 50L * 1024 * 1024) config.MaxSnapshotBytes = 50L * 1024 * 1024;
        if (config.MaxArchiveBytes < 10L * 1024 * 1024) config.MaxArchiveBytes = 10L * 1024 * 1024;
        if (config.OperationTimeoutSeconds < 5) config.OperationTimeoutSeconds = 5;
        if (config.OperationTimeoutSeconds > 900) config.OperationTimeoutSeconds = 900;
        if (string.IsNullOrWhiteSpace(config.Theme)) config.Theme = "system";
        if (config.Theme is not ("system" or "dark" or "light")) config.Theme = "system";
        if (string.IsNullOrWhiteSpace(config.LogLevel)) config.LogLevel = "info";
        if (string.IsNullOrWhiteSpace(config.AutoUpgradePolicy)) config.AutoUpgradePolicy = "A";

        // 二期新增字段：旧 config.json 缺字段时由属性默认值兜底，这里只做范围归一化
        // （0 表示“未配置”，按默认值处理——避免手改配置写成 0 导致 0 秒超时）。
        if (config.UpgradeQueryTimeoutSeconds == 0) config.UpgradeQueryTimeoutSeconds = 60;
        if (config.UpgradeQueryTimeoutSeconds < 5) config.UpgradeQueryTimeoutSeconds = 5;
        if (config.UpgradeQueryTimeoutSeconds > 600) config.UpgradeQueryTimeoutSeconds = 600;
        if (config.UpgradeInstallTimeoutSeconds == 0) config.UpgradeInstallTimeoutSeconds = 900;
        if (config.UpgradeInstallTimeoutSeconds < 30) config.UpgradeInstallTimeoutSeconds = 30;
        if (config.UpgradeInstallTimeoutSeconds > 3600) config.UpgradeInstallTimeoutSeconds = 3600;
        if (config.PackageRestoreInstallTimeoutSeconds == 0) config.PackageRestoreInstallTimeoutSeconds = 600;
        if (config.PackageRestoreInstallTimeoutSeconds < 30) config.PackageRestoreInstallTimeoutSeconds = 30;
        if (config.PackageRestoreInstallTimeoutSeconds > 3600) config.PackageRestoreInstallTimeoutSeconds = 3600;
        if (config.PackageRestoreReadyTimeoutSeconds == 0) config.PackageRestoreReadyTimeoutSeconds = 20;
        if (config.PackageRestoreReadyTimeoutSeconds < 3) config.PackageRestoreReadyTimeoutSeconds = 3;
        if (config.PackageRestoreReadyTimeoutSeconds > 300) config.PackageRestoreReadyTimeoutSeconds = 300;
        if (config.KeepRollbackBackups == 0) config.KeepRollbackBackups = 5;
        if (config.KeepRollbackBackups < 1) config.KeepRollbackBackups = 1;
        if (config.KeepRollbackBackups > 50) config.KeepRollbackBackups = 50;
    }

    /// <summary>判断 <see cref="Normalize"/> 是否会改动该对象（用于决定是否先生成 .bak）。</summary>
    private static bool RequiresNormalization(LoomConfig config) =>
        config.SchemaVersion != 1
        || config.KeepSnapshots is < 1 or > 50
        || config.MaxSnapshotBytes < 50L * 1024 * 1024
        || config.MaxArchiveBytes < 10L * 1024 * 1024
        || config.OperationTimeoutSeconds is < 5 or > 900
        || string.IsNullOrWhiteSpace(config.Theme)
        || config.Theme is not ("system" or "dark" or "light")
        || string.IsNullOrWhiteSpace(config.LogLevel)
        || string.IsNullOrWhiteSpace(config.AutoUpgradePolicy)
        || config.UpgradeQueryTimeoutSeconds is < 5 or > 600
        || config.UpgradeInstallTimeoutSeconds is < 30 or > 3600
        || config.PackageRestoreInstallTimeoutSeconds is < 30 or > 3600
        || config.PackageRestoreReadyTimeoutSeconds is < 3 or > 300
        || config.KeepRollbackBackups is < 1 or > 50;

}

/// <summary>Loads/saves state.json (tool state, snapshot pointers, pending update).</summary>
public static class StateStore
{
    public static LoomState Load(LoomPaths paths)
    {
        var path = paths.StateFile;
        string text;
        try
        {
            if (!File.Exists(path)) return new LoomState();
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch
        {
            return new LoomState();
        }

        LoomState state;
        try
        {
            state = JsonDefaults.Deserialize<LoomState>(text) ?? new LoomState();
        }
        catch
        {
            // 损坏文件：先 .bak 再 quarantine，行为与 AtomicFile.ReadJsonSafe 一致。
            StoreBackup.BackupIfExists(path);
            AtomicFile.TryQuarantine(path);
            return new LoomState();
        }

        if (state.SchemaVersion != 1)
        {
            // 旧 schema 先备份；未来 schema（>1）备份后再隔离，避免新版本数据被静默覆盖。
            StoreBackup.BackupIfExists(path);
            if (state.SchemaVersion > 1)
            {
                AtomicFile.TryQuarantine(path);
                state = new LoomState();
            }
        }

        state.SchemaVersion = 1;
        NotificationLedger.Normalize(state);
        return state;
    }

    public static void Save(LoomPaths paths, LoomState state)
    {
        state.SchemaVersion = 1;
        state.UpdatedAt = DateTimeOffset.Now;
        NotificationLedger.Normalize(state);
        AtomicFile.WriteJson(paths.StateFile, state);
    }
}

/// <summary>
/// 跨进程操作门：同一时刻只允许一个 Loom 写操作（命名互斥量 + 数据根文件锁 + 进程内信号量）。
///
/// P1-1 修复：旧实现在 <c>AcquireAsync</c> 里用 <c>using var mutex</c>，方法返回时 Mutex 已被
/// Dispose，<c>Releaser</c> 再调用 <c>ReleaseMutex()</c> 只会抛 <see cref="ObjectDisposedException"/>
/// 并被吞掉——跨进程互斥实际只存在“WaitOne 到 return”的几微秒。
///
/// 现在 Mutex 的生命周期完全交给 <see cref="Releaser"/>，并且把 <c>WaitOne/ReleaseMutex</c> 固定到
/// 一条专属线程上：Windows 的 Mutex 是**线程亲和**的，异步操作在 await 之后普遍会换线程，若在另一个
/// 线程上 ReleaseMutex 会抛 <see cref="ApplicationException"/>（同样会被吞掉，等于没修）。
/// 因此真正的互斥量所有者是一条短命的后台线程；<c>AcquireAsync</c> 等待它获取成功，
/// <c>Releaser.Dispose</c> 通知它先 ReleaseMutex 再 Dispose，跨进程互斥覆盖整个操作生命周期。
///
/// P2-R2 修复（两层，缺一不可）：
/// <list type="number">
///   <item><b>命名互斥量按数据根派生</b>（<c>Local\LoomForDsh-&lt;hash16&gt;-Operation-v1</c>）：
///         不同数据根不再互相阻塞；同一数据根在同一登录会话内仍是同一把门。</item>
///   <item><b>数据根文件锁</b>（<c>&lt;data-root&gt;\loom.operation.lock</c>，<see cref="FileShare.None"/>）：
///         <c>Local\</c> 只覆盖同一登录会话，便携版共享目录被两个用户使用、或计划任务以 session 0
///         与交互托盘共享同一数据根时，两边仍会争用这个文件——文件锁跨会话有效、按根隔离、进程
///         退出/崩溃由操作系统自动释放。保留 <c>Local\</c> 而不改 <c>Global\</c> 的原因见
///         <see cref="ProcessNames"/>。</item>
/// </list>
///
/// 另提供 <see cref="TryAcquireCrossProcessLease"/>：CLI 的 upgrade/recovery 这类超过队列上限的
/// 长操作会先用 <c>WaitOne(0)</c> 做非阻塞探测并在成功后**预占**门（拿不到 → CLI 返回 Busy），
/// 预占期间本实例的 <see cref="AcquireAsync"/> 只做进程内串行化、不重复获取命名互斥量（避免自锁）。
/// </summary>
public sealed class OperationGate : IOperationGate, IDisposable
{
    /// <summary>默认数据根对应的互斥量名（测试/兼容用途；实际实例以构造参数为准）。</summary>
    public static string MutexName => MutexNameFor(null);

    /// <summary>按数据根派生命名互斥量名（P3-R1 / P2-R2；固定长度、仅安全字符）。</summary>
    public static string MutexNameFor(string? dataRoot) => ProcessNames.OperationMutex(dataRoot);

    /// <summary>等待命名互斥量时的轮询切片；取消/探测最多延迟这个时间。</summary>
    private const int WaitSliceMilliseconds = 100;

    private readonly SemaphoreSlim _local = new(1, 1);
    private readonly object _leaseGate = new();
    private readonly string _mutexName;
    private readonly string _lockFilePath;
    private CrossProcessLease? _preAcquiredLease;
    private bool _disposed;

    /// <param name="dataRoot">
    /// 数据根：门名与锁文件都由它派生。null 表示默认数据根（<see cref="LoomPaths.DefaultDataRoot"/>）。
    /// </param>
    public OperationGate(string? dataRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(dataRoot) ? LoomPaths.DefaultDataRoot : dataRoot;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch
        {
            root = root.Trim();
        }

        _mutexName = MutexNameFor(root);
        _lockFilePath = Path.Combine(root, "loom.operation.lock");
    }

    /// <summary>本实例实际使用的命名互斥量名（跨进程验证可用）。</summary>
    public string LeaseMutexName => _mutexName;

    /// <summary>本实例实际使用的数据根文件锁路径（跨会话互斥）。</summary>
    public string LockFilePath => _lockFilePath;

    /// <summary>
    /// 非阻塞探测跨进程门，成功时**持有**命名互斥量直到返回的句柄 Dispose。
    /// 返回 null 表示门已被其它进程（或本进程的另一个写操作）占用。
    /// 供 CLI 长操作（upgrade/recovery）直连前使用：拿不到就返回 Busy，绝不静默并发。
    /// </summary>
    public IDisposable? TryAcquireCrossProcessLease(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_leaseGate)
        {
            if (_preAcquiredLease is not null)
            {
                // 已经预占（同一进程重复调用）：返回一个不拥有互斥量的句柄。
                return new PreAcquiredLeaseHandle(this, ownsLease: false);
            }

            var lease = CrossProcessLease.TryAcquire(_mutexName, _lockFilePath, timeout, cancellationToken);
            if (lease is null) return null;
            _preAcquiredLease = lease;
            return new PreAcquiredLeaseHandle(this, ownsLease: true);
        }
    }

    /// <summary>当前实例是否已预占跨进程门（CLI 长操作专用）。</summary>
    public bool HasCrossProcessLease
    {
        get
        {
            lock (_leaseGate) return _preAcquiredLease is not null;
        }
    }

    public async Task<IDisposable> AcquireAsync(string operationKind, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _local.WaitAsync(cancellationToken).ConfigureAwait(false);
        CrossProcessLease? lease = null;
        try
        {
            var borrowed = false;
            lock (_leaseGate) borrowed = _preAcquiredLease is not null;
            if (!borrowed)
            {
                lease = await CrossProcessLease.AcquireAsync(_mutexName, _lockFilePath, cancellationToken).ConfigureAwait(false);
            }

            return new Releaser(lease, _local);
        }
        catch
        {
            // 异常路径：回收已创建的所有者线程并释放进程内信号量，绝不留半开的门。
            lease?.Dispose();
            _local.Release();
            throw;
        }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly CrossProcessLease? _lease;
        private readonly SemaphoreSlim _local;
        private int _released;

        public Releaser(CrossProcessLease? lease, SemaphoreSlim local)
        {
            _lease = lease;
            _local = local;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            try
            {
                // CrossProcessLease.Dispose：在所有者线程上先 ReleaseMutex()，再 Dispose() 互斥量。
                _lease?.Dispose();
            }
            finally
            {
                _local.Release();
            }
        }
    }

    /// <summary>CLI 预占句柄：Dispose 时释放命名互斥量并清空实例上的预占状态。</summary>
    private sealed class PreAcquiredLeaseHandle : IDisposable
    {
        private readonly OperationGate _gate;
        private readonly bool _ownsLease;
        private int _disposed;

        public PreAcquiredLeaseHandle(OperationGate gate, bool ownsLease)
        {
            _gate = gate;
            _ownsLease = ownsLease;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (!_ownsLease) return;

            CrossProcessLease? lease = null;
            lock (_gate._leaseGate)
            {
                lease = _gate._preAcquiredLease;
                _gate._preAcquiredLease = null;
            }

            lease?.Dispose();
        }
    }

    /// <summary>
    /// 命名互斥量 + 数据根文件锁的真正所有者：所有 WaitOne/ReleaseMutex/文件打开关闭都在同一条线程上
    /// 执行，因此 Dispose 之后门一定被释放（不再依赖调用方恰好回到获取线程）。
    /// </summary>
    private sealed class CrossProcessLease : IDisposable
    {
        private readonly Thread _ownerThread;
        private readonly TimeSpan _waitTimeout;
        private readonly TaskCompletionSource<bool> _acquiredTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _releaseRequested = new(false);
        private readonly ManualResetEventSlim _finished = new(false);
        private volatile bool _abort;
        private volatile bool _ownsGate;
        private Exception? _error;
        private int _closed;

        /// <param name="mutexName">按数据根派生的命名互斥量（同会话内互斥）。</param>
        /// <param name="lockFilePath">数据根下的文件锁（跨会话互斥，P2-R2）。</param>
        /// <param name="waitTimeout">
        /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> = 一直等到可用；
        /// <see cref="TimeSpan.Zero"/> = 只做一次 <c>WaitOne(0)</c> 探测；正数 = 限时等待。
        /// </param>
        private CrossProcessLease(string mutexName, string lockFilePath, TimeSpan waitTimeout)
        {
            _waitTimeout = waitTimeout;
            _ownerThread = new Thread(() => Run(mutexName, lockFilePath))
            {
                IsBackground = true,
                Name = "Loom.OperationGate",
            };
            _ownerThread.Start();
        }

        /// <summary>阻塞等待直到获得门；取消时回收所有者线程再抛 <see cref="OperationCanceledException"/>。</summary>
        public static async Task<CrossProcessLease> AcquireAsync(
            string mutexName,
            string lockFilePath,
            CancellationToken cancellationToken)
        {
            var lease = new CrossProcessLease(mutexName, lockFilePath, Timeout.InfiniteTimeSpan);
            try
            {
                await lease.WaitAcquiredAsync(cancellationToken).ConfigureAwait(false);
                lease.ThrowIfFailed();
                if (!lease._ownsGate)
                {
                    throw new OperationCanceledException("操作已取消（等待其它 Loom 写操作结束）。", cancellationToken);
                }

                return lease;
            }
            catch
            {
                lease.Abort();
                throw;
            }
        }

        /// <summary>非阻塞（或限时）尝试取得门；失败返回 null，绝不抛出“忙”。</summary>
        public static CrossProcessLease? TryAcquire(
            string mutexName,
            string lockFilePath,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var probeOnly = timeout <= TimeSpan.Zero;
            var lease = new CrossProcessLease(mutexName, lockFilePath, probeOnly ? TimeSpan.Zero : timeout);
            // 只探测时也要等所有者线程完成那一次 WaitOne(0)（通常几毫秒），而不是立刻看 TCS。
            var wait = probeOnly ? TimeSpan.FromSeconds(2) : timeout + TimeSpan.FromSeconds(2);
            try
            {
                if (!lease._acquiredTcs.Task.Wait(wait))
                {
                    lease.Abort();
                    return null;
                }

                lease.ThrowIfFailed();
                if (!lease._ownsGate)
                {
                    lease.Abort();
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return lease;
            }
            catch
            {
                lease.Abort();
                throw;
            }
        }

        private void ThrowIfFailed()
        {
            if (_error is not null)
            {
                throw new InvalidOperationException("无法获取跨进程操作门。", _error);
            }
        }

        private async Task WaitAcquiredAsync(CancellationToken cancellationToken)
        {
            if (_acquiredTcs.Task.IsCompleted)
            {
                await _acquiredTcs.Task.ConfigureAwait(false);
                return;
            }

            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                canceled);
            var completed = await Task.WhenAny(_acquiredTcs.Task, canceled.Task).ConfigureAwait(false);
            if (completed == canceled.Task && !_acquiredTcs.Task.IsCompleted)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            await _acquiredTcs.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// 所有者线程：命名互斥量 WaitOne → 文件锁（FileShare.None）非阻塞打开 → 等待释放 →
        /// 关闭文件锁 → ReleaseMutex → Dispose。Abandoned 视为已获得。
        /// 同会话内的进程先过 mutex；跨会话（Local\ 不可见）的进程由文件锁互斥。
        /// </summary>
        private void Run(string mutexName, string lockFilePath)
        {
            Mutex? mutex = null;
            FileStream? lockFile = null;
            var deadline = _waitTimeout == Timeout.InfiniteTimeSpan
                ? DateTimeOffset.MaxValue
                : DateTimeOffset.UtcNow + _waitTimeout;
            try
            {
                mutex = new Mutex(initiallyOwned: false, mutexName);
                while (!_abort)
                {
                    bool acquiredMutex;
                    try
                    {
                        if (_waitTimeout == TimeSpan.Zero)
                        {
                            // 非阻塞探测：只问一次，绝不等其它进程。
                            acquiredMutex = mutex.WaitOne(0);
                        }
                        else if (_waitTimeout == Timeout.InfiniteTimeSpan)
                        {
                            acquiredMutex = mutex.WaitOne(WaitSliceMilliseconds);
                        }
                        else
                        {
                            var remaining = deadline - DateTimeOffset.UtcNow;
                            if (remaining <= TimeSpan.Zero) break; // 限时等待已到期：未获得门
                            var slice = remaining < TimeSpan.FromMilliseconds(WaitSliceMilliseconds)
                                ? remaining
                                : TimeSpan.FromMilliseconds(WaitSliceMilliseconds);
                            acquiredMutex = mutex.WaitOne(slice);
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                        // 上一个持门进程崩溃：本线程接管所有权，之后仍可正常 ReleaseMutex。
                        acquiredMutex = true;
                    }

                    if (!acquiredMutex)
                    {
                        if (_waitTimeout == TimeSpan.Zero) break; // 只探测一次
                        continue;
                    }

                    // 同会话内已拿到 mutex；再抢跨会话文件锁。拿不到说明别的登录会话正在操作同一数据根。
                    if (TryOpenLockFile(lockFilePath, out lockFile, out var lockError))
                    {
                        _ownsGate = true;
                        break;
                    }

                    // 不是“被占用”而是路径/权限根本不可用：报可读错误，避免每 100ms 永久重试。
                    if (lockError is UnauthorizedAccessException or NotSupportedException
                        or PathTooLongException or DirectoryNotFoundException)
                    {
                        _error = new InvalidOperationException(
                            "无法使用数据根操作锁文件（" + lockFilePath + "）：" + lockError.Message,
                            lockError);
                        break;
                    }

                    // 文件锁被其它会话持有：释放 mutex（让本会话的等待者公平排队），等一个切片后重试。
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                        // 兜底：不因释放异常终止所有者线程。
                    }

                    if (_waitTimeout == TimeSpan.Zero) break;
                    if (deadline != DateTimeOffset.MaxValue && DateTimeOffset.UtcNow >= deadline) break;
                    if (_releaseRequested.Wait(TimeSpan.FromMilliseconds(WaitSliceMilliseconds))) break;
                }

                if (!_ownsGate) return; // 取消/超时：尚未获得门
                _acquiredTcs.TrySetResult(true);
                _releaseRequested.Wait();
            }
            catch (Exception exception)
            {
                _error = exception;
            }
            finally
            {
                // 先释放跨会话文件锁，再释放命名互斥量；两者都 best effort，绝不让线程崩溃。
                try
                {
                    lockFile?.Dispose();
                }
                catch
                {
                    // best effort
                }

                if (_ownsGate)
                {
                    try
                    {
                        mutex?.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                        // 理论上不会发生（始终在所有者线程释放）；保留兜底，绝不让线程崩溃。
                    }
                }

                try
                {
                    mutex?.Dispose();
                }
                catch
                {
                    // best effort
                }

                _acquiredTcs.TrySetResult(true);
                try
                {
                    // 与 DisposeEvents 的 10 秒超时路径竞态：事件可能已被释放，绝不在后台线程抛出。
                    _finished.Set();
                }
                catch (ObjectDisposedException)
                {
                    // best effort
                }
            }
        }

        /// <summary>以 <see cref="FileShare.None"/> 打开数据根文件锁；失败（被其它会话持有/权限）返回 false。</summary>
        private static bool TryOpenLockFile(string path, out FileStream? stream, out Exception? error)
        {
            stream = null;
            error = null;
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (Exception exception)
            {
                error = exception;
                return false;
            }
        }

        /// <summary>放弃获取（取消/探测失败）：唤醒线程收尾并回收资源。</summary>
        public void Abort() => Close();

        public void Dispose() => Close();

        /// <summary>Abort/Dispose 幂等（P3-R13）：只执行一次等待与事件释放。</summary>
        private void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _abort = true;
            _releaseRequested.Set();
            WaitFinished();
            DisposeEvents();
        }

        private void WaitFinished()
        {
            try
            {
                _finished.Wait(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // 事件已释放等极端情况：忽略。
            }
        }

        private void DisposeEvents()
        {
            try
            {
                _acquiredTcs.TrySetResult(true);
                _releaseRequested.Dispose();
                _finished.Dispose();
            }
            catch
            {
                // best effort
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CrossProcessLease? lease;
        lock (_leaseGate)
        {
            lease = _preAcquiredLease;
            _preAcquiredLease = null;
        }

        lease?.Dispose();
        _local.Dispose();
    }
}

/// <summary>Semver subset sufficient for app-version comparisons and installer filenames.</summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    public int Major { get; init; }
    public int Minor { get; init; }
    public int Patch { get; init; }
    public string? Prerelease { get; init; }
    public string? Build { get; init; }

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = new SemVersion();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = System.Text.RegularExpressions.Regex.Match(
            text.Trim(),
            @"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$");
        if (!match.Success) return false;
        version = new SemVersion
        {
            Major = int.Parse(match.Groups[1].Value),
            Minor = int.Parse(match.Groups[2].Value),
            Patch = int.Parse(match.Groups[3].Value),
            Prerelease = match.Groups[4].Success ? match.Groups[4].Value : null,
            Build = match.Groups[5].Success ? match.Groups[5].Value : null,
        };
        return true;
    }

    public static SemVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"Invalid semver: {text}");

    public static bool TryParseFromFileName(string fileName, out SemVersion version)
    {
        version = new SemVersion();
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        // Updater file names look like deepseek-harness-<version>-win-x64.exe. Strip the platform
        // and archive suffixes first so the prerelease identifiers ("rc.1") do not swallow them.
        var candidate = System.Text.RegularExpressions.Regex.Replace(
            fileName,
            @"-(win32|win64|win|linux|darwin|mac|osx|arm64|x64|ia32|armv7l?)[-.].*$",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        candidate = System.Text.RegularExpressions.Regex.Replace(
            candidate,
            @"\.(exe|msi|zip|7z|blockmap|dmg|deb|rpm|appimage)$",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var match = System.Text.RegularExpressions.Regex.Match(
            candidate,
            @"(\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?)");
        return match.Success && TryParse(match.Groups[1].Value, out version);
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            if (index >= left.Length) return -1;
            if (index >= right.Length) return 1;
            var leftNumeric = int.TryParse(left[index], out var leftNumber);
            var rightNumeric = int.TryParse(right[index], out var rightNumber);
            int cmp;
            if (leftNumeric && rightNumeric) cmp = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric) cmp = -1;
            else if (rightNumeric) cmp = 1;
            else cmp = string.CompareOrdinal(left[index], right[index]);
            if (cmp != 0) return cmp;
        }

        return 0;
    }

    public bool Equals(SemVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString()
    {
        var text = $"{Major}.{Minor}.{Patch}";
        if (!string.IsNullOrEmpty(Prerelease)) text += "-" + Prerelease;
        if (!string.IsNullOrEmpty(Build)) text += "+" + Build;
        return text;
    }

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
}
