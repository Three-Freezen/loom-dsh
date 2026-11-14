using System.Text.Json;
using System.Text.Json.Nodes;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Recovery;

/// <summary>
/// 包级恢复计划器（设计 12.5 / 12.6 / 9.4）。**纯函数**：
/// 只根据“快照内容 + 当前 profile 状态 + 运行时开关”计算有序步骤、是否需要 DSH 完全退出、
/// 前置条件与失败分类；不读文件、不探测进程、不结束进程、不调用 CLI。
/// 真正执行由 <see cref="PackageRestoreService"/> / <see cref="OfflineRollbackService"/> 负责。
/// </summary>
public static class PackageRestorePlanner
{
    /// <summary>离线安装参数（dsh plugin --profile &lt;name&gt; 之后的部分）。</summary>
    public static readonly IReadOnlyList<string> OfflineInstallArgs =
        new[] { "install", "--offline", "--frozen-lockfile" };

    /// <summary>联网安装参数（只有用户明确同意后才追加该步骤，设计 12.5 第 3 步）。</summary>
    public static readonly IReadOnlyList<string> OnlineInstallArgs =
        new[] { "install", "--frozen-lockfile" };

    /// <summary>快照恢复时必须保留的“用户新增字段”排除项（依赖段和 dsh 段由快照决定）。</summary>
    private static readonly HashSet<string> PreservedDenyList = new(StringComparer.OrdinalIgnoreCase)
    {
        "dependencies",
        "devDependencies",
        "optionalDependencies",
        "peerDependencies",
        "bundledDependencies",
        "bundleDependencies",
        "dsh",
    };

    // ------------------------------------------------------------------ plan

    /// <summary>
    /// 生成包级恢复计划。步骤顺序：备份 → 写 package.json → 写 pnpm-lock.yaml →
    /// install --offline --frozen-lockfile →（可选）联网 install → 校验。
    /// </summary>
    public static PackageRestorePlan Create(PackageRestorePlanInput input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));

        var snapshot = input.Snapshot ?? new SnapshotManifest();
        var current = input.Current ?? new ProfileState();
        var installation = input.Installation ?? new DshInstallation();
        var options = input.Options ?? new PackageRestoreOptions();
        var operationId = string.IsNullOrWhiteSpace(input.OperationId)
            ? LoomPaths.NewOperationId("recovery")
            : input.OperationId;

        var plan = new PackageRestorePlan
        {
            OperationId = operationId,
            SnapshotId = snapshot.Id,
            SnapshotDir = input.SnapshotDir ?? "",
            ProfileDir = installation.ProfileDir ?? "",
            RequiresFullExit = true,
            OfflineOnly = !options.AllowNetworkFallback,
        };

        // ---------------------------------------------------------- 期望状态（来自快照）
        var expectedBundles = snapshot.Profile.Bundles
            .Select(bundle => bundle.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        var expectedDependencies = snapshot.Profile.Dependencies.Keys.ToList();
        var expectedVersions = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in snapshot.Profile.Dependencies)
        {
            expectedVersions[pair.Key] = string.IsNullOrWhiteSpace(pair.Value.Resolved) ? null : pair.Value.Resolved;
        }

        plan.ExpectedBundles = expectedBundles;
        plan.ExpectedDependencies = expectedDependencies;
        plan.ExpectedDependencyVersions = expectedVersions;

        // ---------------------------------------------------------- 恢复前状态（写入回滚点）
        plan.PreviousBundles = current.Bundles.ToList();
        plan.PreviousDependencies = current.Dependencies.Keys.ToList();
        plan.PreviousPackageJsonSha256 = CurrentPackageJsonSha(current);
        plan.PreviousLockfileSha256 = string.IsNullOrWhiteSpace(current.LockfileSha256)
            ? null
            : current.LockfileSha256;

        // ---------------------------------------------------------- 合并出目标 package.json
        var mergedPackageJson = MergePackageJson(input.SnapshotPackageJson, current.PackageJsonText, out var preservedKeys);
        var mergedSha = ShaOrNull(mergedPackageJson);
        var snapshotLockfileSha = ShaOrNull(input.SnapshotLockfile);
        if (!string.IsNullOrWhiteSpace(mergedPackageJson) && preservedKeys.Count > 0)
        {
            plan.Notes.Add("保留当前 package.json 中快照没有的顶层字段：" + string.Join("，", preservedKeys));
        }

        // ---------------------------------------------------------- 步骤
        var steps = new List<PackageRestoreStep>();
        var backupDirHint = Path.Combine("restore-backup", LoomPaths.SanitizeFileName(operationId));
        steps.Add(new PackageRestoreStep
        {
            Order = steps.Count + 1,
            Kind = PackageRestoreStepKind.BackupProfile,
            TargetFile = "*",
            Description = $"写前备份：把 {DshDefaults.ManifestFile} / {DshDefaults.LockfileFile} 复制到 " +
                          $"{backupDirHint}\\ 并记录 sha256（设计 9.4，回滚只回滚本 op）",
            BeforeSha256 = plan.PreviousPackageJsonSha256,
            RequiresFullExit = false,
        });

        var packageJsonSkip = !options.Force
                              && mergedSha is not null
                              && string.Equals(plan.PreviousPackageJsonSha256, mergedSha, StringComparison.OrdinalIgnoreCase);
        steps.Add(new PackageRestoreStep
        {
            Order = steps.Count + 1,
            Kind = PackageRestoreStepKind.WritePackageJson,
            TargetFile = DshDefaults.ManifestFile,
            Description = packageJsonSkip
                ? "当前 package.json 已与快照合并结果一致，跳过写入（幂等）"
                : "写入快照的 package.json（保留用户新增的顶层字段；不改安装目录、不写 app.asar）",
            BeforeSha256 = plan.PreviousPackageJsonSha256,
            AfterSha256 = mergedSha,
            Content = mergedPackageJson,
            Skip = packageJsonSkip,
            SkipReason = packageJsonSkip ? "package.json 已一致" : null,
            RequiresFullExit = false,
        });

        var lockfileSkip = !options.Force
                           && snapshotLockfileSha is not null
                           && string.Equals(plan.PreviousLockfileSha256, snapshotLockfileSha, StringComparison.OrdinalIgnoreCase);
        steps.Add(new PackageRestoreStep
        {
            Order = steps.Count + 1,
            Kind = PackageRestoreStepKind.WriteLockfile,
            TargetFile = DshDefaults.LockfileFile,
            Description = lockfileSkip
                ? "当前 pnpm-lock.yaml 与快照一致，跳过写入（幂等）"
                : "写入快照的 pnpm-lock.yaml（恢复可复现的 lockfile，供 --frozen-lockfile 使用）",
            BeforeSha256 = plan.PreviousLockfileSha256,
            AfterSha256 = snapshotLockfileSha,
            Content = input.SnapshotLockfile,
            Skip = lockfileSkip,
            SkipReason = lockfileSkip ? "pnpm-lock.yaml 已一致" : null,
            RequiresFullExit = false,
        });

        steps.Add(new PackageRestoreStep
        {
            Order = steps.Count + 1,
            Kind = PackageRestoreStepKind.InstallOffline,
            Args = OfflineInstallArgs.ToList(),
            DisplayCommand = BuildDisplayCommand(installation, OfflineInstallArgs),
            Description = "离线安装：dsh plugin install --offline --frozen-lockfile（需要 store 有缓存；不使用 file:<tgz>）",
            ExitCode = null,
            RequiresFullExit = true,
            TimeoutSeconds = options.InstallTimeoutSeconds,
        });

        if (options.AllowNetworkFallback)
        {
            steps.Add(new PackageRestoreStep
            {
                Order = steps.Count + 1,
                Kind = PackageRestoreStepKind.InstallOnline,
                Args = OnlineInstallArgs.ToList(),
                DisplayCommand = BuildDisplayCommand(installation, OnlineInstallArgs),
                Description = "用户已同意联网重试：dsh plugin install --frozen-lockfile（仅在离线失败后人工确认路径中启用）",
                RequiresFullExit = true,
                TimeoutSeconds = options.InstallTimeoutSeconds,
            });
        }

        steps.Add(new PackageRestoreStep
        {
            Order = steps.Count + 1,
            Kind = PackageRestoreStepKind.Verify,
            Description = "安装后校验：package.json 可解析、依赖版本存在、bundles 齐全、lockfile 与快照一致",
            RequiresFullExit = false,
        });

        plan.Steps = steps;

        // ---------------------------------------------------------- 前置条件
        BuildPreconditions(plan, input, mergedPackageJson, input.SnapshotLockfile, backupDirHint);
        var blocking = plan.Preconditions.FirstOrDefault(precondition => !precondition.Satisfied);
        if (blocking is not null)
        {
            plan.IsBlocked = true;
            plan.BlockingFailure = FailureForPrecondition(blocking);
        }

        plan.Summary = BuildSummary(plan, options, blocking);
        return plan;
    }

    // ------------------------------------------------------------------ package.json merge

    /// <summary>
    /// 合并要写回的 package.json：以快照内容为基线，把当前 profile 中“快照没有且不属于依赖/dsh 段”的
    /// 顶层字段原样保留（设计 12.5“保留用户新增字段”）。快照不是合法 JSON 时返回 null（计划会阻塞）。
    /// </summary>
    public static string? MergePackageJson(string? snapshotJson, string? currentJson) =>
        MergePackageJson(snapshotJson, currentJson, out _);

    private static string? MergePackageJson(string? snapshotJson, string? currentJson, out List<string> preservedKeys)
    {
        preservedKeys = new List<string>();
        if (string.IsNullOrWhiteSpace(snapshotJson)) return null;

        JsonObject? snapshotRoot;
        try
        {
            snapshotRoot = JsonNode.Parse(snapshotJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (snapshotRoot is null) return null;

        var merged = (JsonObject)snapshotRoot.DeepClone();
        JsonObject? currentRoot = null;
        if (!string.IsNullOrWhiteSpace(currentJson))
        {
            try
            {
                currentRoot = JsonNode.Parse(currentJson) as JsonObject;
            }
            catch (JsonException)
            {
                // 当前 package.json 损坏时按纯快照恢复处理（它本来就要被覆盖）。
                currentRoot = null;
            }
        }

        if (currentRoot is not null)
        {
            foreach (var pair in currentRoot)
            {
                if (merged.ContainsKey(pair.Key)) continue;
                if (PreservedDenyList.Contains(pair.Key)) continue;
                merged[pair.Key] = pair.Value?.DeepClone();
                preservedKeys.Add(pair.Key);
            }
        }

        return JsonSerializer.Serialize(merged, JsonDefaults.Options) + "\n";
    }

    // ------------------------------------------------------------------ preconditions

    private static void BuildPreconditions(
        PackageRestorePlan plan,
        PackageRestorePlanInput input,
        string? mergedPackageJson,
        string? snapshotLockfile,
        string backupDirHint)
    {
        var installation = input.Installation ?? new DshInstallation();
        var options = input.Options ?? new PackageRestoreOptions();
        var snapshot = input.Snapshot ?? new SnapshotManifest();
        var operationId = plan.OperationId;
        var displayOnline = BuildDisplayCommand(installation, OnlineInstallArgs);

        Add(
            PackageRestorePreconditionCode.ProfileReadable,
            !string.IsNullOrWhiteSpace(installation.ProfileDir),
            string.IsNullOrWhiteSpace(installation.ProfileDir)
                ? "profile 目录未知"
                : "profile 目录：" + installation.ProfileDir,
            "无法定位 profile：请重新定位 DSH 安装或在 config.json 指定 profileDir。");

        Add(
            PackageRestorePreconditionCode.SnapshotPackageJson,
            !string.IsNullOrWhiteSpace(input.SnapshotPackageJson) && mergedPackageJson is not null,
            string.IsNullOrWhiteSpace(input.SnapshotPackageJson)
                ? "快照 profile\\package.json 缺失"
                : mergedPackageJson is null
                    ? "快照 package.json 不是合法 JSON 对象"
                    : "快照 package.json 可解析（" + Short(input.SnapshotPackageJson) + "）",
            "快照缺少可解析的 package.json：请重新采集快照或人工恢复该文件后重试。");

        Add(
            PackageRestorePreconditionCode.SnapshotLockfile,
            !string.IsNullOrWhiteSpace(snapshotLockfile),
            string.IsNullOrWhiteSpace(snapshotLockfile)
                ? "快照 profile\\pnpm-lock.yaml 缺失（--frozen-lockfile 无法执行）"
                : "快照 pnpm-lock.yaml 存在（" + Short(snapshotLockfile) + "）",
            "快照缺少 pnpm-lock.yaml：无法离线 --frozen-lockfile；请重新采集快照，或人工执行 install 后重试。");

        Add(
            PackageRestorePreconditionCode.CliAvailable,
            !string.IsNullOrWhiteSpace(installation.CliPath) || !string.IsNullOrWhiteSpace(installation.InstallDir),
            string.IsNullOrWhiteSpace(installation.CliPath)
                ? "未记录 dsh.cmd 路径（将回退到安装目录探测）"
                : "dsh.cmd：" + installation.CliPath,
            "找不到 DSH CLI：请在设置中重新定位安装目录。");

        var fileSpecs = snapshot.Profile.Dependencies
            .Where(pair => pair.Value.Spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key + "=" + pair.Value.Spec)
            .ToList();
        Add(
            PackageRestorePreconditionCode.NoFileSpecs,
            fileSpecs.Count == 0,
            fileSpecs.Count == 0
                ? "快照依赖没有 file: 归档 spec"
                : "快照依赖含 file: 归档 spec：" + string.Join("，", fileSpecs),
            "设计 12.5 不允许残留 file:<tgz>：请人工把依赖改回 registry spec 并重建 lockfile 后再恢复。");

        Add(
            PackageRestorePreconditionCode.DshFullyExited,
            !options.AppRunning,
            options.AppRunning ? "DSH 仍在运行（包级恢复要求完全退出）" : "DSH 未运行",
            "请完全退出 DeepSeek Harness（含托盘/后台进程）后重试。Loom 不会结束任何 DSH 进程。");

        Add(
            PackageRestorePreconditionCode.ProfileNotLocked,
            !options.ProfileLocked,
            options.ProfileLocked ? "profile 正被 DSH / 插件管理器写入" : "profile 写锁空闲",
            "等待插件管理器结束，或删除超过 120 秒的残留 package.json.lock 后重试。");

        Add(
            PackageRestorePreconditionCode.OfflineCache,
            options.OfflineCacheAvailable,
            options.OfflineCacheAvailable
                ? "假定 pnpm store 已缓存 lockfile 所需包（离线安装）"
                : "pnpm store 缺缓存：离线安装必然失败",
            "离线缺缓存，已直接失败（不自动联网、不用 file:<tgz>）。人工入口：" +
            $"1) 确认 store/磁盘后重试；2) 经你同意后联网执行 {displayOnline}；" +
            $"3) 回滚本操作：loom-dsh recovery rollback {operationId}（备份 {backupDirHint}）。");

        var diskUnknown = options.AvailableBytes is null || options.EstimatedBytes <= 0;
        var diskOk = diskUnknown || options.AvailableBytes >= options.EstimatedBytes;
        Add(
            PackageRestorePreconditionCode.DiskSpace,
            diskOk,
            diskUnknown
                ? "未提供磁盘估算，跳过空间校验"
                : $"可用 {options.AvailableBytes} 字节 / 预计 {options.EstimatedBytes} 字节",
            "磁盘空间不足：清理后重试。");

        void Add(PackageRestorePreconditionCode code, bool satisfied, string detail, string? manualAction)
        {
            plan.Preconditions.Add(new PackageRestorePrecondition
            {
                Code = code,
                Satisfied = satisfied,
                Detail = detail,
                ManualAction = satisfied ? null : manualAction,
            });
        }
    }

    private static PackageRestoreFailure FailureForPrecondition(PackageRestorePrecondition precondition)
    {
        var kind = precondition.Code switch
        {
            PackageRestorePreconditionCode.ProfileReadable => PackageRestoreFailureKind.Precondition,
            PackageRestorePreconditionCode.SnapshotPackageJson => PackageRestoreFailureKind.SnapshotInputMissing,
            PackageRestorePreconditionCode.SnapshotLockfile => PackageRestoreFailureKind.SnapshotInputMissing,
            PackageRestorePreconditionCode.CliAvailable => PackageRestoreFailureKind.CliMissing,
            PackageRestorePreconditionCode.NoFileSpecs => PackageRestoreFailureKind.UnsupportedSpec,
            PackageRestorePreconditionCode.DshFullyExited => PackageRestoreFailureKind.ProfileBusy,
            PackageRestorePreconditionCode.ProfileNotLocked => PackageRestoreFailureKind.ProfileBusy,
            PackageRestorePreconditionCode.OfflineCache => PackageRestoreFailureKind.OfflineCacheMissing,
            PackageRestorePreconditionCode.DiskSpace => PackageRestoreFailureKind.Disk,
            _ => PackageRestoreFailureKind.Precondition,
        };

        var message = kind switch
        {
            PackageRestoreFailureKind.ProfileBusy => "DSH 未完全退出或 profile 仍被占用：包级恢复未执行（不会结束任何进程）。",
            PackageRestoreFailureKind.OfflineCacheMissing => "离线缓存缺失：包级恢复直接失败，不自动联网、不使用 file:<tgz>。",
            PackageRestoreFailureKind.SnapshotInputMissing => "快照缺少 package.json / pnpm-lock.yaml，无法进行包级恢复。",
            PackageRestoreFailureKind.UnsupportedSpec => "快照依赖含 file: 归档 spec，二期拒绝自动恢复（不允许残留 file:）。",
            PackageRestoreFailureKind.CliMissing => "找不到 DSH CLI，无法执行离线安装。",
            PackageRestoreFailureKind.Disk => "磁盘空间不足，包级恢复未执行。",
            _ => "前置条件不满足，包级恢复未执行。",
        };

        return new PackageRestoreFailure
        {
            Kind = kind,
            Message = message,
            Detail = precondition.Detail,
            ManualAction = precondition.ManualAction,
            CanRetryWithNetwork = kind == PackageRestoreFailureKind.OfflineCacheMissing,
        };
    }

    private static string BuildSummary(
        PackageRestorePlan plan,
        PackageRestoreOptions options,
        PackageRestorePrecondition? blocking)
    {
        var parts = new List<string>
        {
            "包级恢复：" + string.Join(" → ", plan.OrderedSteps
                .Where(step => step.Kind != PackageRestoreStepKind.BackupProfile)
                .Select(step => step.Kind switch
                {
                    PackageRestoreStepKind.WritePackageJson => step.Skip ? "package.json（已一致）" : "package.json",
                    PackageRestoreStepKind.WriteLockfile => step.Skip ? "pnpm-lock.yaml（已一致）" : "pnpm-lock.yaml",
                    PackageRestoreStepKind.InstallOffline => "install --offline --frozen-lockfile",
                    PackageRestoreStepKind.InstallOnline => "install --frozen-lockfile（联网，已授权）",
                    PackageRestoreStepKind.Verify => "校验",
                    _ => step.Kind.ToString(),
                })),
            plan.RequiresFullExit
                ? options.AppRunning ? "需要 DSH 完全退出（当前仍在运行）" : "需要 DSH 完全退出（当前已退出）"
                : "无需退出 DSH",
            plan.OfflineOnly ? "离线优先，不联网" : "允许用户在失败后联网重试",
        };

        if (blocking is not null)
        {
            parts.Add("阻塞：" + blocking.Code + "（" + blocking.Detail + "）");
        }

        return string.Join("；", parts) + "。";
    }

    // ------------------------------------------------------------------ CLI failure classification

    /// <summary>把 CLI 调用结果映射为失败分类（纯函数，便于全分支单测）。</summary>
    public static PackageRestoreFailure ClassifyCliFailure(CliCallResult result, string? operationId = null)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        return ClassifyInstallFailure(
            result.ExitCode,
            result.TimedOut,
            result.StdOut,
            result.StdErr,
            operationId);
    }

    /// <summary>
    /// 按退出码 / 超时 / 输出特征分类安装失败（design 11.6）：
    /// 离线缺缓存、network、auth、not-found、peer-conflict、disk、permission、timeout、install-failed。
    /// 未识别的输出一律归为 InstallFailed，绝不猜测成功。
    /// </summary>
    public static PackageRestoreFailure ClassifyInstallFailure(
        int exitCode,
        bool timedOut,
        string? stdOut,
        string? stdErr,
        string? operationId = null)
    {
        var output = Tail((stdErr ?? "") + "\n" + (stdOut ?? ""), 800);
        var text = output.ToLowerInvariant();
        var opId = string.IsNullOrWhiteSpace(operationId) ? "<opId>" : operationId;

        PackageRestoreFailure Build(PackageRestoreFailureKind kind, string message)
        {
            return new PackageRestoreFailure
            {
                Kind = kind,
                Message = message,
                ExitCode = exitCode,
                TimedOut = timedOut,
                OutputTail = output,
                ManualAction = ManualActionFor(kind, opId),
                CanRetryWithNetwork = kind is PackageRestoreFailureKind.OfflineCacheMissing
                    or PackageRestoreFailureKind.Network
                    or PackageRestoreFailureKind.Auth
                    or PackageRestoreFailureKind.NotFound
                    or PackageRestoreFailureKind.InstallFailed,
            };
        }

        if (timedOut)
        {
            return Build(PackageRestoreFailureKind.Timeout, "安装超时：CLI 进程树已终止（未触碰 DSH 进程）。");
        }

        if (ContainsAny(text, "err_pnpm_no_offline_tarball", "err_pnpm_no_offline_location", "enotcached",
                "no offline tarball", "offline mode"))
        {
            return Build(PackageRestoreFailureKind.OfflineCacheMissing, "离线缓存缺包：pnpm store 没有 lockfile 所需 tarball。");
        }

        if (ContainsAny(text, "eacces", "eperm", "permission denied", "access is denied"))
        {
            return Build(PackageRestoreFailureKind.Permission, "权限不足：无法写入 profile 或 store。");
        }

        if (ContainsAny(text, "enospc", "no space left", "disk full"))
        {
            return Build(PackageRestoreFailureKind.Disk, "磁盘空间不足：安装无法完成。");
        }

        if (ContainsAny(text, "err_pnpm_peer_dep_issues", "peer dep", "peer dependency"))
        {
            return Build(PackageRestoreFailureKind.PeerConflict, "peer 依赖冲突：需要人工调整依赖版本。");
        }

        if (ContainsAny(text, "e401", "err_pnpm_fetch_401", "unauthorized", "authentication", "_authtoken"))
        {
            return Build(PackageRestoreFailureKind.Auth, "registry 认证失败：检查 .npmrc / 私有源凭据。");
        }

        if (ContainsAny(text, "e404", "err_pnpm_fetch_404", "404 not found", "not found"))
        {
            return Build(PackageRestoreFailureKind.NotFound, "registry 中不存在该包版本（或离线镜像缺包）。");
        }

        if (ContainsAny(text, "eai_again", "etimedout", "econnrefused", "econnreset", "getaddrinfo",
                "err_pnpm_meta_fetch_fail", "network", "socket hang up"))
        {
            return Build(PackageRestoreFailureKind.Network, "网络不可达：包级恢复默认离线，联网需用户明确同意。");
        }

        return Build(PackageRestoreFailureKind.InstallFailed,
            exitCode == 0 ? "安装未成功（输出未命中已知失败模式）。" : $"安装失败（退出码 {exitCode}）。");
    }

    private static string ManualActionFor(PackageRestoreFailureKind kind, string opId) => kind switch
    {
        PackageRestoreFailureKind.OfflineCacheMissing =>
            "人工入口：1) 确认 pnpm store 缓存/磁盘后重试；2) 经你同意后联网执行 " +
            "dsh.cmd plugin --profile desktop install --frozen-lockfile；" +
            $"3) 回滚本操作：loom-dsh recovery rollback {opId}（备份 restore-backup\\{opId}）。",
        PackageRestoreFailureKind.Permission =>
            "以当前用户检查 profile 目录与 pnpm store 的 ACL 后重试；不要用管理员强改 profile。" +
            $"必要时回滚：loom-dsh recovery rollback {opId}。",
        PackageRestoreFailureKind.Disk =>
            "清理磁盘后重试；当前 profile 文件已按 restore-backup 回滚。",
        PackageRestoreFailureKind.PeerConflict =>
            "peer 依赖冲突需要人工调整依赖版本；确认后重新采集快照再恢复。",
        PackageRestoreFailureKind.Auth =>
            "检查 .npmrc 与私有源凭据；不要回退公网 registry。",
        PackageRestoreFailureKind.NotFound =>
            "人工确认快照 lockfile 与 registry 是否一致；不要自动替换版本。",
        PackageRestoreFailureKind.Network =>
            "网络不可达：二期包级恢复默认离线。经你同意后可用 " +
            "dsh.cmd plugin --profile desktop install --frozen-lockfile 重试。",
        PackageRestoreFailureKind.Timeout =>
            "确认磁盘/安全软件后重跑；超时只终止 CLI 进程树，不影响 DSH。",
        _ =>
            $"查看 journal 与 CLI 输出；可回滚本操作：loom-dsh recovery rollback {opId}。",
    };

    // ------------------------------------------------------------------ first-start criteria

    /// <summary>
    /// 首启失败判据（设计 12.6）：任一命中即 verify-failed，允许自动回滚本次 op：
    /// startup 类新 crash（且落在本次 op 时间窗）→ 进程提前退出 → N 秒未 ready → check 仍缺 bundles/依赖。
    /// 早于本次 op 的 crash 视为用户手动崩溃，不误判。
    /// </summary>
    public static FirstStartVerdict EvaluateFirstStart(
        FirstStartObservation observation,
        FirstStartCriteria? criteria = null)
    {
        if (observation is null) throw new ArgumentNullException(nameof(observation));
        criteria ??= new FirstStartCriteria();

        var verdict = new FirstStartVerdict { Reason = FirstStartFailureReason.None };
        var signals = verdict.Signals;
        var tolerance = criteria.CrashWindowTolerance < TimeSpan.Zero
            ? TimeSpan.Zero
            : criteria.CrashWindowTolerance;
        var windowStart = observation.OperationStartedAt - tolerance;

        var crashInWindow = observation.StartupCrash
                            && (observation.StartupCrashAt is null || observation.StartupCrashAt >= windowStart);
        if (observation.StartupCrash && !crashInWindow)
        {
            signals.Add("startup crash 早于本次操作窗口（疑似用户手动崩溃），不计入首启失败");
        }

        FirstStartVerdict Fail(FirstStartFailureReason reason, string summary)
        {
            verdict.Reason = reason;
            verdict.Failed = true;
            verdict.AutoRollbackAllowed = true;
            verdict.Summary = summary;
            return verdict;
        }

        if (criteria.DetectStartupCrash && crashInWindow)
        {
            signals.Add("startup crash：" + (observation.CrashDetail ?? observation.StartupCrashAt?.ToString() ?? "未知"));
            return Fail(FirstStartFailureReason.StartupCrash, "首启失败：产生 startup 类新 crash。");
        }

        if (observation.ProcessExited)
        {
            signals.Add("宿主进程提前退出" +
                        (observation.ProcessExitedAt is null ? "" : "（" + observation.ProcessExitedAt + "）"));
            return Fail(FirstStartFailureReason.ProcessExitedEarly, "首启失败：宿主进程提前退出。");
        }

        var threshold = Math.Max(1, criteria.ReadyTimeoutSeconds);
        if (!observation.HostReady && observation.ObservedSeconds >= threshold)
        {
            signals.Add($"已等待 {observation.ObservedSeconds}s ≥ {threshold}s 仍未 ready");
            return Fail(FirstStartFailureReason.NotReadyTimeout, $"首启失败：{threshold} 秒内 host 未 ready。");
        }

        if (criteria.RequireBundles && observation.MissingBundles.Count > 0)
        {
            signals.Add("check 仍缺 bundles：" + string.Join("，", observation.MissingBundles));
            return Fail(FirstStartFailureReason.BundlesMissing, "首启失败：check 仍发现 bundles 缺失。");
        }

        if (observation.MissingDependencies.Count > 0)
        {
            signals.Add("check 仍缺依赖：" + string.Join("，", observation.MissingDependencies));
            return Fail(FirstStartFailureReason.DependenciesMissing, "首启失败：check 仍发现依赖缺失。");
        }

        verdict.Summary = observation.HostReady
            ? "首启通过：host 已 ready，无新 startup crash、bundles 与依赖齐全。"
            : $"仍在等待 ready（{observation.ObservedSeconds}s / {threshold}s），暂不判定失败。";
        if (observation.HostReady) signals.Add("host ready");
        return verdict;
    }

    // ------------------------------------------------------------------ small helpers

    /// <summary>构造 dsh.cmd 的展示命令（真正执行仍用参数数组，避免 shell 注入）。</summary>
    public static string BuildDisplayCommand(DshInstallation installation, IReadOnlyList<string> args)
    {
        var tokens = new List<string> { "dsh.cmd", "plugin", "--profile", ResolveProfileName(installation) };
        tokens.AddRange(args ?? Array.Empty<string>());
        return string.Join(" ", tokens);
    }

    /// <summary>profile 名（与 DshCli 的解析规则一致：取 profileDir 目录名，缺省 desktop）。</summary>
    public static string ResolveProfileName(DshInstallation installation)
    {
        try
        {
            if (installation is not null && !string.IsNullOrWhiteSpace(installation.ProfileDir))
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(installation.ProfileDir));
                if (!string.IsNullOrWhiteSpace(name)) return name!;
            }
        }
        catch
        {
            // 回退到默认 profile
        }

        return DshDefaults.DesktopProfile;
    }

    private static string? CurrentPackageJsonSha(ProfileState current)
    {
        if (current.FileSha256.TryGetValue(DshDefaults.ManifestFile, out var sha) && !string.IsNullOrWhiteSpace(sha))
        {
            return sha;
        }

        return ShaOrNull(current.PackageJsonText);
    }

    private static string? ShaOrNull(string? text) =>
        string.IsNullOrEmpty(text) ? null : Hashing.Sha256HexOfText(text);

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.Ordinal));

    private static string Tail(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[^max..];
    }

    private static string Short(string text) =>
        text.Length <= 32 ? text.Replace("\r", " ").Replace("\n", " ") : text.Length + " 字符";
}
