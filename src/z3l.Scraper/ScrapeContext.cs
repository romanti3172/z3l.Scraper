using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using z3l.FastHtmlParser;
using z3l.Scraper.Crawling;

namespace z3l.Scraper;

/// <summary>What a <see cref="ScrapeContext"/> works for: a run, or <see cref="Scraper.ParseAsync(ScrapeRequest, CancellationToken)"/>.</summary>
internal interface IScrapeHost
{
    ILogger Logger { get; }

    /// <summary>Takes a request the handler of <paramref name="parent"/> queued (its URL absolute); false when it is not taken.</summary>
    bool Enqueue(ScrapeRequest request, ScrapeRequest? parent);
}

/// <summary>
/// What a handler gets: the request, the loaded page, and ways to queue more pages and emit items.
/// Valid only while the handler runs: then the page is let go, so a context kept by mistake does not keep it in memory.
/// </summary>
public sealed class ScrapeContext
{
    private readonly IScrapeHost _host;
    private readonly object _lock = new();
    private PageResponse? _response;
    private HtmlParser? _html;
    private Sitemap? _sitemap;
    private List<object>? _items;
    private bool _completed;

    internal ScrapeContext(IScrapeHost host, ScrapeRequest request, PageResponse response, CancellationToken cancellationToken)
    {
        _host = host;
        Request = request;
        _response = response;
        CancellationToken = cancellationToken;
    }

    /// <summary>The request being handled.</summary>
    public ScrapeRequest Request { get; }

    /// <summary>The loaded page.</summary>
    /// <exception cref="InvalidOperationException">The handler has finished.</exception>
    public PageResponse Response => Volatile.Read(ref _response) ?? throw Finished();

    /// <summary>Cancelled when the run stops.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Logger of the scraper.</summary>
    public ILogger Logger => _host.Logger;

    /// <summary>
    /// The page parsed with z3l.FastHtmlParser, made on first use; relative links resolve against the final page URL.
    /// The parser is disposed when the handler ends and its tags stop working: take the values you need
    /// (<c>Text()</c>, <c>AttributeHref()</c>, <c>XPathValue()</c>...), not the tags.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handler has finished.</exception>
    public HtmlParser Html => Volatile.Read(ref _html) ?? CreateParser();

    private HtmlParser CreateParser()
    {
        var response = Response;
        var parser = HtmlParser.Parse(response.TextMemory, response.Url);      // reads the text in place; disposed before the text is released
        lock (_lock)
        {
            if (_completed || _html != null)
            {
                parser.Dispose();
                if (_completed) throw Finished();
                return _html!;      // another thread of the handler made it first
            }
            _html = parser;
            return parser;
        }
    }

    /// <summary>The body deserialized from JSON (web defaults: property names ignore case).</summary>
    [RequiresUnreferencedCode("Deserializing arbitrary types needs metadata that trimming can remove; use Json(JsonTypeInfo<T>) in trimmed or AOT apps.")]
    [RequiresDynamicCode("Deserializing arbitrary types can need runtime code generation; use Json(JsonTypeInfo<T>) in AOT apps.")]
    public T? Json<T>() => JsonSerializer.Deserialize<T>(Response.TextMemory.Span, JsonOptions);

    /// <summary>The body deserialized from JSON with source-generated metadata (trimming and AOT safe).</summary>
    public T? Json<T>(JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(Response.TextMemory.Span, typeInfo);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Queues a page that needs only its link (a listing, a category). A relative URL resolves against the current page.
    /// Returns false for a duplicate, for a URL that is not http(s) (mailto:, javascript:...) and over the limits.
    /// </summary>
    /// <param name="url">Absolute or relative URL; null and empty are ignored.</param>
    /// <param name="label">Handler label: a <see cref="PageType"/> or a string; the default label when omitted.</param>
    /// <param name="priority">Queue priority.</param>
    public bool Enqueue(string? url, ScrapeLabel label = default, ScrapePriority priority = ScrapePriority.Normal)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Enqueue(new ScrapeRequest(url, label) { Priority = priority });
    }

    /// <summary>
    /// Queues a page with its data (what the current page already knows about it: a price, a category...).
    /// The handler of the label, registered with <c>Route&lt;TData&gt;</c>, gets the data typed.
    /// </summary>
    /// <param name="url">Absolute or relative URL; null and empty are ignored.</param>
    /// <param name="label">Handler label.</param>
    /// <param name="data">Data of the page.</param>
    /// <param name="priority">Queue priority.</param>
    public bool Enqueue<TData>(string? url, ScrapeLabel label, TData data, ScrapePriority priority = ScrapePriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Enqueue(new ScrapeRequest(url, label) { Data = data, Priority = priority });
    }

    /// <summary>Queues pages with one label. Returns how many were new.</summary>
    public int Enqueue(IEnumerable<string?> urls, ScrapeLabel label = default, ScrapePriority priority = ScrapePriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var added = 0;
        foreach (var url in urls)
        {
            if (Enqueue(url, label, priority)) added++;
        }
        return added;
    }

    /// <summary>Queues a request. A relative URL resolves against the current page. Returns false when it is not queued.</summary>
    public bool Enqueue(ScrapeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfCompleted();

        var page = Response.Url;
        if (!UrlHelper.TryResolve(page, request.Url, out var absolute)) return false;
        return _host.Enqueue(request with { Url = absolute, ParentUrl = page }, parent: Request);
    }

    /// <summary>The loaded page read as a sitemap: XML (urlset or sitemapindex), gzipped or not, or a text list of URLs.</summary>
    public Sitemap ParseSitemap() => _sitemap ??= Sitemap.Parse(Response);

    /// <summary>
    /// Queues what the loaded sitemap lists: its pages with <paramref name="label"/>, and the nested sitemaps of a sitemap index
    /// with <paramref name="sitemapLabel"/> (the label of this request when omitted, so one handler walks the whole tree).
    /// Returns how many requests were new.
    /// </summary>
    /// <param name="label">Label of the pages.</param>
    /// <param name="sitemapLabel">Label of nested sitemaps; the current one by default.</param>
    /// <param name="filter">Which pages to take, for example by URL or by last modification date.</param>
    public int EnqueueSitemap(ScrapeLabel label, ScrapeLabel? sitemapLabel = null, Func<SitemapUrl, bool>? filter = null)
    {
        var sitemap = ParseSitemap();
        var added = 0;
        foreach (var nested in sitemap.Sitemaps)
        {
            if (Enqueue(nested, sitemapLabel ?? new ScrapeLabel(Request.Label))) added++;
        }
        foreach (var page in sitemap.Urls)
        {
            if ((filter == null || filter(page)) && Enqueue(page.Url, label)) added++;
        }
        return added;
    }

    /// <summary>
    /// Emits an item of any type. Items reach the consumer when the handler finishes; if the handler throws, its items are
    /// dropped, so a retried page does not give duplicates. Thread-safe.
    /// </summary>
    /// <exception cref="ArgumentException">The item is a parser object or the context: they belong to the page.</exception>
    public void Emit(object item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfPageBound(item, nameof(item));
        lock (_lock)
        {
            ThrowIfCompleted();
            (_items ??= []).Add(item);
        }
    }

    /// <summary>
    /// Ends the context and lets the page go: later calls throw, the parser is disposed, and a context or a tag kept by mistake
    /// does not keep the page in memory. Returns the emitted items.
    /// </summary>
    internal List<object>? Complete()
    {
        HtmlParser? html;
        List<object>? items;
        lock (_lock)
        {
            _completed = true;
            html = _html;
            items = _items;
            _html = null;
            _sitemap = null;
            _items = null;
            Volatile.Write(ref _response, null);
        }
        html?.Dispose();
        return items;
    }

    /// <summary>
    /// Parser objects and the context belong to the page: the parser is disposed when the handler ends, and until then each of
    /// them keeps the whole page in memory. Items and request data carry values taken from the page.
    /// </summary>
    internal static void ThrowIfPageBound(object? value, string paramName)
    {
        var what = value switch
        {
            IEnumerable<ParserTag> => "z3l.FastHtmlParser objects (HtmlParser, ParserTag, lists of tags)",
            ScrapeContext => "the ScrapeContext",
            _ => null,
        };
        if (what != null)
        {
            throw new ArgumentException(
                $"Items and request data cannot be {what}: they belong to the page, which is let go when the handler ends. "
                + "Take the values you need: Text(), AttributeHref(), XPathValue()...", paramName);
        }
    }

    private void ThrowIfCompleted()
    {
        if (_completed) throw Finished();
    }

    private static InvalidOperationException Finished() =>
        new("The handler has finished: the page, Emit and Enqueue are there only while it runs (await the work started in the handler)");
}
