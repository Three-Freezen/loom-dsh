# 二期 · 策略 A：自动查询并升级到与目标 dshVersion 兼容的插件版本

> 状态：**二期实验性，0.2.0 起已接线（默认关闭）**。核心逻辑与执行器见下；CLI `loom-dsh upgrade`、
> 设置页「插件自动升级（策略 A）」、`ILoomService.UpgradePluginAsync` 与 Tick 自动执行均已接入
> （`autoUpgradeEnabled=false` 时 Tick 不查询、不安装）；首启健康检查的自动回滚判断仍由上层负责。
> 策略 A 默认只在“有兼容版本”时安装，没有兼容版本时**只给人工提示，绝不自动授予风险豁免**。
>
> 代码位置（本模块自身文件；0.2.0 的公共入口见 §8）：
> - `src/Loom.Core/Upgrade/UpgradeModels.cs`：候选 / 决策 / 审计模型 + 两个可注入端口
> - `src/Loom.Core/Upgrade/UpgradePlanner.cs`：纯函数决策表
> - `src/Loom.Core/Upgrade/UpgradeService.cs`：执行器 + 默认 registry + 命令拼装 + 失败分类
> - `src/Loom.Tests/Upgrade/UpgradePlannerTests.cs`、`UpgradeServiceTests.cs`：决策全分支与执行/回滚/审计
>
> 设计依据：`docs\实现方案-v2.md` §11（决策表、registry、`--save-exact`、major/prerelease、审计、失败与离线）、
> §8.4（upgrading 状态与超时）、§9.4（`restore-backup\<opId>\` + 哈希）、§15（脱敏与安全边界）。

---

## 1. 与 `AutoUpgradePolicy` 的关系

| `config.autoUpgradePolicy` | 行为 |
|---|---|
| `A`（默认） | 允许自动查询候选并自动安装“与目标 dshVersion 兼容”的最高稳定版本；本文件描述的策略。Tick 自动执行另需 `autoUpgradeEnabled=true`（默认 **false**：不查询、不安装；显式 CLI `upgrade` 不受该开关限制）。 |
| `B` / `C` | **不自动安装**。`UpgradePlanner.EvaluateGates` 直接返回 `PolicyNotAuto`，执行器不会查询 registry、不会执行任何命令，只生成人工提示。 |
| 空 / 非法值 | 归一化后为空 → 不自动升级（按 `InvalidInput` 处理），避免默认放开。 |

其它相关配置：

| 配置 | 作用 |
|---|---|
| `allowMajorUpgrade`（默认 false） | 候选的 major 大于已装 major 时拒绝自动安装，提示“可能涉及插件数据格式迁移”。 |
| `operationTimeoutSeconds` | 与本模块无关；执行器使用请求里的 `QueryTimeout`（默认 60s）/ `InstallTimeout`（默认 15min）。 |
| prerelease 允许开关 | 本模块用请求上的 `AllowPrerelease`（+ `PrereleaseJustification`）表达；0.2.0 由 `config.AllowPrereleaseUpgrade`（默认 false）与 CLI `--allow-prerelease` 注入（两者取或）。 |

---

## 2. 输入与决策表

`UpgradePlanner.Plan(UpgradePlannerInput)` 是纯函数：不读取文件、不调 CLI、不联网。
输入 = 包名、已装版本、目标 `dshVersion`、候选列表（每个候选带自己的 evaluator / static 结论）、
`LoomConfig`、是否离线、是否允许 prerelease、候选上限（默认 20）。

### 2.1 前置闸门（`EvaluateGates`，在查询 registry 之前）

| 顺序 | 条件 | 结果 | Kind |
|---|---|---|---|
| 1 | 缺少输入 / 包名 / config | `InvalidInput`，不自动升级 | `ManualOnly` |
| 2 | `Offline = true` | `Offline`，不查询、不安装，返回“无法查询/离线” | `NoAction` |
| 3 | 策略非 `A` | `PolicyNotAuto`，只给人工提示 | `ManualOnly` |
| 4 | 目标 dshVersion 不可解析 | `TargetUnknown` | `ManualOnly` |
| 5 | 已装版本不可解析 | `InstalledVersionUnknown` | `ManualOnly` |

执行器先跑闸门；命中即返回，**不会发起 registry 查询，也不会调用 dsh.cmd**。

### 2.2 候选筛选（按顺序，逐条记录拒绝原因）

| 顺序 | 规则 | 拒绝代码（`UpgradeRejectionCodes`） |
|---|---|---|
| 1 | 版本可解析；按版本降序最多检查 20 个，超出上限直接拒绝 | `E_CANDIDATE_VERSION_INVALID` / `E_CANDIDATE_LIMIT` |
| 2 | 必须高于已装版本 | `E_CANDIDATE_NOT_NEWER` |
| 3 | 非 deprecated（**deprecated 候选一律拒绝**；本实现不提供“用户确认后安装”的路径，与 `docs/实现方案-v2.md` §11.7 差异表一致） | `E_CANDIDATE_DEPRECATED` |
| 4 | `dsh.bundle.platforms` 为空或包含 `win32` | `E_CANDIDATE_PLATFORM` |
| 5 | 必须有 evaluator 结论；`unknown` / `unverified` / `incompatible` 全部拒绝（**unknown 绝不视为兼容**），未满足项原样保留 | `E_CANDIDATE_EVALUATOR_MISSING` / `_UNKNOWN` / `_UNVERIFIED` / `_INCOMPATIBLE` |
| 6 | 不得依赖风险豁免（`Exempted = true` 拒绝：不自动授予、也不自动依赖豁免） | `E_CANDIDATE_EXEMPTED` |
| 7 | 静态预检必须为 `ok` 或 `notApplicable`（`unverified` / `missing` 只进报告） | `E_CANDIDATE_STATIC_UNVERIFIED` |
| 8 | major 升级需要 `allowMajorUpgrade = true` | `E_CANDIDATE_MAJOR` |
| 9 | prerelease 需要显式允许 | `E_CANDIDATE_PRERELEASE` |

### 2.3 排序与结果

排序：**稳定版优先 → 版本号降序 → 同版本按 registry 返回顺序**。
每个进入排序的候选都会写入 `SortReason`，解释“为什么选它 / 为什么没选它”。

| 结果 | `BlockReason` / 决策 | 行为 |
|---|---|---|
| 命中 | `ShouldUpgrade = true`、`TargetVersion`、`Selected` | 生成 `add <pkg>@<ver> --save-exact` 并执行 |
| 候选列表为空 | `NoCandidates`（`NoAction`） | 不安装 |
| 没有更新版本 | `UpToDate`（`NoAction`） | 不安装 |
| 有 major 候选通过兼容检查但被 `allowMajorUpgrade` 拦下 | `MajorNotAllowed`（`ManualOnly`） | 提示开启配置或手动升级 |
| 只有 prerelease 通过兼容检查但未允许 | `PrereleaseNotAllowed`（`ManualOnly`） | 提示 nightly/预发布通道 |
| 其它无兼容版本 | `NoCompatibleCandidate`（`ManualOnly`） | `UpgradeDecision.ManualOnlyHint` 文本三选一提示：手动升级 / 保留旧版并申请风险豁免（需用户自行二次确认）/ 跳过；0.2.0 不弹专门对话框，也不代执行 |

`UpgradeDecision.RiskExemptionGranted` 在所有路径上恒为 `false`；代码中不存在自动
`allow-version` / 写 `compatibility.json` 的路径。

---

## 3. 命令

真实 DSH CLI 把余下参数转发给 profile 的 pnpm（见 `dsh plugin --help`），因此：

```text
查询候选：
  "<install>\resources\runtime\cli\bin\dsh.cmd" plugin --profile desktop view <pkg> versions dist-tags --json
拉取候选 manifest（最多 20 个）：
  "<install>\resources\runtime\cli\bin\dsh.cmd" plugin --profile desktop view <pkg>@<ver> --json
执行安装（一次一个包）：
  "<install>\resources\runtime\cli\bin\dsh.cmd" plugin --profile desktop add <pkg>@<ver> --save-exact
```

说明：
- 任务书里的“`install --save-exact <pkg>@<ver>`”对应 DSH CLI 中转发给 pnpm 的 `add`；本实现固定
  `add + --save-exact`（与设计 §11.1/§11.3 一致），prerelease 版本号原样透传。
- registry / 私服 / 认证 / 代理全部复用 profile 的 pnpm 与 `.npmrc`，Loom 不解析、不覆盖、不拼接认证参数；
  `--json` 输出只解析 JSON，日志与审计统一走 `Redactor.Redact`。
- 审计中的 `commandLine` 由 `UpgradeCommandBuilder.BuildDisplayCommand` 生成，每个 token 都加双引号
  （安装目录/profile 可含空格），**只用于记录**，实际执行仍走参数数组，不经 shell 拼接。
- 私有源不可达时**不回退公网**；离线由调用方在请求上置 `Offline = true`，执行器直接返回，不发起任何查询。

---

## 4. 执行流程与失败边界

`UpgradeService.UpgradeAsync(UpgradeRequest, CancellationToken)`：

1. 输入校验（profile / 数据目录 / 包名）→ 失败返回 `Failed`，不写盘。
2. 前置闸门（同上）→ 命中即返回，**不查询、不安装**。
3. （可选注入 `IProcessProbe`）若 DSH 仍在运行 → `NeedsManual` + `AppRunning`；
   **只拒绝，绝不结束进程**。probe 抛错时按“仍在运行”失败关闭。
4. `IPackageRegistry.QueryAsync` 查询候选；失败按 network/auth/not-found/timeout/… 分类返回。
5. 对“有 manifest、尚无结论”的候选，用 **目标 dshVersion**（克隆 installation 覆盖 `DshVersion`）调
   `ICompatibilityEvaluator`；没有 manifest 或 evaluator 的候选保持“未评估”，planner 按未知拒绝。
6. `UpgradePlanner.Plan` 决策；不升级则返回 `NeedsManual` / `Skipped`，不写 profile。
7. `DryRun`：只返回决策 + 命令预览，**不备份、不安装、不写 journal/审计文件**。
8. 写前备份：把 `package.json`、`pnpm-lock.yaml`、`cordis.patch.yml`、`compatibility.json`、`cordis.yml`
   复制到 `restore-backup\<opId>\`，逐文件记录 `sha256` / `size` / 是否存在，并写 `backup.json`；
   journal 记 `Backup`。
9. 执行 `add <pkg>@<ver> --save-exact`（journal `Apply`）；超时或非 0 退出 → 分类 + 回滚。
10. 回读验证：`node_modules\<pkg>\package.json` 的版本必须等于目标；
    `package.json` 里该依赖 spec 必须是**精确版本**（`^1.2.3`/`~`/`>=` 等判失败）；
    安装前在 `dsh.profile.bundles` 里的包必须仍在；安装前存在的 `pnpm-lock.yaml` 必须仍存在。
11. 成功：`Succeeded`，`ProfileChanged = true`，提示重启 DSH 生效。

**回滚边界**

- 只回滚本次 op 备份过的 profile 文件，使用 `restore-backup\<opId>\` 内的副本 + 哈希校验；`RollbackPath` 写入审计。
- `node_modules` **不由本模块回滚**：设计 §11.6 的 `install --offline --frozen-lockfile` 修复不在本模块，
  需要时由包级恢复（`recovery apply`）或人工执行兜底（见 §7）。回滚失败时失败分类升级为 `RollbackFailed`，并写入待人工消息。
- 任何异常都转成 `UpgradeExecutionResult` 返回，不向外抛；取消（`OperationCanceledException`）
  若已开始安装，同样回滚后返回 `Cancelled`。
- 不写 DSH 安装目录 / `app.asar`；不删除更新器缓存；不读取 `.credentials.yaml` 等凭据文件。

**失败分类**（`UpgradeFailureClass`）：`Offline`、`Network`、`Auth`、`NotFound`、`Timeout`、
`PeerConflict`、`Disk`、`Permission`、`VerifyFailed`、`InstallFailed`、`BackupFailed`、
`RollbackFailed`、`AppRunning`、`Cancelled`、`InvalidResponse`、`Unknown`。
分类只依据 CLI 输出/退出码/超时标记，用于报告与重试提示。

**journal**：`journal\<opId>.jsonl` 逐阶段追加 `Start / Plan / Backup / Apply / Verify / Rollback / Done`；
最后一跳用 `done` / `failed` / `cancelled` 收尾，满足 `HasUnfinishedOperation` 的判定。
所有状态类磁盘写（备份副本、`backup.json`、审计 JSON、回滚写入）都走 `AtomicFile`；journal 复用既有的
append-only `JournalWriter`。

---

## 5. 审计字段

审计写入 `reports\upgrade-<opId>.json`（UTF-8 无 BOM，`AtomicFile`），并返回 `AuditPath`：

| 字段 | 说明 |
|---|---|
| `operationId` / `startedAt` / `finishedAt` / `duration` | 操作标识与耗时 |
| `packageName` / `installedVersionBefore` / `installedVersionAfter` | 包与装前/装后版本 |
| `targetVersion` / `targetDshVersion` | 决策目标与目标运行时 |
| `policy` / `allowMajorUpgrade` / `offline` / `dryRun` | 决策上下文 |
| `candidates[]` | 版本、`deprecated`、`distTags`、`integrity`、`registry`、`evaluatorLevel`、`engine`、`requirements`、`exempted`、`staticResult`、`staticMissingRefs`、`sortReason` |
| `rejected[]` | 版本 + 稳定代码 + 原因（evaluator 未满足项原样保留） |
| `selectedRegistry` / `selectedIntegrity` | 供应链信息 |
| `commandLine` / `exitCode` / `timedOut` | 实际命令与退出码；`installDuration`、`queryDuration`、`queryAttempts` |
| `backupDir` / `backupFiles[]` / `rollbackPath` / `rolledBack` | 备份与回滚证据（含逐文件 sha256） |
| `failureClass` / `failureDetail` / `verifyDetail` / `outcome` | 失败分类与校验结论 |

审计只写路径/哈希/版本/结果，不写文件内容、环境变量或凭据；`commandLine` 与错误摘要先经 `Redactor`。

---

## 6. 决策表覆盖的测试

`UpgradePlannerTests`（纯决策）：A/B/C、大小写策略、缺 config、离线、major 允许/拒绝、prerelease
允许/拒绝/稳定优先、无兼容（保留 evaluator 未满足项）、unknown/unverified/incompatible/未评估、
static unverified/missing、deprecated、豁免候选、平台不含 win32、旧版本/同版本、候选上限 20、
同版本 registry 顺序、输入不被修改、目标/已装版本不可解析、null 输入。

`UpgradeServiceTests`（执行器）：成功（备份在安装前、哈希、journal、审计、`--save-exact`、目标 dshVersion 评估）、
prerelease 命令与引号、dry-run 不落盘、安装失败回滚（NotFound）、写完 `^` spec 的校验失败回滚、
取消回滚、离线不查询不安装、策略 B/C 不查询、App 运行中拒绝、无兼容不安装不豁免、
无 manifest/无结论保守拒绝、registry 失败分类、空候选、默认 registry 的 JSON 解析（数组/版本映射两种形状）、
失败分类（auth/404/timeout/network/peer/disk/permission）、超时与非法 JSON、命令拼装与展示引号。

---

## 7. 已知限制 / 待决策

1. **private registry 名称未解析**：默认实现不读 `.npmrc`，审计的 `selectedRegistry` 只有调用方在
   `PackageRegistryQueryRequest.Registry` 显式给出时才有值（或由注入的 registry 提供）。
2. **单次决策总预算**：实现了“最多 20 个候选 / 每次查询 60s”，未实现设计里的“总计划 5min”硬上限；
   建议在调用层用 `CancellationTokenSource.CancelAfter` 兜底。
3. **`node_modules` 修复**：失败后只回滚 profile 文件；设计 §11.6 的
   `install --offline --frozen-lockfile` 修复不在本模块，需要时由包级恢复（`recovery apply`）或人工执行兜底。
4. **首启健康检查/自动回滚**：`HealthCheckSeconds` 与 §10.6 的 verify-failed 自动回滚不在本模块；
   本模块只提供 `Succeeded` + `BackupDir` 供上层观察。
5. **磁盘空间检查**：设计要求的“store + profile 至少 2× 预估”未实现（预估依赖 pnpm store，留给后续）。
6. **候选 manifest 的 static 预检**：候选尚未安装时，helper 的静态预检可能把 bundle 候选判为
   `unverified`/`missing`（patch/client 文件不在 profile 内），planner 会因此拒绝自动升级——这是保守且
   符合“unverified 不自动升级”的预期行为，但会降低自动化命中率。若要提升命中率，需要给 evaluator
   提供“解包到临时目录再预检”的能力（二期后续）。
7. **dry-run 仍会查询 registry**（只读），但绝不写 journal/审计/备份。

---

## 8. 组合根注入与入口签名（0.2.0 已接线）

```csharp
// 1) 默认实现（dsh.cmd 视角；离线由 request.Offline 短路）
var dshCli = new Loom.Core.Environment.DshCli(logger);
IDshCli cli = new DshCliAdapter(dshCli);
IPackageRegistry registry = new DshCliPackageRegistry(cli, logger);

// 2) 组合：evaluator 与 processProbe 可选但强烈建议注入
var service = new UpgradeService(
    registry,
    cli,
    logger,
    evaluator: electronEvaluator,     // ICompatibilityEvaluator，用目标 dshVersion 复检候选
    processProbe: processProbe);      // IProcessProbe：App 未完全退出时拒绝（不 kill）

// 3) 调用
var result = await service.UpgradeAsync(new UpgradeRequest
{
    Installation = installation,
    Paths = paths,
    Config = config,
    PackageName = pluginName,
    InstalledVersion = installedVersion,   // 从 profile 读；未知则不升级
    TargetDshVersion = targetDshVersion,   // update-info 目标版本 / 当前运行时
    Offline = offline,
    DryRun = dryRun,
    AllowPrerelease = allowPrerelease,
    PrereleaseJustification = "nightly 通道",
    OperationId = opId,                    // 复用全局 opId 以便 journal/审计串联
    MaxCandidates = 20,
    QueryTimeout = TimeSpan.FromSeconds(60),
    InstallTimeout = TimeSpan.FromMinutes(15),
});
```

已接入 `ILoomService`（0.2.0 最终签名）：

```csharp
Task<UpgradeExecutionResult> UpgradePluginAsync(UpgradeRequest request, CancellationToken cancellationToken = default);
Task<UpgradeExecutionResult> UpgradePluginAsync(UpgradeRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
```

批量入口 `UpgradePluginsAsync` **未实现**：Tick 自动升级每次最多处理 3 个（`MaxAutoUpgradesPerTick`，失败即停止本轮），CLI 不带 `--pkg` 时按最近一次检查结果逐个调用单包入口；“普通第三方插件 → dshmarket 最后”的顺序也未实现（见 `docs/实现方案-v2.md` §11.7）。

`LoomConfig` 已有以下字段（`src/Loom.Core/Models/CoreModels.cs`；`StateAndConfig.cs` 负责归一化与默认值）：

```csharp
bool AutoUpgradeEnabled { get; set; }                  // 默认 false：总开关，关闭时 Tick 不查询、不安装
bool AllowMajorUpgrade { get; set; }                   // 默认 false
bool AllowPrereleaseUpgrade { get; set; }              // 默认 false；nightly 为 true
int UpgradeQueryTimeoutSeconds { get; set; } = 60;
int UpgradeInstallTimeoutSeconds { get; set; } = 900;  // 单包 15min
bool RequireAppExitForUpgrade { get; set; } = true;    // false 时把进程探针视为“已退出”
```

调用顺序（已由 `LoomService.UpgradePluginAsync` 落实）：`BeginOperationAsync("upgrade")` 取门并写 journal/state →
`IProfileReader` 读出已装版本 → 确认 DSH 完全退出 → 执行升级 → 更新 `state.lastOperation` 并按结果发通知。
CLI 直连前另由 `TrayCommandRouting` 以 `WaitOne(0)` 探测跨进程门（非 `--dry-run`；拿不到返回退出码 4）。

---

## 9. 安全 / 隐私核对（§15）

- 不联网（除 registry 查询与 CLI 安装，均为用户配置的 registry）；本模块自身无遥测、无下载。
- 不在无用户同意时把私有包名发到公网：查询失败不回退公网 registry。
- 不读取 `.credentials.yaml` / DPAPI / 浏览器存储；不复制 credentials 类文件。
- 日志与审计统一 `Redactor`：`_authToken`、`Authorization`、URL query、npm/GitHub token。
- 报告只写路径/哈希/版本/结果；不写 `cordis.patch.yml` 内容、环境变量或对话内容。
- 不 kill DSH/pnpm 之外的进程；`DshCli` 超时只终止它自己启动的子进程树。
- 所有写操作先备份、可回滚；备份目录 ACL 由 `LoomPaths.EnsureDirectories` 的既有逻辑收紧。
