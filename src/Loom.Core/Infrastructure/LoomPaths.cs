using System.Text;

namespace Loom.Core.Infrastructure;

/// <summary>Well-known constants and layout of the DSH desktop profile.</summary>
public static class DshDefaults
{
    public const string DesktopProfile = "desktop";
    public const string ProfileLockFile = "lock";
    public const string ManifestLockFile = "package.json.lock";
    public const string PluginManagerDir = ".plugin-manager";
    public const string PluginManagerRunFile = "run.json";
    public const string ManifestFile = "package.json";
    public const string LockfileFile = "pnpm-lock.yaml";
    public const string PatchFile = "cordis.patch.yml";
    public const string CordisFile = "cordis.yml";
    public const string CompatibilityFile = "compatibility.json";

    /// <summary>Installation-owned profile bundles (always taken from the current install template, never from an old snapshot).</summary>
    public static readonly string[] BaselineBundles =
    {
        "@deepseek-ai/dsh-base",
        "@deepseek-ai/dsh-web-app",
    };

    public const string UpdaterFolderName = "@deepseek-aidsh-desktop-updater";
    public const string ProcessName = "DeepSeek Harness";
    public const string ExecutableName = "DeepSeek Harness.exe";

    public static bool IsBaselineBundle(string name) =>
        BaselineBundles.Any(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsOfficialBundle(string name) =>
        name.StartsWith("@deepseek-ai/dsh", StringComparison.OrdinalIgnoreCase);

    public static Models.BundleClass ClassifyBundle(string name)
    {
        if (IsBaselineBundle(name)) return Models.BundleClass.Baseline;
        return IsOfficialBundle(name) ? Models.BundleClass.Official : Models.BundleClass.ThirdParty;
    }
}

/// <summary>Tool data layout (all under %LOCALAPPDATA%\Loom by default).</summary>
public sealed class LoomPaths
{
    public const string ToolVersion = "0.1.1";

    public LoomPaths(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string DataRoot { get; }
    public string ConfigFile => Path.Combine(DataRoot, "config.json");
    public string StateFile => Path.Combine(DataRoot, "state.json");
    public string JournalDir => Path.Combine(DataRoot, "journal");
    public string SnapshotsDir => Path.Combine(DataRoot, "snapshots");
    public string RestoreBackupDir => Path.Combine(DataRoot, "restore-backup");
    public string ReportsDir => Path.Combine(DataRoot, "reports");
    public string LogsDir => Path.Combine(DataRoot, "logs");
    public string QueueDir => Path.Combine(DataRoot, "queue");
    public string QueueRequestsDir => Path.Combine(QueueDir, "requests");
    public string QueueResultsDir => Path.Combine(QueueDir, "results");
    public string HelperDir => Path.Combine(DataRoot, "helper");
    public string HelperPath => Path.Combine(HelperDir, "helper.mjs");
    public string LockFilePath => Path.Combine(DataRoot, "loom.operation.lock");
    public string LogFilePath => Path.Combine(LogsDir, "loom.log");

    public static string DefaultDataRoot => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "Loom");

    public static LoomPaths Create(string? dataRootOverride = null)
    {
        var root = string.IsNullOrWhiteSpace(dataRootOverride) ? DefaultDataRoot : dataRootOverride!;
        return new LoomPaths(root);
    }

    public void EnsureDirectories()
    {
        var directories = new[]
        {
            DataRoot,
            JournalDir,
            SnapshotsDir,
            RestoreBackupDir,
            ReportsDir,
            LogsDir,
            QueueDir,
            QueueRequestsDir,
            QueueResultsDir,
            HelperDir,
        };

        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
            HardenDirectoryAcl(directory);
        }
    }

    /// <summary>
    /// Design section 15: the tool data root (snapshots, restore backups, reports, logs) is readable
    /// only by the current user and SYSTEM. Best effort: non-NTFS/network roots keep inherited ACLs.
    /// </summary>
    private static void HardenDirectoryAcl(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();
            if (security.AreAccessRulesProtected) return;

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    currentUser,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit
                    | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            }

            var system = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid,
                null);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                system,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit
                | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch
        {
            // best effort: FAT32, network shares or restricted tokens keep inherited permissions
        }
    }

    public string SnapshotDir(string snapshotId) => Path.Combine(SnapshotsDir, snapshotId);

    public string RestoreBackupDirFor(string operationId) => Path.Combine(RestoreBackupDir, operationId);

    public string JournalFile(string operationId) => Path.Combine(JournalDir, operationId + ".jsonl");

    public static string NewOperationId(string kind)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var safeKind = new string(kind.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return $"{stamp}-{safeKind}-{suffix}";
    }

    public static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }
}
