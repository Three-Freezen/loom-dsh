using System.IO;
using System.Text;

namespace Loom;

/// <summary>Extracts the embedded helper.mjs next to the tool data root so the Electron bridge can run it.</summary>
public static class HelperBootstrapper
{
    private const string ResourceName = "Loom.helper.mjs";

    public static void Ensure(Core.Infrastructure.LoomPaths paths)
    {
        try
        {
            var assembly = typeof(HelperBootstrapper).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var desiredHash = Core.Infrastructure.Hashing.Sha256Hex(bytes);

            paths.EnsureDirectories();
            if (File.Exists(paths.HelperPath))
            {
                try
                {
                    var current = Core.Infrastructure.Hashing.Sha256Hex(paths.HelperPath);
                    if (string.Equals(current, desiredHash, StringComparison.OrdinalIgnoreCase)) return;
                }
                catch
                {
                    // rewrite below
                }
            }

            Core.Infrastructure.AtomicFile.WriteAllBytes(paths.HelperPath, bytes);
        }
        catch
        {
            // helper extraction is best effort; check will fall back to "unknown" engine
        }
    }
}
