using Microsoft.Extensions.Logging;
using z3l.Scraper.Crawling;
using z3l.Scraper.Loading;
using z3l.Scraper.Validation;

namespace z3l.Scraper;

/// <summary>
/// One page through its handler for <see cref="Scraper.ParseAsync(ScrapeRequest, CancellationToken)"/>: the requests the handler
/// queues are checked and taken as a run takes them, but kept instead of loaded.
/// </summary>
internal sealed class PageParse : IScrapeHost
{
    private readonly Scraper _scraper;
    private readonly CrawlScope _scope;
    private readonly List<ScrapeRequest> _found = [];
    private readonly HashSet<KeyHash> _seen = [];

    private PageParse(Scraper scraper, ScrapeRequest page, CrawlScope scope)
    {
        _scraper = scraper;
        _scope = scope;
        if (!page.SkipDuplicateCheck) _seen.Add(KeyHash.Of(page.GetUniqueKey()));     // the page itself is seen, as in a run
    }

    public ILogger Logger => _scraper.Logger;

    /// <summary>The requests the handler queued, in its order.</summary>
    private IReadOnlyList<ScrapeRequest> Found
    {
        get
        {
            lock (_found) return [.. _found];
        }
    }

    public bool Enqueue(ScrapeRequest request, ScrapeRequest? parent)
    {
        if (!UrlHelper.TryNormalizeUri(request.Url, out var uri)) return false;
        _scraper.RouteOf(request);

        // as a run takes it: without the ignored query parameters, and a link out of the crawl scope is skipped
        uri = _scope.Clean(uri);
        if (parent != null && !_scope.Allows(uri)) return false;

        var depth = parent == null ? 0 : parent.Depth + 1;
        request = request with { Url = uri.AbsoluteUri, Attempts = 0, NotFoundCount = 0, Depth = depth, HasThrottleSlot = false, SkipCache = false };
        KeyHash? key = request.SkipDuplicateCheck ? null : KeyHash.Of(request.GetUniqueKey(uri));
        lock (_found)
        {
            if (key.HasValue && !_seen.Add(key.Value)) return false;
            _found.Add(request);
            return true;
        }
    }

    /// <summary>Loads the page unless it is given, checks it, and runs its handler when it passes.</summary>
    internal static async Task<ParseResult> ParseAsync(Scraper scraper, ScrapeRequest request, PageResponse? response, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!UrlHelper.TryNormalizeUri(request.Url, out var uri))
        {
            throw new ArgumentException($"The page needs an absolute http(s) URL: \"{request.Url}\"", nameof(request));
        }
        var route = scraper.RouteOf(request);
        var scope = new CrawlScope(scraper.Options);
        // own copy, as a run makes: the first attempt of the page, its URL without the ignored query parameters
        request = request with { Url = scope.Clean(uri).AbsoluteUri, Attempts = 1, NotFoundCount = 0, HasThrottleSlot = false, SkipCache = false };

        response ??= await LoadAsync(scraper, request, token).ConfigureAwait(false);
        var verdict = ResponseValidator.Validate(request, response, scraper.Options.Rules.Build(request.Label));
        if (verdict.Outcome != ValidationOutcome.Ok) return new ParseResult { Request = request, Response = response, Validation = verdict, Items = [], Found = [] };

        var parse = new PageParse(scraper, request, scope);
        var context = new ScrapeContext(parse, request, response, token);
        Exception? error = null;
        List<object>? items;
        try
        {
            await route.Handler(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            error = ex;     // its items stay: they show how far the handler got
        }
        finally
        {
            items = context.Complete();     // the parser is disposed; the page stays in the result
        }
        return new ParseResult { Request = request, Response = response, Validation = verdict, Items = items ?? [], Found = parse.Found, Exception = error };
    }

    /// <summary>One load with the loader of the label, as the first attempt of a run.</summary>
    private static async Task<PageResponse> LoadAsync(Scraper scraper, ScrapeRequest request, CancellationToken token)
    {
        var loader = scraper.LoaderFor(request.Label);
        var timeout = scraper.Options.RequestTimeout;
        // a queue loader's load may wait in its queue for any time; the loader finds lost loads itself
        var limited = timeout != Timeout.InfiniteTimeSpan && loader is not IQueuePageLoader;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (limited) timeoutSource.CancelAfter(timeout);
        try
        {
            return await loader.LoadAsync(request, timeoutSource.Token).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"{loader.GetType().Name}.LoadAsync returned null");
        }
        catch (OperationCanceledException ex) when (limited && timeoutSource.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException($"No response in {timeout.TotalSeconds:0.###} s: {request.Url}", ex);
        }
    }
}
