using Loom.Core.Abstractions;

namespace Loom.Core.Infrastructure;

/// <summary>Tick 闸门的一次状态快照（doctor / 诊断用，不新增 UI）。</summary>
public sealed record TickSnapshot(
    bool IsRunning,
    DateTimeOffset? RunningSince,
    DateTimeOffset? LastCompletedAt,
    TimeSpan? RunningFor,
    bool Stalled,
    DateTimeOffset? LastResetAt = null,
    int StalledResets = 0);

/// <summary>一次看门狗复位的信息：旧 Tick 的 opId、已运行时长、是否等到它真正退出（否则记为 abandoned）。</summary>
public sealed record TickResetInfo(string OperationId, TimeSpan RunningFor, bool Abandoned);

/// <summary>
/// 非阻塞的 Tick 闸门：托盘定时器每 15 秒触发一次，长操作（完整快照/兼容检查/自动升级）可能超过这个周期。
/// 用状态机保证同一时刻只有一个 Tick 在跑；重叠的 Tick 直接跳过并记 debug 日志，不排队、不阻塞调用线程。
///
/// P2-5 / P3-R2 看门狗与安全复位：
/// <list type="bullet">
///   <item>超过 <see cref="StallThreshold"/>（默认 5 分钟）仍未完成：取消该 Tick 的 <see cref="CancellationToken"/>，
///         记 ERROR；下一次 Tick 可以继续（不再“永久停摆”）。</item>
///   <item>复位时**不并发启动第二个 Tick**：先在 <c>_state = resetting</c> 下等待旧 Tick 调用 <see cref="TickLease.Dispose"/>
///         真正退出（最多 <see cref="ResetWait"/>）；超时才把旧 Tick 记为 abandoned 并允许下一轮。</item>
///   <item>abandoned 的旧 Tick 之后退出时会因 generation 不匹配而**不能**清掉新一轮的状态；同一时刻最多一个
///         活跃 lease，故不会出现两个 Tick 同时“被认可”。</item>
/// </list>
/// 复位信息通过 <see cref="TickLease.ResetOf"/> 交给调用方（LoomService 负责写 ERROR 日志与
/// <see cref="Loom.Core.Models.NotificationKind.Failed"/> 通知，eventId 带旧 Tick 的 opId）。
/// </summary>
public sealed class TickGate
{
    private readonly ILogger? _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _tickFinished = new(true);

    private int _state; // 0 = idle, 1 = running, 2 = resetting
    private int _generation;
    private CancellationTokenSource? _currentCts;
    private string? _currentOperationId;
    private long _enteredAtTicks;
    private long _lastCompletedAtTicks;
    private long _lastSkipLogAtTicks;
    private long _lastResetAtTicks;
    private int _stalledResets;

    public TickGate(ILogger? logger = null, Func<DateTimeOffset>? clock = null)
    {
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>超过该时长仍未完成即视为卡死（默认 5 分钟）。</summary>
    public TimeSpan StallThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>“Tick 跳过”debug 日志的最小间隔（默认 60 秒；首次必记）。</summary>
    public TimeSpan SkipLogInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>复位时等待旧 Tick 真正退出的上限；超时则标记 abandoned 并放行下一轮。</summary>
    public TimeSpan ResetWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>当前是否有 Tick 在运行（复位等待期间也算运行）。</summary>
    public bool IsRunning => Volatile.Read(ref _state) != 0;

    /// <summary>当前 Tick 的开始时间（未运行为 null）。</summary>
    public DateTimeOffset? RunningSince =>
        IsRunning && Volatile.Read(ref _enteredAtTicks) != 0
            ? new DateTimeOffset(Volatile.Read(ref _enteredAtTicks), TimeSpan.Zero)
            : null;

    /// <summary>最近一次 Tick 正常完成时间（从未完成为 null）。</summary>
    public DateTimeOffset? LastCompletedAt =>
        Volatile.Read(ref _lastCompletedAtTicks) == 0
            ? null
            : new DateTimeOffset(Volatile.Read(ref _lastCompletedAtTicks), TimeSpan.Zero);

    /// <summary>最近一次看门狗复位时间（null = 从未复位）。</summary>
    public DateTimeOffset? LastResetAt =>
        Volatile.Read(ref _lastResetAtTicks) == 0
            ? null
            : new DateTimeOffset(Volatile.Read(ref _lastResetAtTicks), TimeSpan.Zero);

    /// <summary>看门狗强制复位次数（含 waited / abandoned 两种）。</summary>
    public int StalledResets => Volatile.Read(ref _stalledResets);

    /// <summary>当前 Tick 已运行时长（未运行为 null）。</summary>
    public TimeSpan? RunningFor
    {
        get
        {
            var since = RunningSince;
            return since is null ? null : _clock() - since.Value;
        }
    }

    /// <summary>是否已超过 <see cref="StallThreshold"/> 仍未完成。</summary>
    public bool IsStalled => RunningFor is { } running && running >= StallThreshold;

    /// <summary>诊断快照（doctor 使用）。</summary>
    public TickSnapshot Snapshot() => new(
        IsRunning,
        RunningSince,
        LastCompletedAt,
        RunningFor,
        IsStalled,
        LastResetAt,
        StalledResets);

    /// <summary>
    /// 尝试进入 Tick；已有 Tick 在正常运行返回 null。若上一轮已超过阈值：取消它并等待其退出
    /// （最多 <see cref="ResetWait"/>），然后返回**新一轮**的 lease；调用方必须 <c>using</c>/Dispose。
    /// </summary>
    public TickLease? TryEnter(string? operationId = null)
    {
        var now = _clock();
        CancellationTokenSource? toCancel = null;
        string? oldOperationId = null;
        TimeSpan oldRunningFor = TimeSpan.Zero;

        lock (_sync)
        {
            if (_state == 0)
            {
                // 正常空闲：直接开始；复位信息为空。
                return StartLocked(operationId, now, resetOf: null);
            }

            var entered = Volatile.Read(ref _enteredAtTicks);
            oldRunningFor = entered == 0 ? TimeSpan.Zero : now - new DateTimeOffset(entered, TimeSpan.Zero);

            if (oldRunningFor < StallThreshold)
            {
                if (ShouldLog(ref _lastSkipLogAtTicks, now, SkipLogInterval))
                {
                    _logger?.Log(LogLevel.Debug, "Tick 跳过：上一轮 Tick 仍在运行，避免重叠执行。");
                }

                return null;
            }

            if (_state == 2)
            {
                // 另一个调用者已经在执行复位；本次跳过，等它把新一轮跑起来。
                return null;
            }

            // 进入“复位中”：先冻结入口（state=2），保证复位期间不会再启动第二个 Tick。
            Volatile.Write(ref _state, 2);
            toCancel = _currentCts;
            oldOperationId = _currentOperationId;
        }

        _logger?.Log(
            LogLevel.Error,
            $"Tick 卡死：上一轮已运行 {FormatDuration(oldRunningFor)}（阈值 {FormatDuration(StallThreshold)}，"
            + $"op={oldOperationId ?? "?"}）。开始取消并等待旧 Tick 退出，然后放行下一轮。");

        try
        {
            toCancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 旧 Tick 恰好在取消前退出并释放了 CTS：继续按“已退出/abandoned”流程处理。
        }

        var exited = _tickFinished.Wait(ResetWait);

        lock (_sync)
        {
            Volatile.Write(ref _lastResetAtTicks, now.UtcTicks);
            _stalledResets++;
            var abandoned = !exited && _state == 2;
            if (abandoned)
            {
                // 旧 Tick 没在 ResetWait 内退出：作废旧 lease 的 generation，之后它的 Exit 不会清掉新状态。
                _generation++;
            }

            if (!exited)
            {
                _logger?.Log(
                    LogLevel.Error,
                    $"Tick 复位等待 {FormatDuration(ResetWait)} 超时：旧 Tick（op={oldOperationId ?? "?"}）"
                    + "标记为 abandoned，允许下一轮继续；旧 Tick 之后退出时不会再改动闸门状态。");
            }

            var reset = new TickResetInfo(oldOperationId ?? "", oldRunningFor, abandoned);
            return StartLocked(operationId, _clock(), reset);
        }
    }

    /// <summary>退出 Tick（必须与成功的 <see cref="TryEnter"/> 配对，建议 using/Dispose）。</summary>
    internal void Exit(TickLease lease)
    {
        lock (_sync)
        {
            if (lease.Generation != _generation)
            {
                // 已被看门狗标记 abandoned：旧 Tick 退出时不能清掉新一轮的状态/CTS。
                lease.DisposeTokenSource();
                return;
            }

            Volatile.Write(ref _lastCompletedAtTicks, _clock().UtcTicks);
            Volatile.Write(ref _state, 0);
            _currentCts = null;
            _currentOperationId = null;
            _tickFinished.Set();
        }

        lease.DisposeTokenSource();
    }

    private TickLease StartLocked(string? operationId, DateTimeOffset now, TickResetInfo? resetOf)
    {
        Volatile.Write(ref _state, 1);
        _generation++;
        var cts = new CancellationTokenSource();
        _currentCts = cts;
        _currentOperationId = operationId;
        Volatile.Write(ref _enteredAtTicks, now.UtcTicks);
        // 新一轮开始：跳过日志的节流重新计时。
        Volatile.Write(ref _lastSkipLogAtTicks, 0);
        _tickFinished.Reset();
        return new TickLease(this, _generation, cts, resetOf);
    }

    private static bool ShouldLog(ref long lastTicks, DateTimeOffset now, TimeSpan interval)
    {
        var last = Volatile.Read(ref lastTicks);
        if (last != 0 && now - new DateTimeOffset(last, TimeSpan.Zero) < interval) return false;
        Interlocked.Exchange(ref lastTicks, now.UtcTicks);
        return true;
    }

    internal static string FormatDuration(TimeSpan value) =>
        value.TotalMinutes >= 1
            ? $"{value.TotalMinutes:0.#} 分钟"
            : $"{value.TotalSeconds:0.#} 秒";
}

/// <summary>一次 Tick 的租约：Token 供 Tick 内部使用；Dispose 表示本轮结束（幂等）。</summary>
public sealed class TickLease : IDisposable
{
    private readonly TickGate _gate;
    private readonly CancellationTokenSource _cts;
    private int _disposed;

    internal TickLease(TickGate gate, int generation, CancellationTokenSource cts, TickResetInfo? resetOf)
    {
        _gate = gate;
        Generation = generation;
        _cts = cts;
        ResetOf = resetOf;
    }

    internal int Generation { get; }

    /// <summary>本轮 Tick 的取消令牌；看门狗复位或应用退出时取消。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>非 null 表示本轮是“卡死复位后的第一轮”，带旧 Tick 的 opId/运行时长/是否 abandoned。</summary>
    public TickResetInfo? ResetOf { get; }

    internal void DisposeTokenSource()
    {
        try
        {
            _cts.Dispose();
        }
        catch
        {
            // best effort
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gate.Exit(this);
    }
}
