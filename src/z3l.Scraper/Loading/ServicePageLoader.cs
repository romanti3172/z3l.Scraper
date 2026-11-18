using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace z3l.Scraper.Loading;

/// <summary>Settings of a <see cref="ServicePageLoader"/>.</summary>
public class ServicePageLoaderOptions
{
    /// <summary>
    /// Calls to the service at once: the limit of your plan. The scraper gives the loader every page; the others wait in the loader,
    /// and no time limit counts that wait.
    /// </summary>
    public int MaxConcurrency { get; set; } = 5;

    /// <summary>
    /// How long one call may take: the service's own limit and a little more. A call without an answer by then is lost, and the
    /// scraper retries the page.
    /// </summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>The client of the calls; null for one of the loader's own. A client of yours is not disposed by the loader.</summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>Clock of the call timeouts (tests).</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;
}

/// <summary>
/// Base of the loaders of scraping services that load a page per HTTP call (ScraperAPI, ScrapingBee, Zyte API...): the proxies, the
/// browsers and the captchas are the service's, the loader only asks and waits.
/// <list type="bullet">
/// <item>The scraper gives the loader every page at once (<see cref="IQueuePageLoader"/>); at most
/// <see cref="ServicePageLoaderOptions.MaxConcurrency"/> calls go at once, the limit of the plan, whatever
/// <see cref="ScraperOptions.MaxConcurrency"/> is.</item>
/// <item>No new call starts while the scraper pauses the loader (its handlers are behind); the calls that go finish.</item>
/// <item>A call without an answer in <see cref="ServicePageLoaderOptions.CallTimeout"/> is lost, and the scraper retries the page.</item>
/// <item>Levels: a service with several ways to load (plain, premium proxies, a browser) names them by level, 0 the cheapest, up to
/// <see cref="MaxLevel"/>. A page that comes back blocked is loaded again one level up, and the host goes on from the level that
/// got through.</item>
/// </list>
/// A derived class makes the call in <see cref="CallAsync"/>: it turns the request into the service's request and the service's answer
/// into the page as the site gave it, or throws <see cref="RemoteLoadException"/> when the service had no page and
/// <see cref="NonRetryableException"/> when another call cannot help (a wrong key, no credits left). When the service refused the call
/// for its limits, the exception says so (<see cref="RemoteLoadException.RateLimited"/>, with the service's
/// <see cref="RemoteLoadException.RetryAfter"/>: <see cref="RetryAfterOf"/>), and the scraper's retry takes no attempt.
/// </summary>
public abstract class ServicePageLoader : QueuePageLoader, IBlockAwarePageLoader
{
    private readonly ServicePageLoaderOptions _options;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _calls;
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly ConditionalWeakTable<ScrapeRequest, StrongBox<int>> _requestLevels = [];
    private readonly ConditionalWeakTable<PageResponse, StrongBox<int>> _servedAt = [];
    private readonly ConcurrentDictionary<string, int> _hostLevels = new(StringComparer.OrdinalIgnoreCase);
    private long _creditsThousandths;

    /// <summary>Creates the loader.</summary>
    /// <exception cref="ArgumentOutOfRangeException">MaxConcurrency is below 1, or CallTimeout is not positive.</exception>
    protected ServicePageLoader(ServicePageLoaderOptions options, ILogger? logger = null)
        : base(logger, (options ?? throw new ArgumentNullException(nameof(options))).Clock)
    {
        if (options.MaxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(options), options.MaxConcurrency, "MaxConcurrency must be 1 or more");
        if (options.CallTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), options.CallTimeout, "CallTimeout must be positive");
        _options = options;
        _client = options.HttpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };     // CallTimeout limits the calls
        _ownsClient = options.HttpClient == null;
        _calls = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
    }

    /// <summary>The highest level: 0, the default, when the service has one way to load.</summary>
    protected virtual int MaxLevel => 0;

    /// <summary>Credits the calls cost, as <see cref="AddCredits"/> counted them.</summary>
    public double Credits => Interlocked.Read(ref _creditsThousandths) / 1000.0;

    /// <summary>
    /// Loads a page through the service: one call at <paramref name="level"/>. Returns the page as the site gave it (its status,
    /// headers, final URL and body), not the service's own answer. Throws <see cref="RemoteLoadException"/> when the service had no page
    /// (its error, its timeout, its 5xx), with <see cref="RemoteLoadException.RateLimited"/> when it refused the call for its limits,
    /// and <see cref="NonRetryableException"/> when another call cannot help.
    /// </summary>
    /// <param name="client">The client of the calls.</param>
    /// <param name="request">The request.</param>
    /// <param name="level">How to load, 0 the cheapest, up to <see cref="MaxLevel"/>.</param>
    /// <param name="cancellationToken">Cancelled on <see cref="ServicePageLoaderOptions.CallTimeout"/>, when the run stops or the loader is disposed.</param>
    protected abstract Task<PageResponse> CallAsync(HttpClient client, ScrapeRequest request, int level, CancellationToken cancellationToken);

    /// <summary>Counts what a call cost, as the service tells it.</summary>
    protected void AddCredits(double credits) => Interlocked.Add(ref _creditsThousandths, (long)Math.Round(credits * 1000));

    /// <summary>
    /// The <c>Retry-After</c> of the service's answer, seconds or a date, as <see cref="RemoteLoadException.RetryAfter"/> takes it; null
    /// when it has none or none that reads.
    /// </summary>
    protected static TimeSpan? RetryAfterOf(HttpResponseMessage answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.Headers.RetryAfter is not { } retryAfter) return null;
        if (retryAfter.Delta is { } delta) return delta;
        if (retryAfter.Date is not { } date) return null;
        var wait = date - DateTimeOffset.UtcNow;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    /// <inheritdoc />
    protected sealed override Task SendAsync(RemoteLoad load, CancellationToken cancellationToken)
    {
        var call = CallInTurnAsync(load);
        if (!call.IsCompleted)
        {
            _running.TryAdd(call, 0);
            _ = call.ContinueWith(static (task, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(task, out _),
                _running, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return Task.CompletedTask;     // sent: the call goes when a place is free, its answer comes through Answer or Fail
    }

    /// <summary>Waits for a place, then calls; never throws: what happened goes to the load.</summary>
    private async Task CallInTurnAsync(RemoteLoad load)
    {
        using var given = CancellationTokenSource.CreateLinkedTokenSource(load.CancellationToken, Stopping);
        try
        {
            await _calls.WaitAsync(given.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;     // the run stopped or the loader is disposed: the load is cancelled already
        }

        try
        {
            await WaitWhilePausedAsync(given.Token).ConfigureAwait(false);
            if (!TryGetPending(load.Id, out _)) return;

            var level = LevelOf(load.Request);
            using var timer = new CancellationTokenSource(_options.CallTimeout, _options.Clock);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(given.Token, timer.Token);
            PageResponse page;
            try
            {
                page = await CallAsync(_client, load.Request, level, call.Token).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"{GetType().Name}.CallAsync returned null");
            }
            catch (OperationCanceledException) when (timer.IsCancellationRequested && !given.IsCancellationRequested)
            {
                Lose(load.Id, string.Create(CultureInfo.InvariantCulture, $"no answer from the service in {_options.CallTimeout.TotalSeconds:0.###} s"));
                return;
            }
            _servedAt.AddOrUpdate(page, new StrongBox<int>(level));
            Answer(load.Id, page);
        }
        catch (OperationCanceledException) when (given.IsCancellationRequested)
        {
            // the run stopped or the loader is disposed: the load is cancelled
        }
        catch (Exception ex) when (ex is RemoteLoadException or NonRetryableException)
        {
            Fail(load.Id, ex);
        }
        catch (Exception ex)
        {
            Fail(load.Id, new RemoteLoadException($"The call to the service failed: {ex.Message}", ex));
        }
        finally
        {
            _calls.Release();
        }
    }

    /// <summary>The level of the next call of a request: the one it got to, or the one its host goes on from.</summary>
    private int LevelOf(ScrapeRequest request)
    {
        var host = HostOf(request.Url);
        var level = host != null && _hostLevels.TryGetValue(host, out var hostLevel) ? hostLevel : 0;
        if (_requestLevels.TryGetValue(request, out var own) && own.Value > level) level = own.Value;
        return Math.Min(level, MaxLevel);
    }

    private static string? HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : null;

    /// <summary>The page came back blocked: the next call of the request goes one level up, up to <see cref="MaxLevel"/>.</summary>
    public virtual void ReportBlocked(ScrapeRequest request, PageResponse response, string reason) =>
        LevelUp(request, _servedAt.TryGetValue(response, out var at) ? at.Value : LevelOf(request), reason);

    /// <summary>
    /// The site resisted the call at <paramref name="level"/>, as the service tells it (it could not scrape the page): the next call of
    /// the request goes one level up, up to <see cref="MaxLevel"/>. Call it from <see cref="CallAsync"/> before throwing; a page that
    /// comes back blocked does it by itself.
    /// </summary>
    protected void LevelUp(ScrapeRequest request, int level, string reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (level >= MaxLevel) return;
        _requestLevels.AddOrUpdate(request, new StrongBox<int>(level + 1));
        Logger.LogDebug("{Url} did not get through at level {Level} ({Reason}): the next call goes at level {Next}", request.Url, level, reason, level + 1);
    }

    /// <summary>The page got through: when it took a level above 0, its host goes on from that level.</summary>
    public virtual void ReportGoodResponse(ScrapeRequest request, PageResponse response)
    {
        if (!_servedAt.TryGetValue(response, out var at) || at.Value == 0 || HostOf(request.Url) is not { } host) return;
        var level = at.Value;
        if (_hostLevels.AddOrUpdate(host, level, (_, old) => Math.Max(old, level)) == level)
        {
            Logger.LogDebug("{Host} goes on from level {Level}", host, level);
        }
    }

    /// <summary>The page was not the page: nothing changes, the next call goes at the same level.</summary>
    public virtual void ReportBadResponse(ScrapeRequest request, PageResponse response, string reason)
    {
    }

    /// <summary>Not called: the scraper's RequestTimeout does not apply to a queue loader.</summary>
    public virtual void ReportTimeout(ScrapeRequest request)
    {
    }

    /// <summary>Sent, answered, failed by the service, lost, late answers, waiting, credits; paused.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{base.ToString()}, credits {Credits:0.###}");

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        try
        {
            await Task.WhenAll(_running.Keys).ConfigureAwait(false);     // cancelled by Stopping: they end at once
        }
        catch (OperationCanceledException)
        {
        }
        if (_ownsClient) _client.Dispose();
    }
}
