using System.Buffers;
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
        _body = body ?? [];
        ContentType = contentType;
        Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private PageResponse(int statusCode, string url, string text, string? contentType, IReadOnlyDictionary<string, string>? headers)
    {
        StatusCode = statusCode;
        Url = url;
        _text = text;
        ContentType = contentType;
        Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a response from text (loaders that get text, such as browsers). <see cref="Body"/> is made from the text
    /// only if something asks for it, so the page is not kept twice.
    /// </summary>
    public static PageResponse FromText(int statusCode, string url, string text, string? contentType = null, IReadOnlyDictionary<string, string>? headers = null) =>
        new(statusCode, url, text ?? string.Empty, contentType, headers);

    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Final URL after redirects.</summary>
    public string Url { get; }

    /// <summary>Raw body; for a response made from text, the text in UTF-8.</summary>
    public byte[] Body => Volatile.Read(ref _body) ?? EncodeText();

    private byte[]? _body;

    private byte[] EncodeText()
    {
        var bytes = Encoding.UTF8.GetBytes(_text!);
        return Interlocked.CompareExchange(ref _body, bytes, null) ?? bytes;
    }

    /// <summary>The body is not empty (without making the bytes of a text response).</summary>
    internal bool HasBody => Volatile.Read(ref _body) is { } body ? body.Length > 0 : _text!.Length > 0;

    /// <summary>Content-Type header.</summary>
    public string? ContentType { get; }

    /// <summary>Response headers (case-insensitive names).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>True for 2xx codes.</summary>
    public bool IsSuccessStatusCode => StatusCode is >= 200 and < 300;

    /// <summary>
    /// Body decoded as text, the way browsers do it: a byte order mark, then the charset from Content-Type,
    /// then &lt;meta charset&gt; or the XML declaration (HTML and XML only), then UTF-8. Made once on first use.
    /// The scraper reads pages without this string (the response checks, <see cref="ScrapeContext.Html"/>,
    /// <see cref="ScrapeContext.Json{T}()"/>), so a page becomes a string only when a handler asks for it.
    /// </summary>
    public string Text => _text ??= Decode();

    private string? _text;

    private string Decode()
    {
        if (!HasBody) return string.Empty;
        var encoding = DetectEncoding(out var preamble);
        return encoding.GetString(Body.AsSpan(preamble));
    }

    /// <summary>The encoding of the body and the length of its byte order mark.</summary>
    private Encoding DetectEncoding(out int preamble)
    {
        var span = Body.AsSpan();
        if (span.StartsWith(Encoding.UTF8.Preamble)) { preamble = 3; return Encoding.UTF8; }
        if (span.StartsWith(Encoding.Unicode.Preamble)) { preamble = 2; return Encoding.Unicode; }
        if (span.StartsWith(Encoding.BigEndianUnicode.Preamble)) { preamble = 2; return Encoding.BigEndianUnicode; }

        preamble = 0;
        return GetEncoding(CharsetFromContentType(ContentType))
               ?? (IsMarkup(ContentType) ? GetEncoding(CharsetFromMarkup(Body)) : null)
               ?? Encoding.UTF8;
    }

    // While the scraper handles a page, its text is decoded into a pooled buffer instead of a string: the response checks
    // and the parser read it in place, and it goes back to the pool when the handler ends (ReleaseText).
    private char[]? _chars;
    private int _charCount;
    private bool _released;

    /// <summary>Bigger pages become strings: the pool should not keep huge arrays.</summary>
    private const int MaxPooledChars = 4 * 1024 * 1024;

    /// <summary>
    /// The text without making a string: the string if there is one, otherwise the text decoded into a pooled buffer,
    /// valid until <see cref="ReleaseText"/>. For the scraper, while it handles the page.
    /// </summary>
    internal ReadOnlyMemory<char> TextMemory
    {
        get
        {
            if (Volatile.Read(ref _text) is { } text) return text.AsMemory();
            if (Volatile.Read(ref _chars) is { } chars) return chars.AsMemory(0, _charCount);
            if (Volatile.Read(ref _released) || !HasBody) return Text.AsMemory();

            var encoding = DetectEncoding(out var preamble);
            var bytes = Body.AsSpan(preamble);
            var maxChars = encoding.GetMaxCharCount(bytes.Length);
            if (maxChars > MaxPooledChars) return Text.AsMemory();

            var rented = ArrayPool<char>.Shared.Rent(maxChars);
            var count = encoding.GetChars(bytes, rented);
            _charCount = count;     // threads that decode at the same time get the same count
            if (Interlocked.CompareExchange(ref _chars, rented, null) is { } first)
            {
                ArrayPool<char>.Shared.Return(rented);
                return first.AsMemory(0, count);
            }
            return rented.AsMemory(0, count);
        }
    }

    /// <summary>The scraper is done with the page: the pooled text goes back to the pool. <see cref="Text"/> still works.</summary>
    internal void ReleaseText()
    {
        Volatile.Write(ref _released, true);
        if (Interlocked.Exchange(ref _chars, null) is { } chars) ArrayPool<char>.Shared.Return(chars);
    }

    /// <summary>The text is in a pooled buffer.</summary>
    internal bool HasPooledText => Volatile.Read(ref _chars) != null;

    /// <summary>The text has been made into a string.</summary>
    internal bool HasTextString => Volatile.Read(ref _text) != null;

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
