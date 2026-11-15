using System.Text.Json;
using Microsoft.Extensions.Logging;
using z3l.FastHtmlParser;

namespace z3l.Scraper;

/// <summary>What a handler gets: the request, the loaded page, and ways to queue more pages and emit items.</summary>
/// <typeparam name="TItem">Type of the scraped items.</typeparam>
public sealed class ScrapeContext<TItem>
{
    private readonly ScraperRun<TItem> _run;
    private HtmlParser? _html;

    internal ScrapeContext(ScraperRun<TItem> run, ScrapeRequest request, PageResponse response, CancellationToken cancellationToken)
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

    /// <summary>The body deserialized from JSON (property names ignore case).</summary>
    public T? Json<T>() => JsonSerializer.Deserialize<T>(Response.Text, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Queues a page. A relative URL resolves against the current page.
    /// Returns false for a duplicate or for a URL that is not http(s).
    /// </summary>
    /// <param name="url">Absolute or relative URL; null and empty are ignored.</param>
    /// <param name="label">Handler label; <see cref="ScrapeRequest.DefaultLabel"/> when omitted.</param>
    /// <param name="userData">Data for the next handler.</param>
    /// <param name="priority">Queue priority.</param>
    public bool Enqueue(string? url, string? label = null, object? userData = null, ScrapePriority priority = ScrapePriority.Normal)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Enqueue(new ScrapeRequest(url, label) { UserData = userData, Priority = priority });
    }

    /// <summary>Queues pages with one label. Returns how many were new.</summary>
    public int Enqueue(IEnumerable<string?> urls, string? label = null, ScrapePriority priority = ScrapePriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var added = 0;
        foreach (var url in urls)
        {
            if (Enqueue(url, label, null, priority)) added++;
        }
        return added;
    }

    /// <summary>Queues a request. A relative URL resolves against the current page. Returns false for a duplicate.</summary>
    public bool Enqueue(ScrapeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            if (!Uri.TryCreate(new Uri(Response.Url), request.Url, out uri) || uri.Scheme is not ("http" or "https")) return false;
        }
        if (uri.AbsoluteUri != request.Url) request = request with { Url = uri.AbsoluteUri };

        return _run.Enqueue(request);
    }

    /// <summary>Hands an item to the consumer. Waits while the consumer is <see cref="ScraperOptions.ItemBufferSize"/> items behind.</summary>
    public ValueTask EmitAsync(TItem item) => _run.EmitAsync(item, CancellationToken);
}
