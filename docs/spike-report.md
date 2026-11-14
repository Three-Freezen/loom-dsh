# Loom for DSH · 1～2 天 spike 结果（2026-11-14）

> 目的：验证实现方案 v2 中"需 spike 确认"的关键假设（方案 18.2），回填实现参数。
> 环境：Windows 10/11 x64，.NET SDK 10.0.300，DSH 桌面版 0.2.2-rc.1（Electron 44.7.0，nightly）。
> 结论：**全部关键假设通过，可以按范围 A 实现**；两项限制（更新时序无法主动复现、Toast 未做）记录在文末。

## 1. .NET 10 WPF 自包含单文件发布

| 项 | 结果 |
|---|---|
| SDK | 10.0.300（`C:\Program Files\dotnet`），WindowsDesktop 运行时 10.0.8 |
| 离线 restore | NuGet 源 `nuget.azure.cn` 不可达；所需包（runtime pack 10.0.8 / host pack 10.0.8 / xunit）都在本机缓存与 SDK packs 中，**可离线构建** |
| 发布命令 | `dotnet restore -r win-x64 /p:SelfContained=true` + `dotnet publish -c Release -r win-x64 --self-contained true --no-restore /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true` |
| 产物 | 单文件 **约 139 MB**（scratch WPF），符合方案预估 100–150 MB；启动正常（WPF 窗口） |
| 结论 | 主实现可行；发布脚本见 `scripts/publish.ps1`。WPF 不支持 NativeAOT/trimming，保持默认裁剪策略。 |

说明：`dotnet publish` 首次直接 restore 会因不可达源报 NU1301；先 `dotnet restore -r win-x64 /p:SelfContained=true` 再 `publish --no-restore` 可稳定离线。CI/用户构建机需有网或预热 NuGet 缓存。

## 2. Electron Node 读取 app.asar 与官方 evaluator

`D:\DeepSeek\resources\app.asar` 是**单文件 asar 归档**（约 213 MB），C# `File`/`Directory` 不能读取；Electron Node（`ELECTRON_RUN_AS_NODE=1` + `--expose-internals`）的 fs **可以**读取。

实测脚本（`spike/probe.mjs`）：

```powershell
$env:ELECTRON_RUN_AS_NODE=1
& 'D:\DeepSeek\DeepSeek Harness.exe' --expose-internals 'D:\BS\Loom-DSH\spike\probe.mjs'
```

结果：

| 项 | 结果 |
|---|---|
| `import('file:///D:/DeepSeek/resources/app.asar/dsh/node_modules/@deepseek-ai/dsh-app-boot/lib/index.js')` | ✅ 成功 |
| 导出 | `evaluatePluginCompatibility`、`getDshRuntimeVersion`、`readProfilePlugins`、`sanitizeProfile`、`bundlePatchPaths` 等 |
| `getDshRuntimeVersion()` | `0.2.2-rc.1` |
| `evaluatePluginCompatibility(manifest, {}, runtime)` | **兼容返回 `undefined`**；不兼容返回 `{ name, version, runtimeVersion, requirements, exempted }` |
| 三个真实插件 | `dshmarket` / `dsh-plugin-wallpaper-engine` / `dsh-whale-widget` 在 0.2.2-rc.1 下均未返回 issue（兼容） |

→ helper.mjs 必须按"undefined = compatible"解析；engine 选择 official → fallback → unknown 三级方案保持。

## 3. DSH CLI 入口

| 命令 | 结果 |
|---|---|
| `resources\runtime\cli\bin\dsh.cmd --help` | ✅ 可运行；`dsh [--profile] <name> ...` |
| `dsh.cmd plugin --profile desktop --help` | ✅ 显示"转发 pnpm"；`--profile` 在 `plugin` 之后 |
| 普通 `dsh` CLI | 有 `rejectElectronProfile`，不能直接 `--profile desktop`（必须走安装目录下 dsh.cmd） |
| `install --offline --frozen-lockfile` | 本 spike 未做真实安装（范围 A 不做包级还原，P2 再验证） |

→ DshCli 固定 `<installDir>\resources\runtime\cli\bin\dsh.cmd`、参数数组直传；范围 A 不调用安装命令。

## 4. 更新器缓存与时序信号（只读）

本机 `%LOCALAPPDATA%\@deepseek-aidsh-desktop-updater\`：

```
pending\update-info.json                         {"fileName":"deepseek-harness-0.2.2-rc.1-win-x64.exe","sha512":"...","isAdminRightsRequired":false}
pending\deepseek-harness-0.2.2-rc.1-win-x64.exe   293 MB（11-07 残留）
pending\current.blockmap / installer.exe
```

- 版本比较：残留包目标版本 **等于**当前 appVersion 0.2.2-rc.1 → 必须被"≤ 当前版本"规则忽略（验收 A04）。
- `temp-*` 出现在下载期，最终 rename + `update-info.json`；两者都可作为触发信号（v2 8.2 已按此设计）。
- **无法主动复现一次真实 nightly 更新**（会实际升级用户环境）；状态机按 v2 8.1 的三次启动定义实现，真实时序留待用户下次更新时用日志校准。

## 5. 本机 DSH 数据与锁

| 项 | 值 |
|---|---|
| DSH_HOME | `C:\Users\<user>\.dsh`（环境变量已设置） |
| profile | `profiles\desktop`；package.json bundles：base、web-app、agent-team、dshmarket、wallpaper-engine、whale-widget |
| dependencies | `dsh-plugin-wallpaper-engine` 1.4.2（精确）、`dsh-whale-widget` ^0.4.2、`dshmarket` ^1.73.19 |
| 锁文件 | 当前 profile 无 `lock`/`package.json.lock`；`.plugin-manager\` 仅 `logs`（无 run.json） |
| 更新器缓存 | 约 586 MB（只读展示，不清理、不复制进快照） |

## 6. 其他落地参数

- 应用版本文件：`resources\runtime\primary-runtime\runtime.json` → `desktopVersion`（已实测）。
- 工具数据：`%LOCALAPPDATA%\Loom`（本会话沙箱不可写，开发/测试用 `--data-root` 指向工作区；真实用户环境正常）。
- 单文件产物约 139 MB → 发布说明需给出 SHA256；代码签名/SmartScreen 留二期。
- Toast：首版仍用 `NotifyIcon.ShowBalloonTip`（未签名 exe 最可靠），AUMID/Toast 留二期。

## 7. 未完成 / 后续

1. 包级还原（`install --offline --frozen-lockfile`）与策略 A 完整链路 → 二期。
2. 真实 nightly 更新时序抓取（用户下次更新时用 `journal` 日志校准）。
3. 30 条验收（A01–A30）在 QA 阶段逐条跑；A12/A14 需破坏性演练（沙箱副本）。
4. .NET 10 自包含单文件在目标用户机（无 SDK）上的启动/杀软误报需真机抽样。
