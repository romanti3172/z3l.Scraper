namespace z3l.Scraper;

internal static class UrlHelper
{
    /// <summary>
    /// Makes an absolute http(s) URL. A value without a scheme is relative to <paramref name="baseUrl"/>.
    /// The value is parsed as relative explicitly: on Windows "//host/x" parses as an absolute UNC path and on Unix
    /// "/x" as an absolute file path, so <c>Uri.TryCreate(value, UriKind.Absolute)</c> cannot tell links apart.
    /// </summary>
    public static bool TryResolve(string? baseUrl, string? url, out string absolute)
    {
        absolute = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var value = url.Trim();
        Uri? result;
        if (HasScheme(value))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out result)) return false;
        }
        else
        {
            if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || !IsHttp(baseUri)) return false;
            if (!Uri.TryCreate(value, UriKind.Relative, out var relative) || !Uri.TryCreate(baseUri, relative, out result)) return false;
        }

        if (!IsHttp(result) || string.IsNullOrEmpty(result.Host)) return false;

        absolute = result.AbsoluteUri;
        return true;
    }

    /// <summary>Checks and normalizes an absolute http(s) URL.</summary>
    public static bool TryNormalize(string? url, out string absolute) => TryResolve(null, url, out absolute);

    /// <summary>RFC 3986: scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) ":"</summary>
    private static bool HasScheme(string value)
    {
        if (value.Length == 0 || !char.IsAsciiLetter(value[0])) return false;
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == ':') return true;
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.')) return false;
        }
        return false;
    }

    private static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}
