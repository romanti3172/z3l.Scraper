using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using z3l.FastHtmlParser;
using z3l.Scraper.Crawling;

namespace z3l.Scraper;

/// <summary>
/// What a handler gets: the request, the loaded page, and ways to queue more pages and emit items.
/// Valid only while the handler runs.
/// </summary>
public sealed class ScrapeContext
{
    private readonly ScraperRun _run;
    private readonly object _lock = new();
    private HtmlParser? _html;
    private Sitemap? _sitemap;
    private List<object>? _items;
    private bool _completed;

    internal ScrapeContext(ScraperRun run, ScrapeRequest request, PageResponse response, CancellationToken cancellationToken)
    {
        _run = run;
        Request = request;
        Response = response;
        CancellationToken = cancellationToken;
    }

    /// <summary>The request being handled.</summary>
    public ScrapeRequest Request { get; }

    /// <summary>The loaded page.</summary>
    public PageResponse Response { get; }

    /// <summary>Cancelled when the run stops.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Logger of the scraper.</summary>
    public ILogger Logger => _run.Logger;

    /// <summary>
    /// The page parsed with z3l.FastHtmlParser, made on first use; relative links resolve against the final page URL.
    /// </summary>
    public HtmlParser Html => _html ??= new HtmlParser(Response.Text, Response.Url);

    /// <summary>The body deserialized from JSON (web defaults: property names ignore case).</summary>
    [RequiresUnreferencedCode("Deserializing arbitrary types needs metadata that trimming can remove; use Json(JsonTypeInfo<T>) in trimmed or AOT apps.")]
    [RequiresDynamicCode("Deserializing arbitrary types can need runtime code generation; use Json(JsonTypeInfo<T>) in AOT apps.")]
    public T? Json<T>() => JsonSerializer.Deserialize<T>(Response.Text, JsonOptions);

    /// <summary>The body deserialized from JSON with source-generated metadata (trimming and AOT safe).</summary>
    public T? Json<T>(JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(Response.Text, typeInfo);

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

        if (!UrlHelper.TryResolve(Response.Url, request.Url, out var absolute)) return false;
        return _run.Enqueue(absolute == request.Url ? request : request with { Url = absolute }, parent: Request);
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
    public void Emit(object item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_lock)
        {
            ThrowIfCompleted();
            (_items ??= []).Add(item);
        }
    }

    /// <summary>Ends the context: later calls throw. Returns the emitted items.</summary>
    internal List<object>? Complete()
    {
        lock (_lock)
        {
            _completed = true;
            return _items;
        }
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The handler has finished: Emit and Enqueue work only while it runs (await the work started in the handler)");
        }
    }
}
