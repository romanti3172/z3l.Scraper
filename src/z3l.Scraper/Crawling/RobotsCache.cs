using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Crawling;

/// <summary>robots.txt of every host of a run, loaded once with the scraper's loader on the first request to the host.</summary>
internal sealed class RobotsCache(IPageLoader loader, ScraperOptions options, ILogger logger, CancellationToken runToken)
{
    internal const string Label = "robots.txt";

    private readonly ConcurrentDictionary<string, Lazy<Task<RobotsTxt>>> _files = new(StringComparer.OrdinalIgnoreCase);

    public Task<RobotsTxt> GetAsync(Uri url)
    {
        var root = url.GetLeftPart(UriPartial.Authority);
        return _files.GetOrAdd(root, key => new Lazy<Task<RobotsTxt>>(() => LoadAsync(key))).Value;
    }

    /// <summary>
    /// 2xx: the rules; 4xx: no rules (RFC 9309). A 5xx or a load error should mean "disallow all" by the RFC;
    /// here the host is crawled without rules and a warning is logged, so one flaky robots.txt does not stop a scrape.
    /// </summary>
    private async Task<RobotsTxt> LoadAsync(string root)
    {
        var url = root + "/robots.txt";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            if (options.RequestTimeout != Timeout.InfiniteTimeSpan) timeout.CancelAfter(options.RequestTimeout);

            var response = await loader.LoadAsync(new ScrapeRequest(url, Label), timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var robots = RobotsTxt.Parse(response.Text);
                logger.LogDebug("robots.txt of {Root}: {Sitemaps} sitemaps, crawl delay {Delay}", root, robots.Sitemaps.Count, robots.CrawlDelay(options.RobotsUserAgent));
                return robots;
            }
            if (response.StatusCode is >= 400 and < 500) return RobotsTxt.AllowAll;

            logger.LogWarning("robots.txt of {Root} answered {Status}: the host is crawled without its rules", root, response.StatusCode);
            return RobotsTxt.AllowAll;
        }
        catch (Exception ex) when (!runToken.IsCancellationRequested)
        {
            logger.LogWarning("robots.txt of {Root} could not be loaded ({Message}): the host is crawled without its rules", root, ex.Message);
            return RobotsTxt.AllowAll;
        }
    }
}
