using System.Globalization;
using System.IO.Compression;
using System.Text;
using z3l.FastHtmlParser;

namespace z3l.Scraper.Crawling;

/// <summary>A page a sitemap lists.</summary>
/// <param name="Url">The &lt;loc&gt; URL.</param>
/// <param name="LastModified">The &lt;lastmod&gt; date, if the sitemap gives one.</param>
public sealed record SitemapUrl(string Url, DateTimeOffset? LastModified);

/// <summary>
/// A parsed sitemap: the pages of a &lt;urlset&gt;, the nested sitemaps of a &lt;sitemapindex&gt;, or the lines of a text sitemap.
/// Gzipped sitemaps (.xml.gz) are unpacked.
/// </summary>
public sealed class Sitemap
{
    /// <summary>Largest unpacked sitemap read; the protocol allows 50 MB.</summary>
    internal const int MaxUnpackedBytes = 100 * 1024 * 1024;

    private Sitemap(List<SitemapUrl> urls, List<string> sitemaps)
    {
        Urls = urls;
        Sitemaps = sitemaps;
    }

    /// <summary>Pages of a &lt;urlset&gt; or of a text sitemap.</summary>
    public IReadOnlyList<SitemapUrl> Urls { get; }

    /// <summary>Nested sitemaps of a &lt;sitemapindex&gt;.</summary>
    public IReadOnlyList<string> Sitemaps { get; }

    /// <summary>Parses a loaded sitemap; a gzipped body is unpacked.</summary>
    /// <exception cref="InvalidDataException">The gzipped body unpacks to more than 100 MB.</exception>
    public static Sitemap Parse(PageResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = IsGzip(response.Body) ? Gunzip(response.Body) : response.Text;
        return Parse(text, response.Url);
    }

    /// <summary>Parses sitemap text: XML or one URL per line.</summary>
    /// <param name="text">The sitemap.</param>
    /// <param name="url">URL of the sitemap, to resolve relative locations (the protocol wants absolute ones, some sites do not care).</param>
    public static Sitemap Parse(string? text, string? url = null)
    {
        var urls = new List<SitemapUrl>();
        var sitemaps = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return new Sitemap(urls, sitemaps);

        text = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (text[0] != '<')
        {
            foreach (var line in text.Split('\n'))
            {
                if (Location(line, url) is { } location) urls.Add(new SitemapUrl(location, null));
            }
            return new Sitemap(urls, sitemaps);
        }

        using var document = new HtmlParser(text, url ?? string.Empty);
        foreach (var entry in document.FindAll("url"))
        {
            if (Location(entry.Find("loc"), url) is { } location)
            {
                urls.Add(new SitemapUrl(location, Date(entry.Find("lastmod").Text())));
            }
        }
        foreach (var entry in document.FindAll("sitemap"))
        {
            if (Location(entry.Find("loc"), url) is { } location) sitemaps.Add(location);
        }
        return new Sitemap(urls, sitemaps);
    }

    /// <summary>The URL of a &lt;loc&gt;: its text with entities decoded, or the content of a CDATA section as written.</summary>
    private static string? Location(ParserTag? tag, string? baseUrl)
    {
        if (tag == null) return null;
        var body = tag.Body()?.Trim();
        var value = body != null && body.StartsWith("<![CDATA[", StringComparison.Ordinal) && body.EndsWith("]]>", StringComparison.Ordinal)
            ? body[9..^3]
            : tag.Text();
        return Location(value, baseUrl);
    }

    private static string? Location(string? value, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return UrlHelper.TryResolve(baseUrl, value.Trim(), out var absolute) ? absolute : null;
    }

    private static DateTimeOffset? Date(string? value) =>
        DateTimeOffset.TryParse(value?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    private static bool IsGzip(byte[] body) => body.Length > 2 && body[0] == 0x1F && body[1] == 0x8B;

    private static string Gunzip(byte[] body)
    {
        using var input = new MemoryStream(body);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MaxUnpackedBytes) throw new InvalidDataException($"The sitemap unpacks to more than {MaxUnpackedBytes / 1024 / 1024} MB");
        }
        return new PageResponse(200, "", output.ToArray(), "application/xml").Text;   // BOM, XML declaration, UTF-8
    }
}
