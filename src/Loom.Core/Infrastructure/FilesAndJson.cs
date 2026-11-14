using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Loom.Core.Infrastructure;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Keep Chinese readable in reports/config/state (still valid JSON).
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>UTF-8 (no BOM) atomic text writes: temp file + rename.</summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(temp, content, Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    public static void WriteAllBytes(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    public static void WriteJson<T>(string path, T value) => WriteAllText(path, JsonDefaults.Serialize(value));

    public static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path, Encoding.UTF8);
        return JsonDefaults.Deserialize<T>(text);
    }

    /// <summary>Reads JSON; a corrupt file is renamed to .corrupt-&lt;ts&gt; and null is returned.</summary>
    public static T? ReadJsonSafe<T>(string path) where T : class
    {
        try
        {
            return ReadJson<T>(path);
        }
        catch (Exception)
        {
            TryQuarantine(path);
            return null;
        }
    }

    public static void TryQuarantine(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var target = $"{path}.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
            File.Move(path, target, overwrite: true);
        }
        catch
        {
            // ignore: quarantine is best effort
        }
    }
}

public static class Hashing
{
    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256HexOfText(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha512Base64(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA512.Create();
        return Convert.ToBase64String(sha.ComputeHash(stream));
    }

    public static string Sha256HexOrDefault(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
        try
        {
            return Sha256Hex(path);
        }
        catch
        {
            return "";
        }
    }
}

public static class FileSystemUtil
{
    public static long DirectorySize(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; } catch { /* best effort */ }
        }

        return total;
    }

    public static void CopyFile(string source, string destination, bool backupExisting)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (backupExisting && File.Exists(destination))
        {
            var backup = $"{destination}.loom-bak-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
            File.Copy(destination, backup, overwrite: true);
        }

        File.Copy(source, destination, overwrite: true);
    }

    public static long AvailableBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "C:\\";
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    public static void DeleteDirectoryBestEffort(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    public static void MoveDirectoryBestEffort(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            Directory.Move(source, destination);
        }
        catch
        {
            // best effort
        }
    }
}
