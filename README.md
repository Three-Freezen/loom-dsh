# Loom for DSH · DSH 插件更新守护

> 状态：**0.2.0 已发布**（Latest）（二期：策略 A 自动升级 / 包级恢复 / 离线完整回滚 + 一批 P2 健壮性修复；自动升级与包级恢复默认关闭、标实验性）；0.1.1 为上一个已发布版本（修复 0.1.0-beta 设置窗口打不开）；范围 A + 托盘右键菜单/图标一致性 + Inno Setup 安装包 + 安装目录数据根与旧数据迁移 + 应用内更新（GitHub Releases）均已完成；第三轮独立 QA 通过（`docs/验收报告.md` §13，无 P0/P1，4 项真机 smoke 见下）；收口轮次独立复审 **P1=0**，全量单测 **403/403**
> 更新：2026-11-14 · GitHub 公开仓库（MIT）：<https://github.com/Three-Freezen/loom-dsh> · 最新已发布版 **0.2.0**

Loom（织机）是一个 Windows 托盘小工具 + 同名 CLI，用于在 DeepSeek Harness 桌面版每次自动更新后，自动保护/恢复已安装的插件（profile bundles、配置与兼容性状态），并在存在版本兼容问题时给出提示与处置（默认策略 A：升级到兼容版本；自动执行需显式开启，默认关闭）。

## 已确认决策

| 项 | 结论 |
|---|---|
| 名称 | **Loom**；命令行 **`loom-dsh`**；产品名 Loom for DSH |
| 形态 | 托盘工具 + 同名 CLI（**不是 DSH 插件**）；提供 **Inno Setup 安装包** 与绿色单文件两种分发 |
| UI 技术 | **.NET 10 WPF（自包含 win-x64）+ WinForms NotifyIcon**；CLI 与托盘同一 exe |
| 托盘菜单 | WPF 自绘现代菜单（深/浅主题跟随应用主题；8~10px 圆角 / 36px 行高 / 图标 / 贯穿分割线 / 淡入） |
| 图标 | 对钩版（浅底/深底 + 多尺寸 ICO）；软件内 LogoMark、窗口图标、托盘图标同源同配色 |
| 主题 | 深色 + 浅色（对齐 DSH 原生 token） |
| 安装/更新 | Inno Setup 6（免管理员、用户目录、固定安装包名）；**应用内更新已实现**：GitHub Releases 检查 + 下载 + `/SILENT` 静默升级 + 自动重启 |
| 许可 | **MIT**（`LICENSE`） |
| 数据目录 | 安装版 `<安装目录>\Data`；绿色版/开发 `%LOCALAPPDATA%\Loom`；便携标记优先；`--data-root` 最高优先 |
| 兼容策略 | 策略 A：自动升级到兼容版本；无兼容版本时只提示 + 人工处理 |
| 首版范围 | **A**：快照 + check + 文件级还原 + 报告 + 托盘 + 设置；策略 A 进二期（标实验性）→ **已实现（0.2.0，默认关闭，实验性）** |
| 重启预期 | 接受"最多多一次重启"（三次启动定义见方案 8.1） |
| 插件范围 | 只守护 `desktop` profile；市场安装的 npm 插件；暂不备份插件业务数据 |

## 目录结构

```
Loom-DSH\
  docs\                         方案、审核报告、设计说明、spike 报告、验收、安装与更新
  design\
    ui\                         深/浅主题界面矢量参考图（SVG + PNG）；impl-preview\ 为实际实现渲染
    icons\                      定稿图标（对钩，浅/深底）、托盘尺寸、ICO、备选
    sources\                    设计稿生成源码（Python，输出 SVG + PNG）
  src\                          .NET 10 工程（见 src\README.md）
    Loom.slnx                   解决方案（NET 10 新格式）
    Loom.Core\                  快照/检查/还原/报告/环境适配/数据根/迁移/CLI/应用内更新（可单测）
    Loom\                       WPF 托盘 + CLI（输出 loom-dsh.exe）
    Loom.Tests\                 xunit（403 用例）
  installer\                    Inno Setup 安装脚本（loom-dsh.iss）与中文语言文件
  scripts\                      build.ps1 / test.ps1 / publish.ps1 / pack-installer.ps1 / acceptance-sandbox.ps1
  dist\                         loom-dsh.exe + loom-dsh-Setup.exe + SHA256SUMS.txt + 发布说明.txt
  README.md                     本文件
```

## 文档索引

| 文档 | 说明 |
|---|---|
| [实现方案-v2.md](docs/实现方案-v2.md) | 实现规格：架构、锁/一致性、更新状态机、快照/兼容/升级/还原、UI、验收 30 条、排期；第 19 节为审核 36 项闭环表 |
| [方案审核报告.md](docs/方案审核报告.md) | 独立审查报告（P0 7 / P1 17 / P2 12），含关键事实修正与验收用例 |
| [命名与设计说明.md](docs/命名与设计说明.md) | Loom 命名、图标寓意、DSH 设计 token、深/浅界面说明与文件索引 |
| [spike-report.md](docs/spike-report.md) | 1～2 天 spike 结果：.NET 10 单文件发布、Electron evaluator、CLI、更新器缓存、锁 |
| [验收计划.md](docs/验收计划.md) | A01–A30 验收矩阵、环境、上线门槛、自动化映射 |
| [验收报告.md](docs/验收报告.md) | QA 独立验收结果（范围 A 第一/二轮 + 本轮托盘/图标/安装包/迁移） |
| [安装与更新.md](docs/安装与更新.md) | Inno Setup 安装/静默升级/卸载、数据目录与优先级、旧数据迁移、应用内更新（GitHub Releases） |
| [ui.md](docs/ui.md) | UI 实现说明：托盘弹层/菜单/设置/向导/报告、主题 token、预览渲染、根因与限制 |
| [core-flows.md](docs/core-flows.md) | 核心工作流（快照/检查/还原/报告）实现取舍 |
| [env-adapters.md](docs/env-adapters.md) | 环境适配层（定位/锁/CLI/更新器/Electron evaluator）与数据根 |
| [方案v1-历史存档.md](docs/方案v1-历史存档.md) | 初稿存档（路径/命令/时序已被 v2 修正） |

## 安装与使用

```powershell
# ① 安装包（推荐）
dist\loom-dsh-Setup.exe                 # 向导安装，免管理员，默认装到 %LOCALAPPDATA%\Programs\Loom
                                         # 安装后可勾选“立即运行”；开始菜单/可选桌面图标

# ② 绿色版（免安装）
dist\loom-dsh.exe                        # 直接启动托盘

# CLI（同一个 exe，可在终端使用）
loom-dsh.exe status                      # 状态
loom-dsh.exe snapshot                    # 立即快照
loom-dsh.exe check                       # 兼容性检查（有未验证/不兼容时 exit 2）
loom-dsh.exe restore --dry-run           # 还原演练
loom-dsh.exe upgrade [--pkg <包名>] [--dry-run]   # 策略 A：升级到兼容版本（--save-exact，写前备份；不带 --pkg 按最近检查结果批量）
loom-dsh.exe recovery plan|apply [--snapshot <id>] [--dry-run]   # 包级恢复：写回 package.json/pnpm-lock.yaml + 离线 install
loom-dsh.exe recovery rollback <opId>    # 按 opId 离线完整回滚（恢复文件 + 离线重装 + 再次校验）
loom-dsh.exe doctor                      # 环境体检
loom-dsh.exe migrate-data                # 把旧数据目录迁移到当前数据根（--dry-run/--remove-source/--json）
loom-dsh.exe update                      # 检查应用更新（--download 下载 / --apply 静默升级 / --json）
loom-dsh.exe --help                      # 完整帮助
```

安装 / 静默升级 / 卸载：

```powershell
loom-dsh-Setup.exe                                  # 交互安装（可选择安装目录）
loom-dsh-Setup.exe /SILENT /DIR="<当前安装目录>"      # 静默升级（应用内更新/脚本；装完自动重启）
<安装目录>\unins000.exe /VERYSILENT /NORESTART        # 静默卸载；用户数据目录 Data 不会被删除
```

> 安装包未签名，SmartScreen 可能提示；请核对 `dist\SHA256SUMS.txt`。代码签名在二期。

## 数据目录与旧数据迁移

| 场景 | 数据根 |
|---|---|
| `--data-root <目录>`（CLI/开发/验收） | 指定目录（最高优先级，不迁移） |
| 便携模式：`<exe目录>\Data\portable.flag` 存在 | `<exe目录>\Data` |
| **Inno 安装版**（exe 旁有 `unins000.exe`） | **`<安装目录>\Data`**（卸载保留） |
| 绿色版 / 开发版（无 `unins000.exe`） | `%LOCALAPPDATA%\Loom`（历史默认，行为不变） |

- 安装版**首次启动**会把旧默认 `%LOCALAPPDATA%\Loom` 的设置/快照/报告/日志自动复制到 `<安装目录>\Data`：幂等、不覆盖已有文件、跳过 queue/锁/临时文件；自动重写 `state.json`/`config.json` 内的旧绝对路径；写 `Data\migration.json`；**只复制不删除**旧目录。
- 清理 C 盘旧目录：`loom-dsh.exe migrate-data --remove-source`（确认新目录数据完整后再用）。
- 安装目录里的 `Data` 不在安装器 `[Files]` 清单内，**升级与卸载都不会删除**。

## 应用内更新（GitHub Releases）

安装版在「设置 → 更新与兼容 → 应用更新」里提供检查、下载与「重启并更新」；托盘右键菜单也有「检查应用更新」。
启动后台检查默认开启（24 小时最多一次，成功/失败都计入间隔，可在设置里关闭）。

- 版本源：`https://github.com/Three-Freezen/loom-dsh/releases/latest` 的 302 tag（REST API 仅兜底）。
- 资产：固定文件名 `loom-dsh-Setup.exe`，直链 `releases/latest/download/loom-dsh-Setup.exe`。
- 下载：用户“下载”文件夹（不可写时回退 `<数据根>\updates`）；`.part` 原子改名，< 1 MB 视为失败。
- 校验：下载同时写旁车 `<数据根>\updates\loom-dsh-Setup.exe.meta.json`（SHA256/版本/大小/时间）；`update --apply`（含退出托盘后的跨进程调用）启动安装包前校验 文件存在 → 旁车 → SHA256/大小 → 内嵌 `ProductVersion` → 目标版本高于当前，任一不符即拒绝并提示重新下载。
- 安装：`/SILENT /DIR="<当前安装目录>"` → 应用退出让出单实例 mutex → Inno 静默升级 → `[Run]` 自动重启。
- 升级后清理：下载完成写 `pending-setup-cleanup.txt`（安装包**绝对路径**；旧版相对值按数据根解析）；新版首启确认升级成功后删除安装包与旁车（默认开启）。
- 绿色版/开发版：可检查版本，但需手动下载安装包（应用内安装仅在安装版可用）。
- CLI：`loom-dsh update [--download] [--apply] [--json]`；托盘运行中 `--apply` 会拒绝（退出码 4）。
- 发布新版：改 `Loom.csproj <Version>` → `scripts\publish.ps1` → tag `v<版本>` → Release 上传固定名安装包。

离线验证钩子：`--update-source <dir>`（本地固定资产目录）+ `--download-dir <dir>`，可完整跑通检查→下载→Ready。

## 代码与构建

工程说明、架构、共享契约与环境事实见 [src/README.md](src/README.md)。

```powershell
# 构建 + 测试（沙箱会自动回退到进程内 TestRunner）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1

# 发布：默认产出 dist\loom-dsh.exe + dist\loom-dsh-Setup.exe（找不到 Inno Setup 6 时只警告）
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

# 只出绿色版 / 只编译安装包
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -NoInstaller
powershell -ExecutionPolicy Bypass -File scripts\pack-installer.ps1 -SkipPublish
```

开发期在受限环境运行时可用 `--data-root <工作区内目录>` 覆盖数据根。

## 设计资产

- 界面：`design/ui/LOOM-UI-dark.svg` / `.png`，`design/ui/LOOM-UI-light.svg` / `.png`
- 图标：`design/icons/LOOM-icons.svg` / `.png`（定稿总览）；浅底/深底对钩 SVG/PNG/ICO（16–256）；圆点结备选在 `design/icons/alternatives/`
- 实现渲染：`design/ui/impl-preview/`——`popup|settings|settings-update|settings-compat|wizard|tray-menu-{dark,light}.png`（已入库 **12 张**，2x）+ `icon-parity.png`（软件内 LogoMark vs 托盘 ico 对照）；0.2.0 起 `ui render` 产出 **12 张**，其中 `settings-compat-{dark,light}.png` 为整页渲染（覆盖策略 A / 包级恢复卡片）

## 当前进度（范围 A + 本轮迭代）

1. ✅ spike：WPF 单文件发布、Electron evaluator、dsh.cmd、更新器缓存、profile 锁（见 [spike-report.md](docs/spike-report.md)）
2. ✅ 范围 A 实现：快照 / check / 文件级还原 / 报告 / 托盘 / 设置 / 锁与 journal / 首启向导 / 单实例与 CLI 队列
3. ✅ 本轮迭代：
   - 托盘右键菜单改 WPF 自绘（现代样式，深/浅主题；原「打开状态面板点击无反应」已按根因修复：先关菜单再执行 + 弹层失焦保护 + 前台锁捕获兜底）
   - 软件内图标与托盘图标统一：LogoMark 按设计 SVG 精确重绘、深/浅主题两套、对钩改品牌蓝；窗口图标随主题；托盘 ico 底色跟应用生效主题
   - Inno Setup 安装包（`installer\loom-dsh.iss`）：免管理员、`{userpf}\Loom`、固定包名 `loom-dsh-Setup.exe`、静默升级、卸载保留 Data、AppMutex 单实例保护
   - 数据目录改为安装版 `<安装目录>\Data`（绿色/开发保持 `%LOCALAPPDATA%\Loom`），首次启动自动迁移旧数据（可 `migrate-data` 手动/清理）
4. ✅ 验证：第三轮独立 QA **通过**（报告 §13）：单元测试 **175/175**（QA 时 143，新增 32 个应用内更新用例）；沙箱验收脚本 **9/9**；UI 深/浅预览 **10 张** + 图标对照；安装/升级/卸载/迁移 e2e（SandboxTest）**21/21 PASS**；真实 profile/安装目录只读 SHA256 零漂移；正式安装包/通知区右键/静默升级重启/ACL 生效 4 项标 ENV-LIMITED，需真机 smoke（本节为 0.1.1 轮次口径；0.2.0 收口为 **403/403** 用例与 **12 张**预览，见文首与 #7）
5. ✅ 应用内更新（本轮新增）：GitHub Releases 检查（24h 节流）/ 下载（`.part` 原子）/ 静默升级 / 升级后清理；设置页更新卡片、托盘菜单「检查应用更新」、启动自动检查通知、CLI `update`；离线自测钩子与 32 个单元测试
6. ✅ **0.1.1 修复**（已发布）：设置窗口打不开 ——「应用更新」卡片的 `ProgressBar.Value` 默认 TwoWay 绑定只读属性 `UpdateProgress`，WPF 在窗口 `Show()` 前抛 `InvalidOperationException` 并被 dispatcher 吞掉。改为 `Mode=OneWay`；同时让界面创建失败时写日志并弹提示、`ui render` 在 `finally` 无条件退出、`TryLogException` 复用服务 logger（修掉两个 FileLogger 互相覆盖日志的缺陷），并新增 `settings-update-*` 预览防回归。详见 [ui.md §10](docs/ui.md)
7. ✅ 二期（0.2.0 已发布；自动升级与包级恢复默认关闭、标实验性）：
   - 策略 A 自动升级：`UpgradePlanner` 纯函数决策（前置闸门 → 候选筛选 → 稳定版优先排序，绝不自动授予风险豁免）+ `UpgradeService`（写前备份 `restore-backup\<opId>\` → `plugin add <pkg>@<ver> --save-exact` → 回读校验 → 失败只回滚本次）；自动升级只在 `autoUpgradeEnabled=true`、策略 A、DSH 完全退出时由 Tick 串行执行（每次最多 3 个）
   - 包级恢复（实验性）：`recovery plan|apply` 写前备份两个核心文件 → 写回 `package.json`/`pnpm-lock.yaml` → `install --offline --frozen-lockfile` → 校验；`packageRestoreEnabled` 默认关，需开关 + DSH 完全退出，默认不联网
   - 离线完整回滚：`recovery rollback <opId>` 按 `restore-backup\<opId>\package-restore.json` 只回滚该 op（恢复文件 + 离线重装 + 再次校验）；回滚点保留 `keepRollbackBackups` 份（默认 5）
   - P1 修复：跨进程操作门在整个操作生命周期内真正持有（命名互斥量交给专属所有者线程）；`upgrade` / `recovery apply|rollback`（非 dry-run）直连前非阻塞探测跨进程门，拿不到返回退出码 4（不再与托盘静默并发）
   - P2 修复：日志跨进程加锁、config/state 迁移前 `.bak-<时间戳>` 备份、后台字段写回走锁内 `MutateConfig`、Tick 看门狗与 doctor `tick` 检查、非交互会话不弹模态框、通知按最近 20 个 eventId 去重、托盘数据根握手 `tray-root.json`、应用内更新下载后校验 `SHA256SUMS.txt`、CLI 参数错误退出码 1
   - 收口轮次（独立复审 **P1=0**）：跨进程写操作门改为“数据根派生命名互斥量 + 数据根 `loom.operation.lock` 文件锁”（跨会话有效，不同数据根不互相阻塞）；应用内更新写旁车并在 `--apply` 前校验 SHA256/大小/内嵌版本（跨进程同样生效）；Tick 卡死 5 分钟自动取消并安全复位（等旧 Tick 退出 ≤5s，超时记 abandoned）；`ui render` 12 张（新增 `settings-compat-*` 整页）且跳过清理/自动检查更新/队列与定时器；dry-run 与 `recovery plan` 视为只读放行
8. ⏳ 仍未做：代码签名；跨进程 `config.json` 写竞态（AtomicFile 原子写但无跨进程互斥）；复审剩余 P3（队列超时口径不一致、写操作门覆盖范围、日志锁仍 `Local\` 且等锁可阻塞调用线程、握手 pid 复用等，见 [验收报告 §14.7](docs/验收报告.md)）

## 发布包

### 历史发布：0.1.1（GitHub Release）

| 产物 | 大小 | SHA256 |
|---|---|---|
| `dist\loom-dsh.exe` | 173,643,760 B | `c35405524e18c8476a43a16e2def1052741a1c5320380917c349ecd2a7df2f9c` |
| `dist\loom-dsh-Setup.exe` | 51,862,695 B | `caa1b921f3abbe3d815d6bbf3b042fd39cf33872f0b4ada4c0803c62ed73e7aa` |

### 已发布：0.2.0（GitHub Release）

本次发布资产（已上传到 GitHub Release）：

| 产物 | 大小 | SHA256 |
|---|---|---|
| `dist\loom-dsh.exe` | 173,921,776 B | `1f1fccfa843d51933506a2de3c449ce11383e08d363c66117e923359454908e5` |
| `dist\loom-dsh-Setup.exe` | 51,945,784 B | `a9fa36cd73e0196ac024ac58c63c30cfce7764aa4ba56073cbb49a3e1fb6d9e7` |

- `dist\SHA256SUMS.txt`（两行）、`dist\发布说明.txt`（使用/安装/数据目录/迁移/范围说明）
- 构建/发布脚本：`scripts\build.ps1`、`scripts\test.ps1`、`scripts\publish.ps1`、`scripts\pack-installer.ps1`；验收脚本：`scripts\acceptance-sandbox.ps1`

> Release：<https://github.com/Three-Freezen/loom-dsh/releases/tag/v0.2.0>（tag `v0.2.0`）；正文见 [docs/发布说明-v0.2.0.md](docs/发布说明-v0.2.0.md)。

## 已知限制

- 安装包/exe 未签名，SmartScreen 可能提示；代码签名二期。
- 自动迁移只复制不删除旧目录（需手动 `migrate-data --remove-source` 清理 C 盘）。
- 正式安装包的卸载注册项/开始菜单项在受限沙箱无法验证（用 `-SandboxTest` 变体验证同一套安装/升级/卸载机制）；真机需人工安装确认一次。
- 托盘菜单/弹层的前台激活与高 DPI/混合 DPI 双屏行为需真机 smoke（沙箱不能注册真实通知区图标）；未激活时 Esc/方向键不可用属 Windows 限制（已由鼠标捕获兜底保证点击外部可关闭）。
- 数据目录 ACL 收紧（当前用户+SYSTEM）在受限令牌环境可能回退为继承权限（真机复核项）。
- 发布前真机 smoke 清单（沙箱 ENV-LIMITED）：① 正式安装包安装/升级/卸载（卸载注册项、开始菜单、中文向导、图标）；② 通知区右键→打开状态面板→弹层可交互、点外部/Esc 关闭；③ 静默升级 `[Run]` 自动重启应用；④ ACL 实际生效（`icacls <安装目录>\Data`）。
- 应用内更新需要能访问 GitHub；代理/限流/断网时只提示“检查更新失败”，不影响插件守护功能。
- 安装包与 exe 未签名，应用内静默升级同样可能触发 SmartScreen 提示；代码签名二期。
- 策略 A 自动升级与包级恢复**默认关闭、标实验性**：需在「设置 → 更新与兼容」显式打开；自动升级还要求策略 A、DSH 完全退出且 profile 未被插件管理器占用，否则只提示/跳过（绝不结束 DSH 进程）。
- 包级恢复要求 **DSH 完全退出**（含托盘/后台进程）；App 在跑时直接失败并给人工入口。当前只走离线 `install --offline --frozen-lockfile`；设置里的「允许联网回退」为预留开关，暂无执行路径会真正发起联网安装。
- 自动升级失败时只按本次 `restore-backup\<opId>\` 回滚 profile 文件，不额外修复 `node_modules`（可用 `recovery apply` 或人工 `dsh plugin install --offline --frozen-lockfile`）。
- 托盘正在执行其他操作时，`upgrade` / `recovery apply|rollback` 会返回退出码 4（忙）：长操作直连但必须持有跨进程门，不与托盘静默并发。
- 跨进程 `config.json` 写竞态仍未协调：托盘与 CLI 各自原子写，极端并发下后写者会覆盖先写者（快照/回滚点/state 不受影响）。
- 日志互斥量仍为 `Local\`（同一登录会话）：跨会话只靠“打开即定位 EOF”兜底，且等锁上限 5 秒会阻塞调用线程（UI 线程遇到另一进程持锁时可能有秒级卡顿）。
- 应用内更新下载后校验 `SHA256SUMS.txt`；清单取不到时标记“未校验”但仍允许继续（离线/网关场景），此时请自行核对来源。`update --apply` 另用下载旁车校验文件未被替换（SHA256+大小+内嵌版本），但旁车只证明文件未改动，不能证明构建者可信。

## Git 与发布

- 仓库：<https://github.com/Three-Freezen/loom-dsh>（公开，**MIT**）；`main` 分支。
- 公开仓库从**单个初始提交**开始（`Initial public release v0.1.0-beta`），不携带本地开发历史与 QA 证据。
- `dist/`、`bin/`、`obj/`、`.qa-accept/`、`spike/`、`.tmp-*/` 不入库；QA 证据只保留在本地。
- 提交作者使用 GitHub noreply 邮箱，仓库不含个人邮箱 / 本机用户名。
- 发布新版：`scripts\publish.ps1` → tag `v<版本>` → GitHub Release 上传 `dist\loom-dsh-Setup.exe`
  （固定资产名，应用内更新依赖）与 `dist\SHA256SUMS.txt`。



