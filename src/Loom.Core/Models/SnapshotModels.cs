namespace Loom.Core.Models;

public sealed class SnapshotManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTimeOffset TakenAt { get; set; }
    public SnapshotTrigger Trigger { get; set; }
    public string AppVersion { get; set; } = "unknown";
    public string DshVersion { get; set; } = "unknown";
    public string InstallDir { get; set; } = "";
    public string ProfileDir { get; set; } = "";
    public string DshHome { get; set; } = "";
    public ProfileSnapshot Profile { get; set; } = new();
    public List<PluginSnapshot> Plugins { get; set; } = new();
    public SnapshotFiles Files { get; set; } = new();
    public PackageArchive PackageArchive { get; set; } = new();
    public SnapshotEnvironment Environment { get; set; } = new();
}

public sealed class ProfileSnapshot
{
    public List<BundleSnapshot> Bundles { get; set; } = new();
    public Dictionary<string, DependencySnapshot> Dependencies { get; set; } = new();
    public PatchSnapshot Patch { get; set; } = new();
    public CompatibilitySnapshot Compatibility { get; set; } = new();
    public string? LockfileSha256 { get; set; }
    public Dictionary<string, string> FileSha256 { get; set; } = new();
}

public sealed class BundleSnapshot
{
    public string Name { get; set; } = "";
    public BundleClass Class { get; set; }
    public string? Version { get; set; }
    public string? ManifestPath { get; set; }
}

public sealed class DependencySnapshot
{
    public string Spec { get; set; } = "";
    public string? Resolved { get; set; }
    public string? Integrity { get; set; }
}

public sealed class PatchSnapshot
{
    public string? Sha256 { get; set; }
    public string File { get; set; } = "cordis.patch.yml";
    public List<string> Backups { get; set; } = new();
}

public sealed class CompatibilitySnapshot
{
    public string? File { get; set; }
    public string? Sha256 { get; set; }
    public Dictionary<string, List<string>> Exemptions { get; set; } = new();
}

public sealed class PluginSnapshot
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";
    public string Dir { get; set; } = "";
    public Dictionary<string, string>? Engines { get; set; }
    public Dictionary<string, string>? PeerDependencies { get; set; }
    public BundleDeclaration? Dsh { get; set; }
    public StaticPrecheck Static { get; set; } = new();
    public CompatibilityLevel Compatibility { get; set; } = CompatibilityLevel.Unknown;
    public CompatibilityEngine Engine { get; set; } = CompatibilityEngine.Unknown;
    public List<string> Requirements { get; set; } = new();
    public string? CompatibilityError { get; set; }
}

public sealed class BundleDeclaration
{
    public bool Bundle { get; set; }
    public List<string>? ClientInject { get; set; }
    public List<string>? Platforms { get; set; }
    public List<string>? Patch { get; set; }
}

public enum StaticCheckResult
{
    Ok,
    Unverified,
    Missing,
    NotApplicable,
}

public sealed class StaticPrecheck
{
    public StaticCheckResult Result { get; set; } = StaticCheckResult.NotApplicable;
    public List<string> PatchFiles { get; set; } = new();
    public List<string> ClientInject { get; set; } = new();
    public List<string> MissingRefs { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public sealed class SnapshotFiles
{
    public string? PackageJson { get; set; }
    public string? PnpmLock { get; set; }
    public string? CordisPatch { get; set; }
    public string? Compatibility { get; set; }
    public string? CordisYml { get; set; }
}

public sealed class PackageArchive
{
    public string Kind { get; set; } = "none";
    public List<ArchivedFile> Files { get; set; } = new();
    public long TotalBytes { get; set; }
}

public sealed class ArchivedFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public sealed class SnapshotEnvironment
{
    public string Platform { get; set; } = "win32";
    public string Arch { get; set; } = "x64";
    public string ToolVersion { get; set; } = "";
    public string? ElectronVersion { get; set; }
}
