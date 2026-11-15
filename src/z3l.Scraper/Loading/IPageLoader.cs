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
/// A loader that rotates proxies, user agents or sessions: the scraper tells it which responses were
/// blocked (status 403/429, captcha or block markers), so it can retire the proxy or session that got them.
/// </summary>
public interface IBlockAwarePageLoader : IPageLoader
{
    /// <summary>Called when a response from this loader was recognised as blocked.</summary>
    void ReportBlocked(ScrapeRequest request, PageResponse response, string reason);
}
