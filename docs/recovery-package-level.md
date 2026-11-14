# 包级恢复与离线完整回滚（二期 · 实验性）

> 对应设计 `docs/实现方案-v2.md` §9.4（损坏与回滚）、§12.5（包级恢复顺序）、§12.6（验证/回滚/首启失败判据）、§11.6（失败、回滚与离线）。
> 代码位置：`src/Loom.Core/Recovery/**`；测试：`src/Loom.Tests/Recovery/**`。
> 状态：**二期实验性，0.2.0 起已接线（默认关闭）**。CLI `loom-dsh recovery plan|apply|rollback`、设置页
> 「包级恢复（实验性）」与 `ILoomService` 入口均已接入；`packageRestoreEnabled=false` 时直接拒绝且不写 profile
> （见 §10、§11）。

## 1. 目标与边界

包级恢复解决“文件级还原救不回 node_modules”的问题：当 profile 的 `package.json` / `pnpm-lock.yaml` 已被还原，但依赖目录与 lockfile 不一致、插件包缺失时，按 **App 完全退出 → 恢复两个核心文件 → 离线安装 → 校验** 的顺序重建依赖树。

硬约束（代码内已落实，测试覆盖）：

- **绝不自动结束 DSH 进程**；只读取调用方传入的运行状态，运行中直接失败并给人工入口。
- **不写安装目录 / `app.asar`**；只写 `profile\package.json`、`profile\pnpm-lock.yaml`（经 `AtomicFile`）与工具数据目录。
- **写前必须备份**：`restore-backup\<opId>\` 保存写前文件与 sha256，回滚**只回滚当前 op**。
- **离线可用**：默认只执行 `install --offline --frozen-lockfile`，不联网、不查询 registry。
- **不使用 `file:<tgz>` 归档**：依赖 spec 含 `file:` 时 planner 直接阻塞（设计 §12.5 最后手段明确禁止残留 `file:`）。
- planner **纯函数**：不 IO、不探测进程、不调用 CLI；执行器与回滚服务才做 IO。

## 2. 前置条件

planner 把“事实”作为输入（`PackageRestoreOptions`），逐项判定并写入 `plan.Preconditions`；首个不满足项成为 `plan.BlockingFailure`：

| 代码 | 要求 | 不满足时的分类 / 人工入口 |
|---|---|---|
| `ProfileReadable` | profile 目录已知 | `Precondition`，重新定位安装 |
| `SnapshotPackageJson` | 快照 `profile\package.json` 存在且可解析 | `SnapshotInputMissing`，重新采集快照 |
| `SnapshotLockfile` | 快照 `profile\pnpm-lock.yaml` 存在 | `SnapshotInputMissing`（`--frozen-lockfile` 前提） |
| `CliAvailable` | `dsh.cmd` / 安装目录可定位 | `CliMissing` |
| `NoFileSpecs` | 快照依赖无 `file:` spec | `UnsupportedSpec` |
| `DshFullyExited` | App 已完全退出 | `ProfileBusy`，提示退出；**不结束进程** |
| `ProfileNotLocked` | `lock` / `package.json.lock` / `run.json` 空闲 | `ProfileBusy` |
| `OfflineCache` | pnpm store 已缓存 lockfile 所需包 | `OfflineCacheMissing`，直接失败 + 人工入口 |
| `DiskSpace` | 可用空间 ≥ 估算（调用方提供时） | `Disk` |

`OfflineCacheAvailable` 默认 `true`（调用方未预检时保持乐观），真实缺缓存由 CLI 输出在失败分类里兜底为 `OfflineCacheMissing`。

## 3. 流程与步骤顺序（设计 §12.5）

`PackageRestorePlanner.Create(...)` 产出固定顺序的步骤（`PackageRestoreStep.Order`）：

| # | 步骤 | 内容 | 需要完全退出 |
|---|---|---|---|
| 1 | `BackupProfile` | 复制 `package.json` + `pnpm-lock.yaml` 到 `restore-backup\<opId>\`，记录 sha256/size 并写回滚点 | 否 |
| 2 | `WritePackageJson` | 以快照为基线写回；保留当前 profile 中快照没有的**顶层用户字段**；已一致则 `Skip` | 否 |
| 3 | `WriteLockfile` | 写回快照 `pnpm-lock.yaml`；已一致则 `Skip` | 否 |
| 4 | `InstallOffline` | `dsh plugin --profile <profile> install --offline --frozen-lockfile` | 是 |
| 5 | `InstallOnline`（可选） | `install --frozen-lockfile`，**仅**当调用方传入 `AllowNetworkFallback=true`（用户已同意）且 `config.PackageRestoreAllowNetworkFallback` 同为 true；0.2.0 没有调用方置 true，实际只走离线（预留） | 是 |
| 6 | `Verify` | 校验版本存在、bundles 齐全、lockfile 与快照一致 | 否 |

合并规则（`MergePackageJson`）：

- 以快照 `package.json` 为基线；
- 当前 profile 存在而快照没有的顶层字段原样保留（如 `customUserField`）；
- `dependencies` / `devDependencies` / `optionalDependencies` / `peerDependencies` / `dsh` 段**以快照为准**——否则写回的 lockfile 与 package.json 不一致，`--frozen-lockfile` 必然失败。

## 4. 命令与参数

执行器只使用注入的 `IDshCli`，以**参数数组**调用（`DshCli` 内部走 `dsh.cmd`，cwd=安装目录，环境已清洗）：

```text
dsh.cmd plugin --profile desktop install --offline --frozen-lockfile     # 默认路径
dsh.cmd plugin --profile desktop install --frozen-lockfile               # 仅用户同意后（可选步骤）
```

- 不调用系统 `pnpm`、不拼 shell 字符串、不联网查询 registry。
- 超时由 `PackageRestoreOptions.InstallTimeoutSeconds`（默认 600s）写入步骤，执行器按步骤超时执行；超时由 `DshCli` 终止 **CLI 进程树**（不触碰 DSH）。
- 回滚/修复也只使用离线参数。

## 5. 文件与数据布局

```text
<data-root>\restore-backup\<opId>\
  package.json              # 写前副本（存在才写）
  pnpm-lock.yaml            # 写前副本（存在才写）
  package-restore.json      # 回滚点：opId/profileDir/snapshotId/scope/files[{name,existed,sha256,size}]/bundles/dependencies/lockfileSha256
<data-root>\journal\<opId>.jsonl   # 逐阶段 JSONL：Start/Backup/Apply/Verify/Rollback/Done|Failed
```

回滚点（`OfflineRollbackPoint`）记录**操作前**的 bundle 列表、依赖包名与 lockfile 哈希，用于“回滚后再次校验”；`InstallExitCode` 在正向安装成功后回填，便于审计。

## 6. 校验与首启失败判据（设计 §12.6）

安装后校验（`RecoveryVerification.VerifyProfile`，失败分类 `VerifyFailed`）：

1. `package.json` 可解析；
2. `dsh.profile.bundles` 含快照的全部 bundle；
3. `dependencies` 含快照全部依赖包名；
4. 每个依赖在 `node_modules\<name>\package.json` 存在，且版本与快照 lockfile 解析版本一致（“版本存在”）；
5. `pnpm-lock.yaml` 的 sha256 与快照一致。

首启判据（`PackageRestorePlanner.EvaluateFirstStart`，纯函数）任一命中即 `verify-failed`，`AutoRollbackAllowed=true`：

| 优先级 | 判据 | 判定 |
|---|---|---|
| 1 | startup 类新 crash **且落在本次 op 时间窗**（op 开始时间 − 容差 之后） | `StartupCrash` |
| 2 | 宿主进程提前退出 | `ProcessExitedEarly` |
| 3 | 已等待 ≥ N 秒（默认 20s，`FirstStartCriteria.ReadyTimeoutSeconds`）仍未 ready | `NotReadyTimeout` |
| 4 | check 仍缺 bundles | `BundlesMissing` |
| 5 | check 仍缺依赖 | `DependenciesMissing` |

早于本次 op 的 crash 视为用户手动崩溃，只记录信号、不判失败（避免误判）。未到 N 秒且未 ready 时返回“仍在等待”，不判失败。

## 7. 失败分类与人工入口

`PackageRestoreFailureKind`：`Precondition` / `ProfileBusy` / `SnapshotInputMissing` / `UnsupportedSpec` / `CliMissing` / `OfflineCacheMissing` / `Network` / `Auth` / `NotFound` / `PeerConflict` / `Disk` / `Permission` / `Timeout` / `InstallFailed` / `VerifyFailed` / `BackupFailed` / `BackupCorrupt` / `RollbackFailed` / `FirstStartFailed` / `Cancelled` / `Unknown`。

分类来源：planner 前置条件；`PackageRestorePlanner.ClassifyInstallFailure` 对 CLI 退出码/超时/输出（`ERR_PNPM_NO_OFFLINE_TARBALL`、`EACCES`、`ENOSPC`、peer、401、404、`EAI_AGAIN` 等）做纯函数映射。每个失败都带中文 `Message`、`Detail` 与 `ManualAction`（可照抄的命令）。

失败时的行为：

- 先回滚当前 op 的文件（`restore-backup\<opId>\`），再判断是否需要离线修复安装；
- `OfflineCacheMissing` / `Permission` / `Disk` / `CliMissing` / `SnapshotInputMissing` / `UnsupportedSpec` / `Precondition` **不重跑修复安装**，直接 `NeedsManual`；
- 其余失败在回滚后尝试一次 `install --offline --frozen-lockfile` 修复 node_modules；修复失败同样 `NeedsManual`；
- **绝不**自动联网重试；联网命令只写在 `ManualAction` 里，等用户同意。

## 8. 离线完整回滚（`OfflineRollbackService`）

```csharp
var service = new OfflineRollbackService(paths, dshCli, logger);
bool ok = service.CanRollback(opId, out var reason);   // 备份目录 + 回滚点 + 备份哈希校验
var result = await service.RollbackAsync(opId, installation);
```

流程：

1. `CanRollback`：`restore-backup\<opId>\package-restore.json` 存在且可解析、备份文件齐全、sha256 与记录一致；
2. 校验不过 → `BackupCorrupt`，**不写 profile**（防篡改 / 防半写备份）；
3. 按回滚点恢复文件：`Existed=true` 用备份覆盖，`Existed=false` 删除目标；
4. `install --offline --frozen-lockfile`（`Scope=FilesOnly` 时跳过）；
5. 用回滚点里的 bundles / 依赖 / lockfile 哈希再次校验；
6. 任何一步失败 → `Failure` + 人工入口；文件已恢复但离线安装失败时标记 `NeedsManual`（绝不联网补救）。

边界：只读/写 `<data-root>\restore-backup\<opId>\` 与 profile 两个文件；不触碰其它 op 的备份、不碰安装目录、不结束进程。

## 9. 与文件级还原的关系

| 能力 | `RestoreService`（一期，文件级） | `PackageRestoreService`（二期，包级） |
|---|---|---|
| 对象 | `package.json` 合并、`cordis.patch.yml`、`compatibility.json` | `package.json` + `pnpm-lock.yaml` + `node_modules` |
| 依赖处理 | 只补 `dependencies` spec，不调用 pnpm | `install --offline --frozen-lockfile` 重建依赖树 |
| 证据/冲突 | 合并规则、保留用户改动、冲突待处理 | 明确的前置条件 + 失败分类 |
| 回滚 | 本次写入的文件 | 本次 op 的两个核心文件 + 离线重装 |

推荐编排（0.2.0 中包级恢复仍由用户/上层显式触发，尚未自动串联）：**先文件级还原**（保留用户改动、处理 patch/compatibility）→ **再包级恢复**（把 lockfile 与 node_modules 拉回快照状态）→ 重启 DSH → 按 §6 判定首启；失败则用 `OfflineRollbackService` 回滚当前包级 op。

## 10. 组合根注入与 `ILoomService` 入口（0.2.0 已接线）

### 10.1 依赖注入

```csharp
// IDshCli 定义在 src/Loom.Core/Recovery/DshCliSeam.cs（只含 RunPluginAsync）。
// 注意：Upgrade 侧另有同形端口 Loom.Core.Upgrade.IDshCli（同样只含 RunPluginAsync）。
// 0.2.0 组合根（LoomServiceFactory）只创建一个 DshCli 实例，两处各套一层薄适配器。
// a) 适配器（不改 Environment\DshCli.cs）：
IDshCli dshCli = new DshCliAdapter(new DshCli(logger));
// b) 让既有 DshCli 直接实现 IDshCli（一行改动，方法签名完全一致）。

var planner  = PackageRestorePlanner.Create(input);          // 纯函数，可在 UI 预览线程调用
var executor = new PackageRestoreService(dshCli, logger);
PackageRestoreResult result = await executor.RestoreAsync(
    new PackageRestoreRequest
    {
        Installation = installation,
        Paths = paths,
        Plan = planner,
        OperationId = operationId,
        AppRunning = processProbe.HasAny("DeepSeek Harness"),  // 只读探测；Loom 不结束进程
    },
    cancellationToken);

var rollback = new OfflineRollbackService(paths, dshCli, logger);
bool can = rollback.CanRollback(opId);
OfflineRollbackResult rolled = await rollback.RollbackAsync(opId, installation, cancellationToken);
```

`PackageRestorePlanInput` 需要调用方读取快照文本（planner 不做 IO）：

```csharp
var snapshotDir = snapshotRef.Path;
var input = new PackageRestorePlanInput
{
    Snapshot = manifest,                       // SnapshotService.LoadAsync 校验后的 snapshot.json
    SnapshotDir = snapshotDir,
    SnapshotPackageJson = ReadText(snapshotDir, "profile", DshDefaults.ManifestFile),
    SnapshotLockfile    = ReadText(snapshotDir, "profile", DshDefaults.LockfileFile),
    Current = profileReader.Read(installation, strict: false),
    Installation = installation,
    Config = config,
    Options = new PackageRestoreOptions
    {
        AppRunning = appRunning,               // IProcessProbe（只读）
        ProfileLocked = lockStatus.Busy,       // ILockManager.Inspect
        OfflineCacheAvailable = storeProbe.Ok, // 可选：pnpm store 预检；不确定就保持 true
        AllowNetworkFallback = userConsent,    // 只有用户明确同意才 true
    },
    OperationId = operationId,
};
```

### 10.2 已接入 `ILoomService`

```csharp
Task<PackageRestorePlan> PreviewPackageRestoreAsync(
    PackageRestoreRequest request, CancellationToken cancellationToken = default);

Task<PackageRestoreResult> PackageRestoreAsync(
    PackageRestoreRequest request, IProgress<OperationProgress>? progress = null,
    CancellationToken cancellationToken = default);

bool CanRollbackPackageOp(string opId);
Task<OfflineRollbackResult> RollbackPackageOpAsync(
    string opId, CancellationToken cancellationToken = default);
```

（`PackageRestoreRequest` 携带 `Plan` / `DryRun` / `AllowNetworkFallback` / `AppRunning` / `InstallTimeoutSeconds`；
`PackageRestoreAsync` 内部用 `BeginOperationAsync("recovery")` 复用 operation gate / journal，`--dry-run` 只出计划、不写 profile。）

CLI 侧（`TrayCommandRouting` + `Program.cs`）：`recovery apply` / `recovery rollback`（非 `--dry-run`）直连前先以
`WaitOne(0)` 非阻塞探测跨进程写操作门，拿不到返回退出码 **4**（忙），拿到则一直持有到命令结束；
`recovery plan` 与 `--dry-run` 视为只读——不入队、不探测，也不因“托盘使用其他数据根”被拒绝（退出码 2 只针对写命令）。

### 10.3 `LoomConfig` 字段（已实现）

| 字段 | 默认 | 说明 |
|---|---|---|
| `PackageRestoreEnabled` | `false` | 二期实验性开关（默认关闭，需显式开启） |
| `PackageRestoreAllowNetworkFallback` | `false` | 离线失败后是否允许**询问**用户联网重试（仍需逐次同意） |
| `PackageRestoreInstallTimeoutSeconds` | `600` | 单次 install 超时 |
| `PackageRestoreReadyTimeoutSeconds` | `HealthCheckSeconds`(20) | 首启 N 秒判据 |
| `KeepRollbackBackups` | `5` | `restore-backup\*` 保留份数：包级恢复成功后由 `LoomService` 清理最旧目录（绝不删当前 op） |

## 11. 接线状态与未实现项

- **CLI 已接线**：`loom-dsh recovery plan|apply|rollback <opId>`（另有 `--snapshot <id>` / `--dry-run` / `--json`），
  设置页有「包级恢复（实验性）」卡片与「回滚上一次包级操作」按钮；**没有** `recovery status` 子命令——
  状态由 `status` / `doctor` 与设置页的最近可回滚 opId 呈现。
- **pnpm store 预检未实现**：`OfflineCacheAvailable` 由调用方传入；未预检时保持 `true`，缺缓存由 CLI 输出分类兜底。
- **`file:<tgz>` 归档恢复未实现**（设计 §12.5 最后手段），当前遇 `file:` spec 直接阻塞。
- **首启判据的进程/crash 采集未实现**：`FirstStartObservation` 由 integrator 用进程探针 + crash 日志窗口填充；本模块只做纯判定。
- **`restore-backup` 保留已实现（有边界）**：包级恢复成功后按 `KeepRollbackBackups` 清理最旧的回滚点目录，绝不删当前 op；独立的维护 tick 清理未接线，长期运行仍建议上层周期检查。
- **`dsh.cmd plugin install --offline --frozen-lockfile` 的真实支持度**需在集成环境 spike 验证（设计 §12.5 注）；若不支持，应在 `DshCli` 层改用 bundled pnpm（仍不调用系统 pnpm），Recovery 的调用签名不变。
- **并发/崩溃恢复**：执行器在有意写文件前就落回滚点，崩溃后可用 `OfflineRollbackService` 以 opId 回滚；`JournalWriter.HasUnfinishedOperation` 的 `repair` 入口需 integrator 接入。

## 12. 测试

`src/Loom.Tests/Recovery/`：

- `PackageRestoreTests.cs`：planner 全分支（步骤顺序、`RequiresFullExit`、离线缺缓存阻塞、联网开关、幂等跳过、`file:` 阻塞、保留用户字段、9 类 CLI 失败分类、6 个首启判据）+ 执行器（成功/失败只回滚当前 op/拒绝在运行时执行/演练）。
- `OfflineRollbackTests.cs`：`CanRollback`、文件+离线重装+回滚后校验、离线失败不联网、篡改备份拒回滚、演练、双 op 只影响指定 op。

全部使用 `%TEMP%` 临时 sandbox 与假 `IDshCli`，不触碰真实 profile、`%LOCALAPPDATA%`、安装目录或网络。
