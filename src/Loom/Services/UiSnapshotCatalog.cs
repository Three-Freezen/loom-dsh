namespace Loom.Services;

/// <summary>
/// <c>loom-dsh ui render</c> 的固定快照清单（P3-R6）。10 张主界面 + 2 张“更新与兼容”整页快照。
/// <see cref="TrayHost.RenderSnapshots"/> 会按该清单校验产物数量，任何一张缺失都会让 render 失败，
/// 避免“少截了但退出码仍为 0”。测试直接断言清单为 12 张且文件名唯一。
/// </summary>
public static class UiSnapshotCatalog
{
    /// <summary>期望产出的 PNG 数量（5 个界面 × 2 主题 + 1 个整页界面 × 2 主题）。</summary>
    public const int ExpectedCount = 12;

    /// <summary>固定文件名（dark 在前、light 在后）。</summary>
    public static IReadOnlyList<string> FileNames { get; } = new[]
    {
        "popup-dark.png",
        "settings-dark.png",
        "settings-update-dark.png",
        "settings-compat-dark.png",
        "wizard-dark.png",
        "tray-menu-dark.png",
        "popup-light.png",
        "settings-light.png",
        "settings-update-light.png",
        "settings-compat-light.png",
        "wizard-light.png",
        "tray-menu-light.png",
    };
}
