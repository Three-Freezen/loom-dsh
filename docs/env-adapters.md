# 环境适配层实现说明（env-adapters 交付文档）

> 工作流：env-adapters · 范围：`Loom.Core/Environment/*`、`Loom.Core/Evaluation/*`、`Loom/Assets/helper.mjs`、
> `Loom.Tests/Environment/*`。数据事实基于本机实测（2026-11-14，DSH `0.2.2-rc.1`，安装目录 `D:\DeepSeek`，
> profile `C:\Users\<user>\.dsh\profiles\desktop`）。**所有真实环境访问均为只读**。

## 0. 交付清单

| 文件 | 内容 |
|---|---|
| `Loom.Core/Environment/InstallerLocator.cs` | 安装定位、三件套校验、版本读取、profile 解析、更新器根目录 |
| `Loom.Core/Environment/ProcessProbe.cs` | 只读进程发现 / 存活判断（不 kill） |
| `Loom.Core/Environment/LockManager.cs` | 三套锁检测、静默等待、`package.json.lock` 获取/释放 |
| `Loom.Core/Environment/DshCli.cs` | `dsh.cmd` 封装（参数数组、cwd、超时、流式日志、环境清洗、Electron 回退） |
| `Loom.Core/Environment/UpdaterMonitor.cs` | 更新器缓存只读探测（download / ready、版本过滤、sha512） |
| `Loom.Core/Environment/AutostartService.cs` | HKCU Run 自启（引号、幂等、异常吞掉并日志） |
| `Loom.Core/Environment/DoctorService.cs` | 环境体检（英文 check key + 中文 detail） |
| `Loom.Core/Evaluation/ElectronHelper.cs` | Electron Node helper 子进程桥（stdin JSON / stdout JSON / 超时 / 不抛异常） |
| `Loom.Core/Evaluation/ElectronEvaluator.cs` | `ICompatibilityEvaluator` 实现（official/fallback/unknown + 静态预检 → `CompatibilityBatchResult`） |
| `Loom/Assets/helper.mjs` | 自包含 ESM helper：官方 evaluator 优先 + vendored semver fallback + 静态预检 |
| `Loom.Tests/Environment/*` | 9 个测试类 / 35 个用例（含真实 app.asar 集成对拍） |

## 1. 安装定位器（InstallerLocator）

### 1.1 候选顺序（第一个通过校验者胜出）

| 顺序 | 来源 | `DshInstallation.Source` |
|---|---|---|
| 1 | `config.InstallDir`（`LoomOptions.InstallDir` / `--install-dir` / config.json） | `config.installDir` |
| 2 | 运行中 `DeepSeek Harness` 进程的 `MainModule.FileName` 所在目录 | `process` |
| 3 | `HKCU`/`HKLM` 的 `...\Uninstall`（64/32 视图）：`DisplayName`/`InstallLocation` 含 deepseek | `registry:{hive}/{view}` |
| 4 | `resources\app-update.yml` 目录推断：标准根（`%LOCALAPPDATA%`、`%LOCALAPPDATA%\Programs`、`Program Files`、`Program Files (x86)`）+ 各固定磁盘根，扫描一层子目录 | `app-update.yml` |

注册表项优先用 `InstallLocation`；缺失时从 `UninstallString`/`QuietUninstallString`/`DisplayIcon` 的引号路径推导。
本机命中：`HKCU Uninstall\1bf39983-...` → `InstallLocation=D:\DeepSeek`。

### 1.2 校验（三者 + Electron 存在才接受）

- `resources\runtime\primary-runtime\runtime.json`
- `resources\runtime\cli\bin\dsh.cmd`
- `resources\app.asar`（**单文件 asar，C# `File` 不能读内容，只校验存在**；打包内文件由 helper 的 Electron fs 读取）
- `DeepSeek Harness.exe`（helper/CLI 回退需要）

任何一项缺失 → 该候选被丢弃并 `Debug` 日志；全部失败 → 返回 `null`（LoomService 进入“未找到 DSH”，不会循环报错）。
`Invalidate()` 为空实现：**定位器本身不缓存**，10 秒缓存由 Lead 的 `LoomService.ResolveInstallationAsync` 负责。

### 1.3 版本

- **appVersion**：`runtime.json` 的 `desktopVersion`（本机 `0.2.2-rc.1`）；读不到时用 helper 读 `app.asar/package.json` 的 `version`；再失败 `"unknown"`。
- **dshVersion**：helper `probe` 模式调用官方 `getDshRuntimeVersion()`（读取 `app.asar\dsh\node_modules\@deepseek-ai\dsh-app-boot\package.json` 的 `version`）。
  helper 缺失/失败/超时 → `"unknown"`，**不猜测**（`SemVersion.TryParse` 校验后才采用）。
- **BaseBundles**：helper 从官方 `@deepseek-ai/dsh-official-bundles` 的 `PROFILE_TEMPLATES.web.bundles` 取
  `["@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app"]`；取不到则保留 `DshDefaults.BaselineBundles`。
- **UpdaterRoot**：`%LOCALAPPDATA%\@deepseek-aidsh-desktop-updater`；若安装目录 `app-update.yml` 有 `updaterCacheDirName` 则用其值。

### 1.4 profile 解析优先级

`config.ProfileDir` > `DSH_HOME` 环境变量（`config.DshHome` 覆盖仍优先）> `%USERPROFILE%\.dsh\profiles\desktop`。
`DshHome` 同样记录到安装对象（供 CLI `--profile` 与诊断使用）。`DshInstallation.Found` = 安装目录非空 **且** profile 目录存在。

## 2. 进程探测（ProcessProbe）

- 枚举全部进程，名称等于或以 `DeepSeek Harness` 开头（.NET `ProcessName`，如 `DeepSeek Harness`）都纳入；按 PID 去重。
- 可读 `MainModule.FileName` 且 `installDir` 非空时：**路径目录必须等于 installDir**，否则视为“其它安装”排除。
- 路径不可读（权限/跨用户/提权进程）时**按名称保守计入**（`ExecutablePath = null`），由调用方提示。
- `IsProcessAlive(pid)`：`Process.GetProcessById` + `HasExited`，全异常吞掉返回 false；**从不 kill**。
- 已知限制：helper 子进程（`ELECTRON_RUN_AS_NODE`）的进程名同样是 `DeepSeek Harness`，评估器运行瞬间可能被计为
  “App 运行中”（约 200–300ms）。LoomService 的 `TickAsync` 先探测后评估，实际影响极小；需要严格判定时可加命令行过滤。

## 3. 锁矩阵（LockManager）

均只读检测；Loom 只删除**自己创建**的 `package.json.lock`，绝不删别人的锁、绝不 kill 进程。格式来自 app.asar 内源码实测。

| 文件 | 实测格式 | 检测规则 | Loom 动作 |
|---|---|---|---|
| `<profile>\lock` | PID 文本（桌面端 profile 操作） | 内容可解析且存活 → Busy；已退出 → Detail 记残留；不可解析 → Busy（保守） | 只读等待，超时转“稍后重试” |
| `<profile>\package.json.lock` | `<pid>\n`（`@deepseek-ai/dsh-atomic-write` 的 `withFileLock` 用 `wx` 创建，mode 0600） | 同上；存活且文件 mtime > **120s** 时在 Detail 注明“超过 DSH CLI 120s 等待窗口”（仍 Busy，pnpm 长安装可能合法） | Loom 自己写 manifest 时持有；**调用 `dsh.cmd` 前必须释放** |
| `<profile>\.plugin-manager\run.json` | `{"pid":n,"grouped":bool}\n`（写自 `dsh-plugin-manager` 的 `recordRun`，成功后删除） | `pid` 存活 → Busy；已退出 → Detail 记残留（DSH 下次自动清理）；JSON/PID 无效 → Busy（保守，需人工） | snapshot/restore 前检查 |

源码依据（app.asar 内，DSH 0.2.2-rc.1）：

- `dsh-atomic-write/lib/index.js`：`withFileLock(filename)` 的锁文件是 `<filename>.lock`，内容 `${process.pid}\n`，
  失败重试 20→200ms；死 PID 通过 takeover-claim 接管。CLI/插件管理器传 `lockWaitMs: 12e4` → 120s。
- `dsh-plugin-manager/lib/types/operations.js`：`runPluginCommand` 用 `withFileLock(join(dir,'package.json'),…,{waitMs:120000})`；
  `recordRun` 写 `.plugin-manager/run.json`，`activeRecordedRun` 用 `awaitTreeGone` 等待进程树。
- `<profile>\lock` 的 PID 语义来自审核报告 1.5 第 7 条与方案 7.1（桌面端 `applyRelease`/`disableAllPlugins`）。

### 3.1 等待与获取

- `WaitForQuietAsync`：20ms 起指数退避（上限 2s），直到三锁均不 Busy 或超时；返回 `LockWaitResult{Quiet,Status,Waited}`。
- `AcquireManifestLockAsync`：`FileMode.CreateNew` + 写 `<pid>\n` + `Flush(flushToDisk)`；`FileShare.ReadWrite`
  （让 DSH/其它 .NET 读者能读 holder PID）。遇到“死 holder”按 DSH 语义删除后重试；活 holder 等待到超时返回 `null`。
  **禁止嵌套**：拿到锁后不能调用 `dsh.cmd`（CLI 会再取同一把锁 → 120s 死锁），调用方必须先 Dispose。
- 读取锁记录统一用 `FileShare.ReadWrite` 打开（否则持锁者 `FileShare.Read` 之外的写句柄会让 `File.ReadAllText` 共享冲突）。

## 4. DSH CLI 封装（DshCli）

- 固定 `<installDir>\resources\runtime\cli\bin\dsh.cmd`；`RunPluginAsync(installation,args,…)` 实际执行
  `dsh.cmd plugin --profile <profileName> <args...>`（profileName 取 profile 目录名，默认 `desktop`）。
- `ArgumentList` 直传（**不拼 shell 字符串**）；`.cmd` 无法被 CreateProcess 直接启动，故经 `cmd.exe /d /s /c`，
  执行行形如：`cmd.exe /d /s /c ""D:\...\dsh.cmd" "plugin" "--profile" "desktop" "add" "pkg@1.2.3""`。
  参数含空格/cmd 元字符才加引号；`"`、换行、`%` 被移除（cmd 无法安全转义，实际调用参数均为包名/精确版本/flag）。
- cwd = 安装目录；超时（默认调用方传 120s；安装类 900s 由上层决定）；`TimedOut=true` 时 `Kill(entireProcessTree)`。
- stdout/stderr 逐行流式写 `ILogger`（logger 已脱敏），同时累积到 `CliCallResult.StdOut/StdErr`；`CommandLine` 经 `Redactor` 脱敏后记录。
- 环境变量清洗（对齐 DSH `dsh-subprocess` 的 `scrubbedParentEnv()` 实测源码）：
  - `SENSITIVE_ENV_PATTERN = /KEY|PASSWORD|SECRET|TOKEN|AUTH/i`（本实现含 `AUTH`，比 DSH 原正则更严）；
  - 移除全部 `DSH_*`（大小写不敏感）；
  - 保留 `PATH`/`SystemRoot`/locale/proxy 等；
  - 显式加回 `ELECTRON_RUN_AS_NODE=1`、`DSH_DESKTOP_NODE_EXECUTABLE=<electron>`。
- 仅当 `dsh.cmd` **缺失**时回退：`DeepSeek Harness.exe --expose-internals <app.asar>\dsh\node_modules\@deepseek-ai\dsh-desktop-host\lib\cli.js ...`。
- 本版只封装，不主动执行安装/升级。

## 5. 更新器缓存（UpdaterMonitor，只读）

本机缓存实测：

```
%LOCALAPPDATA%\@deepseek-aidsh-desktop-updater\
  pending\temp-<fileName>          # 下载开始（最早提前量）
  pending\<fileName>               # 下载完成
  pending\update-info.json         # {"fileName":"deepseek-harness-0.2.2-rc.1-win-x64.exe","sha512":"...","isAdminRightsRequired":false}
  installer.exe / current.blockmap # electron-updater 工作副本（不参与判定）
```

- `stage=download`：`pending\temp-*`（FileName 去掉 `temp-` 前缀），`Sha512=null`。
- `stage=ready`：`update-info.json` 的 `fileName`/`sha512`，并校验最终安装包存在。
- 规则：目标版本 **必须 > 当前 appVersion**（本机残留的 `0.2.2-rc.1` ≤ 当前版本 → 忽略，返回 null）；
  同版本 ready 优先于 download；多版本取最高；`currentAppVersion` 非法时不做过期过滤（交给上层再判断）。
- 文件名版本提取：DSH 文件名 `<product>-<version>-<platform>-<arch>.exe`。**注意**：`SemVersion.TryParseFromFileName`
  （Lead 的基础设施实现）会贪婪匹配 prerelease，把 `-win-x64.exe` 吃进 prerelease → `0.2.3-win-x64.exe`。
  本实现先用本地正则去掉 `-(win|windows|mac|darwin|linux|universal)[-._]…` 后缀再 `SemVersion.TryParse`，
  已验证 `0.2.2-rc.1/0.2.3/0.2.4/0.2.5` 正确。建议 Lead 修复 `SemVersion.TryParseFromFileName`（Infrastructure 测试已暴露 2 例）。

## 6. 自启（AutostartService）

- 唯一机制：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值名 `Loom for DSH`，命令 `"<exe 全路径>" --tray`。
- 不做计划任务（避免审核报告 P2-12 的双启动问题）。
- 幂等：命令相同不重写；关闭时值不存在不报错；注册表异常一律 `Warn` 日志后吞掉（不阻塞配置保存/启动）。
- `BuildCommand()` / `NormalizeCommand()` 公开为纯函数并被单测覆盖（含“路径带空格但未加引号”的修复：以 `.exe` 为界拆分）。

## 7. 环境体检（DoctorService）

`RunAsync(installation, paths, config)`，check key 英文稳定、detail 中文：

| Name | 说明 |
|---|---|
| `installation` / `profile` | 安装目录、profile 目录存在性/路径 |
| `runtime-manifest` / `dsh-cli` / `electron` / `app-asar` | 定位三件套 + Electron 具体路径或“缺失：…” |
| `profile-locks` | `LockManager.Inspect` 结果（占用原因或“无锁占用”） |
| `helper` | `paths.HelperPath` 存在且非空（缺失→评估器只能 unknown） |
| `updater-cache` | 只读目录大小（MB），>1GB 时在 Summary 提示（不删除） |
| `data-root-writable` | 在 dataRoot 建/删探针文件 |
| `disk-space` | 剩余空间，<512MB 判失败 |
| `autostart` | 是否已启用 + 注册命令 |
| `version-consistency` | appVersion/dshVersion 均可解析才算通过（dshVersion unknown → 失败，提示 helper） |
| `config` | 当前 installDir/policy/keepSnapshots/theme（信息项） |

`Healthy = 所有 check.Ok`；LoomService 追加 `data-root`/`unfinished-journal`/`last-snapshot`/`autostart` 后会再算一次总健康。

## 8. Electron helper 桥（ElectronEvaluator + helper.mjs）

### 8.1 启动方式

```
ELECTRON_RUN_AS_NODE=1 "<installDir>\DeepSeek Harness.exe" --expose-internals "<paths.HelperPath>"
```

- helper 由 `HelperBootstrapper`（Lead）解压到 `<数据根>\helper\helper.mjs`（安装版数据根 =
  `<安装目录>\Data`，开发/绿色版 = `%LOCALAPPDATA%\Loom`，见 §11）；Evaluator 只用 `paths.HelperPath`，
  **helper 或 Electron 缺失 → engine=unknown，绝不抛异常**。
- stdin = 单个 JSON；stdout = 单个 JSON（**禁止其它 stdout 输出**）；诊断一律 stderr。
- 30s 超时，超时 `Kill(entireProcessTree)`；崩溃/非 0 退出/JSON 解析失败 → 返回 null → 上层 unknown。
- 实测单次调用 **约 200–280ms**（含 Electron Node 启动 + app.asar 内 ESM 导入）。

### 8.2 协议（schemaVersion 1）

输入（C# `HelperRequest`）：

```json
{
  "schemaVersion": 1,
  "mode": "evaluate",                  // "evaluate" | "probe"
  "appRoot": "D:/DeepSeek/resources/app.asar",
  "profileDir": "C:/Users/<user>/.dsh/profiles/desktop",
  "runtimeVersion": "0.2.2-rc.1",      // 可空；空则官方 getDshRuntimeVersion() / package.json 读取
  "exemptions": { "pkg@1.2.3": ["0.2.2-rc.1"] },
  "plugins": [{ "name": "...", "version": "...", "dir": "...", "manifestJson": "{...}" }],
  "forceFallback": false               // 测试钩子：跳过官方模块，强制 fallback
}
```

输出（C# `HelperResponse`）：

```json
{
  "schemaVersion": 1,
  "mode": "evaluate",
  "engine": "official",                // official | fallback | unknown
  "engineDetail": "official @deepseek-ai/dsh-app-boot (...)",
  "runtimeVersion": "0.2.2-rc.1",
  "appVersion": "0.2.2-rc.1",
  "baseBundles": ["@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app"],
  "results": [{
    "name": "...", "version": "...",
    "level": "compatible",             // compatible | incompatible | unverified | unknown
    "compatible": true,
    "exempted": false,
    "requirements": ["engines.dsh: ^0.2.0-rc.1"],
    "static": {
      "result": "ok",                  // ok | unverified | missing | notApplicable
      "patchFiles": ["./cordis.patch.yml"],
      "clientInject": ["@deepseek-ai/dsh-client-ui-chat"],
      "missingRefs": [],
      "notes": []
    },
    "error": null
  }],
  "error": null
}
```

`probe` 模式只返回 engine/runtimeVersion/appVersion/baseBundles（供 InstallerLocator/Doctor 使用），`results=[]`。

### 8.3 engine 选择

1. **official**：`import(app.asar\dsh\node_modules\@deepseek-ai\dsh-app-boot\lib\index.js)` 成功且导出
   `getDshRuntimeVersion`/`evaluatePluginCompatibility`；调用官方函数，`engineDetail` 记录模块路径。
2. **fallback**：官方导入失败（asar 结构变化、Electron 不能读 asar 等）时用 helper 内置 **vendored semver** 复刻官方规则：
   - `engines.dsh` 存在 → 只用它；否则检查全部 `@deepseek-ai/dsh` 与 `@deepseek-ai/dsh-*` peer；
   - `peerDependenciesMeta` **不豁免**（optional peer 仍检查）；其它 peer（cordis/schemastery/react…）忽略；
   - `includePrerelease: true`；peer 的 `workspace:^/~/*` 视为当前 runtime；`engines.dsh` 的 workspace 形式 → 不满足；
   - **非法 range 返回 false（即 incompatible），不是 unknown**——与官方 `semver.satisfies()` 一致（其内部 catch 解析错误后返回 false）；
   - 运行时版本非法 / engines/peer 字段类型非法 / 不满足且缺 name/version → 抛错 → 该插件 **unknown**。
3. **unknown**：helper 本身不可用、输入非法、运行时版本无法确定、逐插件抛错。
   `unknown` 绝不等于 compatible（LoomService/CheckService 需要按“需人工”处理）。

静态预检（两种 engine 共用，只读）：

- `dsh.bundle.patch`：声明可为字符串或数组；文件存在、`js-yaml`（`createRequire(appRoot/dsh/package.json)('js-yaml')`）解析为数组；
  递归遍历 `insert` 列表的 `name`，忽略 `cordis:*` 与相对/绝对路径，逐个在 `appRoot dsh`、`appRoot`、profile、插件目录锚点解析；
  不可解析 → `missing`。
- `dsh.client.inject`：每个包名在**安装目录运行时 node_modules**（appRoot 锚点）解析；不可解析 → `unverified`（设计 10.3）。
- `dsh.bundle.platforms`：缺省允许所有平台；非空字符串列表且（win32 时）必须包含 `win32`；否则 `missing`。
- 无 `dsh.bundle` 且无 `dsh.client` → `notApplicable`。
- 合成规则：manifest 不兼容 → `incompatible`；静态 `missing` → `incompatible`；静态 `unverified` → `unverified`；
  其余 `compatible`。豁免命中（exempted）→ `compatible` 且 `Exempted=true`、保留 requirements。

### 8.4 真实环境实测结论（2026-11-14，本机）

probe 输出：

```
engine = official
engineDetail = official @deepseek-ai/dsh-app-boot (D:/DeepSeek/resources/app.asar/dsh/node_modules/@deepseek-ai/dsh-app-boot/lib/index.js)
runtimeVersion = 0.2.2-rc.1
appVersion = 0.2.2-rc.1
baseBundles = @deepseek-ai/dsh-base, @deepseek-ai/dsh-web-app
```

三个真实插件（official 与 fallback 结果一致）：

| 插件 | 版本 | level | static | 说明 |
|---|---|---|---|---|
| dshmarket | 1.73.19 | compatible | ok | patch: 插入 `dshmarket` 可解析；client.inject 3 个官方包均可解析 |
| dsh-plugin-wallpaper-engine | 1.4.2 | **unverified** | unverified | `dsh.client.inject` 的 `@deepseek-ai/dsh-client-runtime` **不在安装目录运行时 node_modules**（本机实测不存在），只能“未验证” |
| dsh-whale-widget | 0.4.2 | compatible | ok | 无 engines/peer，patch 插入自身可解析 |

Fallback 对拍：三个真实插件 + 24 个 fixtures（engines 缺失/prerelease/旧版本/optional peer/workspace/非法 range/非法字段/缺 name/
其它 peer/豁免/patch 缺失/平台/patch 插入不可解析/client 不可解析…）在 official 与 fallback 下 **0 差异**
（level / static.result / requirements / exempted 全一致）。见
`Loom.Tests/Environment/ElectronEvaluatorTests.OfficialAndFallbackEnginesAgreeOnFixturesAndRealPlugins`。

### 8.5 与设计/任务书的偏差（实测修正，需 Lead 知悉）

1. 任务书 8/设计 10.2 写“非法 range → unknown”。**实测 0.2.2-rc.1 官方 `semver.satisfies()` 对非法 range 返回 false**
   （`new Range()` 才抛错），因此官方结果是不满足（incompatible），fallback 与之一致。仍为 unknown 的情形：
   运行时版本非法、engines/peer **字段类型**非法、不满足且缺 name/version。
2. `engines.dsh: "workspace:*"` → 官方 `satisfies` 返回 false → incompatible（设计 10.2 的“视为不满足”得到印证）。
3. peer `workspace:^/~/*` → 官方换成当前 runtime 后判断 → 满足。
4. `dsh.bundle.platforms` 是独立检查（官方 evaluator 不覆盖），只能由静态预检给出；平台不含 win32 → incompatible。
5. wallpaper-engine 在 0.2.2-rc.1 上是 **unverified 而非 compatible**：其 client 注入依赖未随安装目录发布，
   纯 manifest 判定会漏掉（审核报告 P1-3 的实例证据）。

## 9. 测试

`Loom.Tests/Environment/`（命名空间 `Loom.Tests.Env`，避免遮蔽 `System.Environment`）：

| 文件 | 覆盖 |
|---|---|
| `EnvTestSupport.cs` | %TEMP% 沙箱、假安装树、RecordingLogger、FakeProcessProbe、RealDsh 只读常量、helper 源定位 |
| `InstallerLocatorTests.cs` | config/process/registry/app-update.yml 四种来源；三件套缺一校验失败；profile 三级优先级；真实安装只读定位 + helper probe 版本 |
| `UpdaterMonitorTests.cs` | 过期 0.2.2-rc.1 忽略、temp-* download、update-info ready+sha512、ready 优先、最高版本、非法当前版本、目录缺失 |
| `LockManagerTests.cs` | 三种锁存活/退出/不可解析；120s 说明；Wait 超时/立即静默；Acquire 创建/释放/超时/接管死锁 |
| `ProcessProbeTests.cs` | 存活/死 PID、外部安装目录过滤、真实进程只读 |
| `DshCliTests.cs` | 参数数组+profile 前缀、环境清洗（secret 不转发、ELECTRON_RUN_AS_NODE=1）、CommandLine 脱敏、超时 kill、CLI 缺失报错 |
| `AutostartServiceTests.cs` | 命令引号/幂等纯函数、注册表只读不抛 |
| `DoctorServiceTests.cs` | 稳定 check key 集合、helper 缺失失败、无安装 unhealthy |
| `ElectronEvaluatorTests.cs` | 真实三插件 official 判定、helper probe、official↔fallback 全量对拍、helper 缺失降级 unknown（真实 app.asar） |

**沙箱运行注意**：本会话 `dotnet test` 的 VSTest testhost 在受限沙箱里无法 OpenProcess 父进程
（`Win32Exception 5`，发生在 `SetParentProcessExitCallback`），与代码无关。替代验证方式：用 xunit
`runner.utility` 直接跑测试程序集（本次交付验证脚本在 `D:\BS\_probe\runner`，未纳入仓库）。
在有正常权限的终端里应使用标准命令：
`cd D:\BS\Loom-DSH\src; dotnet build Loom.slnx; dotnet test Loom.Tests\Loom.Tests.csproj`。
本次实测结果：**env-adapters 35/35 通过**；全套 116 例中 113 通过，剩余 3 例为 Lead 的 Infrastructure
（`Redactor` Bearer 分支、`SemVersion.TryParseFromFileName` 贪婪 prerelease），已在汇报中标注。

## 10. 已知限制 / 集成注意

1. **定位成本**：`LocateAsync` 每次都会跑一次 helper probe（~200–280ms）以拿到 dshVersion；LoomService 每 10s 刷新一次，
   即约 2–3% 时间有一个 Electron Node 子进程。若在意，可把 dshVersion 的刷新与 `LocateAsync` 解耦（Lead 决定）。
2. **helper 不可用 = unknown**：不降级为 compatible；UI/报告按“需人工”处理。`HelperBootstrapper` 保证正常情况下可用。
3. **helper 子进程同样叫 `DeepSeek Harness`**：评估瞬间 `ProcessProbe` 可能误报 App 运行（见 §2 限制）。
4. **锁获取不支持同进程重入**：`AcquireManifestLockAsync` 由调用方保证“拿锁 → 写 → 释放 → 再调 dsh.cmd”的顺序，
   禁止嵌套；与 DSH `withFileLock` 语义一致。
5. **cmd.exe 引用限制**：`dsh.cmd` 只能经 `cmd.exe /d /s /c` 执行；参数中的 `"`、换行、`%` 会被移除（防注入）。
   当前调用集（包名/精确版本/flag）不受影响；若未来要传任意 npm range（含 `^`/`|`），需改为“启动 cmd 前写响应文件”方案。
6. **app-update.yml 推断**：仅扫标准根 + 固定磁盘根一层子目录，避免全盘遍历；换到非常规盘符/深层目录时依赖 registry 或 config。
7. **更新器 `SemVersion.TryParseFromFileName` bug**：已在 UpdaterMonitor 内绕过；建议 Lead 修 Infrastructure（2 条测试已红）。
8. 未实现（按范围）：不调用安装/升级、不删更新器缓存、不 kill 进程、不写真实 profile/安装目录。

## 11. 数据根定位（installer-data 追加，2026-11-14）

`LoomServiceFactory.Create` 启动最早阶段调用 `DataRootResolver.Resolve()`，解析顺序（第一个命中者胜出）：

| 顺序 | 条件 | 数据根 | `DataRootSource` |
|---|---|---|---|
| 1 | `--data-root <dir>` / `LoomOptions.DataRoot` | 指定目录 | `Explicit` |
| 2 | `<exe目录>\Data\portable.flag` 存在 | `<exe目录>\Data` | `Portable` |
| 3 | `<exe目录>\unins000.exe` 存在（Inno 安装版） | `<安装目录>\Data` | `Installed` |
| 4 | 其余（`dotnet run` / `bin\Debug` / 绿色版） | `%LOCALAPPDATA%\Loom` | `LegacyDefault` |

- **对现有环境适配层无行为改变**：测试、`dotnet run`、`scripts\acceptance-sandbox.ps1`（显式传 `--data-root`）
  仍使用原目录；只有 Inno 安装版（或放了 `portable.flag` 的绿色版）换成 `<exe目录>\Data`。
- 安装版首启会在加载 config/state 之前，把 `%LOCALAPPDATA%\Loom` 旧数据**复制**到 `<安装目录>\Data`
  （幂等、不覆盖、跳过 `queue\`/`*.lock`/`*.tmp-*`，重写 state 里的旧绝对路径，写 `migration.json`；
  失败只记日志不阻塞启动）。手动入口：CLI `migrate-data [--dry-run] [--remove-source] [--json]`。
- `version --json` 新增 `dataRootSource` / `installedBuild` / `legacyDataRoot` 字段，便于排障与验收；
  `data-root-writable` 体检与 helper 解压均自动跟随解析后的 `paths.DataRoot`。
- 测试钩子 `LOOM_LEGACY_DATA_ROOT`（仅测试/验收；生产不设置）可把“旧默认根”重定向到工作区内伪造目录，
  用于沙箱端到端迁移验证。
- 详细设计、升级/卸载与未来应用内更新方案见 `docs/安装与更新.md`。
