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
    /// Body decoded as text, the way browsers do it: a byte order mark, then the charset from Content-Type,
    /// then &lt;meta charset&gt; or the XML declaration (HTML and XML only), then UTF-8. Decoded once on first use.
    /// </summary>
    public string Text => _text ??= Decode();

    private string? _text;

    private string Decode()
    {
        if (Body.Length == 0) return string.Empty;

        var span = Body.AsSpan();
        if (span.StartsWith(Encoding.UTF8.Preamble)) return Encoding.UTF8.GetString(span[3..]);
        if (span.StartsWith(Encoding.Unicode.Preamble)) return Encoding.Unicode.GetString(span[2..]);
        if (span.StartsWith(Encoding.BigEndianUnicode.Preamble)) return Encoding.BigEndianUnicode.GetString(span[2..]);

        var encoding = GetEncoding(CharsetFromContentType(ContentType))
                       ?? (IsMarkup(ContentType) ? GetEncoding(CharsetFromMarkup(Body)) : null)
                       ?? Encoding.UTF8;
        return encoding.GetString(Body);
    }

    /// <summary>Finds an encoding by its label; labels that .NET does not know but sites use are mapped first.</summary>
    internal static Encoding? GetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return null;

        var name = charset.Trim().Trim('"', '\'');
        if (Aliases.TryGetValue(name, out var alias)) name = alias;

        try { return Encoding.GetEncoding(name); }
        catch (ArgumentException) { return null; }
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["utf8"] = "utf-8",
        ["cp1251"] = "windows-1251",
        ["cp-1251"] = "windows-1251",
        ["win-1251"] = "windows-1251",
        ["win1251"] = "windows-1251",
        ["windows1251"] = "windows-1251",
        ["x-cp1251"] = "windows-1251",
        ["cp1252"] = "windows-1252",
        ["win-1252"] = "windows-1252",
        // browsers decode these as windows-1252 (WHATWG Encoding Standard): 0x80 is "€", not a control character
        ["iso-8859-1"] = "windows-1252",
        ["iso8859-1"] = "windows-1252",
        ["latin1"] = "windows-1252",
        ["l1"] = "windows-1252",
        ["ascii"] = "windows-1252",
        ["us-ascii"] = "windows-1252",
    };

    private static bool IsMarkup(string? contentType) =>
        contentType is null
        || contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase);

    private static string? CharsetFromContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return null;
        var match = CharsetRegex().Match(contentType);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? CharsetFromMarkup(byte[] body)
    {
        var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 2048));
        var match = head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ? XmlEncodingRegex().Match(head) : CharsetRegex().Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""charset\s*=\s*["']?([\w\-:.]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetRegex();

    [GeneratedRegex("""^<\?xml[^>]*?encoding\s*=\s*["']([\w\-:.]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex XmlEncodingRegex();
}
