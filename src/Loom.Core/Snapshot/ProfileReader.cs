using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Snapshot;

/// <summary>
/// Reads the desktop profile with the "stable read protocol" of design 7.2:
/// wait until lock files are quiet, record baseline hashes, read the content,
/// then re-read the baseline hashes and retry with 20 ms → 2 s backoff when they changed.
/// strict = false performs a single best-effort read (used by previews/status).
/// </summary>
public sealed class ProfileReader : IProfileReader
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly TimeSpan _quietTimeout;
    private readonly TimeSpan _maxBackoff;

    public ProfileReader(ILogger logger)
        : this(logger, TimeSpan.FromSeconds(60), DefaultMaxBackoff)
    {
    }

    /// <summary>
    /// Timeout is injectable so tests can exercise the (short) waiting path without a 60 s stall.
    /// The composition root keeps using the single-argument constructor.
    /// </summary>
    public ProfileReader(ILogger logger, TimeSpan quietTimeout, TimeSpan? maxBackoff = null)
    {
        _logger = logger;
        _quietTimeout = quietTimeout <= TimeSpan.Zero ? TimeSpan.Zero : quietTimeout;
        _maxBackoff = maxBackoff is { } value && value > TimeSpan.Zero ? value : DefaultMaxBackoff;
    }

    public ProfileState Read(DshInstallation installation, bool strict = true)
    {
        if (installation is null) throw new ArgumentNullException(nameof(installation));
        if (string.IsNullOrWhiteSpace(installation.ProfileDir))
        {
            throw new LoomException(LoomErrorCodes.NotFound, "profile 目录未知，无法读取插件清单。");
        }

        var profileDir = installation.ProfileDir;
        if (!Directory.Exists(profileDir))
        {
            throw new LoomException(LoomErrorCodes.NotFound, $"profile 目录不存在：{profileDir}");
        }

        if (!strict)
        {
            return ReadOnce(profileDir);
        }

        var deadline = DateTimeOffset.UtcNow + _quietTimeout;
        var delay = InitialBackoff;
        while (true)
        {
            var busy = DescribeBusy(profileDir);
            if (busy is not null)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new LoomException(
                        LoomErrorCodes.Busy,
                        "DSH 或插件管理器正在写入 profile（" + busy + "），已等待 " +
                        _quietTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " 秒。");
                }

                Thread.Sleep(delay);
                delay = NextBackoff(delay);
                continue;
            }

            var before = HashBaseline(profileDir);
            ProfileState state;
            try
            {
                state = ReadOnce(profileDir);
            }
            catch (IOException exception)
            {
                // File may be swapped mid-read; retry within the deadline.
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new LoomException(LoomErrorCodes.Busy, "读取 profile 失败：文件持续变化。", exception);
                }

                Thread.Sleep(delay);
                delay = NextBackoff(delay);
                continue;
            }

            var after = HashBaseline(profileDir);
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                return state;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new LoomException(
                    LoomErrorCodes.Busy,
                    "profile 在读取期间持续变化（package.json / pnpm-lock.yaml / cordis.patch.yml），已放弃本次采集。");
            }

            _logger.Log(LogLevel.Debug, "profile baseline changed during read; retrying");
            Thread.Sleep(delay);
            delay = NextBackoff(delay);
        }
    }

    private TimeSpan NextBackoff(TimeSpan current)
    {
        var next = TimeSpan.FromTicks(current.Ticks * 2);
        return next > _maxBackoff ? _maxBackoff : next;
    }

    private static string HashBaseline(string profileDir)
    {
        var manifest = Path.Combine(profileDir, DshDefaults.ManifestFile);
        var lockfile = Path.Combine(profileDir, DshDefaults.LockfileFile);
        var patch = Path.Combine(profileDir, DshDefaults.PatchFile);
        return string.Join("|",
            Hashing.Sha256HexOrDefault(manifest),
            Hashing.Sha256HexOrDefault(lockfile),
            Hashing.Sha256HexOrDefault(patch));
    }

    /// <summary>Returns a human readable reason when the profile is currently being written, otherwise null.</summary>
    private static string? DescribeBusy(string profileDir)
    {
        var lockPath = Path.Combine(profileDir, DshDefaults.ProfileLockFile);
        if (File.Exists(lockPath))
        {
            var pid = TryReadInt(lockPath);
            if (pid is null || IsProcessAlive(pid.Value)) return "profile\\lock";
        }

        var manifestLock = Path.Combine(profileDir, DshDefaults.ManifestLockFile);
        if (File.Exists(manifestLock))
        {
            try
            {
                var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(manifestLock);
                // DSH treats a manifest lock older than 120 s as stale; mirror that window.
                if (age < TimeSpan.FromSeconds(120)) return "package.json.lock";
            }
            catch
            {
                return "package.json.lock";
            }
        }

        var runFile = Path.Combine(profileDir, DshDefaults.PluginManagerDir, DshDefaults.PluginManagerRunFile);
        if (File.Exists(runFile))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(runFile, Encoding.UTF8)) as JsonObject;
                var pid = FindPid(root);
                if (pid is not null && IsProcessAlive(pid.Value)) return ".plugin-manager\\run.json";
            }
            catch
            {
                // Unreadable run record: do not block forever on garbage.
            }
        }

        return null;
    }

    private static int? FindPid(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        foreach (var pair in obj)
        {
            if (pair.Key.Equals("pid", StringComparison.OrdinalIgnoreCase) && pair.Value is JsonValue value)
            {
                if (value.TryGetValue<int>(out var pid)) return pid;
                if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed)) return parsed;
            }
        }

        return null;
    }

    private static int? TryReadInt(string path)
    {
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8).Trim();
            var match = Regex.Match(text, @"\d+");
            return match.Success && int.TryParse(match.Value, out var pid) ? pid : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private ProfileState ReadOnce(string profileDir)
    {
        var state = new ProfileState { ProfileDir = profileDir };

        var manifestPath = Path.Combine(profileDir, DshDefaults.ManifestFile);
        state.PackageJsonPath = manifestPath;
        if (File.Exists(manifestPath))
        {
            state.PackageJsonText = ReadAllTextShared(manifestPath);
            state.FileSha256[DshDefaults.ManifestFile] = Hashing.Sha256HexOrDefault(manifestPath);
            ParsePackageJson(state);
        }

        var lockfilePath = Path.Combine(profileDir, DshDefaults.LockfileFile);
        state.LockfilePath = lockfilePath;
        if (File.Exists(lockfilePath))
        {
            state.LockfileSha256 = Hashing.Sha256HexOrDefault(lockfilePath);
            state.FileSha256[DshDefaults.LockfileFile] = state.LockfileSha256;
            try
            {
                ApplyLockfileResolutions(state, ReadAllTextShared(lockfilePath));
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "pnpm-lock.yaml 解析失败，解析字段将留空");
            }
        }

        var patchPath = Path.Combine(profileDir, DshDefaults.PatchFile);
        state.PatchPath = patchPath;
        if (File.Exists(patchPath))
        {
            state.PatchSha256 = Hashing.Sha256HexOrDefault(patchPath);
            state.FileSha256[DshDefaults.PatchFile] = state.PatchSha256;
        }

        state.PatchBackups = ListPatchBackups(profileDir);
        TryApplyBackupHashes(state, profileDir);

        var compatibilityPath = Path.Combine(profileDir, DshDefaults.CompatibilityFile);
        if (File.Exists(compatibilityPath))
        {
            state.CompatibilityPath = compatibilityPath;
            var compatibility = ProfileFiles.ReadCompatibility(compatibilityPath);
            state.FileSha256[DshDefaults.CompatibilityFile] = compatibility.Sha256 ?? "";
            // Convention (documented in docs/core-flows.md): a non-null path with a null sha means the file
            // exists but the official reader would reject it (rewritable = false), so Loom must not overwrite it.
            state.CompatibilitySha256 = compatibility.Rewritable ? compatibility.Sha256 : null;
        }

        var cordisYmlPath = Path.Combine(profileDir, DshDefaults.CordisFile);
        if (File.Exists(cordisYmlPath))
        {
            state.CordisYmlPath = cordisYmlPath;
            state.CordisYmlSha256 = Hashing.Sha256HexOrDefault(cordisYmlPath);
            state.FileSha256[DshDefaults.CordisFile] = state.CordisYmlSha256;
        }

        var marketState = Path.Combine(profileDir, ".dsh-market", "state.json");
        if (File.Exists(marketState))
        {
            state.FileSha256[".dsh-market/state.json"] = Hashing.Sha256HexOrDefault(marketState);
        }

        state.Plugins = ReadPlugins(state);
        return state;
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void ParsePackageJson(ProfileState state)
    {
        var text = state.PackageJsonText ?? "";
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException exception)
        {
            throw new LoomException(LoomErrorCodes.Failed, "package.json 不是合法 JSON：" + exception.Message, exception);
        }

        if (root is null)
        {
            throw new LoomException(LoomErrorCodes.Failed, "package.json 顶层必须是对象。");
        }

        if (root["dependencies"] is JsonObject dependencies)
        {
            foreach (var pair in dependencies)
            {
                state.Dependencies[pair.Key] = new DependencySnapshot
                {
                    Spec = JsonScalarText(pair.Value),
                };
            }
        }

        if (root["dsh"] is JsonObject dsh && dsh["profile"] is JsonObject profile
            && profile["bundles"] is JsonArray bundles)
        {
            foreach (var item in bundles)
            {
                var name = JsonScalarText(item);
                if (!string.IsNullOrWhiteSpace(name)) state.Bundles.Add(name);
            }
        }

        state.HasManifest = true;
    }

    private static string JsonScalarText(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text)) return text;
            return value.ToJsonString();
        }

        return node.ToJsonString();
    }

    /// <summary>
    /// Minimal pnpm-lock.yaml reader (v6/v9): top-level importer dependencies
    /// (specifier/version) plus package resolution integrity. It is intentionally not a YAML parser.
    /// </summary>
    private static void ApplyLockfileResolutions(ProfileState state, string text)
    {
        var importerDeps = new Dictionary<string, (string? Specifier, string? Version)>(StringComparer.Ordinal);
        var integrity = new Dictionary<string, string>(StringComparer.Ordinal);

        var section = LockSection.None;
        string? importer = null;
        string? dependencySection = null;
        string? currentDependency = null;
        string? currentPackage = null;

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            var trimmedStart = rawLine.TrimStart();
            if (trimmedStart.StartsWith("#", StringComparison.Ordinal)) continue;

            var indent = rawLine.Length - trimmedStart.Length;
            var line = trimmedStart.TrimEnd();

            if (indent == 0)
            {
                section = line switch
                {
                    "importers:" => LockSection.Importers,
                    "packages:" => LockSection.Packages,
                    "snapshots:" => LockSection.Snapshots,
                    _ => LockSection.None,
                };
                importer = null;
                dependencySection = null;
                currentDependency = null;
                currentPackage = null;
                continue;
            }

            if (section == LockSection.Importers)
            {
                if (indent == 2 && line.EndsWith(':'))
                {
                    importer = UnquoteKey(line[..^1]);
                    dependencySection = null;
                    currentDependency = null;
                    continue;
                }

                if (indent == 4 && line.EndsWith(':'))
                {
                    dependencySection = UnquoteKey(line[..^1]);
                    currentDependency = null;
                    continue;
                }

                if (indent == 6 && line.EndsWith(':'))
                {
                    currentDependency = UnquoteKey(line[..^1]);
                    if (importer == "." && dependencySection is "dependencies" or "devDependencies" or "optionalDependencies")
                    {
                        importerDeps.TryAdd(currentDependency, (null, null));
                    }

                    continue;
                }

                if (indent == 8 && currentDependency is not null && importer == ".")
                {
                    if (TrySplitKeyValue(line, out var key, out var value))
                    {
                        var existing = importerDeps.TryGetValue(currentDependency, out var tuple) ? tuple : (null, null);
                        if (key == "specifier") existing.Specifier = value;
                        else if (key == "version") existing.Version = value;
                        importerDeps[currentDependency] = existing;
                    }
                }

                continue;
            }

            if (section == LockSection.Packages)
            {
                if (indent == 2 && line.EndsWith(':') && !line.Contains(": ", StringComparison.Ordinal))
                {
                    currentPackage = UnquoteKey(line[..^1]);
                    continue;
                }

                if (indent == 4 && currentPackage is not null && line.StartsWith("resolution:", StringComparison.Ordinal))
                {
                    var match = Regex.Match(line, @"integrity:\s*([^\s,}]+)");
                    if (match.Success) integrity[currentPackage] = match.Groups[1].Value;
                    currentPackage = null;
                }
            }
        }

        foreach (var dependency in state.Dependencies.Values)
        {
            dependency.Resolved = null;
            dependency.Integrity = null;
        }

        foreach (var pair in state.Dependencies)
        {
            if (importerDeps.TryGetValue(pair.Key, out var info))
            {
                pair.Value.Spec = string.IsNullOrEmpty(info.Specifier) ? pair.Value.Spec : info.Specifier!;
                pair.Value.Resolved = info.Version;
                if (info.Version is not null
                    && integrity.TryGetValue(pair.Key + "@" + info.Version, out var hash))
                {
                    pair.Value.Integrity = hash;
                }
            }
        }
    }

    private static bool TrySplitKeyValue(string line, out string key, out string value)
    {
        key = "";
        value = "";
        var separator = line.IndexOf(':');
        if (separator <= 0) return false;
        key = UnquoteKey(line[..separator]);
        value = line[(separator + 1)..].Trim().Trim('\'', '"');
        return true;
    }

    private static string UnquoteKey(string text)
    {
        var value = text.Trim();
        if (value.Length >= 2 && (value[0] == '\'' && value[^1] == '\'' || value[0] == '"' && value[^1] == '"'))
        {
            value = value[1..^1];
        }

        return value;
    }

    private static List<string> ListPatchBackups(string profileDir)
    {
        try
        {
            return Directory.EnumerateFiles(profileDir, DshDefaults.PatchFile + ".bak-*")
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => ProfileFiles.TryParseBackupTime(name, out var time) ? time : DateTimeOffset.MinValue)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Records hashes of the current .bak-* files under their relative name so the planner can
    /// remember which files were present at capture time (metadata only, never patch content).
    /// </summary>
    private static void TryApplyBackupHashes(ProfileState state, string profileDir)
    {
        foreach (var name in state.PatchBackups)
        {
            var path = Path.Combine(profileDir, name);
            var sha = Hashing.Sha256HexOrDefault(path);
            if (!string.IsNullOrEmpty(sha)) state.FileSha256[name] = sha;
        }
    }

    private List<PluginSnapshot> ReadPlugins(ProfileState state)
    {
        var plugins = new List<PluginSnapshot>();
        foreach (var pair in state.Dependencies)
        {
            plugins.Add(ReadPlugin(state.ProfileDir, pair.Key, pair.Value));
        }

        return plugins;
    }

    private PluginSnapshot ReadPlugin(string profileDir, string name, DependencySnapshot dependency)
    {
        var directory = Path.Combine(profileDir, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "package.json");
        var plugin = new PluginSnapshot
        {
            Name = name,
            Version = dependency.Resolved ?? "",
            Dir = directory,
            Source = dependency.Spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                     || dependency.Spec.StartsWith("workspace:", StringComparison.OrdinalIgnoreCase)
                     || dependency.Spec.StartsWith("link:", StringComparison.OrdinalIgnoreCase)
                ? "local"
                : "registry",
        };

        if (!File.Exists(manifestPath))
        {
            plugin.Static.Result = StaticCheckResult.Missing;
            plugin.Static.Notes.Add("node_modules 中缺少该包的 manifest");
            return plugin;
        }

        try
        {
            var text = ReadAllTextShared(manifestPath);
            var root = JsonNode.Parse(text) as JsonObject;
            var version = root?["version"] is JsonValue versionValue ? JsonScalarText(versionValue) : "";
            if (!string.IsNullOrEmpty(version)) plugin.Version = version;

            plugin.Engines = ReadStringMap(root?["engines"]);
            plugin.PeerDependencies = ReadStringMap(root?["peerDependencies"]);
            plugin.Dsh = ReadBundleDeclaration(root?["dsh"]);
            plugin.Static = BuildStaticPrecheck(plugin, directory);
        }
        catch (JsonException exception)
        {
            plugin.Static.Result = StaticCheckResult.Unverified;
            plugin.Static.Notes.Add("manifest 解析失败：" + exception.Message);
        }
        catch (IOException exception)
        {
            plugin.Static.Result = StaticCheckResult.Unverified;
            plugin.Static.Notes.Add("manifest 读取失败：" + exception.Message);
        }

        return plugin;
    }

    private static Dictionary<string, string>? ReadStringMap(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in obj)
        {
            map[pair.Key] = JsonScalarText(pair.Value);
        }

        return map;
    }

    private static BundleDeclaration? ReadBundleDeclaration(JsonNode? node)
    {
        if (node is not JsonObject dsh) return null;
        var declaration = new BundleDeclaration();
        if (dsh["bundle"] is JsonValue bundleValue)
        {
            declaration.Bundle = bundleValue.TryGetValue<bool>(out var isBundle) && isBundle;
        }
        else if (dsh["bundle"] is JsonObject bundleObject)
        {
            declaration.Bundle = true;
            if (bundleObject["platforms"] is JsonArray platforms)
            {
                declaration.Platforms = platforms.Select(JsonScalarText)
                    .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            }

            if (bundleObject["patch"] is JsonArray patches)
            {
                declaration.Patch = patches.Select(JsonScalarText)
                    .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            }
            else if (bundleObject["patch"] is JsonValue patchValue)
            {
                var patchText = JsonScalarText(patchValue);
                if (!string.IsNullOrWhiteSpace(patchText)) declaration.Patch = new List<string> { patchText };
            }
        }

        if (dsh["client"] is JsonObject client)
        {
            if (client["inject"] is JsonArray inject)
            {
                declaration.ClientInject = inject.Select(JsonScalarText)
                    .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            }
        }

        return declaration;
    }

    private static StaticPrecheck BuildStaticPrecheck(PluginSnapshot plugin, string directory)
    {
        var precheck = new StaticPrecheck();
        var declaration = plugin.Dsh;
        if (declaration is null)
        {
            precheck.Result = StaticCheckResult.NotApplicable;
            return precheck;
        }

        if (declaration.ClientInject is { Count: > 0 } inject)
        {
            precheck.ClientInject.AddRange(inject);
            precheck.Notes.Add("client 注入需要运行时解析，此处仅记录");
        }

        if (declaration.Patch is { Count: > 0 } patches)
        {
            foreach (var patch in patches)
            {
                if (patch.StartsWith("cordis:", StringComparison.OrdinalIgnoreCase)) continue;
                if (patch.StartsWith(".", StringComparison.Ordinal) || !patch.Contains(":", StringComparison.Ordinal))
                {
                    var path = Path.GetFullPath(Path.Combine(directory, patch));
                    if (File.Exists(path)) precheck.PatchFiles.Add(path);
                    else precheck.MissingRefs.Add(patch);
                }
                else
                {
                    // Package spec inside a patch is resolved by the evaluator at check time.
                    precheck.ClientInject.Add(patch);
                }
            }
        }

        if (precheck.MissingRefs.Count > 0)
        {
            precheck.Result = StaticCheckResult.Missing;
        }
        else if (precheck.ClientInject.Count > 0)
        {
            precheck.Result = StaticCheckResult.Unverified;
        }
        else
        {
            precheck.Result = StaticCheckResult.Ok;
        }

        return precheck;
    }

    private enum LockSection
    {
        None,
        Importers,
        Packages,
        Snapshots,
    }
}

/// <summary>
/// Small shared parsers for profile artefacts (compatibility.json, patch backup names).
/// Kept next to the reader so Check and Restore use exactly the same rules.
/// </summary>
internal static class ProfileFiles
{
    private static readonly Regex ExactVersion = new(
        @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.Compiled);

    private static readonly Regex PackageName = new(
        @"^(@[a-z0-9-~][a-z0-9-._~]*/)?[a-z0-9-~][a-z0-9-._~]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal readonly record struct CompatibilityFile(
        Dictionary<string, List<string>> Exemptions,
        bool Rewritable,
        string? Sha256,
        List<string> Warnings);

    /// <summary>
    /// Mirrors the official reader (app.asar lib/types/profile-resolution): a missing file authorises
    /// nothing; malformed JSON or a rejected record yields warnings and rewritable = false so callers
    /// never overwrite user content.
    /// </summary>
    internal static CompatibilityFile ReadCompatibility(string path)
    {
        var exemptions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var warnings = new List<string>();
        if (!File.Exists(path))
        {
            return new CompatibilityFile(exemptions, true, null, warnings);
        }

        string text;
        string sha;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
            sha = Hashing.Sha256Hex(path);
        }
        catch (Exception exception)
        {
            warnings.Add("compatibility.json 无法读取：" + exception.Message);
            return new CompatibilityFile(exemptions, false, null, warnings);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException exception)
        {
            warnings.Add("compatibility.json 不是合法 JSON：" + exception.Message);
            return new CompatibilityFile(exemptions, false, null, warnings);
        }

        if (root is not JsonObject obj)
        {
            warnings.Add("compatibility.json 顶层必须是对象。");
            return new CompatibilityFile(exemptions, false, null, warnings);
        }

        foreach (var pair in obj)
        {
            var separator = pair.Key.LastIndexOf('@');
            if (separator <= 0
                || !PackageName.IsMatch(pair.Key[..separator])
                || !ExactVersion.IsMatch(pair.Key[(separator + 1)..]))
            {
                warnings.Add($"compatibility.json 键 {pair.Key} 不是精确的包名@版本，记录被忽略。");
                continue;
            }

            if (pair.Value is not JsonArray versions)
            {
                warnings.Add($"compatibility.json 中 {pair.Key} 的值必须是 DSH 版本数组，记录被忽略。");
                continue;
            }

            var list = new List<string>();
            var valid = true;
            foreach (var version in versions)
            {
                var textVersion = version is JsonValue value && value.TryGetValue<string>(out var typed) ? typed : "";
                if (!ExactVersion.IsMatch(textVersion))
                {
                    valid = false;
                    break;
                }

                list.Add(textVersion);
            }

            if (!valid)
            {
                warnings.Add($"compatibility.json 中 {pair.Key} 含非精确版本，记录被忽略。");
                continue;
            }

            exemptions[pair.Key] = list.Distinct(StringComparer.Ordinal).ToList();
        }

        return new CompatibilityFile(exemptions, warnings.Count == 0, sha, warnings);
    }

    /// <summary>Parses the timestamp suffix of <c>cordis.patch.yml.bak-&lt;timestamp&gt;</c>.</summary>
    internal static bool TryParseBackupTime(string? fileName, out DateTimeOffset time)
    {
        time = DateTimeOffset.MinValue;
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        const string marker = DshDefaults.PatchFile + ".bak-";
        var index = fileName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;
        var suffix = fileName[(index + marker.Length)..];
        if (suffix.Length == 0) return false;

        if (suffix.All(char.IsDigit))
        {
            if (suffix.Length >= 13 && long.TryParse(suffix[..13], out var millis))
            {
                time = DateTimeOffset.FromUnixTimeMilliseconds(millis);
                return true;
            }

            if (suffix.Length is 10 or 11 && long.TryParse(suffix, out var seconds))
            {
                time = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            }
        }

        if (DateTimeOffset.TryParse(
                suffix,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            time = parsed;
            return true;
        }

        return false;
    }

    /// <summary>All exact runtime versions granted for the given package@version key.</summary>
    internal static IReadOnlyList<string> VersionsFor(Dictionary<string, List<string>> exemptions, string packageAtVersion)
        => exemptions.TryGetValue(packageAtVersion, out var versions) ? versions : Array.Empty<string>();
}
