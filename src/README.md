# Loom for DSH · 代码工程说明（开工基线 v0.1）

> 状态：骨架已建立并可编译（2026-11-14）。三个工作流并行实现：核心工作流 / 环境适配 / WPF 界面。
> 设计依据：`..\docs\实现方案-v2.md`（首版范围 A：快照 + check + 文件级还原 + 报告 + 托盘 + 设置）。
> 设计资产：`..\design\`。

## 1. 工程结构

```
src\
  Loom.slnx                      .NET 10 解决方案（注意：不是 .sln）
  Directory.Build.props         共用编译设置（net10.0 / net10.0-windows）
  Loom.Core\                    net10.0 类库（无 UI 依赖，可单测）
    Models\                     数据模型（Lead 维护）
    Abstractions\               I* 接口（Lead 维护）
    Infrastructure\             路径/JSON/原子写/哈希/日志/journal/配置状态/操作闸门
    Snapshot\                   快照采集、持久化、保留策略（core-flows 负责）
    Check\                      更新后检查、证据分级、兼容性（core-flows 负责）
    Restore\                    还原计划与文件级应用（core-flows 负责）
    Reporting\                  report.json / report-*.md（core-flows 负责）
    Environment\                定位器/进程/锁/CLI/更新器/自启/体检（env-adapters 负责）
    Evaluation\                 Electron helper 桥（env-adapters 负责）
    Cli\                        CLI 解析与命令实现（Lead 完成）
    Ipc\                        单实例 + 命令队列（Lead 完成）
    LoomService.cs              编排/状态机/通知（Lead 完成）
    Composition\                组合根（Lead 完成）
  Loom\                         WPF 托盘 + CLI 同一 exe（loom-dsh.exe）
    Program.cs / App.xaml(.cs)  入口、CLI/tray 分流（Lead 维护）
    AppServices.cs              服务容器 + 队列执行器（Lead 维护）
    Tray\ Views\ ViewModels\ Themes\ Services\   UI（ui-tray 负责）
    Assets\helper.mjs           Electron Node 评估器桥（env-adapters 负责）
    Assets\Icons\               图标（ui-tray 负责新增状态图标）
  Loom.Tests\                   xunit 测试（各工作流在自己子目录添加）
```

## 2. 构建与测试（离线环境）

```powershell
cd D:\BS\Loom-DSH
powershell -ExecutionPolicy Bypass -File scripts\build.ps1     # 构建 + 全部单元测试
powershell -ExecutionPolicy Bypass -File scripts\test.ps1      # 只跑测试
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1   # 单文件自包含 win-x64
powershell -ExecutionPolicy Bypass -File scripts\acceptance-sandbox.ps1  # 沙箱验收 A01/A06/A14/A15/A17/A23/A24/A26
```

- 本机 NuGet 源（`nuget.azure.cn`）不可达；依赖已在本机缓存，**不要新增 NuGet 包**（如需新包先联系 Lead）。
- 受限沙箱（如 DSH 自身）里 `dotnet build Loom.slnx` 与 `dotnet test` 会因 Workload SDK resolver / VSTest testhost 的进程句柄限制失败；`scripts\build.ps1` 改为逐项目构建并自动回退到 `src\Loom.TestRunner`（进程内反射 runner，支持 `[Fact]` 与 `[Theory]+[InlineData]`）。正常桌面环境仍可直接 `dotnet test`。
- 发布（单文件自包含，约 **173.5 MB**，含 SHA256）：
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
  # 产物：dist\loom-dsh.exe、dist\SHA256SUMS.txt、dist\发布说明.txt
  ```
  脚本内部先 `dotnet restore -r win-x64 /p:SelfContained=true` 再 `publish --no-restore`，以适配离线缓存。
- 中文文本文件一律用 `write`/`edit` 工具写；若必须用 PowerShell，用
  `[System.IO.File]::WriteAllText($p,$c,(New-Object System.Text.UTF8Encoding($false)))`，
  不要用 `Set-Content`（会破坏 UTF-8）。
- 当前 PowerShell 是 5.1：`Set-Content -Encoding utf8NoBOM` 不支持；**含中文的 .ps1 必须带 UTF-8 BOM**，否则 5.1 按 ANSI 解析会报"缺少字符串终止符"。
- 受限沙箱运行 CLI/UI 时加 `--data-root <工作区内目录>`（`%LOCALAPPDATA%` 不可写）。

## 3. 关键环境事实（已实测，2026-11-14）

| 项 | 值 |
|---|---|
| 安装目录 | `D:\DeepSeek`（自动定位，不硬编码） |
| 应用版本 | `resources\runtime\primary-runtime\runtime.json` → `desktopVersion`（0.2.2-rc.1） |
| DSH_HOME | `C:\Users\<user>\.dsh`；profile：`profiles\desktop` |
| CLI | `D:\DeepSeek\resources\runtime\cli\bin\dsh.cmd`（`--profile` 在 `plugin` 之后） |
| Electron | `D:\DeepSeek\DeepSeek Harness.exe`；`ELECTRON_RUN_AS_NODE=1` 时可执行 JS 并读取 `app.asar` |
| app.asar | 单文件归档 `D:\DeepSeek\resources\app.asar`（C# `File` 不能读；Electron Node 的 fs 可以） |
| 官方模块 | `app.asar\dsh\node_modules\@deepseek-ai\dsh-app-boot\lib\index.js` |
| evaluator | `evaluatePluginCompatibility(manifest, exemptions, runtimeVersion)`：**兼容返回 undefined**，不兼容返回 issue 对象；`getDshRuntimeVersion()` 可用 |
| 更新器缓存 | `%LOCALAPPDATA%\@deepseek-aidsh-desktop-updater`（pending\temp-*、update-info.json、installer.exe） |
| profile 锁 | `<profile>\lock`（PID）、`<profile>\package.json.lock`、`<profile>\.plugin-manager\run.json` |
| 工具数据 | `%LOCALAPPDATA%\Loom`（由 `LoomPaths` 解析；`--data-root` 可覆盖） |

## 4. 共享契约（Lead 维护，改动需先沟通）

- 模型：`Loom.Core.Models.*`（`CoreModels.cs` / `SnapshotModels.cs` / `FlowModels.cs`）
- 接口：`Loom.Core.Abstractions.I*`（`Interfaces.cs` / `ILoomService.cs`）
- 基础设施：`LoomPaths`、`AtomicFile`、`JsonDefaults`、`Hashing`、`FileLogger`、`JournalWriter`、
  `ConfigStore`、`StateStore`、`OperationGate`、`SemVersion`、`DshDefaults`
- 组合根 `LoomServiceFactory` 使用的构造函数签名**保持稳定**；需要新增依赖时先发消息给 Lead。
- 所有阶段性输出用 `JournalWriter.Append(opId, OperationStage.X, "done|failed", message)`；
  文件写入用 `AtomicFile.*`；日志用 `ILogger`（已脱敏）。
- 错误用 `LoomException(code, message)`，错误码见 `LoomErrorCodes`。

## 5. 安全红线

- 绝不写真实 `C:\Users\<user>\.dsh` profile、绝不修改 `D:\DeepSeek` 安装目录/app.asar。
- 只读访问真实环境可以（读 manifest、列锁文件、读更新器缓存）。
- 测试一律在 `%TEMP%\loom-tests\<guid>` 沙箱内构造假 `DSH_HOME`/安装目录 + 真 profile 的副本。
- 不自动 kill 任何进程；不写 `compatibility.json` 之外的真实配置；不联网（本版无 registry 查询）。

## 6. UI 验证钩子（给 ui-tray）

- `loom-dsh ui render`：启动托盘型进程 → `TrayHost.RenderSnapshots()` 用 `RenderTargetBitmap`
  把托盘弹层/设置/向导渲染成 PNG 到 `%LOCALAPPDATA%\Loom\ui-preview\`，随后自动退出。
- `loom-dsh ui settings|popup|wizard|report`：正常打开窗口（托盘未运行时会自动拉起）。
- `App.ShowTarget` 已把 `render` 路由到 `TrayHost.RenderSnapshots`；`TrayHost` 是 ui-tray 维护的文件。

## 7. 实现状态（2026-11-14，范围 A + 二期 0.2.0）

| 区域 | 状态 |
|---|---|
| 环境适配（定位/进程/三锁/CLI 封装/更新器/自启/体检/Electron helper） | ✅ 完成（env-adapters，35 用例） |
| 核心工作流（ProfileReader/快照/保留/check/证据/还原/报告） | ✅ 完成（core-flows，41 用例） |
| 编排/CLI/单实例/命令队列/状态机/通知 | ✅ 完成（Lead） |
| 二期：策略 A 自动升级 / 包级恢复 / 离线完整回滚 | ✅ 完成（默认关闭，标实验性；`upgrade` / `recovery plan\|apply\|rollback`） |
| 跨进程一致性（数据根派生命名互斥量 + 数据根文件锁 + 跨进程安装包校验） | ✅ 完成（收口轮次） |
| WPF 托盘/弹层/设置/向导/报告/深浅主题/render 自检 | ✅ 完成（ui-tray，render 12 张 2x 预览） |
| 单元测试 | **403/403** 通过（含基础设施、核心、环境、更新、二期与 UI 渲染审计） |
| 沙箱验收脚本 | 9/9 通过（A01/A06/A14/A14b/A15/A16/A17/A23/A26 辅助） |
| 单文件发布 | ✅ `dist\loom-dsh.exe`（0.2.0 本地构建 173,921,776 B，SHA256 `1f1fccfa…4908e5`；未发布） |
| 代码签名 / 跨进程 config 写协调 / 真机 smoke | ⏳ 未做（见 `docs/验收报告.md` §14.6/§14.7） |

### 已知限制（发布说明需保留）

- 自动文件级还原只在**强证据**（`.bak-*`/基线重置/恢复流程日志）下执行，且只加回 check 判定为候选的 bundle；用户主动禁用的插件不会被自动加回（设计 12.4）。
- 数据目录 ACL 收紧为 best-effort（`LoomPaths.EnsureDirectories`）；受限沙箱/非 NTFS 可能回退为继承权限，请勿分享数据目录。
- 本版不做包级恢复（pnpm install）与策略 A 自动升级；`dsh.cmd` 仅封装未用于安装。
- 真实环境 `dsh-plugin-wallpaper-engine@1.4.2` 被 helper 静态预检判为 `unverified`（其 `dsh.client.inject` 依赖 `@deepseek-ai/dsh-client-runtime` 不在运行时 node_modules），因此真实 check 会以 exit 2/托盘黄色呈现，属"未知不当作兼容"的预期行为。

设计取舍与集成注意见 `docs\core-flows.md`、`docs\env-adapters.md`、`docs\ui.md`；验收矩阵见 `docs\验收计划.md`，结果见 `docs\验收报告.md`。
