using Loom.Core.Infrastructure;
using Xunit;

namespace Loom.Tests.Infrastructure;

/// <summary>
/// P1-1 回归：跨进程操作门必须在**整个操作生命周期**内持有命名互斥量。
///
/// 旧实现 <c>using var mutex</c> 在 <c>AcquireAsync</c> 返回时就 Dispose 了 Mutex，
/// <c>Releaser.ReleaseMutex()</c> 抛 <see cref="ObjectDisposedException"/> 被吞 —— 第二个
/// 句柄/进程可以立即进入。这些用例用“另一个线程打开同名 Mutex”证明“还持有”，
/// Dispose 后再证明“已释放”。
///
/// P2-R2 / P3-R1：门名与锁文件都由数据根派生（不同根不互相阻塞、同根稳定），
/// 并且同根下有一把跨会话文件锁（<c>loom.operation.lock</c>）。
///
/// 用 <c>[Collection]</c> 串行化真实命名互斥量/文件锁的用例，避免与并行测试互相抢门。
/// </summary>
[Collection("OperationGate")]
public sealed class OperationGateTests
{
    [Fact]
    public async Task GateStaysHeldUntilDisposeAndIsReleasedAfterwards()
    {
        using var temp = new TempDir();
        using var gate = new OperationGate(temp.Paths.DataRoot);

        var lease = await gate.AcquireAsync("unit-test");
        try
        {
            // 另一个线程打开同名 Mutex（等价于另一个进程）：操作期间 WaitOne(0) 必须为 false。
            Assert.False(
                TryTakeNamedMutexOnOtherThread(gate.LeaseMutexName),
                "持有期间第二个句柄不应拿到同名 mutex");
        }
        finally
        {
            lease.Dispose();
        }

        // Dispose 之后同名 Mutex 必须可再次获取（证明 ReleaseMutex 真的执行了）。
        Assert.True(TryTakeNamedMutexOnOtherThread(gate.LeaseMutexName), "Dispose 之后同名 mutex 应已释放");
    }

    [Fact]
    public async Task SecondGateInstanceIsBlockedUntilRelease()
    {
        using var temp = new TempDir();
        using var first = new OperationGate(temp.Paths.DataRoot);
        using var second = new OperationGate(temp.Paths.DataRoot);

        var lease = await first.AcquireAsync("unit-test");
        try
        {
            // 另一个 OperationGate 实例（等价于另一个进程）：非阻塞探测必须拿不到门。
            Assert.Null(second.TryAcquireCrossProcessLease(TimeSpan.Zero));
            Assert.False(second.HasCrossProcessLease);
        }
        finally
        {
            lease.Dispose();
        }

        using var acquired = second.TryAcquireCrossProcessLease(TimeSpan.Zero);
        Assert.NotNull(acquired);
        Assert.True(second.HasCrossProcessLease);
        // 预占期间其它进程仍然拿不到门。
        Assert.False(TryTakeNamedMutexOnOtherThread(first.LeaseMutexName));
        acquired!.Dispose();

        Assert.True(TryTakeNamedMutexOnOtherThread(first.LeaseMutexName));
    }

    [Fact]
    public async Task PreAcquiredLeaseSkipsNamedMutexInTheSameInstance()
    {
        using var temp = new TempDir();
        using var gate = new OperationGate(temp.Paths.DataRoot);
        using var preAcquired = gate.TryAcquireCrossProcessLease(TimeSpan.Zero);
        Assert.NotNull(preAcquired);

        // CLI 预占后，服务内部的 AcquireAsync 不能重复获取命名互斥量（否则会自锁），
        // 但仍要正常工作、串行化，且 Dispose 后可再次获取。
        var lease = await gate.AcquireAsync("unit-test").WaitAsync(TimeSpan.FromSeconds(5));
        lease.Dispose();

        var again = await gate.AcquireAsync("unit-test").WaitAsync(TimeSpan.FromSeconds(5));
        again.Dispose();
    }

    [Fact]
    public async Task CancelledAcquisitionReleasesProcessLocalSemaphore()
    {
        using var temp = new TempDir();
        using var holder = new OperationGate(temp.Paths.DataRoot);
        using var waiter = new OperationGate(temp.Paths.DataRoot);

        var lease = await holder.AcquireAsync("unit-test");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiter.AcquireAsync("unit-test-wait", cts.Token));
        lease.Dispose();

        // 取消路径必须释放进程内信号量与所有者线程，否则这里会永远等下去。
        var again = await waiter.AcquireAsync("unit-test-again").WaitAsync(TimeSpan.FromSeconds(5));
        again.Dispose();
    }

    // ------------------------------------------------------- P2-R2 / P3-R1 命名与隔离

    [Fact]
    public void MutexNamesAreDerivedFromDataRootAndSafe()
    {
        using var first = new TempDir();
        using var second = new TempDir();
        var firstRoot = first.Paths.DataRoot;
        var secondRoot = second.Paths.DataRoot;

        var firstName = OperationGate.MutexNameFor(firstRoot);
        var secondName = OperationGate.MutexNameFor(secondRoot);

        Assert.NotEqual(firstName, secondName);
        Assert.Equal(firstName, OperationGate.MutexNameFor(firstRoot.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.Matches(@"^Local\\LoomForDsh-[0-9a-f]{16}-Operation-v1$", firstName);
        Assert.True(firstName.Length < 64, "mutex 名必须短于 64 字符：" + firstName);

        // 超长/中文/含空格的路径也只能落成 16 位十六进制，绝不把非法字符带进内核对象名。
        var weirdRoot = Path.Combine(firstRoot, new string('长', 80), "data root");
        var weirdName = OperationGate.MutexNameFor(weirdRoot);
        Assert.Matches(@"^Local\\LoomForDsh-[0-9a-f]{16}-Operation-v1$", weirdName);
        Assert.Equal(weirdName, OperationGate.MutexNameFor(weirdRoot + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void LogMutexNamesAreDerivedFromDataRootAndSafe()
    {
        using var first = new TempDir();
        using var second = new TempDir();

        var firstName = FileLogger.LogMutexNameFor(first.Paths.DataRoot);
        var secondName = FileLogger.LogMutexNameFor(second.Paths.DataRoot);

        Assert.NotEqual(firstName, secondName);
        Assert.Equal(firstName, FileLogger.LogMutexNameFor(first.Paths.DataRoot + Path.DirectorySeparatorChar));
        Assert.Matches(@"^Local\\LoomForDsh-[0-9a-f]{16}-Log-v1$", firstName);
        Assert.True(firstName.Length < 64, "日志 mutex 名必须短于 64 字符：" + firstName);

        var weird = FileLogger.LogMutexNameFor(Path.Combine(first.Path, new string('x', 200)));
        Assert.Matches(@"^Local\\LoomForDsh-[0-9a-f]{16}-Log-v1$", weird);
    }

    [Fact]
    public async Task DifferentDataRootsDoNotBlockEachOther()
    {
        using var first = new TempDir();
        using var second = new TempDir();
        using var gateA = new OperationGate(first.Paths.DataRoot);
        using var gateB = new OperationGate(second.Paths.DataRoot);

        var leaseA = await gateA.AcquireAsync("unit-test");
        try
        {
            using var leaseB = gateB.TryAcquireCrossProcessLease(TimeSpan.Zero);
            Assert.NotNull(leaseB);
        }
        finally
        {
            leaseA.Dispose();
        }
    }

    [Fact]
    public async Task DataRootLockFileIsHeldForCrossSessionMutualExclusion()
    {
        using var temp = new TempDir();
        using var gate = new OperationGate(temp.Paths.DataRoot);
        var lockPath = Path.Combine(temp.Paths.DataRoot, "loom.operation.lock");

        var lease = await gate.AcquireAsync("unit-test");
        try
        {
            // 同一数据根的文件锁被持有：等价于另一个登录会话（Local\ mutex 不可见）的实例。
            Assert.ThrowsAny<IOException>(() =>
            {
                using var conflicting = new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            });
        }
        finally
        {
            lease.Dispose();
        }

        // 释放后文件锁必须可再次独占打开。
        using var after = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.True(after.CanWrite);
    }

    /// <summary>在独立线程上打开指定命名互斥量并尝试 WaitOne(0)；成功时立即释放。</summary>
    private static bool TryTakeNamedMutexOnOtherThread(string mutexName)
    {
        var acquired = false;
        var thread = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(initiallyOwned: false, mutexName);
                acquired = mutex.WaitOne(0);
                if (acquired) mutex.ReleaseMutex();
            }
            catch
            {
                acquired = false;
            }
        });
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(5));
        return acquired;
    }
}
