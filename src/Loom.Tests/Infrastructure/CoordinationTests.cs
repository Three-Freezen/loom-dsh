using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Xunit;

namespace Loom.Tests.Infrastructure;

public sealed class TickGateTests
{
    [Fact]
    public async Task OnlyOneTickCanEnterAtATime()
    {
        var gate = new TickGate();

        using var first = gate.TryEnter("op-first");
        Assert.NotNull(first);
        var contenders = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
            {
                using var competing = gate.TryEnter("op-competing");
                return competing is not null;
            }))
            .ToArray();
        var results = await Task.WhenAll(contenders);

        Assert.All(results, Assert.False);
        Assert.True(gate.IsRunning);

        first!.Dispose();
        Assert.False(gate.IsRunning);
        using var again = gate.TryEnter("op-again");
        Assert.NotNull(again);
    }

    [Fact]
    public void OverlappingTickIsLoggedAtDebugLevel()
    {
        var logger = new CapturingLogger();
        var gate = new TickGate(logger);

        using var first = gate.TryEnter("op-first");
        Assert.Null(gate.TryEnter("op-second"));

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Debug && entry.Message.Contains("跳过", StringComparison.Ordinal));
    }

    [Fact]
    public void StalledTickIsCancelledAndAbandonedSoNextTickCanRun()
    {
        var logger = new CapturingLogger();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gate = new TickGate(logger, () => now)
        {
            StallThreshold = TimeSpan.FromMinutes(5),
            ResetWait = TimeSpan.FromMilliseconds(100),
        };

        var first = gate.TryEnter("op-old");
        Assert.NotNull(first);
        now = now.AddMinutes(6); // 超过阈值仍未完成

        // 第二次进入：取消旧 Tick，等待 ResetWait（旧 Tick 没退出）→ 标 abandoned 并放行新一轮。
        using var second = gate.TryEnter("op-new");
        Assert.NotNull(second);
        Assert.True(first!.Token.IsCancellationRequested, "旧 Tick 必须收到取消");
        Assert.NotNull(second!.ResetOf);
        Assert.Equal("op-old", second.ResetOf!.OperationId);
        Assert.True(second.ResetOf.Abandoned);
        Assert.Equal(1, gate.StalledResets);
        Assert.NotNull(gate.LastResetAt);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("卡死", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("abandoned", StringComparison.Ordinal));

        // abandoned 的旧 Tick 之后退出，不能清掉新一轮的状态。
        first.Dispose();
        Assert.True(gate.IsRunning, "旧 Tick 退出不应把新一轮的闸门清空");
        Assert.Null(gate.TryEnter("op-third"));

        second.Dispose();
        Assert.False(gate.IsRunning);
    }

    [Fact]
    public async Task ResetWaitsForOldTickToExitWhenItStopsQuickly()
    {
        var logger = new CapturingLogger();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gate = new TickGate(logger, () => now)
        {
            StallThreshold = TimeSpan.FromMilliseconds(10),
            ResetWait = TimeSpan.FromSeconds(5),
        };

        var first = gate.TryEnter("op-old");
        Assert.NotNull(first);
        now = now.AddMinutes(1);

        // 旧 Tick 在复位等待期间正常退出：新 lease 不应被标记 abandoned。
        var resetter = Task.Run(() => gate.TryEnter("op-new"));
        Assert.True(first!.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)));
        first.Dispose();

        using var second = await resetter;
        Assert.NotNull(second);
        Assert.NotNull(second!.ResetOf);
        Assert.False(second.ResetOf!.Abandoned);
        Assert.Equal(1, gate.StalledResets);
    }

    [Fact]
    public void SkipDebugLogIsThrottledAfterFirstEntry()
    {
        var logger = new CapturingLogger();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gate = new TickGate(logger, () => now);

        using var first = gate.TryEnter("op-first");
        Assert.Null(gate.TryEnter("op-second"));
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug);

        now = now.AddSeconds(10); // 60 秒节流内
        Assert.Null(gate.TryEnter("op-third"));
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug);

        now = now.AddSeconds(60);
        Assert.Null(gate.TryEnter("op-fourth"));
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Debug));
    }
}

/// <summary>P2-6：非交互会话不弹模态框的判定，以及 UI 异常提示预算（纯逻辑，可单测）。</summary>
public sealed class InteractionPolicyTests
{
    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 0, false)]
    public void ModalOnlyAllowedInInteractiveUserSession(bool interactive, int sessionId, bool expected)
    {
        Assert.Equal(expected, DisplayPolicy.ShouldShowModal(interactive, sessionId));
    }

    [Fact]
    public void UiExceptionBudgetPromptsExactlyOnceAtThreshold()
    {
        var budget = new UiExceptionBudget { PromptThreshold = 3 };

        Assert.False(budget.Record());
        Assert.Equal(1, budget.Count);
        Assert.False(budget.Record());
        Assert.True(budget.Record());
        Assert.False(budget.Record());
        Assert.Equal(4, budget.Count);
    }
}

public sealed class NotificationLedgerTests
{
    [Fact]
    public void AbaSequenceNotifiesOnceAndSurvivesRestart()
    {
        using var temp = new TempDir();
        var state = new LoomState();
        var now = DateTimeOffset.Now;

        Assert.True(NotificationLedger.TryRecord(state, "A", now));
        Assert.True(NotificationLedger.TryRecord(state, "B", now));
        Assert.False(NotificationLedger.TryRecord(state, "A", now));
        Assert.Equal("B", state.LastNotifiedEventId);

        StateStore.Save(temp.Paths, state);
        var reloaded = StateStore.Load(temp.Paths); // 模拟进程重启

        Assert.False(NotificationLedger.TryRecord(reloaded, "A", now));
        Assert.False(NotificationLedger.TryRecord(reloaded, "B", now));
        Assert.True(NotificationLedger.TryRecord(reloaded, "C", now));
        Assert.Equal("C", reloaded.LastNotifiedEventId);
    }

    [Fact]
    public void RecentWindowKeepsOnlyNewestTwentyEventIds()
    {
        var state = new LoomState();
        var now = DateTimeOffset.Now;
        for (var index = 0; index < 25; index++)
        {
            Assert.True(NotificationLedger.TryRecord(state, "event-" + index, now));
        }

        Assert.Equal(NotificationLedger.MaxRememberedEventIds, state.RecentNotifiedEventIds.Count);
        Assert.DoesNotContain("event-0", state.RecentNotifiedEventIds);
        Assert.Contains("event-24", state.RecentNotifiedEventIds);
    }

    [Fact]
    public void LegacyLastEventIdStillSuppressesAfterLoad()
    {
        using var temp = new TempDir();
        File.WriteAllText(
            temp.Paths.StateFile,
            "{\"schemaVersion\":1,\"lastNotifiedEventId\":\"legacy-event\"}");

        var loaded = StateStore.Load(temp.Paths);

        Assert.Contains("legacy-event", loaded.RecentNotifiedEventIds);
        Assert.False(NotificationLedger.TryRecord(loaded, "legacy-event", DateTimeOffset.Now));
    }
}

public sealed class RecentEventSetTests
{
    [Fact]
    public void EvictsOldestWhenCapacityIsExceeded()
    {
        var set = new RecentEventSet(capacity: 3);

        Assert.True(set.Add("a"));
        Assert.True(set.Add("b"));
        Assert.True(set.Add("c"));
        Assert.False(set.Add("b"));
        Assert.Equal(3, set.Count);

        Assert.True(set.Add("d"));
        Assert.Equal(3, set.Count);
        Assert.False(set.Contains("a"));
        Assert.True(set.Contains("d"));
    }

    [Fact]
    public void DefaultCapacityBoundsTheTrayPresenterSet()
    {
        var set = new RecentEventSet();
        for (var index = 0; index < 200; index++)
        {
            Assert.True(set.Add("e" + index));
        }

        Assert.True(set.Add("e200"));
        Assert.Equal(200, set.Count);
        Assert.False(set.Contains("e0"));
        Assert.True(set.Contains("e200"));
    }
}

/// <summary>最小可观测 logger：用于断言 TickGate 的 debug 日志。</summary>
public sealed class CapturingLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate) return _entries.ToList();
        }
    }

    public string LogFilePath => "";

    public void Log(LogLevel level, string message, string? operationId = null)
    {
        lock (_gate) _entries.Add((level, message));
    }

    public void LogException(Exception exception, string message, string? operationId = null)
    {
        lock (_gate) _entries.Add((LogLevel.Error, message + "：" + exception.Message));
    }

    public void Flush()
    {
    }
}
