# 核心工作流实现说明（快照 / 检查 / 文件级还原 / 报告）

> 工作流：core-flows｜对应任务：`task-1`｜设计依据：`docs/实现方案-v2.md` 第 7、9、10、12、13.8、16 节，`docs/方案审核报告.md` P0-4/P0-5/P0-6/P1-2/P1-3/P1-6/P1-9/P1-12 的修正。
> 代码范围：`src/Loom.Core/Snapshot`、`Check`、`Restore`、`Reporting`；测试：`src/Loom.Tests/CoreFlows`。
> 未改动 `Models/`、`Abstractions/`、`LoomService.cs`、`Composition/`、`Cli/`、`Ipc/`；构造函数签名与 `LoomServiceFactory` 保持一致（仅新增了可选的额外参数/属性，见下文「扩展点」）。

## 1. 文件清单

| 文件 | 实现内容 |
|---|---|
| `Snapshot/ProfileReader.cs` | 稳定读协议（7.2）、package.json / pnpm-lock.yaml / cordis.patch.yml / compatibility.json / cordis.yml 解析、每个依赖的 node_modules manifest → `PluginSnapshot`；同文件的 `internal static ProfileFiles` 提供 compatibility.json 与 `.bak-*` 时间戳解析供 Check/Restore 复用 |
| `Snapshot/SnapshotService.cs` | `CaptureAsync`（light/full）、`WriteAsync`（目录 + `snapshot.json` + sha256 + full 归档/降级）、`LoadAsync`、`Verify`、`RetainAsync`、`InspectStorage` |
| `Check/CheckService.cs` | 基线差异、bundle 分类处理、证据分级（强/中/弱/无）、兼容性批量评估、`CheckResult` 全字段 |
| `Restore/RestorePlanner.cs` | 12.3/12.4 纯函数合并计划（bundles/deps/lockfile/patch/compatibility/.dsh-market） |
| `Restore/RestoreService.cs` | 备份 → 原子写 → 校验 → journal；幂等；失败回滚 |
| `Reporting/ReportService.cs` | `reports/report-<opId>.json` + `.md`、`ReadLatestAsync`、`ReadAsync` |
| `Loom.Tests/CoreFlows/*` | 沙箱夹具 + 30 个测试（见第 9 节） |

## 2. ProfileReader

### 2.1 稳定读协议（设计 7.2）

`Read(installation, strict: true)`：

1. **等待空闲**：轮询 `<profile>\lock`（内容 PID 存活才算忙，死 PID 不阻塞、也不删除）、`<profile>\package.json.lock`（mtime 120 秒内算忙，与 DSH 的 120s stale 规则一致）、`<profile>\.plugin-manager\run.json`（JSON 中 pid 存活才算忙）。忙则指数退避 `20ms → 40ms → … → 2s`，总上限默认 60 秒；超时抛 `LoomException(E_BUSY)`，由 LoomService 转成「环境忙」失败，不写半成品。
2. **记录基线哈希**：`package.json`、`pnpm-lock.yaml`、`cordis.patch.yml` 的 sha256。
3. **读取内容**（依赖清单、bundles、各插件 manifest、patch、compatibility、cordis.yml）。
4. **再读基线哈希**：与步骤 2 不一致则退避重试，超时抛 `E_BUSY`（设计 7.2 第 4/6 条）。
5. `strict: false` 只做一次读取、不等待锁（预览/状态/Check 用，避免只读路径被锁拖 60 秒）。

### 2.2 解析规则

- **package.json**：`dependencies`（保留原始 spec）、`dsh.profile.bundles`（顺序即合并基准）。非法 JSON 抛 `LoomException(E_FAILED)`，不静默生成空清单。
- **pnpm-lock.yaml**：自写最小行解析器（不是完整 YAML 解析器）：`importers.` → `dependencies` 的 `specifier`/`version`，`packages` 段的 `resolution.integrity`。解析失败只记日志、字段留空，不阻断读取。
- **cordis.patch.yml**：记录 sha256 与同目录 `cordis.patch.yml.bak-*` **文件名列表**（按解析出的时间戳升序）。
  - **取舍**：`PatchSnapshot.Backups` 只存文件名、不存 mtime（模型无字段）。比较「备份是否比快照新」时一律解析文件名中的时间戳（DSH 的 `.bak-<毫秒时间戳>`，同时兼容 10/11 位秒、ISO/`yyyyMMdd-HHmmss`），避免把「快照时间 + 当前 FS mtime」混用造成不确定。备份文件的 sha256 记录在 `ProfileState.FileSha256["cordis.patch.yml.bak-*"]`，用于判断「当前 patch 是否等于某个旧备份」。
- **compatibility.json**：与官方 `readProfileCompatibility` 对齐——顶层对象，key 必须是精确 `包名@精确版本`，value 必须是精确 DSH 版本数组；缺失=无豁免；任一记录非法=**warnings + rewritable=false**。
  - **约定（避免改共享模型）**：`ProfileState.CompatibilityPath != null && CompatibilitySha256 == null` 表示「文件存在但官方 reader 会拒绝（不可安全改写）」。Planner 据此输出 `SkipCompatibility`，RestoreService 也二次校验、绝不覆盖。
- **cordis.yml**、`.dsh-market/state.json`：只记录 sha256（后者仅用于让 Planner 决定「市场状态不还原」）。
- **插件 manifest**：逐个依赖读 `<profile>\node_modules\<name>\package.json`，生成 `PluginSnapshot`：version（manifest 优先，回退 lock resolved）、dir、engines、peerDependencies、`dsh.bundle`（bundle/platforms/patch）、`dsh.client.inject`；`Source` 为 `file:`/`workspace:`/`link:` 时标 `local`，否则 `registry`。
  - **静态预检（ProfileReader 层）**：只做本地可判定项（`dsh.bundle.patch` 相对路径是否存在 → `PatchFiles`/`MissingRefs`），client 注入只记录、标 `Unverified`（真实解析在 evaluator/helper 里，见 10.3）。`compatibility`/`engine` 此时保持 `Unknown`，由 Check 填充。

## 3. SnapshotService

### 3.1 Capture（light / full）

- 内部用 `ProfileReader.Read(strict: true)` 采集（即具备双读一致性）。
- `id = yyyyMMdd-HHmmss-<appVersion>-<dshVersion>`，经 `LoomPaths.SanitizeFileName`；`WriteAsync` 发现目录已存在时追加 `-2`、`-3`… 保证唯一（测试/同秒多次采集安全）。
- bundle 三级分类：`installation.BaseBundles` ∪ `DshDefaults.BaselineBundles` → `Baseline`；`@deepseek-ai/dsh*` → `Official`；其余 `ThirdParty`。bundle 的 version/manifestPath 从同名插件元数据取。
- `plugins`、`dependencies`、patch、compatibility（含 exemptions）、`FileSha256`、`files`、`environment` 全部按设计 9.1 落 `SnapshotManifest`。
- full 的「意图」由 `PackageArchive.Kind = "node-modules-tree"` 表示，真正的复制延后到 `WriteAsync`（因为只有 Write 拿到 `LoomConfig.MaxArchiveBytes` 与快照根目录，才能做上限/磁盘判断）。

### 3.2 Write

- 目录：`snapshots\<id>\snapshot.json` + `profile\`（package.json / pnpm-lock.yaml / cordis.patch.yml / compatibility.json / cordis.yml，逐字节复制）。
- full：把 `node_modules` 文件树复制到 `packages\node_modules\...`（跳过 reparse point/junction，避免 pnpm `.pnpm` 链接重复），每个文件记 `ArchivedFile{path,sha256,size}`、累计 `TotalBytes`。
- **降级规则**：复制前先估算总大小；超过 `config.MaxArchiveBytes`（默认 500MB，`ConfigStore.Normalize` 下限 10MB）或剩余磁盘 < 2×大小 → 不复制，`PackageArchive.Kind = "none"`（light），并通过：
  - `ILogger.Warn` 记录；
  - `SnapshotService.LastWarnings`（新增的公开只读属性，非接口成员）暴露给调用方/测试。
  - **偏差**：`ISnapshotService.WriteAsync` 只返回 `SnapshotRef`，没有 warnings 通道，因此 `SnapshotOutcome.Warnings` 目前不会自动带上该降级提示；LoomService 若需要展示，可读取 `SnapshotService.LastWarnings`（见「扩展点」）。
- `snapshot.json` 用 `JsonDefaults`（UTF-8 无 BOM，原子写），写后计算 sha256 存入 `SnapshotRef.Sha256`；`SizeBytes`/`PluginCount`/`BundleCount`/`Frozen`（PreUpdate）/`Path` 全部填充。

### 3.3 Load / Verify / InspectStorage

- `Verify`：目录存在 + `snapshot.json` 存在 +（有 sha 时）sha 一致 + JSON 可反序列化。
- `LoadAsync`：sha 不一致返回 null 并记 warn（不抛异常，交由 LoomService 转成「基线损坏」提示）。
- `InspectStorage`：统计 `snapshots\*` 中带 `snapshot.json` 的目录数量与总大小（`corrupt-*` 不计）。

### 3.4 Retain（9.3 + 审核 P1-12）

保护集合（永不删除）：`state.PreUpdateSnapshot`、`state.LastSnapshot`、`state.PendingRestoreSnapshotId`、所有 `Trigger==PreUpdate` 的快照。
保留集合：

1. 最近 `config.KeepSnapshots` 份（默认 5）；
2. 每个 `dshVersion` 的最新 1 份；
3. 保护集合。

删除顺序：先删「不在保留/保护集合」的（最旧优先），再按 `MaxSnapshotBytes` 总配额从最旧的**非冻结、非保护**快照开始淘汰，直到低于配额；返回删除数量。
损坏快照（`snapshot.json` 缺失/不可解析）按 9.4 移到 `snapshots\corrupt-<ts>-<name>\`，不计入删除数。
**实现取舍**：`RetainAsync` 不调用 `ConfigStore.Normalize`（否则测试无法用小配额验证淘汰）；生产路径的 config 在保存时已归一化。

## 4. CheckService

### 4.1 差异分类（12.3 / 12.4）

对 `baseline.Profile.Bundles` 中「非安装包基线」的 bundle，若当前 `dsh.profile.bundles` 缺失：

| 快照类别 | 当前依赖/包状态 | 处理 |
|---|---|---|
| Official（官方可选，如 agent-team） | 任意 | 进 `RemovedBundles`，**强证据下**进 `RestoreCandidates`（P0-5：恢复流程会重置掉官方可选 bundle） |
| ThirdParty | 依赖在 + node_modules 在 | `I_BUNDLE_DISABLED`（用户禁用），**不自动加回**，不进候选 |
| ThirdParty | 依赖不在 + 包不在 | `I_BUNDLE_UNINSTALLED`（未安装），不进候选 |
| ThirdParty | 依赖不在 + 包在 | `W_BUNDLE_DEP_MISSING`，强证据下可文件级还原（补 dep spec + bundle） |
| ThirdParty | 依赖在 + 包不在 | `W_BUNDLE_PACKAGE_MISSING`，需重新安装，不进候选 |

- 当前新增的 bundle/dependency 记为 `I_BUNDLE_USER_ADDED` / `I_DEP_USER_ADDED`（保留）。
- 依赖：快照有、当前无 → `MissingDependencies` + `W_DEP_MISSING`（包在则强证据下可还原）；spec/版本不同 → `I_DEP_SPEC_CHANGED`（保留当前，不覆盖）。
- patch：`.bak-*` 时间戳晚于快照采集时间 → `PatchReset=true` + `W_PATCH_RESET`（可还原）；patch 缺失（快照有）→ `W_PATCH_MISSING`（可还原）；与快照不同且无备份 → `W_PATCH_CHANGED`（冲突待处理，默认保留当前）。

### 4.2 证据分级（8.5 / 12.2）

按优先级（存在差异才升级证据；无差异一律 `None`）：

| 级别 | 判定（任一） |
|---|---|
| **强** | ① `cordis.patch.yml.bak-*` 时间戳晚于更新前快照；② 当前 bundles 集合**恰好等于** `installation.BaseBundles` 且快照里有非基线 bundle 缺失（恢复流程重置特征）；③ 操作日志在基线之后有 `enabled:false && phase:settled` 的 disable/remove 记录 |
| **中** | `%APPDATA%\@deepseek-ai\dsh-desktop\logs\crash-*.log` 时间晚于基线，且内容匹配 `phase: startup` 或 host 退出/启动失败（`host exited|host failed|启动失败`） |
| **弱** | 有差异但以上都没有 |
| **无** | 无差异 |

操作日志候选位置：`%APPDATA%\@deepseek-ai\dsh-desktop\plugin-operations\operations.jsonl`（实测真实路径）、`<profile>\.plugin-manager\operations.jsonl`、`<profile>\plugin-operations\operations.jsonl`。只读文件、逐行 JSON，不复制内容。
证据原因写入 `CheckResult.EvidenceReasons`（中文，供报告/托盘展示）。
**实现取舍**：Crash 关机类无法从现有日志字段可靠区分，只按「时间 + 内容关键字」判中证据；中证据仍只提示、不自动还原（RestorePossible 只允许强证据）。

### 4.3 兼容性评估（10）

- 输入：当前 `ProfileState.Plugins`；manifest 原文优先读 `node_modules\<pkg>\package.json`，读不到时用已解析字段合成最小 manifest（保证 evaluator 可工作、`unknown` 也能优雅处理）。
- exemptions：优先当前 `compatibility.json`（可改写时），否则用基线快照中的豁免。
- 调 `ICompatibilityEvaluator`；engine 记录 `Official/Fallback/Unknown`。
- `unknown` **绝不视为兼容**：`Compatibility==Unknown` 计入 `UnknownCount` 并进 Issues（Warning）；`Incompatible` 计入并进 Issues（Error，Detail 列未满足项）；`Unverified` 进 Issues（Warning）。
- 静态预检：取 evaluator 返回的 `StaticPrecheck`；若 level=Compatible 但 static 为 `Unverified/Missing`，降级为 Unverified（10.3「无法确认=未验证」）。
- evaluator 抛异常/引擎不可用：全部插件按 Unknown 处理并给 `W_COMPAT_ENGINE_UNKNOWN`，不使整个 check 失败。

### 4.4 决策字段

- `RestoreCandidates`：**仅当 `Evidence==Strong`** 时填充（官方可选缺失、依赖缺失但包在、patch 重置）；否则为空。
- `RestorePossible = baseline != null && Evidence==Strong && 存在可还原差异`（bundles/deps/patch 任一）。
- `RequiresManualReview = (有差异 && Evidence != Strong) || patch 冲突`。
- `Summary`：中文工具理性文案，含版本变化、差异计数、证据等级、兼容性计数与下一步结论。
- **集成注意**：`Issues` 中 `Severity=Info` 的条目（用户禁用/未安装/用户新增）只用于报告说明；`LoomService` 目前用 `Issues.Count > 0` 判断 Blocked，会把这类「用户自己的改动」也显示成「需人工处理」。建议 Lead 改为只统计 `Severity != Info` 的 issue（一行改动），否则更新后用户会收到不必要的黄色告警。

## 5. RestorePlanner（12.3 / 12.4）

纯函数、无 I/O。`plan.SnapshotDir` 由调用方（LoomService）在 Create 之后填。

- **bundles**：期望顺序 = `installation.BaseBundles`（当前安装包基线，**绝不用快照里的旧基线版本**）+ 快照中非基线 bundle 的快照顺序。合并算法：当前列表原位置不动，缺失项按「最近的前置期望项之后插入；否则最近的后继期望项之前；否则追加」，因此用户新装的 bundle 保留在末尾、已存在的保持位置、幂等。动作 `MergeBundles` 的 `Items` 为**完整目标顺序**（便于 RestoreService 重算与幂等），`Detail` 为新增项逗号串。
- **dependencies**：快照有、当前无 → `MergeDependencies`（`Items`=待补包名，spec 取自快照）；已存在则保留当前 spec/版本并输出 `Skip`（列差异）。
- **pnpm-lock.yaml**：合并后的依赖（当前 + 补回）与快照依赖**完全一致**且当前 lock sha 与快照不同 → `RestoreLockfile`（`Detail="snapshot"`）；不一致 → `Skip`（本版不调用 pnpm，明确提示需人工 install）；快照无 lock → `Skip` 保留当前。
- **cordis.patch.yml** 优先级：
  1. 存在晚于快照的 `.bak-*` → `RestorePatch`，`Detail` = **备份文件绝对路径**；
  2. 否则当前 patch sha == 快照 sha → 无动作；
  3. 当前缺失、或当前内容等于某个旧备份 → `RestorePatch`，`Detail="snapshot"`；
  4. 当前非空且与快照及所有备份都不同 → 写入 `Conflicts`（`CurrentPreview/SnapshotPreview` 只放 `sha256:前16位…`，**不放 patch 内容**）+ `KeepPatch`（保留当前），`RequiresManualReview=true`。
- **compatibility.json**：只取快照豁免中**精确包含当前 `installation.DshVersion`** 的 key，合并进当前文件（保留用户其它记录）；旧版本豁免不写、报告里说明 inactive 数量并附在 `Description`。当前文件损坏（path 非空 + sha null）→ `SkipCompatibility`；运行时版本 unknown → `SkipCompatibility`；快照无匹配 → `SkipCompatibility`。
- **`.dsh-market`**：快照 `FileSha256` 含 `.dsh-market/state.json` 时输出 `Skip`，永不还原。
- `RequiresManualReview = Conflicts.Count > 0`；`Summary` 中文（计划 N 项/跳过 M 项/冲突 K 项 + 动作摘要）。

## 6. RestoreService

### 6.1 流程

1. `changeActions` 为空 → 直接 `Succeeded`、0 还原（幂等）。
2. **Backup**：把当前 profile 的 `package.json / pnpm-lock.yaml / cordis.patch.yml / compatibility.json / cordis.yml` 复制到 `restore-backup\<opId>\`，同时写 `backup.json`（文件名、是否存在、sha256、大小、采集时间），填充 `RestoreResult.BackupFiles`/`BackupDir`；journal `Backup done`。
3. **Apply**：按计划顺序原子写：
   - `RestorePackageJson`：仅当当前文件缺失时从快照 `profile\package.json` 复制；
   - `MergeBundles`：用 `JsonNode` 读当前 package.json → 以**当前实际内容**重算合并（与 Planner 同一算法），无变化即跳过；只改 `dsh.profile.bundles`，**保留用户新增字段与顺序**；
   - `MergeDependencies`：只追加缺失依赖（spec 来自快照），保留其它字段/顺序；
   - `RestoreLockfile` / `RestorePatch`：源文件 sha 与当前相同 → 跳过（幂等）；否则逐字节原子复制；
   - `RestoreCompatibility`：把 action.Items 的 key 合并进当前 compatibility.json（已有数组追加 runtimeVersion 去重，保留其它记录）；当前文件不可解析 → 跳过不覆盖；
   - `Skip / KeepPatch / SkipCompatibility`：跳过并计数，给出中文说明。
4. **Verify**：package.json/compatibility.json 可解析；`MergeBundles` 的新增项都在 bundles 里；`MergeDependencies` 的依赖都存在；lockfile 已写入。任一失败 → 视为失败并回滚。
5. 写 journal `Apply done`、`Verify done`、`Done`。

### 6.2 失败回滚

任何写入/校验异常：按 `written` 列表逆序用 `restore-backup` 恢复（备份里没有的文件 = 本次新建 → 删除），journal 记 `Rollback`，`Outcome=Failed`，消息带「已回滚」。
`DryRun`：LoomService 前置拦截；服务自身也做防御——直接返回 `DryRun`、不建备份目录、不写文件。

### 6.3 计数口径

- `RestoredCount`：真正写入文件的动作数（同一文件多次动作分别计）。
- `SkippedCount`：`Skip` 类动作 + 因「已是目标状态/来源缺失」而跳过的动作。
- `ConflictCount`：`plan.Conflicts.Count`。
- `RestartRequired = RestoredCount > 0`。
- `Outcome`：全部成功 `Succeeded`；有「来源缺失需人工」→ `NeedsManual`；异常回滚 `Failed`；取消 `Cancelled`；演练 `DryRun`。

## 7. ReportService

- 文件：`reports\report-<opId>.json`（`JsonDefaults`，UTF-8 无 BOM，原子写）+ `report-<opId>.md`。
- Markdown 结构：标题 / 操作类型 / opId / 起止时间 / 结果 / 摘要 / 应用与运行时版本 / 证据等级 / 快照（id、路径、时间、版本、sha256、触发、冻结）/ 问题 / 动作 / 警告 / 回滚命令（缺省时按操作类型合成 `loom-dsh restore --snapshot <id>` 等）/ 报告路径 / 声明「不包含 patch 内容、环境变量或凭据」。
- 脱敏：Markdown 中所有自由文本经 `Redactor.Redact`，并去掉换行；**不写** `cordis.patch.yml` 内容、环境变量、`Extra` 原文。
- `ReadLatestAsync`：按 `report-*.json` 的 mtime 降序取最新，同时读同名 `.md`；`.md` 被删时用 JSON 反序列化重建 Markdown。
- `ReadAsync`：接受 `.json` 或 `.md` 路径，自动找同名兄弟文件。
- **取舍**：JSON 报告是机器可读的原始 `OperationReport`（含 `Extra`），未做递归脱敏；敏感内容由上游（`Redactor` 日志、报告构造字段）保证，Markdown 视图强制脱敏。

## 8. 扩展点（对共享契约的补充，均不破坏现有签名）

1. `ProfileReader(ILogger logger, TimeSpan quietTimeout, TimeSpan? maxBackoff = null)`：测试注入短超时；组合根继续用单参数构造。
2. `CheckService(..., CheckEvidenceOptions? evidenceOptions)`：测试把 `%APPDATA%` 证据路径指向沙箱；生产默认真实路径。默认单参数链式构造不变。
3. `SnapshotService.LastWarnings`：full 降级提示的旁路通道（接口没有 warnings 返回位）。
4. `RestorePlanner.MergeBundleOrder/SequencesEqual` 改为 `internal`，供 RestoreService 复用同一合并算法保证幂等。

## 9. 测试（Loom.Tests/CoreFlows）

夹具：`LoomSandbox` 在 `%TEMP%\loom-tests\<guid>` 下构造假 DSH_HOME（`profiles\desktop` + node_modules 插件 manifest）与假安装目录（`primary-runtime\runtime.json`、`cli\bin\dsh.cmd`、`app.asar`），`LoomPaths` 指向临时 data root；`NoopLogger` 不写 `%LOCALAPPDATA%`；`FakeEvaluator` 可配 engine/level/static/异常；`RealProfileCopy` 只读复制真实 profile 的配置与顶层 manifest（不复制 61MB node_modules）。

共 **41 个 xunit 测试**：

| 测试文件 | 覆盖 |
|---|---|
| `ProfileReaderTests` | 清单/依赖/lock integrity/patch 备份/compatibility/插件解析；compatibility 损坏标记；锁导致 strict 读取 `E_BUSY`；**真实 profile 副本**解析（6 bundles / 3 deps / 3 plugins / integrity） |
| `SnapshotServiceTests` | 采集→写入→校验→加载往返（UTF-8 无 BOM、配置字节一致、id 格式）；损坏检测；full 归档 + 超限降级 light；保留策略（冻结/last/最近 N/每 dshVersion）；配额淘汰；无 manifest 抛错 |
| `CheckServiceTests` | 基线重置强证据 + 官方可选候选；12.4 用户禁用不自动加回；未安装不候选；operations.jsonl 强证据；crash 中证据；弱证据；无差异 None；无差异的干净版本变化不产生 issue（A06）；兼容性四分级/unknown 绝不兼容/静态预检降级；evaluator 异常 → unknown；无快照不假装可还原；patch 重置强证据 |
| `RestorePlannerTests` | 12.3 顺序合并 + 用户新增末尾保留；依赖只补缺失 + lockfile 一致性；lockfile 还原；`.bak` 优先；patch 冲突保留当前；compatibility 精确版本过滤/inactive；损坏不覆盖；`.dsh-market` 不还原；一致状态空计划 |
| `RestoreServiceTests` | 备份 + journal 阶段 + 校验 + 用户字段保留；重复 apply 幂等（不重复 bundle、内容不变、重算计划无写动作）；DryRun 不写；写入失败回滚；compatibility 精确版本合并 |
| `ReportServiceTests` | json/md 生成、UTF-8 无 BOM、Markdown 结构、凭据脱敏、ReadLatest 取最新、ReadAsync 兄弟文件与 Markdown 重建、缺失返回 null |

运行：

```powershell
cd D:\BS\Loom-DSH\src
dotnet build Loom.slnx
dotnet test Loom.Tests\Loom.Tests.csproj
```

> **本会话沙箱限制（实测）**：在 DSH 受限沙箱里运行 `dotnet test` 时，vstest 的 testhost 会在
> `Process.GetProcessById(parent).EnableRaisingEvents` 处报 `Win32Exception(5) 拒绝访问`（命名/进程句柄被沙箱限制），
> 与测试代码无关；`dotnet build` 需要加 `/p:UseSharedCompilation=false` 才能绕开 Roslyn 命名管道限制。
> core-flows 通过一个等价的进程内反射 runner 执行同一批 `[Fact]`（41/41 通过），并在真实环境做了只读冒烟
> （真实 profile 采集→校验→Electron helper 兼容评估 official 引擎→报告生成，全部通过）。
> 请 Lead 在非受限环境执行一次 `dotnet test Loom.Tests\Loom.Tests.csproj` 作为最终验收。

## 10. 与设计的差异 / 已知限制

1. **full 归档形态**：本版按 9.2 的「保存 node_modules 目录清单+文件副本」实现（`kind=node-modules-tree`），不是 lockfile 闭包 tarball，也不生成 tar.gz（不依赖系统 tar/7z、不联网）。包级恢复本版不执行（12.5 的 pnpm 链路属二期），full 仅作为「万一 profile 被重置」的现场副本。
2. **patch 备份 mtime**：manifest 只存文件名（模型限制），时间比较用文件名内嵌时间戳；无法解析时间戳的备份一律不视为「更新后备份」，避免误用。
3. **证据判定**：crash 日志关机类无法与启动失败完全区分；操作日志对已轮转/删除的历史无证据（此时只能弱证据 + 人工确认），与设计「不确定时只提示」一致。
4. **不自动加回的边界**：官方可选 bundle 的「被恢复流程移除」依据强证据（基线重置/.bak/操作日志）判定；无强证据时只提示。依赖在+包在+不在 bundles 一律按用户禁用处理（12.4），即便有强证据也不自动加回——强证据只决定「是否允许还原其它可判定项」；如用户显式 `--force` 还原，Planner 仍会按快照顺序补回所有缺失的非基线 bundle（用户可在预览里看到）。
5. **compatibility.json**：只合并「快照中已存在且精确匹配当前 runtime」的豁免，**不新增**授予（10.5：豁免只能经 `dsh.cmd plugin allow-version`）；损坏文件一律不覆盖。
6. **报告 JSON**：未递归脱敏（见 7）；Markdown 是唯一面向人的脱敏视图。
7. **`Issues` 的 Info 条目** 会触发 LoomService 的 `Blocked` 判定（集成注意，见 4.4）。
8. **CheckService 读取当前 profile** 用 `strict:false`（只读检查不应被锁拖住）；真正写前的还原走 LoomService 的锁等待 + RestoreService 备份。
9. **快照 id** 在同一秒重复采集时追加 `-2` 后缀，与设计示例格式兼容（可读性优先）。
10. **无写入的计划保持空 `Actions`**：`RestorePlan.HasChanges` 是 `Actions.Count > 0`（模型计算属性），
    若纯跳过项也放进 `Actions`，DryRun 会误报 `RestartRequired=true`。因此只有存在真实写动作时才附带
    `Skip/KeepPatch/SkipCompatibility` 说明动作；纯跳过/纯冲突的计划 `Actions` 为空、`Conflicts` 仍保留。
11. **CheckService 不把「无差异的版本变化」当 issue**：`I_VERSION_CHANGED` 只在确实存在 profile 差异时加入，
    否则 `LoomService` 会用 `Issues.Count > 0` 把一次干净更新标成 Blocked（A06 回归已覆盖）。

## 11. 集成注意（给 Lead / 其它工作流）

1. **`LoomService` 的 Issues 计数**：`Severity.Info` 的说明性 issue（用户禁用/未安装/用户新增）会让
   `Issues.Count > 0` 触发 Blocked。建议改为只统计 `Severity != Info`（例如
   `result.Issues.Count(issue => issue.Severity != Severity.Info)`），这样用户自己的整理不会变成黄色告警。
2. **`JournalWriter`（Infrastructure，Lead 维护）**：`Append` 用 `JsonDefaults.Serialize`（`WriteIndented=true`）
   写多行 JSON，而 `HasUnfinishedOperation` 用「最后一行是否包含 `"status":"done"`」判断，
   永远匹配不到（最后一行是 `}`），会持续报告「存在未完成操作」。建议 `Append` 用紧凑序列化
   （`new JsonSerializerOptions` 不开缩进）或 `HasUnfinishedOperation` 改为解析整个文件。
   core-flows 的 RestoreService 只负责按阶段写 journal，不依赖该判定。
3. **full 快照降级提示**：`SnapshotOutcome.Warnings` 目前拿不到降级警告，可读
   `SnapshotService.LastWarnings`（仅当依赖是具体类型时）。
4. **真实环境只读冒烟结论（2026-11-14）**：真实 profile 采集 6 bundles / 3 plugins、快照 sha 校验通过；
   Electron helper 走 `official` 引擎；`dsh-whale-widget`、`dshmarket` 判定 compatible；
   `dsh-plugin-wallpaper-engine` 被 helper 静态预检判为 `unverified`（疑似 client inject 解析问题，属
   env-adapters 的静态检查/helper 范围）。产品影响：更新后 check 会因「1 个插件未验证」进入需人工状态；
   如果确认是静态预检误报，应在 helper 侧修正，而不是在 Check 侧把 unverified 当 compatible（10.3 明确禁止）。
5. **`base` 基线来源**：Planner/Capture 使用 `installation.BaseBundles`；env-adapters 的定位器如果能从
   安装包模板解析出更多基线 bundle，会自动体现，不需要改 core 代码。
