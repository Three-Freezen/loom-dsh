using System.Globalization;
using System.IO;
using Loom.Core.Models;

namespace Loom.ViewModels;

/// <summary>
/// Centralised Chinese UI copy (tool-rational tone: 快照 / 还原 / 兼容).
/// Kept in one place so a future i18n pass only needs to touch this file.
/// </summary>
public static class UiStrings
{
    public const string ProductName = "LOOM";
    public const string ProductTagline = "DSH 插件守护";
    public const string ProductFullName = "Loom for DSH";
    public const string ToolVersionFallback = "0.1.1";

    // 托盘状态
    public const string StatusNormal = "正常";
    public const string StatusBusy = "处理中";
    public const string StatusAwaitingExit = "待退出";
    public const string StatusNeedsManual = "需人工";
    public const string StatusFailed = "失败";
    public const string StatusNotFound = "未找到";

    // 托盘菜单
    public const string MenuSnapshot = "立即快照";
    public const string MenuCheck = "检查兼容性";
    public const string MenuUpdateCheck = "检查应用更新";
    public const string MenuReport = "打开最近报告";
    public const string MenuSettings = "设置";
    public const string MenuBackupFolder = "打开备份目录";
    public const string MenuExit = "退出";
    public const string MenuPopup = "打开状态面板";

    // 弹层
    public const string PopupSnapshot = "立即快照";
    public const string PopupSnapshotBusy = "正在快照…";
    public const string PopupCheck = "检查兼容性";
    public const string PopupCheckBusy = "正在检查…";
    public const string PopupBackupDir = "打开备份目录";
    public const string PopupRecentActivity = "最近活动";
    public const string PopupAllActivity = "全部";
    public const string PopupViewReport = "查看报告";
    public const string PopupRetry = "重试";
    public const string PopupRollback = "回滚";
    public const string PopupPluginManager = "打开插件管理器";
    public const string PopupFooterRunning = "后台守护已启动";

    // 设置
    public const string SettingsTitle = "设置";
    public const string SettingsSave = "保存";
    public const string SettingsSaving = "正在保存…";
    public const string SettingsCancel = "取消";
    public const string SettingsSaved = "已保存";
    public const string SettingsNavGeneral = "通用";
    public const string SettingsNavBackup = "备份与还原";
    public const string SettingsNavCompat = "更新与兼容";
    public const string SettingsNavNotify = "通知";
    public const string SettingsNavStorage = "存储";
    public const string SettingsNavAbout = "关于";

    // 首启
    public const string FirstRunTitle = "首次运行";
    public const string FirstRunFinish = "完成";
    public const string FirstRunLater = "稍后再说";
    public const string FirstRunSnapshot = "立即创建首次快照";
    public const string FirstRunRelocate = "重新定位";
    public const string FirstRunCompleted = "首次运行已完成";

    // 报告
    public const string ReportTitle = "操作报告";
    public const string ReportEmpty = "还没有可显示的报告。";
    public const string ReportCopyPath = "复制路径";
    public const string ReportOpenExternal = "用默认程序打开";
    public const string ReportCopied = "路径已复制";
    public const string ReportNoPath = "暂无报告路径";

    public static string TrayStatusLabel(TrayStatus state) => state switch
    {
        TrayStatus.Normal => StatusNormal,
        TrayStatus.Busy => StatusBusy,
        TrayStatus.AwaitingExit => StatusAwaitingExit,
        TrayStatus.NeedsManual => StatusNeedsManual,
        TrayStatus.Failed => StatusFailed,
        _ => StatusNotFound,
    };

    public static string TrayStatusKey(TrayStatus state) => state switch
    {
        TrayStatus.Normal => "Normal",
        TrayStatus.Busy => "Busy",
        TrayStatus.AwaitingExit => "AwaitingExit",
        TrayStatus.NeedsManual => "NeedsManual",
        TrayStatus.Failed => "Failed",
        _ => "NotFound",
    };

    public static string ThemeLabel(string theme) => theme switch
    {
        "dark" => "深色",
        "light" => "浅色",
        _ => "跟随系统",
    };

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : string.Format(CultureInfo.InvariantCulture, value >= 100 ? "{0:0} {1}" : "{0:0.0} {1}", value, units[unit]);
    }

    public static string FormatTime(DateTimeOffset? value)
        => value is null ? "尚无记录" : value.Value.ToLocalTime().ToString("HH:mm");

    public static string FormatDate(DateTimeOffset? value)
        => value is null ? "尚无记录" : value.Value.ToLocalTime().ToString("MM-dd HH:mm");

    public static string FormatRelative(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var delta = DateTimeOffset.Now - local;
        if (delta.TotalMinutes < 1) return "刚刚";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} 分钟前";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} 小时前";
        return local.ToString("MM-dd");
    }

    public static string ActivityGlyph(string? kind) => kind switch
    {
        "ok" => "\uE73E",
        "warn" => "\uE7BA",
        "error" => "\uE783",
        _ => "\uE946",
    };

    public static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var single = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length <= max ? single : single[..(max - 1)] + "…";
    }
}
