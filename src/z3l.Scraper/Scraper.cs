using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using z3l.Scraper.Loading;

namespace z3l.Scraper;

/// <summary>A registered handler and the type of data it takes (null: it takes none).</summary>
internal sealed record ScrapeRoute(Func<ScrapeContext, Task> Handler, Type? DataType);

/// <summary>
/// Scrapes pages: takes requests from a queue, loads them, checks the responses, retries the bad ones
/// and passes the good ones to the handler registered for the request label. Handlers queue more pages and emit items
/// of any type.
/// </summary>
/// <example>
/// <code>
/// using var loader = new HttpPageLoader();
/// var scraper = new Scraper(loader)
///     .Route(PageType.Listing, ctx =&gt;
///     {
///         foreach (var card in ctx.Html.FindAll(HtmlTag.Div, cls: "card"))
///             ctx.Enqueue(card.Find(HtmlTag.A).AttributeHref(), PageType.Item, new Teaser(card.Find(HtmlTag.Span).Text()));
///         ctx.Enqueue(ctx.Html.XPathValue("//a[@rel='next']/@href"), PageType.Listing);
///     })
///     .Route&lt;Teaser&gt;(PageType.Item, (ctx, teaser) =&gt;
///         ctx.Emit(new Product(ctx.Html.XPathValue("//h1"), teaser.Price)));
///
/// await foreach (var item in scraper.RunAsync(new ScrapeRequest("https://shop.example/catalog", PageType.Listing)))
///     if (item is Product product) Console.WriteLine(product);
/// </code>
/// </example>
public sealed class Scraper
{
    private readonly Dictionary<string, ScrapeRoute> _routes = new(StringComparer.OrdinalIgnoreCase);
    private ScrapeRoute? _fallbackRoute;
    private int _running;
    private ScraperRun? _lastRun;

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

    /// <summary>Loads the pages (those of the labels given to <see cref="UseLoader"/> go to their own loaders).</summary>
    public IPageLoader Loader { get; }

    private readonly Dictionary<string, IPageLoader> _loaders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the pages of these labels with another loader: for example pages through remote browser workers
    /// (an <see cref="IQueuePageLoader"/>) and files with a local <see cref="HttpPageLoader"/>. A queue loader gets every request
    /// at once, a local one works with <see cref="ScraperOptions.MaxConcurrency"/> loads and <see cref="ScraperOptions.RequestTimeout"/>
    /// as always. The scraper does not dispose the loaders. Returns this scraper.
    /// </summary>
    public Scraper UseLoader(IPageLoader loader, params ScrapeLabel[] labels)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Length == 0) throw new ArgumentException("Name the labels of the loader", nameof(labels));
        ThrowIfRunning();
        foreach (var label in labels) _loaders[label.Name] = loader;
        return this;
    }

    /// <summary>The loader of a label.</summary>
    internal IPageLoader LoaderFor(string label) => _loaders.TryGetValue(label, out var loader) ? loader : Loader;

    /// <summary>Every loader: the default one and those of <see cref="UseLoader"/>.</summary>
    internal IEnumerable<IPageLoader> Loaders => _loaders.Values.Prepend(Loader).Distinct();

    private ICachingPageLoader? _cachingPageLoader;
    private ISnapshotPageLoader? _snapshotPageLoader;

    /// <summary>
    /// Keeps the pages the handlers took and gives them back instead of loading them again, for the labels of every loader:
    /// <see cref="Loading.CachingPageLoader"/> keeps them in a folder, for working on handlers without loading the site each time.
    /// Null, the default, for no cache. Set it before the run.
    /// </summary>
    /// <exception cref="InvalidOperationException">Set while the scraper is running.</exception>
    public ICachingPageLoader? CachingPageLoader
    {
        get => _cachingPageLoader;
        set
        {
            ThrowIfRunning();
            _cachingPageLoader = value;
        }
    }

    /// <summary>
    /// Saves the loaded pages that were not taken (blocked, missing, broken, or those whose handler threw), for the labels of every
    /// loader: <see cref="Loading.SnapshotPageLoader"/> saves them in a folder, to show what the site answered. Null, the default, saves
    /// nothing. Set it before the run.
    /// </summary>
    /// <exception cref="InvalidOperationException">Set while the scraper is running.</exception>
    public ISnapshotPageLoader? SnapshotPageLoader
    {
        get => _snapshotPageLoader;
        set
        {
            ThrowIfRunning();
            _snapshotPageLoader = value;
        }
    }

    /// <summary>Settings.</summary>
    public ScraperOptions Options { get; }

    /// <summary>Logger.</summary>
    public ILogger Logger { get; }

    /// <summary>Counters of the current or the last run.</summary>
    public ScraperStats Stats => _lastRun?.Stats ?? new ScraperStats();

    /// <summary>Called for every request that fails (after its retries). Called from worker threads.</summary>
    public event Action<FailedRequest>? RequestFailed;

    /// <summary>
    /// Raised when the run is low on requests: fewer than <see cref="ScraperOptions.RequestsNeededBelow"/> are unfinished (queued, in
    /// progress or waiting for a retry, links found on pages included), or none is left. Handlers give the run more with
    /// <see cref="RequestsNeededEventArgs.Add(ScrapeRequest)"/>, for example the next rows of a database, so a big list of pages need
    /// not sit in memory at once.
    /// <para>
    /// Calls never overlap, and the run does not end while one goes; when a call still leaves fewer requests than the limit
    /// unfinished, the next one follows at once. A call that adds nothing means the source is empty for now: in the middle of the
    /// run the next call comes when nothing is left, and when nothing is left and a call adds nothing, the run ends. Several
    /// handlers are called one after another with the same arguments; the handlers subscribed when the run starts are called.
    /// </para>
    /// <para>
    /// Not raised after <see cref="ScraperOptions.MaxRequests"/> is reached or once the run stops; a call that goes then is
    /// cancelled through <see cref="RequestsNeededEventArgs.CancellationToken"/>. An exception of a handler ends the run with it.
    /// </para>
    /// </summary>
    public event Func<RequestsNeededEventArgs, Task>? RequestsNeeded;

    /// <summary>The handlers of <see cref="RequestsNeeded"/>, taken by a run when it starts.</summary>
    internal Func<RequestsNeededEventArgs, Task>? RequestsNeededHandlers => RequestsNeeded;

    /// <summary>Registers the handler of a label: a <see cref="PageType"/> or a string (labels ignore case). Returns this scraper.</summary>
    public Scraper Route(ScrapeLabel label, Func<ScrapeContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register(label, new ScrapeRoute(handler, null));
    }

    /// <summary>Registers a synchronous handler of a label.</summary>
    public Scraper Route(ScrapeLabel label, Action<ScrapeContext> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Route(label, ctx => { handler(ctx); return Task.CompletedTask; });
    }

    /// <summary>
    /// Registers the handler of a label whose requests carry data of type <typeparamref name="TData"/>
    /// (queued with <see cref="ScrapeContext.Enqueue{TData}(string?, ScrapeLabel, TData, ScrapePriority)"/>);
    /// the handler gets the data typed. A request of this label without such data is an error.
    /// </summary>
    public Scraper Route<TData>(ScrapeLabel label, Func<ScrapeContext, TData, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var name = label.Name;
        return Register(label, new ScrapeRoute(
            ctx => ctx.Request.Data is TData data ? handler(ctx, data) : throw WrongData(name, typeof(TData), ctx.Request.Data),
            typeof(TData)));
    }

    /// <summary>Registers a synchronous handler of a label whose requests carry data of type <typeparamref name="TData"/>.</summary>
    public Scraper Route<TData>(ScrapeLabel label, Action<ScrapeContext, TData> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Route<TData>(label, (ctx, data) => { handler(ctx, data); return Task.CompletedTask; });
    }

    /// <summary>Registers the handler of requests whose label has no handler of its own.</summary>
    public Scraper FallbackRoute(Func<ScrapeContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfRunning();
        _fallbackRoute = new ScrapeRoute(handler, null);
        return this;
    }

    /// <summary>Registers a synchronous handler of requests whose label has no handler of its own.</summary>
    public Scraper FallbackRoute(Action<ScrapeContext> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return FallbackRoute(ctx => { handler(ctx); return Task.CompletedTask; });
    }

    private Scraper Register(ScrapeLabel label, ScrapeRoute route)
    {
        ThrowIfRunning();
        _routes[label.Name] = route;
        return this;
    }

    /// <summary>
    /// Runs the scraping and streams the items as handlers emit them. The run ends when every queued page
    /// is handled or failed, or when <see cref="ScraperOptions.MaxRunDuration"/> passes.
    /// Stopping the enumeration stops the run.
    /// </summary>
    /// <param name="startRequests">First pages (absolute http(s) URLs); strings convert to requests with the default label.</param>
    /// <param name="cancellationToken">Cancels the run; the enumeration then throws <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="InvalidOperationException">No handler for a label, wrong data for a typed handler, or the scraper is already running.</exception>
    /// <exception cref="ArgumentException">A start request URL is not an absolute http(s) URL.</exception>
    public async IAsyncEnumerable<object> RunAsync(IEnumerable<ScrapeRequest> startRequests, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startRequests);
        if (_routes.Count == 0 && _fallbackRoute == null) throw new InvalidOperationException("No handlers: register one with Route(label, handler)");
        Options.Validate();

        if (Interlocked.Exchange(ref _running, 1) == 1) throw new InvalidOperationException("The scraper is already running");
        try
        {
            var run = new ScraperRun(this, cancellationToken);
            _lastRun = run;
            await using (run.ConfigureAwait(false))
            {
                run.Start(startRequests);

                // an item counts as taken when the loop asks for the next one: with a state file its page stays there until then
                ScrapeRequest? taken = null;
                var hasTaken = false;
                await foreach (var emitted in run.Items.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (hasTaken) run.Acknowledge(taken);
                    taken = emitted.Request;
                    hasTaken = true;
                    yield return emitted.Item;
                }
                if (hasTaken) run.Acknowledge(taken);

                await run.Completion.ConfigureAwait(false);
                run.CompleteState();
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Runs the scraping from one or more requests; see <see cref="RunAsync(IEnumerable{ScrapeRequest}, CancellationToken)"/>.</summary>
    public IAsyncEnumerable<object> RunAsync(params ScrapeRequest[] startRequests) => RunAsync((IEnumerable<ScrapeRequest>)startRequests);

    /// <summary>Runs the scraping and collects everything: items, failed requests and counters.</summary>
    public async Task<ScrapeResult> RunToListAsync(IEnumerable<ScrapeRequest> startRequests, CancellationToken cancellationToken = default)
    {
        var items = new List<object>();
        await foreach (var item in RunAsync(startRequests, cancellationToken).ConfigureAwait(false))
        {
            items.Add(item);
        }
        var run = _lastRun!;
        return new ScrapeResult(items, [.. run.FailedRequests], run.Stats);
    }

    /// <summary>Runs the scraping from one or more requests and collects everything.</summary>
    public Task<ScrapeResult> RunToListAsync(params ScrapeRequest[] startRequests) => RunToListAsync((IEnumerable<ScrapeRequest>)startRequests);

    internal ScrapeRoute? FindRoute(string label) => _routes.TryGetValue(label, out var route) ? route : _fallbackRoute;

    internal static InvalidOperationException WrongData(string label, Type expected, object? data) => new(data is null
        ? $"The handler of \"{label}\" takes {expected.Name} data and the request has none: queue it with Enqueue(url, label, data)"
        : $"The handler of \"{label}\" takes {expected.Name} data and the request has {data.GetType().Name}");

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
        if (Volatile.Read(ref _running) == 1) throw new InvalidOperationException("Routes, loaders and the cache cannot change while the scraper is running");
    }
}
