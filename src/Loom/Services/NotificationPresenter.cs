using System.Globalization;
using System.Windows.Forms;
using Loom.Core.Models;

namespace Loom.Services;

/// <summary>
/// Decides whether a balloon notification should be shown: respects the config switch,
/// quiet hours (default 23:00–08:00) and de-duplicates by EventId in memory.
/// (LoomService already persists the last event id; this layer protects against duplicates
/// raised in quick succession and applies the quiet-hours policy.)
/// </summary>
internal sealed class NotificationPresenter
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public LoomNotification? Last { get; private set; }

    public bool ShouldShow(LoomNotification notification, LoomConfig config, DateTimeOffset now)
    {
        if (!config.Notifications) return false;
        if (string.IsNullOrEmpty(notification.EventId)) return true;
        if (config.QuietHoursEnabled && IsQuietHours(config, now)) return false;
        return true;
    }

    public bool Present(NotifyIcon icon, LoomNotification notification, LoomConfig config)
    {
        var now = DateTimeOffset.Now;
        if (string.IsNullOrEmpty(notification.EventId)) notification.EventId = "evt-" + Guid.NewGuid().ToString("N");

        if (!_seen.Add(notification.EventId)) return false;
        if (!ShouldShow(notification, config, now)) return false;

        var title = string.IsNullOrWhiteSpace(notification.Title) ? "Loom for DSH" : notification.Title;
        var message = string.IsNullOrWhiteSpace(notification.Message) ? "状态已更新。" : notification.Message;
        if (message.Length > 250) message = message[..249] + "…";

        Last = notification;
        try
        {
            icon.ShowBalloonTip(4000, title, message, MapIcon(notification.Kind));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ToolTipIcon MapIcon(NotificationKind kind) => kind switch
    {
        NotificationKind.SnapshotTaken or NotificationKind.RestoreCompleted or NotificationKind.Info => ToolTipIcon.Info,
        NotificationKind.UpdateDetected or NotificationKind.NeedsManual => ToolTipIcon.Warning,
        NotificationKind.Failed => ToolTipIcon.Error,
        _ => ToolTipIcon.Info,
    };

    /// <summary>True when the local clock falls inside the configured quiet window (supports wrap midnight).</summary>
    public static bool IsQuietHours(LoomConfig config, DateTimeOffset now)
    {
        if (!TryParseClock(config.QuietHoursStart, out var start) || !TryParseClock(config.QuietHoursEnd, out var end))
        {
            return false;
        }

        var current = now.ToLocalTime().TimeOfDay;
        var from = start;
        var to = end;
        if (from == to) return false;
        return from < to
            ? current >= from && current < to
            : current >= from || current < to;
    }

    private static bool TryParseClock(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!TimeSpan.TryParseExact(text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out value)) return false;
        return value >= TimeSpan.Zero && value < TimeSpan.FromDays(1);
    }
}
