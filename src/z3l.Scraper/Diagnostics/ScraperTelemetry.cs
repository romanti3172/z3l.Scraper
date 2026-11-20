using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Diagnostics;

/// <summary>
/// The meter and the activity source <c>z3l.Scraper</c>. Each is made on first use: by the first run with
/// <see cref="ScraperOptions.PublishMetrics"/> or <see cref="ScraperOptions.PublishTraces"/>, so a process that turns neither on has
/// neither.
/// </summary>
internal static class ScraperTelemetry
{
    public const string Name = "z3l.Scraper";

    public const string NameTag = "z3l.scraper.name";
    public const string LabelTag = "z3l.scraper.label";
    public const string HostTag = "z3l.scraper.host";
    public const string ResultTag = "z3l.scraper.result";
    public const string ReasonTag = "z3l.scraper.reason";
    public const string LoaderTag = "z3l.scraper.loader";
    public const string StateTag = "z3l.scraper.state";

    private static readonly Lazy<Meter> LazyMeter = new(() => new Meter(Name, Version));
    private static readonly Lazy<ScraperInstruments> LazyInstruments = new(() => new ScraperInstruments(LazyMeter.Value));
    private static readonly Lazy<LoaderInstruments> LazyLoaderInstruments = new(() => new LoaderInstruments(LazyMeter.Value));
    private static readonly Lazy<ActivitySource> LazySource = new(() => new ActivitySource(Name, Version));

    /// <summary>The instruments of the runs.</summary>
    public static ScraperInstruments Instruments => LazyInstruments.Value;

    /// <summary>The instruments of the loaders and the proxy lists.</summary>
    public static LoaderInstruments LoaderInstruments => LazyLoaderInstruments.Value;

    /// <summary>The source of the spans.</summary>
    public static ActivitySource Source => LazySource.Value;

    /// <summary>The package version, without the commit that SourceLink adds after '+'.</summary>
    internal static string? Version { get; } =
        typeof(ScraperTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } version
            ? version.Split('+')[0]
            : null;

    /// <summary>
    /// Bounds of the time histograms, in seconds: from a page that comes at once to the slowest a scraping service gives. An exporter that
    /// takes the advice uses them instead of its own.
    /// </summary>
    internal static readonly double[] SecondsBuckets = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120];
}

/// <summary>What a load brought, the tag <c>z3l.scraper.result</c> of <c>z3l.scraper.loads</c>.</summary>
internal enum LoadResult
{
    Ok,
    Blocked,
    BadPage,
    NotFound,
    Timeout,
    LoadError,
    RateLimited,
    NonRetryable,
}

/// <summary>How a handler ended, the tag <c>z3l.scraper.result</c> of <c>z3l.scraper.handler.duration</c>.</summary>
internal enum HandlerResult
{
    Ok,
    Retry,
    Blocked,
    NonRetryable,
    Error,
}

/// <summary>Why a request was not accepted, the tag <c>z3l.scraper.reason</c> of <c>z3l.scraper.requests.skipped</c>.</summary>
internal enum SkipReason
{
    Duplicate,
    Limit,
    OutOfScope,
}

/// <summary>The instruments of the runs, and the runs their gauges read.</summary>
internal sealed class ScraperInstruments
{
    private readonly ConcurrentDictionary<RunMetrics, byte> _runs = new(ReferenceEqualityComparer.Instance);

    public ScraperInstruments(Meter meter)
    {
        Enqueued = meter.CreateCounter<long>("z3l.scraper.requests.enqueued", "{request}", "Requests accepted into the queue.");
        Skipped = meter.CreateCounter<long>("z3l.scraper.requests.skipped", "{request}",
            "Requests not accepted: duplicates, over the limits, out of the crawl scope.");
        Finished = meter.CreateCounter<long>("z3l.scraper.requests.finished", "{request}", "Requests that ended: handled, or failed and why.");
        Loads = meter.CreateCounter<long>("z3l.scraper.loads", "{load}", "Load attempts, by what they brought.");
        CacheHits = meter.CreateCounter<long>("z3l.scraper.cache.hits", "{page}", "Pages the cache gave instead of the loader.");
        Retries = meter.CreateCounter<long>("z3l.scraper.retries", "{retry}", "Retries, by the kind of trouble.");
        Items = meter.CreateCounter<long>("z3l.scraper.items", "{item}", "Items handed to the consumer.");

        var seconds = new InstrumentAdvice<double> { HistogramBucketBoundaries = ScraperTelemetry.SecondsBuckets };
        LoadDuration = meter.CreateHistogram("z3l.scraper.load.duration", "s",
            "Time of a load by the loader; for a remote loader, from sending the page to its answer.", null, seconds);
        HandlerDuration = meter.CreateHistogram("z3l.scraper.handler.duration", "s", "Time of a handler.", null, seconds);

        meter.CreateObservableUpDownCounter("z3l.scraper.requests.queued", () => Observe(static stats => stats.Queued), "{request}",
            "Requests waiting in the queue or in the line of their host.");
        meter.CreateObservableUpDownCounter("z3l.scraper.requests.active", () => Observe(static stats => stats.InProgress), "{request}",
            "Requests being loaded or handled.");
        meter.CreateObservableUpDownCounter("z3l.scraper.requests.pending", () => Observe(static stats => stats.Pending), "{request}",
            "Requests not finished: queued, active or waiting for a retry.");
    }

    public Counter<long> Enqueued { get; }
    public Counter<long> Skipped { get; }
    public Counter<long> Finished { get; }
    public Counter<long> Loads { get; }
    public Counter<long> CacheHits { get; }
    public Counter<long> Retries { get; }
    public Counter<long> Items { get; }
    public Histogram<double> LoadDuration { get; }
    public Histogram<double> HandlerDuration { get; }

    public void Add(RunMetrics run) => _runs.TryAdd(run, 0);
    public void Remove(RunMetrics run) => _runs.TryRemove(run, out _);

    /// <summary>A value of every live run that publishes metrics, added up by the scraper's name.</summary>
    private IEnumerable<Measurement<long>> Observe(Func<ScraperStats, long> value)
    {
        var sums = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var run in _runs.Keys)
        {
            var name = run.Name ?? "";
            sums[name] = sums.GetValueOrDefault(name) + value(run.Stats);
        }
        foreach (var (name, sum) in sums)
        {
            yield return name.Length == 0
                ? new Measurement<long>(sum)
                : new Measurement<long>(sum, new KeyValuePair<string, object?>(ScraperTelemetry.NameTag, name));
        }
    }
}

/// <summary>
/// The metrics of one run with <see cref="ScraperOptions.PublishMetrics"/>. The run calls it where it counts its
/// <see cref="ScraperStats"/>; an instrument nobody listens to costs a check, and its tags are not made. Hosts past the first
/// <see cref="MaxHosts"/> of the run are tagged <see cref="OtherHosts"/>, so a wide crawl does not make a series per host.
/// </summary>
internal sealed class RunMetrics(ScraperInstruments instruments, ScraperStats stats, string? name)
{
    internal const int MaxHosts = 1000;
    internal const string OtherHosts = "other";

    private readonly ConcurrentDictionary<string, byte> _hosts = new(StringComparer.Ordinal);
    private int _hostCount;

    public ScraperStats Stats => stats;
    public string? Name => name;

    /// <summary>The run started: the gauges read its counters from now on.</summary>
    public void Start() => instruments.Add(this);

    /// <summary>The run is over: the gauges leave it.</summary>
    public void Stop() => instruments.Remove(this);

    public void Enqueued(string label)
    {
        if (instruments.Enqueued.Enabled) instruments.Enqueued.Add(1, Tags(label));
    }

    public void Skipped(string label, SkipReason reason)
    {
        if (!instruments.Skipped.Enabled) return;
        var tags = Tags(label);
        tags.Add(ScraperTelemetry.ReasonTag, NameOf(reason));
        instruments.Skipped.Add(1, tags);
    }

    public void Succeeded(string label, string host, int items)
    {
        if (instruments.Finished.Enabled)
        {
            var tags = Tags(label, host);
            tags.Add(ScraperTelemetry.ResultTag, "succeeded");
            instruments.Finished.Add(1, tags);
        }
        if (items > 0 && instruments.Items.Enabled) instruments.Items.Add(items, Tags(label));
    }

    public void Failed(ScrapeRequest request, FailureReason reason)
    {
        if (!instruments.Finished.Enabled) return;
        var tags = Tags(request.Label, Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ? uri.Authority : "");
        tags.Add(ScraperTelemetry.ResultTag, NameOf(reason));
        instruments.Finished.Add(1, tags);
    }

    public void CacheHit(string label)
    {
        if (instruments.CacheHits.Enabled) instruments.CacheHits.Add(1, Tags(label));
    }

    public void Load(string label, string host, LoadResult result, TimeSpan duration)
    {
        var counted = instruments.Loads.Enabled;
        var timed = instruments.LoadDuration.Enabled;
        if (!counted && !timed) return;
        var tags = Tags(label, host);
        tags.Add(ScraperTelemetry.ResultTag, NameOf(result));
        if (counted) instruments.Loads.Add(1, tags);
        if (timed) instruments.LoadDuration.Record(duration.TotalSeconds, tags);
    }

    public void Retry(string label, string host, ReasonKind kind)
    {
        if (!instruments.Retries.Enabled) return;
        var tags = Tags(label, host);
        tags.Add(ScraperTelemetry.ReasonTag, NameOf(kind));
        instruments.Retries.Add(1, tags);
    }

    public void Handler(string label, HandlerResult result, TimeSpan duration)
    {
        if (!instruments.HandlerDuration.Enabled) return;
        var tags = Tags(label);
        tags.Add(ScraperTelemetry.ResultTag, NameOf(result));
        instruments.HandlerDuration.Record(duration.TotalSeconds, tags);
    }

    private TagList Tags(string label)
    {
        var tags = new TagList { { ScraperTelemetry.LabelTag, label } };
        if (name != null) tags.Add(ScraperTelemetry.NameTag, name);
        return tags;
    }

    private TagList Tags(string label, string host)
    {
        var tags = Tags(label);
        tags.Add(ScraperTelemetry.HostTag, HostTagOf(host));
        return tags;
    }

    /// <summary>The host as a tag: its own name for the first <see cref="MaxHosts"/> hosts of the run, <see cref="OtherHosts"/> past them.</summary>
    internal string HostTagOf(string host)
    {
        if (_hosts.ContainsKey(host)) return host;
        if (Volatile.Read(ref _hostCount) >= MaxHosts) return OtherHosts;
        if (_hosts.TryAdd(host, 0)) Interlocked.Increment(ref _hostCount);
        return host;
    }

    internal static string NameOf(LoadResult result) => result switch
    {
        LoadResult.Ok => "ok",
        LoadResult.Blocked => "blocked",
        LoadResult.BadPage => "bad_page",
        LoadResult.NotFound => "not_found",
        LoadResult.Timeout => "timeout",
        LoadResult.LoadError => "load_error",
        LoadResult.RateLimited => "rate_limited",
        LoadResult.NonRetryable => "non_retryable",
        _ => "other",
    };

    internal static string NameOf(HandlerResult result) => result switch
    {
        HandlerResult.Ok => "ok",
        HandlerResult.Retry => "retry",
        HandlerResult.Blocked => "blocked",
        HandlerResult.NonRetryable => "non_retryable",
        HandlerResult.Error => "error",
        _ => "other",
    };

    internal static string NameOf(SkipReason reason) => reason switch
    {
        SkipReason.Duplicate => "duplicate",
        SkipReason.Limit => "limit",
        SkipReason.OutOfScope => "out_of_scope",
        _ => "other",
    };

    internal static string NameOf(FailureReason reason) => reason switch
    {
        FailureReason.RetriesExhausted => "retries_exhausted",
        FailureReason.NotFound => "not_found",
        FailureReason.HandlerError => "handler_error",
        FailureReason.NonRetryable => "non_retryable",
        FailureReason.DisallowedByRobotsTxt => "disallowed_by_robots_txt",
        FailureReason.BadUrl => "bad_url",
        _ => "other",
    };

    internal static string NameOf(ReasonKind kind) => kind switch
    {
        ReasonKind.Blocked => "blocked",
        ReasonKind.BadPage => "bad_page",
        ReasonKind.NotFound => "not_found",
        ReasonKind.Timeout => "timeout",
        ReasonKind.LoadError => "load_error",
        ReasonKind.HandlerError => "handler_error",
        ReasonKind.NonRetryable => "non_retryable",
        ReasonKind.DisallowedByRobotsTxt => "disallowed_by_robots_txt",
        ReasonKind.BadUrl => "bad_url",
        ReasonKind.RateLimited => "rate_limited",
        _ => "other",
    };
}

/// <summary>
/// A loader, or a part of one, with counters of its own: a run with <see cref="ScraperOptions.PublishMetrics"/> turns them on when it
/// starts, and they stay on.
/// </summary>
internal interface IPublishesMetrics
{
    void PublishMetrics();
}

/// <summary>
/// The instruments of the loaders and the proxy lists that publish metrics, and the lists and the loaders their gauges read (held weakly:
/// a list or a loader nobody uses any more goes away).
/// </summary>
internal sealed class LoaderInstruments
{
    private readonly ConditionalWeakTable<ProxyList, object?> _proxyLists = new();
    private readonly ConditionalWeakTable<UserAgentList, object?> _userAgentLists = new();
    private readonly ConditionalWeakTable<QueuePageLoader, object?> _queueLoaders = new();

    public LoaderInstruments(Meter meter)
    {
        ProxyResults = meter.CreateCounter<long>("z3l.scraper.proxy.results", "{load}",
            "Loads through proxies, by how they went: success, failure, blocked, bad_response.");
        UserAgentResults = meter.CreateCounter<long>("z3l.scraper.user_agent.results", "{load}",
            "Loads of proxy sessions by how they went for their user agent: success, failure, blocked, bad_response.");
        meter.CreateObservableUpDownCounter("z3l.scraper.user_agents", ObserveUserAgents, "{user_agent}",
            "User agents by state: alive, resting after failures, resting for some site.");
        RemoteSent = meter.CreateCounter<long>("z3l.scraper.remote.sent", "{load}", "Loads a remote loader sent.");
        RemoteResults = meter.CreateCounter<long>("z3l.scraper.remote.results", "{load}",
            "What came of the loads a remote loader sent: answered, failed, lost, late.");
        Credits = meter.CreateCounter<double>("z3l.scraper.service.credits", "{credit}", "Credits the calls of a scraping service cost.");
        meter.CreateObservableUpDownCounter("z3l.scraper.proxies", ObserveProxies, "{proxy}",
            "Proxies by state: alive, resting after failures, resting for some site.");
        meter.CreateObservableUpDownCounter("z3l.scraper.remote.pending", ObservePending, "{load}", "Loads a remote loader waits answers for.");
    }

    public Counter<long> ProxyResults { get; }
    public Counter<long> UserAgentResults { get; }
    public Counter<long> RemoteSent { get; }
    public Counter<long> RemoteResults { get; }
    public Counter<double> Credits { get; }

    public void Add(ProxyList list) => _proxyLists.AddOrUpdate(list, null);
    public void Add(UserAgentList list) => _userAgentLists.AddOrUpdate(list, null);
    public void Add(QueuePageLoader loader) => _queueLoaders.AddOrUpdate(loader, null);

    /// <summary>The proxies of every list that publishes metrics, added up by state.</summary>
    private IEnumerable<Measurement<long>> ObserveProxies()
    {
        long alive = 0, resting = 0, restingForSite = 0;
        foreach (var (list, _) in _proxyLists)
        {
            foreach (var proxy in list.Status())
            {
                if (proxy.Dead) resting++;
                else if (proxy.BlockedSites > 0) restingForSite++;
                else alive++;
            }
        }
        yield return new Measurement<long>(alive, new KeyValuePair<string, object?>(ScraperTelemetry.StateTag, "alive"));
        yield return new Measurement<long>(resting, new KeyValuePair<string, object?>(ScraperTelemetry.StateTag, "resting"));
        yield return new Measurement<long>(restingForSite, new KeyValuePair<string, object?>(ScraperTelemetry.StateTag, "resting_for_site"));
    }

    /// <summary>The user agents of every list that publishes metrics, added up by state.</summary>
    private IEnumerable<Measurement<long>> ObserveUserAgents()
    {
        long alive = 0, resting = 0, restingForSite = 0;
        foreach (var (list, _) in _userAgentLists)
        {
            foreach (var agent in list.Status())
            {
                if (agent.Dead) resting++;
                else if (agent.BlockedSites > 0) restingForSite++;
                else alive++;
            }
        }
        yield return new Measurement<long>(alive, new KeyValuePair<string, object?>(ScraperTelemetry.StateTag, "alive"));
        yield return new Measurement<long>(resting, new KeyValuePair<string, object?>(ScraperTelemetry.StateTag, "resting"));
        yield return new Measurement<long>(restingForSite, new KeyValuePair<string, object?>(ScraperTelemetry.StateTag, "resting_for_site"));
    }

    /// <summary>The loads waiting for their answers, added up by the type of the loader.</summary>
    private IEnumerable<Measurement<long>> ObservePending()
    {
        var sums = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (loader, _) in _queueLoaders)
        {
            var type = loader.GetType().Name;
            sums[type] = sums.GetValueOrDefault(type) + loader.Pending;
        }
        foreach (var (type, sum) in sums) yield return new Measurement<long>(sum, new KeyValuePair<string, object?>(ScraperTelemetry.LoaderTag, type));
    }
}

/// <summary>
/// The spans of one run with <see cref="ScraperOptions.PublishTraces"/>: <c>scrape {label}</c> for an attempt, current while its page is
/// loaded and handled, and <c>handle {label}</c> for its handler under it. An attempt nobody traces (no listener, or the sampler left
/// it out) gets no activity, and nothing is set.
/// </summary>
internal sealed class RunTraces(ActivitySource source, string? name)
{
    private readonly ConcurrentDictionary<string, (string Scrape, string Handle)> _names = new(StringComparer.Ordinal);

    /// <summary>
    /// The span of an attempt, made current: the spans of the loader's HTTP calls and of the handler go under it. Every attempt is a
    /// trace of its own, linked to the span the run was started in, if any: a crawl of a million pages is no trace of a million spans.
    /// </summary>
    public Activity? StartAttempt(ScrapeRequest request, Uri uri, bool fromCache)
    {
        var ambient = Activity.Current;
        Activity? activity;
        if (ambient == null)
        {
            activity = source.StartActivity(NamesOf(request.Label).Scrape);
        }
        else
        {
            // no parent: the attempt's own trace (the change stays in the async method of the attempt)
            Activity.Current = null;
            activity = source.StartActivity(NamesOf(request.Label).Scrape, ActivityKind.Internal, default(ActivityContext),
                links: [new ActivityLink(ambient.Context)]);
            if (activity == null) Activity.Current = ambient;      // not traced: the handler keeps the span it would have had
        }
        if (activity is not { IsAllDataRequested: true }) return activity;
        activity.SetTag("url.full", request.Url);
        activity.SetTag("server.address", uri.Host);
        if (!uri.IsDefaultPort) activity.SetTag("server.port", uri.Port);
        activity.SetTag(ScraperTelemetry.LabelTag, request.Label);
        activity.SetTag("z3l.scraper.attempt", request.Attempts);
        activity.SetTag("z3l.scraper.depth", request.Depth);
        if (fromCache) activity.SetTag("z3l.scraper.from_cache", true);
        if (name != null) activity.SetTag(ScraperTelemetry.NameTag, name);
        return activity;
    }

    /// <summary>The span of a handler, under the attempt's.</summary>
    public Activity? StartHandler(string label)
    {
        var activity = source.StartActivity(NamesOf(label).Handle);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(ScraperTelemetry.LabelTag, label);
            if (name != null) activity.SetTag(ScraperTelemetry.NameTag, name);
        }
        return activity;
    }

    /// <summary>What the load of an attempt brought: its result and status code; an error status when it is not the page.</summary>
    public static void Loaded(Activity? activity, LoadResult result, int? statusCode, string? reason)
    {
        if (activity is not { IsAllDataRequested: true }) return;
        activity.SetTag(ScraperTelemetry.ResultTag, RunMetrics.NameOf(result));
        if (statusCode is { } status) activity.SetTag("http.response.status_code", status);
        if (result != LoadResult.Ok)
        {
            activity.SetTag("error.type", RunMetrics.NameOf(result));
            activity.SetStatus(ActivityStatusCode.Error, reason);
        }
    }

    /// <summary>A handler that did not take its page: the error status on its span and on the attempt's.</summary>
    public static void HandlerFailed(Activity? handler, Activity? attempt, HandlerResult result, Exception error)
    {
        var type = result == HandlerResult.Error ? error.GetType().FullName : RunMetrics.NameOf(result);
        foreach (var activity in new[] { handler, attempt })
        {
            if (activity is not { IsAllDataRequested: true }) continue;
            activity.SetTag("error.type", type);
            activity.SetStatus(ActivityStatusCode.Error, $"handler: {error.Message}");
        }
    }

    private (string Scrape, string Handle) NamesOf(string label) =>
        _names.TryGetValue(label, out var names) ? names : _names.GetOrAdd(label, static x => ($"scrape {x}", $"handle {x}"));
}
