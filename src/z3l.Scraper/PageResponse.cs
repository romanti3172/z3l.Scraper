using System.Text;
using System.Text.RegularExpressions;

namespace z3l.Scraper;

/// <summary>A loaded page.</summary>
public sealed partial class PageResponse
{
    static PageResponse()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // windows-1251, koi8-r, shift_jis...
    }

    /// <summary>Creates a response from raw bytes.</summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="url">Final URL after redirects; relative links are resolved against it.</param>
    /// <param name="body">Response body.</param>
    /// <param name="contentType">Content-Type header, used to pick the text encoding.</param>
    /// <param name="headers">Response headers.</param>
    public PageResponse(int statusCode, string url, byte[] body, string? contentType = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        StatusCode = statusCode;
        Url = url;
        Body = body ?? [];
        ContentType = contentType;
        Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Creates a response from text (loaders that get text, such as browsers).</summary>
    public static PageResponse FromText(int statusCode, string url, string text, string? contentType = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = new PageResponse(statusCode, url, Encoding.UTF8.GetBytes(text ?? string.Empty), contentType, headers);
        response._text = text ?? string.Empty;
        return response;
    }

    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Final URL after redirects.</summary>
    public string Url { get; }

    /// <summary>Raw body.</summary>
    public byte[] Body { get; }

    /// <summary>Content-Type header.</summary>
    public string? ContentType { get; }

    /// <summary>Response headers (case-insensitive names).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>True for 2xx codes.</summary>
    public bool IsSuccessStatusCode => StatusCode is >= 200 and < 300;

    /// <summary>
    /// Body decoded as text: the charset from Content-Type, then from &lt;meta charset&gt;, then UTF-8.
    /// Decoded once on first use.
    /// </summary>
    public string Text => _text ??= Decode();

    private string? _text;

    private string Decode()
    {
        if (Body.Length == 0) return string.Empty;

        var encoding = GetEncoding(CharsetFromContentType(ContentType)) ?? GetEncoding(CharsetFromMeta(Body)) ?? Encoding.UTF8;

        // BOM wins over the declared charset
        var bomLength = 0;
        if (Body.AsSpan().StartsWith(Encoding.UTF8.Preamble)) { encoding = Encoding.UTF8; bomLength = 3; }

        return encoding.GetString(Body, bomLength, Body.Length - bomLength);
    }

    private static Encoding? GetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return null;
        try { return Encoding.GetEncoding(charset.Trim().Trim('"', '\'')); }
        catch (ArgumentException) { return null; }
    }

    private static string? CharsetFromContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return null;
        var match = CharsetRegex().Match(contentType);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? CharsetFromMeta(byte[] body)
    {
        var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 2048));
        var match = CharsetRegex().Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""charset\s*=\s*["']?([\w\-:.]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetRegex();
}
