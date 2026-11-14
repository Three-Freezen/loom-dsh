# Loom · WPF 托盘与界面实现说明

> 2026-11-14 · 负责：ui-tray（WPF 界面工作流）· 本轮 task-6：托盘右键菜单改 WPF 自绘、「打开状态面板」无反应修复、LogoMark/窗口图标/托盘 ico 主题一致性；复审补丁：托盘菜单前台锁加固（SetForegroundWindow 诊断 + mouse capture 兜底）
> 设计依据：`docs/命名与设计说明.md` 第三节 token、`design/ui/LOOM-UI-dark.png` / `LOOM-UI-light.png`、`docs/实现方案-v2.md` §13 / §8.4 / §14；托盘菜单几何参考本地 DeskOrder 项目（`Resources/Controls/ContextMenu.xaml`）
> 代码根：`src/Loom/`；本文件只覆盖 UI 层（托盘、弹层、菜单、设置、向导、报告、主题、预览渲染）。

---

## 1. 目录与文件

| 路径 | 作用 |
|---|---|
| `src/Loom/Tray/TrayHost.cs` | 托盘宿主：`NotifyIcon` 生命周期、状态图标、右键菜单宿主、弹层/设置/向导/报告窗口、气泡通知、`RenderSnapshots()`（8 页） |
| `src/Loom/Views/TrayMenuView.xaml` | 托盘右键菜单内容（WPF 自绘卡片；预渲染与窗口共用） |
| `src/Loom/Views/TrayMenuWindow.xaml` | 托盘右键菜单窗口：无边框透明置顶、淡入、Esc/方向键/失焦关闭、「先关菜单再执行命令」时序 |
| `src/Loom/ViewModels/TrayMenuViewModel.cs` | 菜单项/命令/忙碌禁用/危险项（退出）模型；`TrayMenuActions` 由 TrayHost 注入 |
| `src/Loom/Services/ThemeManager.cs` | 深/浅主题字典合并与切换、系统主题探测（HKCU `Personalize\AppsUseLightTheme`） |
| `src/Loom/Services/AppIcon.cs` | 窗口图标：按 `ThemeManager.EffectiveTheme` 解码内嵌 ICO 的 PNG 帧（Settings/FirstRun/Report 标题栏/任务栏/Alt-Tab） |
| `src/Loom/Services/TrayIconFactory.cs` | 从内嵌 ICO 生成托盘图标并按状态叠加状态点（自解析 ICO 的 PNG 帧，保留 alpha；HICON 显式 `DestroyIcon`） |
| `src/Loom/Services/NotificationPresenter.cs` | 气泡通知策略：开关、静默时段（跨午夜）、EventId 内存去重 |
| `src/Loom/Services/UiPreviewRenderer.cs` | 无窗口渲染（Measure/Arrange + `RenderTargetBitmap` 2x） |
| `src/Loom/Services/ShellLauncher.cs` | 打开目录/文件、复制路径（best-effort，不抛异常） |
| `src/Loom/Themes/Dark.xaml` / `Light.xaml` | 两套 token（只放颜色/画刷）：基础层、文字、状态、品牌标（含渐变）、托盘菜单、阴影 |
| `src/Loom/Themes/Controls.xaml` | 共用样式：文字、卡片、按钮、开关、复选/单选、输入框、下拉、滚动条、提示、进度、窗口基样式 + 菜单字体 |
| `src/Loom/Views/**` | `LogoMark`（矢量品牌标）、`TrayMenuView/Window`、`TrayPopupView/Window`、`SettingsView/Window`、`FirstRunView/Window`、`ReportView/Window` |
| `src/Loom/ViewModels/**` | `TrayPopupViewModel`、`TrayMenuViewModel`、`SettingsViewModel`、`FirstRunViewModel`、`ReportViewModel`、`UiStrings`、MVVM 基类 |
| `src/Loom/Assets/Icons/*.ico` | 浅底/深底对钩 ICO（16–256 多尺寸 PNG 帧；托盘图标与窗口图标共用） |

App / Program / AppServices 契约保持不变：`new TrayHost(AppServices)`、`Start()`、`ShowPopup()`、`ShowSettings()`、`ShowWizard()`、`ShowReport(string? path = null)`、`RenderSnapshots(string? outputDir = null)`、`Notify(LoomNotification)`、`Dispose()`。

---

## 2. 页面结构

### 2.1 托盘弹层（TrayPopup）
- 宽 360；卡片 18px 圆角 + 1px 描边 + `DropShadowEffect`；窗口 `AllowsTransparency`、`Topmost`、失焦/Esc 关闭（打开后 300ms 内或尚未激活时的失焦忽略，见 §6.4）。
- 结构：品牌头 + 状态胶囊 → 状态卡（DSH 版本 / 已保护插件数 / 上次快照 / 备份占用 / 自动检查）→ 主按钮「立即快照」（异步、执行中禁用并显示细进度条）→ 次级「检查兼容性」「打开备份目录」→ 操作反馈 → 异常/人工面板（重试 / 回滚 / 打开报告）→ 首次运行提示 → 最近活动（最多 4 条）→ 底部「查看报告」+ 打开备份目录 / 切换主题 / 设置 / 退出 + 版本行。
- 定位：`Screen.FromPoint(Cursor.Position)` + `WorkingArea`（设备像素）→ 用光标所在显示器 DPI（`MonitorFromPoint`+`GetDpiForMonitor`，PerMonitorV2）换算 DIP，窗口 `PresentationSource.CompositionTarget.TransformFromDevice` 作兜底；鼠标在屏幕右半 → 贴右下，否则贴左下；按 26px 阴影边距回退，弹层卡片距工作区 8px；多显示器按光标所在屏。
- 状态色：正常=成功绿、处理中=强调蓝、待退出/需人工=警告黄、失败=危险红、未找到=三级文字灰。

### 2.2 设置窗口（SettingsWindow）
- 940×640（含 26px 阴影边距），无边框、居中、单实例；左导航 208px（`SidebarBrush`，仅左侧圆角）+ 右侧内容区。
- 六个分区：
  1. **通用**：主题（跟随系统/深色/浅色，切换即预览）、开机自启、启动后自动快照、数据目录（只读 + 打开）。
  2. **备份与还原**：更新后自动文件级还原、更新前完整快照、保留份数（3/5/10/20）、快照配额 GB（`MaxSnapshotBytes`）、当前占用 + 打开备份目录、还原安全说明。
  3. **更新与兼容**：兼容策略 A/B/C 单选（A 为默认）、更新后自动恢复、范围 A 说明。
  4. **通知**：气泡通知开关、静默时段开关 + 起止时间（HH:mm 校验）、通知方式说明。
  5. **存储**：数据目录/配置文件/日志目录（只读 + 打开）、快照占用、范围说明 + 重新定位。
  6. **关于**：品牌、工具/应用/运行时版本、数据目录、能力与边界、打开数据/日志目录。
- 底部：错误/已保存提示 + 取消/保存。保存调用 `SaveConfigAsync(config)`；自启变化时再调用 `SetAutostartAsync`（服务内部也会应用，调两次是幂等的）；保存成功后 `ThemeManager.Apply(config.Theme)`。
- 取消/关闭：未保存时回滚主题预览（`RevertThemePreview`）；保存后关闭不覆盖。
- 所有窗口均支持拖动标题区、最小化、关闭（无系统边框）。

### 2.3 首启向导（FirstRunWindow）
- 720×660，单页（不强制 3 步）：保护范围（只守护 desktop profile / 不动安装目录与 app.asar / 可回滚）→ 自动定位结果（安装目录、profile、应用/运行时版本；未找到时给「重新定位」）→ 开机自启（默认勾选）+ 首次快照说明与结果提示。
- 打开时若状态尚未定位，会在后台 `LocateAsync()` 后刷新展示（避免首启显示"未找到"的误判）。
- 「立即创建首次快照」→ `TakeSnapshotAsync(SnapshotRequest{Trigger=Manual})`（light），成功/失败都在卡片内显示结果，按钮切换为「完成」。
- 「完成」→ 复制配置、`FirstRunCompleted=true`、写自启选择、`SaveConfigAsync`；「稍后再说」不写完成标记（下次启动仍会提示）。

### 2.4 报告窗口（ReportWindow）
- 860×640；顶部工具条「刷新 / 复制路径 / 用默认程序打开 / 打开报告目录」+ 人工入口条「重试检查 / 回滚 / 打开插件管理器」。
- 正文：等宽字体只读 `TextBox` 显示 Markdown（`GetLatestReportAsync` 或 `GetReportAsync(path)`）；空状态提示；状态栏显示报告路径与操作结果。
- 回滚：先普通还原（`dry-run=false, force=false`），若结果为 `NeedsManual` 弹确认后再 `force=true`；还原完成/失败通过气泡通知并打开新报告。

### 2.5 托盘右键菜单（TrayMenu，WPF 自绘）
- 触发：**右键**通知区图标打开；再次右键收起（失焦关闭后 250ms 内到达的同一右键视为「收起」，避免事件顺序导致菜单刚关又被重开）；左键/双击行为不变（打开/收起状态弹层）。原 WinForms `ContextMenuStrip` 已完全移除。
- 窗口：无边框 + `AllowsTransparency` + `Topmost` + `ShowInTaskbar=False`；卡片外留 20px 阴影边距；打开时 120ms 淡入（`CubicEase/EaseOut`）。
- 结构（`TrayMenuViewModel` 数据驱动，每次右键按当前状态新建）：
  `打开状态面板` ／ 分割线 ／ `立即快照`、`检查兼容性`、`打开最近报告` ／ 分割线 ／ `设置`、`打开备份目录` ／ 分割线 ／ `退出`（危险项，弱化危险色）。
- 样式参数（主题化，全部 `DynamicResource`）：

  | 项 | 值 |
  |---|---|
  | 卡片 | 宽 232；圆角 10px；1px 细边（`MenuBorderBrush`）；内边距 4px；轻阴影（blur 22 / opacity 0.35 / depth 2） |
  | 行 | 高 36px；左右外边距 2px、上下 1px；左内边距 10px；hover/pressed 圆角 4px（`MenuHoverBrush`/`MenuPressedBrush`） |
  | 图标列 | 16px（Segoe MDL2 Assets，13px，`MenuIconBrush`）；文字列间距 12px |
  | 文字 | 14px `Segoe UI Variable Text, Segoe UI`（`MenuFontFamily`）；主色 `MenuTextBrush`；禁用 `MenuTextDisabledBrush`；危险项 `MenuDangerBrush` |
  | 分割线 | 1px，负边距 `-4,4` 贯穿整条菜单（抵消卡片 4px 内边距） |
  | 光标 | 系统箭头（与 Win11 原生菜单一致，非 Hand） |

- 交互：Esc 关闭；↑/↓ 在可用行间循环移动焦点；Home/End 跳到首/末可用行；Enter/Space 由 `Button` 原生触发；点击菜单外部（失焦）关闭；忙碌（快照/检查进行中）时这两项禁用，其余可用。
- **前台锁加固（真机重点）**：点托盘图标时前台是 explorer，`Show()`/`Activate()` 可能被 Windows 前台锁拒绝。`OnLoaded` 里用 `WindowInteropHelper` 取 HWND 并 P/Invoke `SetForegroundWindow`，结果（`foreground=set|denied active=`）写入 `Diagnostics` + Debug 输出，TrayHost 再把 `WARN:` 行落 Loom 日志。若窗口仍未激活（`ActivationFallbackActive`）：菜单内容保持 `Mouse.Capture(Root, CaptureMode.SubTree)`，外部点击 / 捕获丢失（`OnLostMouseCapture`）会主动关菜单，避免「未激活 → 无 Deactivated → 菜单一直悬着」；激活成功后自动释放捕获，回到正常的 `Deactivated` 关闭路径。未激活时 Esc/方向键无法送达（Windows 不把键盘给非前台窗口），这是已知限制（见 §7）。
- 打开状态面板修复（见 §6.4）：**行点击只记录 pending 并同步关闭菜单窗口，动作在 `TrayMenuWindow.OnClosed` 里用 `Dispatcher.BeginInvoke(Background)` 排队执行**；菜单触发的打开走 `forceOpen`（不 toggle、不瞬间关闭）。弹层侧再加 300ms/未激活失焦保护兜底。
- 定位：光标所在显示器工作区（`Screen.FromPoint`），菜单卡片右缘对齐光标、底边在光标上方 8px；工作区放不下时翻到下方/左右居中夹取；设备像素 → DIP 用光标显示器 DPI（`GetDpiForMonitor`，PerMonitorV2）换算（150%/200% 不跑偏，混合 DPI 多屏同样正确）。

---

## 3. 主题与 token 映射

`ThemeManager.Apply("system" | "dark" | "light")`：
1. 解析 system → `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`（0=dark）。
2. 把 `Themes/Controls.xaml`（样式，唯一一份）与当前主题字典（token）合并进 `Application.Current.Resources.MergedDictionaries`；切换时只替换主题字典，已打开窗口通过 `DynamicResource` 实时换肤。
3. `ThemeChanged` 事件 + `ThemeManager.RequestedTheme` / `EffectiveTheme` / `IsDark` / `Brush(key)`。
4. 说明：仅启动时探测系统主题，运行中系统主题变化不自动跟随（见 §7）。

| Token（Key） | 深色 | 浅色 | 用途 |
|---|---|---|---|
| `BgBrush` | `#151517` | `#f2f4f7` | 预览页底 |
| `L1Brush` | `#232324` | `#ffffff` | 窗口/内容底、卡片（弹层状态卡） |
| `L2Brush` | `#2c2c2e` | `#f7f8fa` | 内容卡片、次级按钮、输入框 |
| `L3Brush` | `#353638` | `#eceef2` | 开关轨道、悬停 |
| `SidebarBrush` | `#1b1b1c` | `#f0f2f5` | 设置左导航 |
| `TextPrimary/Secondary/Tertiary/Quaternary` | `#f9fafb` / `#cfd3d6` / `#adb2b8` / `#81858c` | `#0f1115` / `#61666b` / `#81858c` / `#9aa0a6` | 标题/正文/说明/最弱 |
| `AccentBrush` | `#7aaaff` | `#4176e6` | 链接、选中、强调 |
| `Success/Warning/Danger` | `#4ed17e` / `#f59e0b` / `#f25a5a` | `#22c55e` / `#f59e0b` / `#ef4444` | 状态 |
| `BorderBrush` / `BorderStrongBrush` | `#14ffffff` / `#26ffffff` | `#e4e7ec` / `#d4d9e1` | 1px 描边层级 |
| `ButtonPrimaryBg/Text` | `#f9fafb` / `#151517` | `#121317` / `#ffffff` | 主按钮（深色白底、浅色黑底） |
| `ButtonSecondaryBg/Text` | `#2c2c2e` / `#f9fafb` | `#ffffff` / `#0f1115` | 次级按钮 |
| `ToastBrush` / `ToastTextBrush` | `#43454a` / `#ffffff` | 同左 | 气泡/提示语义色 |
| `LogoTileGradientBrush` | `#2e3034 → #232427 → #1a1b1e`（135°） | `#ffffff → #f7f8fa → #e9ecf2`（135°） | 品牌标 tile 渐变（同一 SVG 几何，深浅两套） |
| `LogoTileBrush` / `LogoGlyphBrush` / `LogoLineBrush` | `#232427` / `#f9fafb` / `#4176e6` | `#ffffff` / `#191a1d` / `#4176e6` | 品牌标：深色=深底浅字形，浅色=浅底深字形；S 线与对钩两套都是品牌蓝 |
| `LogoTileBorderBrush` | `#14ffffff` | `#0f0f1115` | 品牌标 tile 描边（SVG 1/1024 细边，按最小可见 0.5px 绘制） |
| `MenuSurfaceBrush` / `MenuBorderBrush` | `#242426` / `#26ffffff` | `#ffffff` / `#e4e7ec` | 右键菜单卡片底/描边 |
| `MenuHoverBrush` / `MenuPressedBrush` | `#2f3033` / `#383a3d` | `#f0f2f6` / `#e6e9ef` | 菜单行悬停/按下 |
| `MenuTextBrush` / `MenuTextDisabledBrush` / `MenuIconBrush` | `#f9fafb` / `#6b6f76` / `#adb2b8` | `#0f1115` / `#b3b8be` / `#61666b` | 菜单文字/禁用文字/图标 |
| `MenuSeparatorBrush` / `MenuDangerBrush` | `#1affffff` / `#f08a8a` | `#e6e9ee` / `#c94a4a` | 贯穿分割线 / 「退出」弱化危险色 |

**图标主题规则（本轮修复）**：托盘图标 variant、窗口 `Icon`、软件内 `LogoMark` **三者统一跟应用生效主题**（`ThemeManager.EffectiveTheme`，config=system 时即系统主题）。`ThemeChanged` 时：托盘图标强制重取（`ReplaceIcon(force:true)`）、已开窗口 `Icon` 刷新、LogoMark 因 `DynamicResource` 自动跟随。这与设计说明「托盘按系统主题选变体」有一处差异：手动把 Loom 主题设为 dark 而 Windows 为浅色时，三者仍保持一致（优先一致性；深浅底对钩 ico 在深浅任务栏下都有描边与对比度）。

圆角/字号：主按钮 44px 高 / 12px 圆角；窗口 18px；卡片 14px（内容卡 12px 内嵌卡）；弹层 18px；标题 21/16/13.5，正文 12.5，说明 11.5，注脚 11/10.5，等宽报告 11.5。字体 `Segoe UI, Microsoft YaHei UI`，品牌 `Montserrat`（缺失时回退 Segoe UI，未打包字体文件）。

所有颜色均为 `DynamicResource`，主题切换/新旧窗口混用均安全；仅不随主题变化的图标用 `#ffffff`（开关滑块、复选对钩）和阴影黑色。

---

## 4. 托盘与通知

| TrayState | 状态点 | 图标底 | 弹层文案色 |
|---|---|---|---|
| `Normal` | 无 | 按应用生效主题（深/浅 ico） | 成功绿 |
| `Busy` | 蓝（半透明） | 同上 | 强调蓝 |
| `AwaitingExit` | 黄 | 同上 | 警告黄 |
| `NeedsManual` | 黄 | 同上 | 警告黄 |
| `Failed` | 红 | 同上 | 危险红 |
| `NotFound` | 灰 | 同上 | 三级文字 |

- 基础 ICO 用 `AppServices.OpenEmbedded("Loom.Icons.light.ico"/"Loom.Icons.dark.ico")`（缺失回退输出目录 `Assets/Icons`）。ICO 是 PNG 压缩帧，GDI+ 直接画会花屏，因此 `TrayIconFactory` 自行解析 ICO 目录取 32px PNG 帧再用 GDI+ 合成状态点；`Bitmap.GetHicon()` 得到的 HICON 在替换时显式 `DestroyIcon`，避免句柄泄漏。
- 托盘图标 variant 由 `ThemeManager.EffectiveTheme` 决定（深色主题 = 深底 ico，浅色 = 浅底 ico），`ThemeChanged` 时 `ReplaceIcon(force:true)` 立即重取；窗口图标走 `AppIcon`、软件内 `LogoMark` 走 DynamicResource，三者同源同主题。
- `NotifyIcon.Text` = 当前 headline，截断到 63 字符。鼠标事件：左键 → 弹层开关（`ShowPopup()`）；双击 → 弹层开关；右键 → WPF 自绘菜单 `TrayMenuWindow`（见 §2.5）。忙碌时菜单里的「立即快照/检查兼容性」禁用；旧 `ContextMenuStrip` 相关字段与 `ShowImageMargin/Font` 设置已删除。
- 通知：`Loom.NotificationRaised` → `Notify()`；遵守 `Config.Notifications`、`QuietHoursEnabled` 与 `QuietHoursStart/End`（支持 23:00–08:00 跨午夜），按 `EventId` 内存 `HashSet` 去重；`ShowBalloonTip(4000, title, message, Info/Warning/Error)`；点击气泡打开对应报告（`NotificationPresenter` 本轮未改动）。
- 通知策略只为 UI 层兜底：`LoomService` 仍然持久化 `LastNotifiedEventId`。

---

## 5. 预览渲染（`loom-dsh ui render`）

实现：`TrayHost.RenderSnapshots(outputDir = null)`：
- 输出目录 `outputDir ?? LoomPaths.DataRoot\ui-preview\`（自动创建）。
- 深/浅主题各渲染 4 页，共 8 个 PNG：`popup-{theme}.png` / `settings-{theme}.png` / `wizard-{theme}.png` / `tray-menu-{theme}.png`（theme = dark/light），返回绝对路径列表。
- 页面为 `UserControl`（不依赖窗口），`UiPreviewRenderer` 先做两遍 Measure/Arrange（让绑定、ItemsControl 完成布局），再 `RenderTargetBitmap` 以 192 DPI（2x）渲染，四周留 40px 以便显示卡片阴影；页底用当前主题 `BgBrush`。
- 渲染数据：优先使用真实安装信息（若托盘已解析），否则使用代表性演示数据（3 个插件 / 4 份快照 / 42.6 MB / 3 条活动），保证截图稳定且不触发 DSH 探测或快照。托盘菜单为 `TrayMenuViewModel.CreatePreview()`（真实菜单项数据、动作全部 no-op）。渲染结束会恢复用户主题。

验证命令（数据目录必须在工作区内，DSH 沙箱不允许写 `%LOCALAPPDATA%\Loom`）：

```powershell
cd D:\BS\Loom-DSH\src
dotnet build Loom\Loom.csproj -p:UseSharedCompilation=false
$exe='D:\BS\Loom-DSH\src\Loom\bin\Debug\net10.0-windows\win-x64\loom-dsh.exe'
cmd /c "`"$exe`" ui render --data-root D:\BS\Loom-DSH\.tmp-ui-tray > out.txt 2> err.txt"   # 退出码 0
Get-ChildItem D:\BS\Loom-DSH\.tmp-ui-tray\ui-preview   # 10 张 PNG
```

注意：
- 若有托盘实例在运行，`ui render` 会经命令队列转给旧托盘执行且**不会退出**；测试前先 `Get-Process loom-dsh` 并结束旧进程。用 `cmd /c` 包一层才能拿到 WinExe 的退出码（PowerShell 的 `&` 对 GUI 子系统进程不等待）。
- 本轮 10 张产物位于 `D:\BS\Loom-DSH\.tmp-ui-tray\ui-preview\`（构建产物目录不入库），已按 `read_image` 逐张核对 token 颜色/层级/菜单样式/LogoMark 深底浅字形，并复制到 `design/ui/impl-preview/`。
  - 0.1.1 起新增 `settings-update-{dark,light}.png`：渲染「更新与兼容」页（应用更新卡片所在页），专门防住“只读属性被 TwoWay 绑定”这类编译期发现不了、运行期才炸的缺陷。
- 图标对照图由 `spike/tray-ui-harness`（gitignored）生成：`design/ui/impl-preview/icon-parity.png`（深/浅两行：LogoMark 96/48 + 托盘 ico 64/32/16 同屏对比，几何/配色一致）。

---

## 6. 交互与线程模型

- 所有窗口操作在 WPF `Dispatcher` 上执行：`Loom.StatusChanged` / `NotificationRaised` 可能来自后台线程，`TrayHost` 统一 `BeginInvoke` 回 UI 线程。
- 异步操作（快照/检查/还原）经 `AsyncRelayCommand` 防重入；VM 统一 try/catch，失败通过界面提示与气泡呈现，不崩溃托盘。
- 弹层/设置/向导/报告均单实例：重复打开时激活已有窗口；弹层失焦自动关闭（见 §6.4 的 300ms 保护）。设置/向导/报告标题栏、任务栏、Alt-Tab 图标统一由 `AppIcon` 按生效主题提供，`ThemeChanged` 时刷新已开窗口。
- 关闭设置窗口若未保存会回滚主题预览；保存后关闭保留。
- 所有 `ShellLauncher` 调用都是 best-effort：目录不存在时尝试创建再打开，失败静默记录。
- 托盘右键菜单与弹层互斥：右键打开菜单会让弹层失焦关闭；菜单项执行后（设置/报告/状态面板）由各自窗口承接焦点。

### 6.4 「打开状态面板」点击无反应：根因与修复

**现象**：WinForms 版托盘菜单里点「打开状态面板」，代码走到了 `ShowPopup()`，但状态弹层从未出现（或一闪即消）。左键点击托盘图标打开正常。

**根因**：`NotifyIcon.ContextMenuStrip` 是**独立的原生激活窗口**。`ToolStripMenuItem.Click` 回调发生在菜单仍处于激活/正在关闭的过程中；此时新建 `TrayPopupWindow.Show()+Activate()`，前台窗口仍是菜单。菜单随后关闭并归还/清空前台激活，WPF 立刻收到新弹层的 `Deactivated`，而旧 `TrayPopupWindow.OnDeactivated` 无条件 `Close()` —— 弹层在用户看到之前就被关掉，表现为「点击无反应」。左键路径没有这个中间的原生菜单窗口，所以正常。

**修复（两道防线）**：
1. 菜单改为 WPF 自绘（§2.5）并**先关后执行**：行点击只把 `TrayMenuItem` 记到 `TrayMenuWindow._pending` 并同步 `Close()`；`OnClosed` 里用 `Dispatcher.BeginInvoke(DispatcherPriority.Background)` 执行动作。动作运行时菜单 HWND/焦点已完全销毁，不再有抢激活的窗口。菜单触发的打开用 `ShowPopupCore(forceOpen:true)`，不会走左键的 toggle 关闭分支。
2. `TrayPopupWindow.OnDeactivated` 加保护：打开后 **300ms 内** 或 **尚未真正激活过**（`OnActivated` 未触发）时忽略失焦；之后正常失焦关闭。即使有第三方窗口抢焦点也不会误关。

**前台锁与捕获兜底（复审补丁）**：点通知区图标时前台窗口是 explorer，本进程没有最近输入事件，WPF `Show()/Activate()` 可能被 Windows 前台锁拒绝。未激活时 `Deactivated` 不会触发（菜单会一直悬着）、Esc/方向键也收不到。因此 `TrayMenuWindow` 增加：
- `OnLoaded` 中 `WindowInteropHelper.Handle` → P/Invoke `SetForegroundWindow`，结果与 `IsActive` 记录到 `Diagnostics`（也写 Debug；`WARN:` 行由 TrayHost 落 Loom 日志），并带一次 `Activate()` 重试；
- 仍未激活时对菜单内容 `Mouse.Capture(Root, CaptureMode.SubTree)` 进入兜底：`OnLostMouseCapture`（决定函数 `ShouldDismissOnMouseCaptureLoss`：仅 `可见 && !IsActive && 非 invoke/非 closing` 时关）与 `OnRootPreviewMouseDown`（`IsPointInsideMenu` 之外的按下即关）双路径关闭菜单；一旦窗口激活成功就释放捕获，回到正常 `Deactivated` 关闭路径。
- 限制：未激活时 Esc/方向键无法送达（Windows 不把键盘输入给非前台窗口）；真机需确认托盘右键在「用户正在别的程序里操作」时菜单仍能弹出/关闭，且激活失败时第一条外部点击被菜单消费（不穿透到下层窗口）。

**回归验证**：`spike/tray-ui-harness`（WPF 小程序，`dotnet build` 后用 `dotnet <dll> --icons <dir>` 运行）共 32 项断言全 PASS、退出码 0：
- 菜单点击后窗口同步关闭、动作不在点击栈内执行、关闭时间戳早于动作时间戳；菜单可再次打开并重复成功触发（不再「点一次之后没反应」）；
- 模拟菜单关闭抢焦（`OnDeactivated` 在 60ms 内）弹层不关，380ms 后再失焦正常关闭；
- 菜单→「打开状态面板」端到端：动作执行时菜单已不可见，弹层保持可见；
- busy=true 禁用快照/检查、busy=false 恢复；
- 前台锁：记录 SetForegroundWindow 尝试、注入「前台被拒绝 + 不激活」模拟后 `ActivationFallbackActive=true`、捕获丢失/外部点击关闭未激活菜单，以及 `ShouldDismissOnMouseCaptureLoss`/`IsPointInsideMenu` 决策表；
- 并生成 `icon-parity.png` 图标对照图。

---

## 7. 已知限制 / 与设计稿的差异

1. **无背景模糊（blur）**：设计稿的毛玻璃/噪点未实现，改用纯色 + 1px 描边 + 阴影表达层级。
2. **无 Windows Toast**：首版使用 `NotifyIcon.ShowBalloonTip`（未签名 exe 最可靠）；Toast 需要开始菜单快捷方式 + AppUserModelID，二期再做。
3. **系统主题只在启动时探测**：运行中修改 Windows 深浅色不会自动切换（设置里可手动覆盖，保存后立即生效）。
4. **品牌字体**：未打包 Montserrat，本机无该字体时用 Segoe UI Semibold 兜底；无字距调整（WPF 无 letter-spacing）。
5. **预览数据**：`ui render` 的截图使用代表性数据（非真实快照计数），仅为视觉验收；真实运行时弹层/设置/报告读取 `LoomStatus`。
6. **设置分区滚动**：窗口固定 640 高（适配 768p 屏），内容较长的分区（备份与还原）在窗口内滚动，不放大窗口。
7. **首启向导为单页**：设计允许 3 步或单页；这里用单页以减少打断，快照结果就地显示。
8. **托盘图标外观只在真实任务栏验证**：预览图不含通知区域截图；图标对照图 `design/ui/impl-preview/icon-parity.png` 展示 LogoMark 与 ico 帧（64/32/16）的深/浅对照，状态点仍只在真机/合成位图中验证（本沙箱不可注册通知区图标）。
9. **深色主题下卡片对比度**：卡片使用 L2、窗口 L1，差异较设计稿更克制（受 CRT/远程桌面压缩影响），可在 `Dark.xaml` 调整。
10. **无设置项写真实系统策略**（如安装目录/SH_HOME 覆盖）到 UI：这些仍走 CLI `--install-dir/--dsh-home`，避免误改。
11. **托盘右键菜单无 Win11 原生毛玻璃/亚克力**：任务决策为跟随 Loom 应用主题的 WPF 纯色卡片 + 1px 描边 + 轻阴影（与 DeskOrder 的「跟随系统主题 + DWM acrylic」不同）；无子菜单/快捷键提示/图标列 24px（Loom 用更紧凑的 16px），行高/圆角/hover/贯穿分割线对齐参考稿。
12. **菜单光标为系统箭头**：与 Win11 原生菜单一致（DeskOrder 同款），不使用 Hand。
13. **窗口图标为单帧 64px BitmapSource**：WPF 会按 DPI 缩放；exe 文件图标仍固定浅底 ico（`ApplicationIcon`，资源管理器不带主题语义），标题栏/任务栏/Alt-Tab 图标跟主题。
14. **托盘菜单/弹层定位依赖显示器 API**：用 `MonitorFromPoint`+`GetDpiForMonitor` 取光标所在屏 DPI 换算 DIP（`PresentationSource` 兜底）；本沙箱无法用真实多显示器/高 DPI 点击验证，已用代码审查 + harness 打开窗口验证，需真机复验 150%/200% 与混合 DPI 双屏。
15. **前台锁场景真机待验证**：`SetForegroundWindow` 被拒绝时菜单靠 mouse capture + 外部点击关闭（未激活时 Esc/方向键不可用为预期限制）；需真机确认「用户在别的程序里操作时点托盘右键」菜单能弹出、第一条外部点击被菜单消费而不穿透下层窗口，并确认 TrayHost 的 `WARN:` 诊断落 Loom 日志（Debug 输出始终可看）。

---

## 8. 集成注意点（给 Lead / 其他工作流）

- `TrayHost` 不修改 `AppServices.UiHandler`（由 App 注入 `UiRouter`），只实现 UI 方法与 `RenderSnapshots`。
- `TrayHost` 不持有/释放 `AppServices`；`Dispose` 只关闭右键菜单窗口、释放 `NotifyIcon`/HICON 并退订主题/服务事件（弹层等窗口由用户关闭）。
- `NotifyIcon` 与 WPF 共用 UI 线程消息循环（`UseWindowsForms`）；不要在其他线程直接操作 `NotifyIcon`。
- `RenderSnapshots` 使用同步 `ResolveRenderStatus`（不触发 `RefreshStatusAsync`），避免 `ui render` 期间 App 定时器触发更新检测/快照等核心操作。
- 主题字典的位置/命名被 `ThemeManager` 按 `pack://application:,,,/<assembly>;component/Themes/*.xaml` 加载，重命名文件需同步 `ThemeManager`。
- 若其他工作流需要新窗口/页面：页面实现为 `UserControl`（固定尺寸，`Width`/`Height`），窗口只做透明 chrome + 阴影边距；渲染器可直接复用 `UiPreviewRenderer.Render`。
- 新文案请加到 `ViewModels/UiStrings.cs`，避免散落在 XAML。
- **契约变更（task-6）**：`TrayHost` 不再创建 `System.Windows.Forms.ContextMenuStrip`；右键在 `NotifyIcon.MouseClick` 中处理。菜单是可复用的 `TrayMenuWindow(TrayMenuViewModel)`，命令通过 `TrayMenuActions` 记录注入（`OpenStatusPanel/Snapshot/Check/OpenReport/OpenSettings/OpenBackupFolder/Exit`）——预览与 harness 用 `TrayMenuActions.Preview`，不需要真实 `TrayHost`。`RenderSnapshots` 现在返回 **8** 个路径（新增 `tray-menu-dark/light.png`），调用方不要假设 6 个。
- **图标契约**：窗口图标统一用 `AppIcon.Current`（主题对应内嵌 ico 的 64px PNG 帧）；新增窗口请同样在创建时赋值并在 `ThemeManager.ThemeChanged` 时刷新（TrayHost 已对 Settings/FirstRun/Report 处理）。托盘图标底色用 `ThemeManager.IsDark` 决定，不要再调用 `DetectSystemTheme()` 单独判断。
- **TrayMenuWindow 测试缝（可选构造参数）**：`diagnosticSink`（诊断行回调，TrayHost 传 `MenuDiagnostics` 把 WARN 落日志）、`foregroundSetter`（默认 P/Invoke `SetForegroundWindow`，harness 注入 `_ => false` 模拟前台锁拒绝）、`activationRetry`（默认 `Activate()`，harness 注入 no-op）。生产代码保持无参默认行为。

---

## 9. 验收状态（本次 task-6）

- `dotnet build src\Loom\Loom.csproj -p:UseSharedCompilation=false`：**0 error**（2 个 WFO0003 DPI manifest 警告，按约定忽略）。
- 测试：`Loom.TestRunner` 反射 runner 全绿 **175/175 PASS**（当前仓内总数；含应用内更新 32 例，0 fail）。
- `loom-dsh.exe ui render --data-root D:\BS\Loom-DSH\.tmp-ui-tray`：**退出码 0**，生成 **10 张** PNG（popup / settings / settings-update / wizard / tray-menu × dark/light），无残留托盘进程；产物已核对并复制到 `design/ui/impl-preview/`。
- 行为验证：`spike/tray-ui-harness` **32/32 checks PASS，退出码 0**（菜单先关后执行、可重复打开、busy 禁用、弹层失焦保护、菜单→状态面板端到端、前台锁/capture 兜底决策、图标对照图生成）。
- 复审补丁（前台锁加固）：`TrayMenuWindow` 增加 `SetForegroundWindow` 尝试 + Diagnostics/WARN 落日志、未激活时 mouse-capture 外部点击关闭兜底、`ShouldDismissOnMouseCaptureLoss`/`IsPointInsideMenu` 决策函数；`dotnet build src\Loom\Loom.csproj -p:UseSharedCompilation=false` 仍 0 error，harness 由 20 → 32 checks 全 PASS。
- 图标一致性：`design/ui/impl-preview/icon-parity.png`（LogoMark 96/48 与托盘 ico 64/32/16 深/浅对照）。
- 未触碰真实 profile 与 `D:\DeepSeek`（只读探测由核心工作流负责）；未联网；未新增 NuGet 包；未修改 `Program.cs` / `AppServices.cs` / `LoomServiceFactory.cs` / `src/Loom.Core/**` / `installer/**`。

## 10. 0.1.1 修复记录：设置窗口打不开（TwoWay 绑定只读属性）

**症状**：从托盘菜单点「设置」（或 loom-dsh ui settings）无任何反应；进程仍在、日志里没有新行；ui render 只产出 popup 一张图后卡住不退出。

**根因**：0.1.1 之前的「应用更新」卡片里写的是

```xml
<ProgressBar Value="{Binding UpdateProgress}" ... />   <!-- 错 -->
```

RangeBase.Value 的默认绑定模式是 **TwoWay**，而 SettingsViewModel.UpdateProgress 只有 private set。WPF 在创建 SettingsView 时抛
InvalidOperationException: A TwoWay or OneWayToSource binding cannot work on the read-only property 'UpdateProgress'：
- 异常出现在 SettingsView 初始化阶段（窗口尚未 Show），被 DispatcherUnhandledException 吞掉（e.Handled = true）→ 窗口静默不出现；
- ui render 里异常发生在 ShowTarget() 内 → Dispatcher.BeginInvoke(Shutdown) 那行永远执行不到 → 进程留在托盘里“假死”。

**修复**：
1. Value="{Binding UpdateProgress, Mode=OneWay}"（只读展示型属性统一显式 Mode=OneWay）。
2. App.OnStartup：初始化界面包 	ry/catch；ender 模式在 inally 里无条件 Shutdown()，渲染失败也会退出并返回非 0。
3. TrayHost.ShowSettingsCore/ShowWizardCore/ShowReportCore：窗口创建失败 → 清理半初始化状态 + 写日志 + 弹一次可读的错误提示，不再静默。
4. AppServices.TryLogException 改用服务自身的 logger（LoomService.LogException）。此前它另开一个 FileLogger 追加同一文件，
   两个句柄各自维护偏移会互相覆盖 —— 本次真正的那行 ERROR 就被后续 INFO 覆盖成了半行，属于“有日志等于没日志”。
5. ui render 增加 settings-update-{dark,light}.png（渲染「更新与兼容」页），把这类只在运行时爆炸的绑定问题纳入回归验证。

**验证**：ui render --data-root <工作区内目录> → 退出码 **0**、**10 张** PNG（含 settings-update 深浅各一张，已逐张 ead_image 核对）；
ui settings → 主窗口 Loom 设置 可见（MainWindowHandle != 0）；全量单测 **175/175 PASS**。