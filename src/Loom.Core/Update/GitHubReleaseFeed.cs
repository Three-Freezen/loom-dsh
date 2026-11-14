using System.Net;
using System.Net.Http;
using System.Text.Json;
using Loom.Core.Infrastructure;

namespace Loom.Core.Update;

/// <summary>Raised when the public repository has no published release yet (normal before the first release).</summary>
internal sealed class NoReleaseException : Exception
{
    public NoReleaseException() : base("上游仓库暂无发布版本。")
    {
    }
}

/// <summary>
/// GitHub Releases feed for the public <c>Three-Freezen/loom-dsh</c> repository (anonymous access).
/// Modeled on DeskOrder's GitHubReleaseFeed:
/// <list type="bullet">
/// <item>Version probe uses the web endpoint <c>releases/latest</c> and reads the 302 <c>Location</c>.
/// The REST API's anonymous quota (60 requests/hour/IP) is easily exhausted behind shared NAT/proxy
/// exits, which used to surface as a bogus "GitHub rate limited" error; the REST API stays as a
/// fallback for gateways that block the redirect.</item>
/// <item>The installer is uploaded under a fixed asset name, so
/// <c>releases/latest/download/loom-dsh-Setup.exe</c> always resolves to the newest build.</item>
/// <item>Downloads stream to a <c>.part</c> temp name and atomically rename, so an interrupted
/// download can never leave a truncated file that looks like a setup program.</item>
/// </list>
/// </summary>
internal static class GitHubReleaseFeed
{
    internal const string RepoOwner = "Three-Freezen";
    internal const string RepoName = "loom-dsh";
    internal const string AssetName = AppInstallInfo.SetupAssetName;

    /// <summary>Smallest plausible setup asset. Guards against HTML error pages / truncated downloads.</summary>
    internal const long MinimumAssetBytes = 1L * 1024 * 1024;

    internal static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";
    internal static string ReleasesLatestUrl => RepoUrl + "/releases/latest";
    internal static string ReleasesApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    internal static string AssetUrl => $"{ReleasesLatestUrl}/download/{AssetName}";

    private static readonly HttpClient RedirectlessHttp = CreateHttp(allowAutoRedirect: false);
    private static readonly HttpClient PlainHttp = CreateHttp(allowAutoRedirect: true);

    private static HttpClient CreateHttp(bool allowAutoRedirect)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        // GitHub requires a User-Agent; reuse the product name so traffic is identifiable.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Loom-DSH-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Latest published tag (including its <c>v</c> prefix). Throws <see cref="NoReleaseException"/>
    /// when the repository has no release yet, and <see cref="HttpRequestException"/> on network errors.
    /// </summary>
    internal static async Task<string?> ResolveLatestTagAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesLatestUrl);
            using var response = await RedirectlessHttp
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var location = response.Headers.Location;
            if (location is null)
            {
                if (response.StatusCode == HttpStatusCode.NotFound) throw new NoReleaseException();
                throw new HttpRequestException(
                    $"releases/latest 未重定向（HTTP {(int)response.StatusCode}）", null, response.StatusCode);
            }

            var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
            var tag = ExtractTag(path);
            if (tag is null) throw new NoReleaseException();
            return tag;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Fallback: REST API (some gateways strip the redirect; 403 here means quota exhausted).
            using var response = await PlainHttp.GetAsync(ReleasesApiUrl, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new NoReleaseException();
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("tag_name", out var tagElement)) throw new NoReleaseException();
            var tag = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(tag)) throw new NoReleaseException();
            return tag;
        }
    }

    /// <summary>Extracts the tag from a releases redirect path (<c>/OWNER/REPO/releases/tag/v1.2.3</c>).</summary>
    internal static string? ExtractTag(string? locationPath)
    {
        if (string.IsNullOrWhiteSpace(locationPath)) return null;
        const string marker = "/tag/";
        var index = locationPath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var tag = locationPath[(index + marker.Length)..].Trim('/');
        if (tag.Length == 0) return null;
        var cut = tag.IndexOfAny(new[] { '?', '#' });
        return cut >= 0 ? tag[..cut] : tag;
    }

    /// <summary>
    /// Returns the normalized remote version when it is newer than <paramref name="currentVersion"/>,
    /// otherwise null (unparsable tags are treated as "no update" — conservative by design).
    /// </summary>
    internal static string? ParseNewerVersion(string? remoteTag, string currentVersion)
    {
        var remoteText = remoteTag?.Trim();
        if (string.IsNullOrEmpty(remoteText)) return null;
        if (remoteText[0] is 'v' or 'V') remoteText = remoteText[1..];
        if (!SemVersion.TryParse(remoteText, out var remote)) return null;
        if (!SemVersion.TryParse(currentVersion.Split('+')[0], out var current)) return null;
        return remote > current ? remote.ToString() : null;
    }

    /// <summary>Streams <paramref name="url"/> to <paramref name="targetPath"/> (via <c>.part</c> + rename).</summary>
    internal static async Task DownloadToFileAsync(
        string url,
        string targetPath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var partPath = targetPath + ".part";
        try
        {
            using var response = await PlainHttp
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1;
            if (total is > 0 and < MinimumAssetBytes)
            {
                throw new InvalidOperationException("下载内容不是有效的安装包（体积过小）。");
            }

            await using (var destination = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await using var source = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                var buffer = new byte[81920];
                long read = 0;
                int count;
                while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    read += count;
                    if (total > 0) progress?.Report((int)(read * 100 / total));
                }
            }

            File.Move(partPath, targetPath, overwrite: true);
            progress?.Report(100);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }
}
