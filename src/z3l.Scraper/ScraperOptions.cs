using z3l.Scraper.Validation;

namespace z3l.Scraper;

/// <summary>Scraper settings. Change them before <c>RunAsync</c>; a running scrape uses the values it started with.</summary>
public sealed class ScraperOptions
{
    /// <summary>
    /// Requests loaded and handled at the same time, of all hosts (<see cref="MaxConcurrencyPerHost"/> limits each host). With an
    /// <see cref="Loading.IQueuePageLoader"/> every request goes to the queue at once, and this limits only the handlers that run at once.
    /// </summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>
    /// With an <see cref="Loading.IQueuePageLoader"/>: loaded pages that may wait for a handler. Above it the loader is paused and
    /// the results wait in its queue, so pages that come faster than the handlers take them do not fill the memory.
    /// </summary>
    public int MaxLoadedNotHandled { get; set; } = 100;

    /// <summary>
    /// Retries after a failed load, a broken page or a blocked response (the first attempt is not counted). A load a scraping service
    /// refused for its limits takes none of them (<see cref="RateLimitRetries"/>).
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Retries of a page that answered a bare status 404 or 410: sites and proxies sometimes answer 404 by mistake. A not found marker
    /// (<see cref="Validation.ResponseRules.NotFoundMarkers"/>) or a <see cref="Validation.ValidationResult.NotFound"/> verdict of a
    /// custom check is sure: the page fails at once.
    /// </summary>
    public int NotFoundRetries { get; set; } = 1;

    /// <summary>
    /// Retries of a page a scraping service refused for its limits (<see cref="Loading.RemoteLoadException.RateLimited"/>: too many
    /// calls at once or per minute), not counted in <see cref="MaxRetries"/>: the page was not tried. Each waits as long as the
    /// service asks (<see cref="Loading.RemoteLoadException.RetryAfter"/>, up to <see cref="MaxRetryDelay"/>), and at least the retry
    /// delay, which grows with the refusals. Past them a refusal is an attempt like a load error, so a run ends even when the service
    /// refuses every call.
    /// </summary>
    public int RateLimitRetries { get; set; } = 20;

    /// <summary>Delay before the first retry; each next retry waits <see cref="RetryBackoffFactor"/> times longer.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Growth of the retry delay.</summary>
    public double RetryBackoffFactor { get; set; } = 2;

    /// <summary>Longest retry delay.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Time for one load; a slower load is cancelled and retried. <see cref="Timeout.InfiniteTimeSpan"/> for no limit.
    /// Not used with an <see cref="Loading.IQueuePageLoader"/>: a load may wait in its queue for any time.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Stop after this time and return what was scraped. Null for no limit.</summary>
    public TimeSpan? MaxRunDuration { get; set; }

    /// <summary>
    /// Accept at most this many requests in a run (start requests included); later ones are skipped and counted in
    /// <see cref="ScraperStats.SkippedByLimit"/>. Handy for trying the selectors on the first pages of a big site. Null for no limit.
    /// </summary>
    public int? MaxRequests { get; set; }

    /// <summary>
    /// Spreads the loads evenly: at most this many loads (retries included) start per minute, whatever
    /// <see cref="MaxConcurrency"/> is. Null for no limit.
    /// </summary>
    public int? MaxRequestsPerMinute { get; set; }

    /// <summary>
    /// Loads of one host (with its port) going at the same time, under <see cref="MaxConcurrency"/>. A request of a host that has this
    /// many waits in the host's line, not in a worker, so the workers load other hosts meanwhile and a big site at its limit does not
    /// hold back the rest of the queue. A load holds its slot until the loader answers, not while the handler runs. With an
    /// <see cref="Loading.IQueuePageLoader"/> it limits the requests of a host in the loader's queue at once. Null, the default, for no limit.
    /// </summary>
    public int? MaxConcurrencyPerHost { get; set; }

    /// <summary>
    /// Least time between the starts of two loads of one host (with its port), each a random 0–50 % longer so the loads do not come
    /// like a clock; requests of the host wait in its line meanwhile, not in a worker. A robots.txt Crawl-delay or a delay of
    /// <see cref="AutoThrottle"/>, when longer, wins. Up to a day; zero, the default, for none.
    /// </summary>
    public TimeSpan HostDelay { get; set; }

    /// <summary>
    /// With handlers of <see cref="Scraper.RequestsNeeded"/>: the event is raised when fewer requests than this are unfinished
    /// (queued, in progress or waiting for a retry, links found on pages included), and when none is left. 0: only when none is left.
    /// </summary>
    public int RequestsNeededBelow { get; set; } = 1000;

    /// <summary>
    /// Deepest link to follow: start requests have depth 0, the pages they queue 1, and so on. Deeper requests are skipped
    /// and counted in <see cref="ScraperStats.SkippedByLimit"/>. 0 loads only the start requests and those of
    /// <see cref="Scraper.RequestsNeeded"/>: the pages the handlers queue are skipped (<see cref="Scraper.ParseAsync(string, ScrapeLabel, CancellationToken)"/>
    /// shows them without loading them). Null for no limit.
    /// </summary>
    public int? MaxDepth { get; set; }

    /// <summary>
    /// Hosts whose links the run follows, each with its subdomains: "shop.example" takes shop.example, www.shop.example and
    /// m.shop.example, but not othershop.example. A link to another host that a handler queues is skipped and counted in
    /// <see cref="ScraperStats.OutOfScope"/>, so the crawl does not wander off to every site its pages link to. Empty, the default: links
    /// to any host are followed.
    /// <para>
    /// Only the links found on pages are checked (<c>ScrapeContext.Enqueue</c>, <see cref="ScrapeContext.EnqueueSitemap"/>): start requests
    /// and those of <see cref="Scraper.RequestsNeeded"/> are taken as given. Host names only, without a scheme, a port or a path; an
    /// internationalized name may be written as it reads or in its xn-- form. Names ignore case.
    /// </para>
    /// </summary>
    public ICollection<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which links found on pages the run follows, asked after <see cref="AllowedHosts"/>: false skips the link, counted in
    /// <see cref="ScraperStats.OutOfScope"/>. It gets the absolute URL without <see cref="IgnoredQueryParameters"/>, for example
    /// <c>url =&gt; !url.AbsolutePath.StartsWith("/blog/")</c>. It runs in the handler that queues the link, so on several threads at once,
    /// and what it throws fails that handler. Start requests and those of <see cref="Scraper.RequestsNeeded"/> are taken as given.
    /// Null, the default, takes every link.
    /// </summary>
    public Func<Uri, bool>? UrlFilter { get; set; }

    /// <summary>
    /// Query parameters dropped from the URL of every request the run takes: names, or prefixes that end in '*'; names ignore case. Links
    /// that differ only by them are one page, loaded once and without them, and <see cref="ScrapeRequest.Url"/> and the items have the
    /// clean URL. By default the tracking parameters of ads, mail and analytics: <c>utm_*</c>, those of Google (<c>gclid</c>,
    /// <c>gclsrc</c>, <c>dclid</c>, <c>gbraid</c>, <c>wbraid</c>, <c>srsltid</c>, <c>_ga</c>, <c>_gl</c>), Meta (<c>fbclid</c>,
    /// <c>igshid</c>), Microsoft (<c>msclkid</c>), Yandex (<c>yclid</c>, <c>ysclid</c>), TikTok (<c>ttclid</c>), X (<c>twclid</c>),
    /// Mailchimp (<c>mc_cid</c>, <c>mc_eid</c>) and HubSpot (<c>_hsenc</c>, <c>_hsmi</c>). Add those of sessions ("sessionid"), or
    /// <c>Clear()</c> it to load URLs as they are.
    /// </summary>
    public ICollection<string> IgnoredQueryParameters { get; } = new HashSet<string>(TrackingParameters, StringComparer.OrdinalIgnoreCase);

    /// <summary>The default <see cref="IgnoredQueryParameters"/>.</summary>
    internal static readonly string[] TrackingParameters =
    [
        "utm_*",
        "gclid", "gclsrc", "dclid", "gbraid", "wbraid", "srsltid", "_ga", "_gl",
        "fbclid", "igshid",
        "msclkid",
        "yclid", "ysclid",
        "ttclid",
        "twclid",
        "mc_cid", "mc_eid",
        "_hsenc", "_hsmi",
    ];

    /// <summary>
    /// Slows down a host that is overloaded or blocks: after a 429 or 503, or a blocked response, all requests to that host
    /// wait in a line and load one at a time with a delay that doubles with each such answer (up to <see cref="MaxThrottleDelay"/>)
    /// and shrinks again with successful loads. A Retry-After header pauses the whole host.
    /// <para>
    /// Off by default. It is for scraping from one address, where such answers mean the site is loaded too hard. With rotating
    /// proxies a block is about the proxy (the proxy list rests it), and with remote loaders the workers set the pace: slowing
    /// the whole host down there only slows the scraping. Without it a 429 / 503 is retried after the retry delay, or as long
    /// as its Retry-After asks (up to <see cref="MaxRetryDelay"/>).
    /// </para>
    /// </summary>
    public bool AutoThrottle { get; set; }

    /// <summary>
    /// Longest delay between loads of a throttled host (and the longest robots.txt Crawl-delay honoured). It does not cut
    /// <see cref="HostDelay"/>.
    /// </summary>
    public TimeSpan MaxThrottleDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Load robots.txt of each host first and skip what it disallows (failed with <see cref="FailureReason.DisallowedByRobotsTxt"/>);
    /// its Crawl-delay spaces the loads of that host. Off by default.
    /// </summary>
    public bool RespectRobotsTxt { get; set; }

    /// <summary>Product token matched against the User-agent lines of robots.txt; groups for "*" apply when none matches.</summary>
    public string RobotsUserAgent { get; set; } = "z3l-scraper";

    /// <summary>
    /// File that keeps the run state: the requests not finished yet. Next to it, <c>{StatePath}.seen</c> keeps the pages already seen,
    /// a 16-byte hash of each key (<see cref="ScrapeRequest.UniqueKey"/>), and every save only appends the new ones. They are written
    /// every <see cref="StateSaveInterval"/> and when the run stops; a run started with an existing file continues where the last one
    /// stopped; both files are deleted when a run has finished all its pages and the consumer has taken all the items.
    /// <para>
    /// A page leaves the file only when its items are taken: an item counts as taken when the <c>await foreach</c> loop asks for
    /// the next one, so save each item in the loop body. Pages in work or not taken when the process died are done again, so
    /// their items can come twice (at least once, never lost). <c>RunToListAsync</c> keeps the items in memory: a crash loses them.
    /// </para>
    /// With a state file, <see cref="ScrapeRequest.Data"/> must be a plain value (a string, a number, a bool, a Guid, a DateTime,
    /// a DateTimeOffset, a JsonElement) or the data of a typed handler, saved with <see cref="StateJson"/>. Null: no state is kept.
    /// </summary>
    public string? StatePath { get; set; }

    /// <summary>How often the state file is written while the run goes.</summary>
    public TimeSpan StateSaveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How the data of typed handlers (<c>Route&lt;TData&gt;</c>) is saved in the state file: a <c>JsonSerializerContext</c> with your
    /// data types (trimming and AOT safe), or <c>new DefaultJsonTypeInfoResolver()</c> (reflection) in an app that is not trimmed.
    /// Needed only with <see cref="StatePath"/> and typed data; plain values (strings, numbers...) need nothing.
    /// </summary>
    public System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver? StateJson { get; set; }

    /// <summary>
    /// Retry the request when the handler throws (by default the request fails at once).
    /// <see cref="RetryRequestException"/> is always retried and <see cref="NonRetryableException"/> never is.
    /// </summary>
    public bool RetryOnHandlerError { get; set; }

    /// <summary>
    /// A request with <see cref="ScrapeRequest.SavePath"/> whose file is there already (a run before saved it) gets the file as its
    /// response instead of a load (<see cref="PageResponse.FromSavedFile"/>). The file goes through the response checks
    /// (<see cref="Validation.ResponseRules.CheckFileContent"/>); a file that fails them is deleted and the page is loaded. On by default.
    /// </summary>
    public bool ReuseSavedFiles { get; set; } = true;

    /// <summary>
    /// Handler errors in a row of one label on one host after which the run stops: the handler is broken (the site changed its pages, a
    /// selector finds nothing), and every next page of the label would be loaded only to fail. A handler error is any exception of the
    /// handler but <see cref="RetryRequestException"/> and <see cref="NonRetryableException"/>, each attempt counted
    /// (<see cref="RetryOnHandlerError"/>); a page the handler takes starts the count again, so odd pages here and there do not stop the run.
    /// <para>
    /// The run stops as after <see cref="MaxRunDuration"/>, with <see cref="ScraperStats.StoppedByHandlerErrors"/>. With
    /// <see cref="StatePath"/> the requests the handler failed in a row stay with the unfinished ones, so the run continued after the fix does
    /// them again. Null, the default, for no limit.
    /// </para>
    /// </summary>
    public int? MaxConsecutiveHandlerErrors { get; set; }

    /// <summary>
    /// Items the consumer has not taken yet; when the buffer is full, workers wait before handing over more
    /// items, so a slow consumer slows the scraping down instead of filling the memory.
    /// </summary>
    public int ItemBufferSize { get; set; } = 1000;

    /// <summary>How often progress is logged. Null to turn it off.</summary>
    public TimeSpan? ProgressInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Publish the metrics of the run through <c>System.Diagnostics.Metrics</c>, the meter <c>z3l.Scraper</c>: requests, loads, retries,
    /// items, the time of loads and handlers, the queue; and those of its loaders that have them (proxies, remote loads, the credits of a
    /// scraping service). OpenTelemetry collects them with <c>AddMeter("z3l.Scraper")</c>, <c>dotnet-counters</c> with
    /// <c>--counters z3l.Scraper</c>. Off by default: the run then makes no meter and does no work for it.
    /// </summary>
    public bool PublishMetrics { get; set; }

    /// <summary>
    /// Publish a trace span per attempt (<c>scrape {label}</c>) and per handler (<c>handle {label}</c>) through the activity source
    /// <c>z3l.Scraper</c>; the spans of the loader's HTTP calls and of what the handler calls go under them. OpenTelemetry collects them
    /// with <c>AddSource("z3l.Scraper")</c>. Off by default: the run then starts no activity.
    /// </summary>
    public bool PublishTraces { get; set; }

    /// <summary>
    /// The name of the scraper in its metrics and traces (the tag <c>z3l.scraper.name</c>), to tell the scrapers of one process apart.
    /// None by default.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Checks of loaded pages by label.</summary>
    public ResponseRulesCollection Rules { get; } = new();

    internal void Validate()
    {
        if (MaxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(MaxConcurrency), MaxConcurrency, "Must be 1 or more");
        if (MaxLoadedNotHandled < 1) throw new ArgumentOutOfRangeException(nameof(MaxLoadedNotHandled), MaxLoadedNotHandled, "Must be 1 or more");
        if (MaxRetries < 0) throw new ArgumentOutOfRangeException(nameof(MaxRetries), MaxRetries, "Must be 0 or more");
        if (NotFoundRetries < 0) throw new ArgumentOutOfRangeException(nameof(NotFoundRetries), NotFoundRetries, "Must be 0 or more");
        if (RateLimitRetries < 0) throw new ArgumentOutOfRangeException(nameof(RateLimitRetries), RateLimitRetries, "Must be 0 or more");
        if (ItemBufferSize < 1) throw new ArgumentOutOfRangeException(nameof(ItemBufferSize), ItemBufferSize, "Must be 1 or more");
        if (RetryBackoffFactor < 1) throw new ArgumentOutOfRangeException(nameof(RetryBackoffFactor), RetryBackoffFactor, "Must be 1 or more");
        if (RetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RetryDelay), RetryDelay, "Must not be negative");
        if (MaxRetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaxRetryDelay), MaxRetryDelay, "Must not be negative");
        if (RequestTimeout <= TimeSpan.Zero && RequestTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), RequestTimeout, "Must be positive or Timeout.InfiniteTimeSpan");
        }
        if (MaxRunDuration is { } duration && (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRunDuration), duration, "Must be positive and under 24 days");
        }
        if (MaxRequests is < 1) throw new ArgumentOutOfRangeException(nameof(MaxRequests), MaxRequests, "Must be 1 or more");
        if (MaxRequestsPerMinute is < 1) throw new ArgumentOutOfRangeException(nameof(MaxRequestsPerMinute), MaxRequestsPerMinute, "Must be 1 or more");
        if (MaxConcurrencyPerHost is < 1) throw new ArgumentOutOfRangeException(nameof(MaxConcurrencyPerHost), MaxConcurrencyPerHost, "Must be 1 or more");
        if (HostDelay < TimeSpan.Zero || HostDelay > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(HostDelay), HostDelay, "Must be from zero to a day");
        }
        if (RequestsNeededBelow < 0) throw new ArgumentOutOfRangeException(nameof(RequestsNeededBelow), RequestsNeededBelow, "Must be 0 or more");
        if (MaxDepth is < 0) throw new ArgumentOutOfRangeException(nameof(MaxDepth), MaxDepth, "Must be 0 or more");
        if (MaxConsecutiveHandlerErrors is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConsecutiveHandlerErrors), MaxConsecutiveHandlerErrors, "Must be 1 or more");
        }
        if (MaxThrottleDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaxThrottleDelay), MaxThrottleDelay, "Must be positive");
        if (StateSaveInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(StateSaveInterval), StateSaveInterval, "Must be positive");
        if (RespectRobotsTxt && string.IsNullOrWhiteSpace(RobotsUserAgent)) throw new ArgumentException("RobotsUserAgent must not be empty", nameof(RobotsUserAgent));
        if (Name != null && string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Name must not be empty: null for none", nameof(Name));
        foreach (var host in AllowedHosts) Crawling.CrawlScope.HostName(host);
        foreach (var name in IgnoredQueryParameters) QueryParameterSet.Check(name);
    }
}
