using System.Globalization;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Snapshot;

namespace Loom.Core.Restore;

/// <summary>
/// Pure merge planner (design 12.3 / 12.4): decides which file-level changes a restore would make.
/// No I/O happens here; RestoreService performs the atomic writes and may skip actions that are already applied.
/// </summary>
public static class RestorePlanner
{
    public static RestorePlan Create(
        SnapshotManifest snapshot,
        ProfileState current,
        DshInstallation installation,
        LoomConfig config,
        RestoreConstraints? constraints = null)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (current is null) throw new ArgumentNullException(nameof(current));
        if (installation is null) throw new ArgumentNullException(nameof(installation));
        config ??= new LoomConfig();
        constraints ??= RestoreConstraints.Permissive;

        var plan = new RestorePlan
        {
            SnapshotId = snapshot.Id,
            SnapshotDir = "",
        };
        var changes = new List<RestoreAction>();
        var skips = new List<RestoreAction>();
        var conflicts = new List<RestoreConflict>();

        var installBaseline = installation.BaseBundles is { Count: > 0 }
            ? installation.BaseBundles
            : DshDefaults.BaselineBundles.ToList();
        var baselineSet = new HashSet<string>(installBaseline, StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ package.json bootstrap
        if (!current.HasManifest)
        {
            changes.Add(new RestoreAction
            {
                Kind = RestoreActionKind.RestorePackageJson,
                TargetFile = DshDefaults.ManifestFile,
                Description = "当前 profile 缺少 package.json：先恢复快照文件作为合并基础",
                Detail = "snapshot",
            });
        }

        // ------------------------------------------------------------------ bundles
        var desiredOrder = BuildDesiredBundleOrder(snapshot, installBaseline, baselineSet);
        var mergedOrder = MergeBundleOrder(current.Bundles, desiredOrder);
        var addedBundles = mergedOrder
            .Where(name => !current.Bundles.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var allowedBundleSet = constraints.AllowedBundles is { } allowedBundles
            ? new HashSet<string>(allowedBundles, StringComparer.OrdinalIgnoreCase)
            : null;
        var restoreBundles = allowedBundleSet is null
            ? addedBundles
            : addedBundles.Where(name => allowedBundleSet.Contains(name)).ToList();
        var blockedBundles = addedBundles
            .Where(name => !restoreBundles.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (blockedBundles.Count > 0)
        {
            skips.Add(new RestoreAction
            {
                Kind = RestoreActionKind.Skip,
                TargetFile = DshDefaults.ManifestFile,
                Subject = "未自动加回的 bundle",
                Description = "证据不足或属于用户主动禁用，保留当前状态：" + string.Join("，", blockedBundles),
                Items = blockedBundles,
            });
        }

        if (!SequencesEqual(mergedOrder, current.Bundles) && restoreBundles.Count > 0)
        {
            var filteredDesired = desiredOrder
                .Where(name => !blockedBundles.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var filteredMerged = MergeBundleOrder(current.Bundles, filteredDesired);
            changes.Add(new RestoreAction
            {
                Kind = RestoreActionKind.MergeBundles,
                TargetFile = DshDefaults.ManifestFile,
                Subject = "dsh.profile.bundles",
                Description = $"按快照顺序插回 {restoreBundles.Count} 个 bundle：" +
                              string.Join("，", restoreBundles) + "；基线使用当前安装包模板，用户新装的保留在末尾",
                Items = filteredMerged,
                Detail = string.Join(",", restoreBundles),
            });
        }

        var newBundles = current.Bundles
            .Where(name => !desiredOrder.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (newBundles.Count > 0)
        {
            skips.Add(new RestoreAction
            {
                Kind = RestoreActionKind.Skip,
                TargetFile = DshDefaults.ManifestFile,
                Subject = "用户新增 bundle",
                Description = "保留更新后新装的 bundle（不删除、不打乱顺序）：" + string.Join("，", newBundles),
                Items = newBundles,
            });
        }

        // ------------------------------------------------------------------ dependencies
        var addedDependencies = snapshot.Profile.Dependencies.Keys
            .Where(name => !current.Dependencies.ContainsKey(name))
            .ToList();
        var allowedDependencySet = constraints.AllowedDependencies is { } allowedDependencies
            ? new HashSet<string>(allowedDependencies, StringComparer.OrdinalIgnoreCase)
            : null;
        var restoreDependencies = allowedDependencySet is null
            ? addedDependencies
            : addedDependencies.Where(name => allowedDependencySet.Contains(name)).ToList();
        var blockedDependencies = addedDependencies
            .Where(name => !restoreDependencies.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (blockedDependencies.Count > 0)
        {
            skips.Add(new RestoreAction
            {
                Kind = RestoreActionKind.Skip,
                TargetFile = DshDefaults.ManifestFile,
                Subject = "未自动补回的依赖",
                Description = "证据不足或需要包级恢复（本版不调用 pnpm），保留当前状态：" +
                              string.Join("，", blockedDependencies),
                Items = blockedDependencies,
            });
        }

        if (restoreDependencies.Count > 0)
        {
            changes.Add(new RestoreAction
            {
                Kind = RestoreActionKind.MergeDependencies,
                TargetFile = DshDefaults.ManifestFile,
                Subject = "dependencies",
                Description = "补回快照中缺失的 " + restoreDependencies.Count + " 个依赖 spec：" +
                              string.Join("，", restoreDependencies),
                Items = restoreDependencies,
            });
        }

        var keptDependencies = new List<string>();
        foreach (var pair in snapshot.Profile.Dependencies)
        {
            if (!current.Dependencies.TryGetValue(pair.Key, out var existing)) continue;
            if (string.Equals(existing.Spec, pair.Value.Spec, StringComparison.Ordinal)
                && string.Equals(existing.Resolved ?? "", pair.Value.Resolved ?? "", StringComparison.Ordinal))
            {
                continue;
            }

            keptDependencies.Add($"{pair.Key}（当前 {existing.Spec} / {existing.Resolved ?? "?"}，快照 {pair.Value.Spec} / {pair.Value.Resolved ?? "?"}）");
        }

        if (keptDependencies.Count > 0)
        {
            skips.Add(new RestoreAction
            {
                Kind = RestoreActionKind.Skip,
                TargetFile = DshDefaults.ManifestFile,
                Subject = "用户升级/修改的依赖",
                Description = "依赖已存在时保留当前 spec/版本；快照 spec 仅用于缺失项：" + string.Join("；", keptDependencies),
            });
        }

        // ------------------------------------------------------------------ lockfile
        var mergedDependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in current.Dependencies)
        {
            mergedDependencies[pair.Key] = pair.Value.Spec;
        }

        foreach (var name in restoreDependencies)
        {
            mergedDependencies[name] = snapshot.Profile.Dependencies[name].Spec;
        }

        var dependenciesMatchSnapshot = mergedDependencies.Count == snapshot.Profile.Dependencies.Count
                                        && mergedDependencies.All(pair =>
                                            snapshot.Profile.Dependencies.TryGetValue(pair.Key, out var expected)
                                            && string.Equals(pair.Value, expected.Spec, StringComparison.Ordinal));

        if (!string.IsNullOrEmpty(snapshot.Profile.LockfileSha256))
        {
            if (!dependenciesMatchSnapshot)
            {
                skips.Add(new RestoreAction
                {
                    Kind = RestoreActionKind.Skip,
                    TargetFile = DshDefaults.LockfileFile,
                    Subject = "pnpm-lock.yaml",
                    Description = "当前 package.json 依赖与快照不一致，跳过 lockfile 还原（本版不调用 pnpm，需人工 install 修复）",
                });
            }
            else if (!string.Equals(current.LockfileSha256, snapshot.Profile.LockfileSha256, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new RestoreAction
                {
                    Kind = RestoreActionKind.RestoreLockfile,
                    TargetFile = DshDefaults.LockfileFile,
                    Description = "package.json 依赖与快照一致：还原快照的 pnpm-lock.yaml 以保持可复现",
                    Detail = "snapshot",
                });
            }
        }
        else if (!string.IsNullOrEmpty(current.LockfileSha256))
        {
            skips.Add(new RestoreAction
            {
                Kind = RestoreActionKind.Skip,
                TargetFile = DshDefaults.LockfileFile,
                Subject = "pnpm-lock.yaml",
                Description = "快照中没有 lockfile：保留当前 lockfile 不动",
            });
        }

        // ------------------------------------------------------------------ cordis.patch.yml
        AddPatchActions(snapshot, current, changes, skips, conflicts, constraints.AllowPatchRestore);

        // ------------------------------------------------------------------ compatibility.json
        AddCompatibilityActions(snapshot, current, installation, changes, skips, constraints.AllowCompatibilityRestore);

        // ------------------------------------------------------------------ .dsh-market
        if (snapshot.Profile.FileSha256.ContainsKey(".dsh-market/state.json"))
        {
            skips.Add(new RestoreAction
            {
                Kind = RestoreActionKind.Skip,
                TargetFile = ".dsh-market/state.json",
                Subject = ".dsh-market",
                Description = "市场/用户偏好状态不还原（仅报告）；还原后请在插件市场确认状态",
            });
        }

        // Skip-only plans stay empty so RestorePlan.HasChanges (Actions.Count > 0) means "there is
        // something to write"; otherwise a no-op preview would claim a restart is required.
        plan.Actions = changes.Count > 0 ? changes.Concat(skips).ToList() : new List<RestoreAction>();
        plan.Conflicts = conflicts;
        plan.RequiresManualReview = conflicts.Count > 0;
        plan.Summary = BuildSummary(changes, skips, conflicts);
        return plan;
    }

    private static List<string> BuildDesiredBundleOrder(
        SnapshotManifest snapshot,
        IReadOnlyList<string> installBaseline,
        HashSet<string> baselineSet)
    {
        var desired = new List<string>();
        foreach (var name in installBaseline)
        {
            if (!desired.Contains(name, StringComparer.OrdinalIgnoreCase)) desired.Add(name);
        }

        foreach (var bundle in snapshot.Profile.Bundles)
        {
            if (bundle.Class == BundleClass.Baseline) continue;
            if (baselineSet.Contains(bundle.Name)) continue;
            if (desired.Contains(bundle.Name, StringComparer.OrdinalIgnoreCase)) continue;
            desired.Add(bundle.Name);
        }

        return desired;
    }

    /// <summary>
    /// Inserts the desired entries into the current order without moving existing entries:
    /// current items keep their positions and new items are inserted after their nearest desired
    /// predecessor (or before their nearest desired successor).
    /// Shared with RestoreService so a repeated apply is idempotent.
    /// </summary>
    internal static List<string> MergeBundleOrder(IReadOnlyList<string> current, IReadOnlyList<string> desired)
    {
        var result = new List<string>(current);
        foreach (var name in desired)
        {
            if (result.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var desiredIndex = IndexOf(desired, name);
            var insertAt = -1;
            for (var index = desiredIndex - 1; index >= 0; index--)
            {
                var position = IndexOf(result, desired[index]);
                if (position < 0) continue;
                insertAt = position + 1;
                break;
            }

            if (insertAt < 0)
            {
                for (var index = desiredIndex + 1; index < desired.Count; index++)
                {
                    var position = IndexOf(result, desired[index]);
                    if (position < 0) continue;
                    insertAt = position;
                    break;
                }
            }

            if (insertAt < 0) insertAt = result.Count;
            result.Insert(insertAt, name);
        }

        return result;
    }

    private static void AddPatchActions(
        SnapshotManifest snapshot,
        ProfileState current,
        List<RestoreAction> changes,
        List<RestoreAction> skips,
        List<RestoreConflict> conflicts,
        bool? allowPatchRestore)
    {
        var snapshotSha = snapshot.Profile.Patch.Sha256;

        if (allowPatchRestore == false)
        {
            var differs = !string.Equals(current.PatchSha256, snapshotSha, StringComparison.OrdinalIgnoreCase);
            if (differs && (!string.IsNullOrEmpty(snapshotSha) || !string.IsNullOrEmpty(current.PatchSha256)))
            {
                skips.Add(new RestoreAction
                {
                    Kind = RestoreActionKind.KeepPatch,
                    TargetFile = DshDefaults.PatchFile,
                    Subject = DshDefaults.PatchFile,
                    Description = "证据不足或不在自动还原候选：保留当前 cordis.patch.yml（仅报告差异）",
                });
            }

            return;
        }

        // Priority 1: a backup created after the baseline snapshot (the recovery flow renames the patch).
        var newestBackup = current.PatchBackups
            .Select(name => (name, parsed: ProfileFiles.TryParseBackupTime(name, out var time), time))
            .Where(item => item.parsed && item.time > snapshot.TakenAt)
            .OrderBy(item => item.time)
            .Select(item => item.name)
            .LastOrDefault();

        if (newestBackup is not null)
        {
            changes.Add(new RestoreAction
            {
                Kind = RestoreActionKind.RestorePatch,
                TargetFile = DshDefaults.PatchFile,
                Subject = newestBackup,
                Description = $"使用更新后备份 {newestBackup} 恢复 cordis.patch.yml（优先于快照）",
                Detail = Path.Combine(current.ProfileDir, newestBackup),
            });
            return;
        }

        if (string.IsNullOrEmpty(snapshotSha))
        {
            if (!string.IsNullOrEmpty(current.PatchSha256))
            {
                skips.Add(new RestoreAction
                {
                    Kind = RestoreActionKind.Skip,
                    TargetFile = DshDefaults.PatchFile,
                    Subject = DshDefaults.PatchFile,
                    Description = "快照中没有 cordis.patch.yml：保留当前文件",
                });
            }

            return;
        }

        if (string.Equals(current.PatchSha256, snapshotSha, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var currentMatchesBackup = !string.IsNullOrEmpty(current.PatchSha256)
                                   && current.PatchBackups.Any(backup =>
                                       current.FileSha256.TryGetValue(backup, out var backupSha)
                                       && string.Equals(backupSha, current.PatchSha256, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(current.PatchSha256) || currentMatchesBackup)
        {
            changes.Add(new RestoreAction
            {
                Kind = RestoreActionKind.RestorePatch,
                TargetFile = DshDefaults.PatchFile,
                Description = string.IsNullOrEmpty(current.PatchSha256)
                    ? "cordis.patch.yml 缺失：还原快照内容"
                    : "当前 cordis.patch.yml 等于旧备份内容：还原快照内容（快照优先）",
                Detail = "snapshot",
            });
            return;
        }

        // Current content is non-empty and differs from both the snapshot and every backup: keep it.
        conflicts.Add(new RestoreConflict
        {
            Subject = DshDefaults.PatchFile,
            Reason = "当前 cordis.patch.yml 与快照及备份都不同（可能是用户修改）；默认保留当前，需人工确认后再改。",
            CurrentPreview = DescribeSha(current.PatchSha256),
            SnapshotPreview = DescribeSha(snapshotSha),
        });
        skips.Add(new RestoreAction
        {
            Kind = RestoreActionKind.KeepPatch,
            TargetFile = DshDefaults.PatchFile,
            Subject = DshDefaults.PatchFile,
            Description = "保留当前 cordis.patch.yml（冲突待处理，不覆盖用户内容）",
            Detail = "conflict",
        });
    }

    private static void AddCompatibilityActions(
        SnapshotManifest snapshot,
        ProfileState current,
        DshInstallation installation,
        List<RestoreAction> changes,
        List<RestoreAction> skips,
        bool? allowCompatibilityRestore)
    {
        if (allowCompatibilityRestore == false)
        {
            skips.Add(new RestoreAction
            {
                Kind = RestoreActionKind.SkipCompatibility,
                TargetFile = DshDefaults.CompatibilityFile,
                Subject = "compatibility.json",
                Description = "自动还原不合并兼容性豁免（证据不足/需人工确认）：保持当前文件",
            });
            return;
        }

        var exemptions = snapshot.Profile.Compatibility.Exemptions;
        if (exemptions.Count == 0) return;

        var dshVersion = installation.DshVersion;
        if (string.IsNullOrWhiteSpace(dshVersion) || string.Equals(dshVersion, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            skips.Add(new RestoreAction
            {
                Kind = RestoreActionKind.SkipCompatibility,
                TargetFile = DshDefaults.CompatibilityFile,
                Subject = "compatibility.json",
                Description = "当前 DSH 运行时版本未知：无法确认精确匹配的豁免，跳过 compatibility.json 还原",
            });
            return;
        }

        var active = new List<string>();
        var inactive = new List<string>();
        foreach (var pair in exemptions)
        {
            if (pair.Value.Contains(dshVersion, StringComparer.Ordinal))
            {
                active.Add(pair.Key);
            }
            else
            {
                inactive.Add(pair.Key);
            }
        }

        var damaged = !string.IsNullOrEmpty(current.CompatibilityPath) && current.CompatibilitySha256 is null;
        if (damaged)
        {
            skips.Add(new RestoreAction
            {
                Kind = RestoreActionKind.SkipCompatibility,
                TargetFile = DshDefaults.CompatibilityFile,
                Subject = "compatibility.json",
                Description = "当前 compatibility.json 无法被官方读取（损坏或含非法记录）：不覆盖，需手工修复后重试",
            });
            return;
        }

        if (active.Count == 0)
        {
            skips.Add(new RestoreAction
            {
                Kind = RestoreActionKind.SkipCompatibility,
                TargetFile = DshDefaults.CompatibilityFile,
                Subject = "compatibility.json",
                Description = $"快照中的 {inactive.Count} 条豁免都不是当前运行时 {dshVersion} 的精确匹配：标记 inactive，不写入",
            });
            return;
        }

        var sameFile = !string.IsNullOrEmpty(current.CompatibilitySha256)
                       && !string.IsNullOrEmpty(snapshot.Profile.Compatibility.Sha256)
                       && string.Equals(current.CompatibilitySha256, snapshot.Profile.Compatibility.Sha256, StringComparison.OrdinalIgnoreCase);
        if (sameFile) return;

        changes.Add(new RestoreAction
        {
            Kind = RestoreActionKind.RestoreCompatibility,
            TargetFile = DshDefaults.CompatibilityFile,
            Subject = "compatibility.json",
            Description = $"恢复 {active.Count} 条精确匹配当前运行时 {dshVersion} 的版本豁免" +
                          (inactive.Count > 0 ? $"；{inactive.Count} 条旧版本豁免标记 inactive（不写）" : ""),
            Items = active,
            Detail = "snapshot",
        });
    }

    private static int IndexOf(IReadOnlyList<string> values, string name)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], name, StringComparison.OrdinalIgnoreCase)) return index;
        }

        return -1;
    }

    internal static bool SequencesEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private static string DescribeSha(string? sha)
    {
        if (string.IsNullOrEmpty(sha)) return "（空）";
        return "sha256:" + (sha.Length > 16 ? sha[..16] + "…" : sha);
    }

    private static string BuildSummary(
        List<RestoreAction> changes,
        List<RestoreAction> skips,
        List<RestoreConflict> conflicts)
    {
        if (changes.Count == 0 && conflicts.Count == 0)
        {
            return skips.Count == 0
                ? "当前 profile 与快照一致，无需还原。"
                : $"无需写文件：{skips.Count} 项按规则跳过（保留用户改动）。";
        }

        var parts = new List<string>
        {
            $"计划还原 {changes.Count} 项文件改动",
        };
        if (skips.Count > 0) parts.Add($"跳过 {skips.Count} 项（用户改动/规则保护）");
        if (conflicts.Count > 0) parts.Add($"冲突 {conflicts.Count} 项需人工确认");
        var description = string.Join("；", changes.Select(action => action.Description));
        parts.Add(description);
        return string.Join("。", parts) + "。";
    }
}
