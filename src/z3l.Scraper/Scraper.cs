using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using z3l.Scraper.Loading;

namespace z3l.Scraper;

/// <summary>
/// Scrapes pages: takes requests from a queue, loads them, checks the responses, retries the bad ones
/// and passes the good ones to the handler registered for the request label. Handlers queue more pages and emit items.
/// </summary>
/// <typeparam name="TItem">Type of the scraped items; use a base type or <see cref="object"/> for several kinds.</typeparam>
/// <example>
/// <code>
/// using var loader = new HttpPageLoader();
/// var scraper = new Scraper&lt;Product&gt;(loader)
///     .Route("list", async ctx =&gt;
///     {
///         ctx.Enqueue(ctx.Html.XPathValues("//a[@class='product']/@href"), "product");
///         ctx.Enqueue(ctx.Html.XPathValue("//a[@rel='next']/@href"), "list");
///     })
///     .Route("product", async ctx =&gt;
///         await ctx.EmitAsync(new Product(ctx.Html.XPathValue("//h1"), ctx.Request.Url)));
///
/// await foreach (var product in scraper.RunAsync(new ScrapeRequest("https://shop.example/catalog", "list")))
///     Console.WriteLine(product);
/// </code>
/// </example>
public class Scraper<TItem>
{
    private readonly Dictionary<string, Func<ScrapeContext<TItem>, Task>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private Func<ScrapeContext<TItem>, Task>? _fallbackRoute;
    private int _running;
    private ScraperRun<TItem>? _lastRun;

    /// <summary>Creates a scraper.</summary>
    /// <param name="loader">Loads the pages, for example <see cref="HttpPageLoader"/>.</param>
    /// <param name="options">Settings; defaults when omitted.</param>
    /// <param name="logger">Logger for progress and failures.</param>
    public Scraper(IPageLoader loader, ScraperOptions? options = null, ILogger? logger = null)
    {
        Loader = loader ?? throw new ArgumentNullException(nameof(loader));
        Options = options ?? new ScraperOptions();
        Logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Loads the pages.</summary>
    public IPageLoader Loader { get; }

    /// <summary>Settings.</summary>
    public ScraperOptions Options { get; }

    /// <summary>Logger.</summary>
    public ILogger Logger { get; }

    /// <summary>Counters of the current or the last run.</summary>
    public ScraperStats Stats => _lastRun?.Stats ?? new ScraperStats();

    /// <summary>Called for every request that fails (after its retries). Called from worker threads.</summary>
    public event Action<FailedRequest>? RequestFailed;

    /// <summary>Registers the handler of a label (labels ignore case). Returns this scraper for chaining.</summary>
    public Scraper<TItem> Route(string label, Func<ScrapeContext<TItem>, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfRunning();
        _routes[label] = handler;
        return this;
    }

    /// <summary>Registers a synchronous handler of a label.</summary>
    public Scraper<TItem> Route(string label, Action<ScrapeContext<TItem>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Route(label, ctx => { handler(ctx); return Task.CompletedTask; });
    }

    /// <summary>Registers the handler of requests whose label has no handler of its own.</summary>
    public Scraper<TItem> FallbackRoute(Func<ScrapeContext<TItem>, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfRunning();
        _fallbackRoute = handler;
        return this;
    }

    /// <summary>
    /// Runs the scraping and streams the items as handlers emit them. The run ends when every queued page
    /// is handled or failed, or when <see cref="ScraperOptions.MaxRunDuration"/> passes.
    /// Stopping the enumeration stops the run.
    /// </summary>
    /// <param name="startRequests">First pages; strings convert to requests with the default label.</param>
    /// <param name="cancellationToken">Cancels the run; the enumeration then throws <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="InvalidOperationException">No handler for a label, or the scraper is already running.</exception>
    public async IAsyncEnumerable<TItem> RunAsync(IEnumerable<ScrapeRequest> startRequests, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startRequests);
        if (_routes.Count == 0 && _fallbackRoute == null) throw new InvalidOperationException("No handlers: register one with Route(label, handler)");
        Options.Validate();

        if (Interlocked.Exchange(ref _running, 1) == 1) throw new InvalidOperationException("The scraper is already running");
        try
        {
            var run = new ScraperRun<TItem>(this, cancellationToken);
            _lastRun = run;
            await using (run.ConfigureAwait(false))
            {
                run.Start(startRequests);

                await foreach (var item in run.Items.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield return item;
                }

                await run.Completion.ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Runs the scraping from one or more requests; see <see cref="RunAsync(IEnumerable{ScrapeRequest}, CancellationToken)"/>.</summary>
    public IAsyncEnumerable<TItem> RunAsync(params ScrapeRequest[] startRequests) => RunAsync((IEnumerable<ScrapeRequest>)startRequests);

    /// <summary>Runs the scraping and collects everything: items, failed requests and counters.</summary>
    public async Task<ScrapeResult<TItem>> RunToListAsync(IEnumerable<ScrapeRequest> startRequests, CancellationToken cancellationToken = default)
    {
        var items = new List<TItem>();
        await foreach (var item in RunAsync(startRequests, cancellationToken).ConfigureAwait(false))
        {
            items.Add(item);
        }
        var run = _lastRun!;
        return new ScrapeResult<TItem>(items, [.. run.FailedRequests], run.Stats);
    }

    /// <summary>Runs the scraping from one or more requests and collects everything.</summary>
    public Task<ScrapeResult<TItem>> RunToListAsync(params ScrapeRequest[] startRequests) => RunToListAsync((IEnumerable<ScrapeRequest>)startRequests);

    internal Func<ScrapeContext<TItem>, Task>? FindRoute(string label) =>
        _routes.TryGetValue(label, out var handler) ? handler : _fallbackRoute;

    internal void RaiseRequestFailed(FailedRequest failed)
    {
        try
        {
            RequestFailed?.Invoke(failed);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "RequestFailed handler threw");
        }
    }

    private void ThrowIfRunning()
    {
        if (Volatile.Read(ref _running) == 1) throw new InvalidOperationException("Routes cannot change while the scraper is running");
    }
}
