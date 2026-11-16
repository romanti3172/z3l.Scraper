namespace z3l.Scraper.Loading;

/// <summary>
/// Loads pages: plain HTTP, a headless browser, a remote loader farm...
/// The scraper calls it from several threads at once.
/// </summary>
public interface IPageLoader
{
    /// <summary>
    /// Loads a page. Any HTTP status is a normal result; throw only when there is no response
    /// (network error, proxy failure): the scraper retries the request.
    /// </summary>
    Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A loader that hands every load to a queue served by remote workers (RabbitMQ...). The queue can be of any length, so the
/// scraper sends each request as soon as it is accepted: <see cref="ScraperOptions.MaxConcurrency"/> limits only the handlers
/// that run at once, and <see cref="ScraperOptions.RequestTimeout"/> does not apply, the loader finds lost loads itself
/// (a load that waits in the queue is not lost). A lost load throws, and the scraper retries it.
/// </summary>
public interface IQueuePageLoader : IPageLoader
{
    /// <summary>
    /// Set by the scraper while more than <see cref="ScraperOptions.MaxLoadedNotHandled"/> loaded pages wait for a handler, and
    /// reset when they are handled: the loader stops taking results (they wait in the queue, not in memory) and does not count
    /// that time against a load.
    /// </summary>
    bool Paused { get; set; }
}

/// <summary>
/// A loader that rotates proxies, user agents or sessions: the scraper tells it which responses were
/// blocked (status 403/429, captcha or block markers), so it can retire the proxy or session that got them.
/// </summary>
public interface IBlockAwarePageLoader : IPageLoader
{
    /// <summary>Called when a response from this loader was recognised as blocked.</summary>
    void ReportBlocked(ScrapeRequest request, PageResponse response, string reason);

    /// <summary>
    /// Called when a response from this loader came but was not the page: it failed the response checks (<c>MustContain</c>,
    /// a cut-off page, the wrong format, an empty body, an unexpected status) or the handler asked for a retry. A proxy that
    /// answers with its own page gives such responses; a loader can rest it for the site. Not called for 429 and 503
    /// (the site is overloaded, not the proxy) nor for blocks (<see cref="ReportBlocked"/>).
    /// </summary>
    void ReportBadResponse(ScrapeRequest request, PageResponse response, string reason)
    {
    }

    /// <summary>
    /// Called when the last load of <paramref name="request"/> ran out of <c>RequestTimeout</c> (the load was cancelled, not the run):
    /// a proxy that hangs is as bad as one that refuses.
    /// </summary>
    void ReportTimeout(ScrapeRequest request)
    {
    }

    /// <summary>Called when a response from this loader was the page: it passed the checks and the handler took it.</summary>
    void ReportGoodResponse(ScrapeRequest request, PageResponse response)
    {
    }
}
