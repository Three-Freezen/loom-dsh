using Loom.Core.Models;

namespace Loom.Core.Infrastructure;

/// <summary>
/// 通知去重账本：把最近若干个 eventId 持久化在 state.json 的
/// <see cref="LoomState.RecentNotifiedEventIds"/> 中，保证 A/B/A 序列只通知一次，且进程重启后不重复。
/// 旧字段 <see cref="LoomState.LastNotifiedEventId"/> 继续维护并参与去重（向后兼容）。
/// </summary>
public static class NotificationLedger
{
    /// <summary>建议保留的最近 eventId 数量（滚动窗口，FIFO）。</summary>
    public const int MaxRememberedEventIds = 20;

    /// <summary>
    /// 归一化状态中的通知记录：去重、裁掉超出上限的旧记录，并把旧版只保存的最后一个 eventId 合并进来。
    /// 该方法只修改内存模型，不写文件。
    /// </summary>
    public static void Normalize(LoomState state)
    {
        if (state is null) return;
        state.RecentNotifiedEventIds ??= new List<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var compact = new List<string>(Math.Min(state.RecentNotifiedEventIds.Count + 1, MaxRememberedEventIds));
        foreach (var eventId in state.RecentNotifiedEventIds)
        {
            if (string.IsNullOrWhiteSpace(eventId)) continue;
            if (!seen.Add(eventId)) continue;
            compact.Add(eventId);
        }

        // 旧版本只持久化 lastNotifiedEventId：把它当作最近一条记录，重启后仍然不会重复通知。
        if (!string.IsNullOrWhiteSpace(state.LastNotifiedEventId) && seen.Add(state.LastNotifiedEventId))
        {
            compact.Add(state.LastNotifiedEventId);
        }

        if (compact.Count > MaxRememberedEventIds)
        {
            compact.RemoveRange(0, compact.Count - MaxRememberedEventIds);
        }

        state.RecentNotifiedEventIds = compact;
    }

    /// <summary>该事件是否已经在最近窗口内通知过。</summary>
    public static bool WasNotified(LoomState state, string eventId)
    {
        if (state is null || string.IsNullOrWhiteSpace(eventId)) return false;
        Normalize(state);
        return state.RecentNotifiedEventIds.Contains(eventId, StringComparer.Ordinal);
    }

    /// <summary>
    /// 记录一次通知：重复事件返回 false；成功记录返回 true，并维护 lastNotifiedEventId/lastNotifiedAt。
    /// 超过窗口上限时从最旧的一条开始淘汰。
    /// </summary>
    public static bool TryRecord(LoomState state, string eventId, DateTimeOffset at)
    {
        if (state is null || string.IsNullOrWhiteSpace(eventId)) return true;
        Normalize(state);
        if (state.RecentNotifiedEventIds.Contains(eventId, StringComparer.Ordinal)) return false;

        state.RecentNotifiedEventIds.Add(eventId);
        if (state.RecentNotifiedEventIds.Count > MaxRememberedEventIds)
        {
            state.RecentNotifiedEventIds.RemoveRange(
                0,
                state.RecentNotifiedEventIds.Count - MaxRememberedEventIds);
        }

        state.LastNotifiedEventId = eventId;
        state.LastNotifiedAt = at;
        return true;
    }
}

/// <summary>
/// 有上限的最近事件集合（FIFO 淘汰）。NotificationPresenter 用它替代无上限的 HashSet，
/// 避免托盘长时间运行时内存缓慢增长。
/// </summary>
public sealed class RecentEventSet
{
    private readonly int _capacity;
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public RecentEventSet(int capacity = 200)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _set.Count;

    public int Capacity => _capacity;

    public bool Contains(string value) => _set.Contains(value);

    /// <summary>加入一个事件；已存在返回 false。超过上限时淘汰最旧的一条。</summary>
    public bool Add(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (!_set.Add(value)) return false;

        _order.Enqueue(value);
        while (_order.Count > _capacity)
        {
            _set.Remove(_order.Dequeue());
        }

        return true;
    }
}
