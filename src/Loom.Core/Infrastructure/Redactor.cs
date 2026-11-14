using System.Text.RegularExpressions;

namespace Loom.Core.Infrastructure;

/// <summary>Removes credentials/tokens from any text that is written to logs or console output.</summary>
public static class Redactor
{
    private static readonly Regex BearerValue = new(
        @"(?i)\b(bearer\s+)([A-Za-z0-9._~+/=-]{8,})", RegexOptions.Compiled);

    private static readonly Regex BasicValue = new(
        @"(?i)\b(basic\s+)([A-Za-z0-9+/=]{8,})", RegexOptions.Compiled);

    private static readonly Regex KeyValue = new(
        @"(?i)(_authToken|_auth|authorization|password|passwd|secret|token)(\s*[:=]\s*)([^\s,;&""']+)",
        RegexOptions.Compiled);

    private static readonly Regex NpmToken = new(@"(?i)(npm_[A-Za-z0-9]{20,})", RegexOptions.Compiled);
    private static readonly Regex GitHubToken = new(@"(?i)(ghp_[A-Za-z0-9]{20,})", RegexOptions.Compiled);
    private static readonly Regex UrlWithQuery = new(
        @"(?i)(https?://)([^/\s""']+)(\?[^\s""']*)", RegexOptions.Compiled);

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";

        var result = value;
        // Bearer/Basic values are redacted before the generic key=value rule, otherwise
        // "Authorization: Bearer abc" would only redact the word "Bearer".
        result = BearerValue.Replace(result, match => match.Groups[1].Value + "***");
        result = BasicValue.Replace(result, match => match.Groups[1].Value + "***");
        result = KeyValue.Replace(result, match => $"{match.Groups[1].Value}{match.Groups[2].Value}***");
        result = NpmToken.Replace(result, "***");
        result = GitHubToken.Replace(result, "***");
        result = UrlWithQuery.Replace(result, match => $"{match.Groups[1].Value}{match.Groups[2].Value}***");
        return result;
    }

    public static string RedactObject(object? value) => Redact(value?.ToString());
}
