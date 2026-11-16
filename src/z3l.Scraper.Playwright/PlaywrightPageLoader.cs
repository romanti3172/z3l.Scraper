using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using z3l.Scraper.Loading;
using PlaywrightTimeoutException = System.TimeoutException;

namespace z3l.Scraper.Playwright;

/// <summary>
/// Loads pages in a headless browser with Microsoft Playwright and returns the page as the browser has drawn it. One browser,
/// a browser context (cookies, cache, user agent) per proxy, <see cref="PlaywrightPageLoaderOptions.MaxPages"/> pages at once.
/// GET requests navigate; other methods are sent with the context's request API, with its cookies. The browser starts with the
/// first load and starts again if it crashes. Thread-safe; dispose it when the scraping is over.
/// </summary>
/// <example>
/// <code>
/// await using var loader = new PlaywrightPageLoader(new PlaywrightPageLoaderOptions
/// {
///     Channel = "chrome",                                   // the installed Chrome; without it: PlaywrightPageLoader.InstallBrowser()
///     Proxies = ProxyList.FromFile("proxies.txt"),
///     WaitForSelector = "div.product",
/// });
/// var scraper = new Scraper(loader, options);
/// </code>
/// </example>
public sealed class PlaywrightPageLoader : IBlockAwarePageLoader, IAsyncDisposable, IDisposable
{
    /// <summary>Key of the context without a proxy.</summary>
    private static readonly ProxyAddress NoProxy = new("none", "none", 1);

    private readonly PlaywrightPageLoaderOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _pages;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private readonly ConcurrentDictionary<ProxyAddress, Lazy<Task<Session>>> _sessions = new();
    private readonly ConditionalWeakTable<PageResponse, Served> _servedBy = [];
    /// <summary>The proxy of the last load of a request, for <see cref="ReportTimeout"/> (a timed-out load has no response).</summary>
    private readonly ConditionalWeakTable<ScrapeRequest, ProxyList.Entry> _lastProxy = [];
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private int _browserGeneration;
    private volatile bool _disposed;

    /// <summary>Creates the loader; the browser starts with the first load.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PlaywrightPageLoaderOptions.MaxPages"/> is less than 1.</exception>
    public PlaywrightPageLoader(PlaywrightPageLoaderOptions? options = null)
    {
        _options = options ?? new PlaywrightPageLoaderOptions();
        if (_options.MaxPages < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxPages must be at least 1");
        _logger = _options.Logger ?? NullLogger.Instance;
        _pages = new SemaphoreSlim(_options.MaxPages, _options.MaxPages);
    }

    /// <summary>
    /// Downloads a browser for Playwright (<c>playwright install</c>); not needed with <see cref="PlaywrightPageLoaderOptions.Channel"/>.
    /// Returns the exit code, 0 when it went well.
    /// </summary>
    /// <param name="browser">The browser to download.</param>
    /// <param name="withDependencies">Also install the system libraries it needs (Linux; needs root).</param>
    public static int InstallBrowser(PlaywrightBrowser browser = PlaywrightBrowser.Chromium, bool withDependencies = false)
    {
        var name = browser.ToString().ToLowerInvariant();
        return Microsoft.Playwright.Program.Main(withDependencies ? ["install", "--with-deps", name] : ["install", name]);
    }

    /// <summary>Contexts open now, one per proxy in use (for tests and dashboards).</summary>
    public int SessionCount => _sessions.Count;

    /// <inheritdoc />
    public async Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new NonRetryableException($"Not an http(s) URL: \"{request.Url}\"");
        }

        await _pages.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var proxy = _options.Proxies is { } proxies ? await proxies.AcquireAsync(uri.Host, cancellationToken).ConfigureAwait(false) : null;
            if (proxy != null) _lastProxy.AddOrUpdate(request, proxy);
            Session? session = null;
            PageResponse response;
            try
            {
                session = await GetSessionAsync(proxy?.Address ?? NoProxy, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref session.OpenPages);
                response = request.Method == HttpMethod.Get
                    ? await NavigateAsync(session, request, cancellationToken).ConfigureAwait(false)
                    : await FetchAsync(session, request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or NonRetryableException))
            {
                if (proxy != null) _options.Proxies!.ReportFailure(proxy, ex.Message);
                if (session != null && !_disposed && _browser?.IsConnected == false) _logger.LogWarning("The browser is gone: it starts again with the next load");
                throw;
            }
            finally
            {
                if (session != null) await ReleaseAsync(session).ConfigureAwait(false);
            }

            if (proxy != null)
            {
                if (response.StatusCode == (int)HttpStatusCode.ProxyAuthenticationRequired) _options.Proxies!.ReportFailure(proxy, "407 Proxy Authentication Required");
                else _options.Proxies!.ReportSuccess(proxy);
            }
            _servedBy.AddOrUpdate(response, new Served(session!, proxy));
            return response;
        }
        finally
        {
            _pages.Release();
        }
    }

    /// <summary>The scraper saw a block: the proxy rests for the site, and the context starts anew (<see cref="PlaywrightPageLoaderOptions.ResetSessionOnBlock"/>).</summary>
    public void ReportBlocked(ScrapeRequest request, PageResponse response, string reason)
    {
        if (!_servedBy.TryGetValue(response, out var served)) return;
        if (served.Proxy != null && _options.Proxies is { } proxies && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            proxies.ReportBlocked(served.Proxy, uri.Host);
        }
        if (_options.ResetSessionOnBlock) Retire(served.Session, reason);
    }

    /// <summary>The response was not the page: after bad responses in a row the proxy that loaded it rests for the site.</summary>
    public void ReportBadResponse(ScrapeRequest request, PageResponse response, string reason)
    {
        if (_servedBy.TryGetValue(response, out var served) && served.Proxy != null && _options.Proxies is { } proxies
            && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            proxies.ReportBadResponse(served.Proxy, uri.Host, reason);
        }
    }

    /// <summary>The last load of the request timed out: a failure of its proxy.</summary>
    public void ReportTimeout(ScrapeRequest request)
    {
        if (_options.Proxies is { } proxies && _lastProxy.TryGetValue(request, out var proxy)) proxies.ReportFailure(proxy, "timeout");
    }

    /// <summary>The response was the page: the bad responses in a row of its proxy for the site start again.</summary>
    public void ReportGoodResponse(ScrapeRequest request, PageResponse response)
    {
        if (_servedBy.TryGetValue(response, out var served) && served.Proxy != null && _options.Proxies is { } proxies
            && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            proxies.ReportGoodResponse(served.Proxy, uri.Host);
        }
    }

    // ---------------------------------------------------------------- loading

    private async Task<PageResponse> NavigateAsync(Session session, ScrapeRequest request, CancellationToken cancellationToken)
    {
        var page = await session.Context.NewPageAsync().ConfigureAwait(false);
        try
        {
            // the scraper's timeout closes the page: Playwright calls take no token
            await using var closeOnCancel = cancellationToken.Register(static p => _ = ((IPage)p!).CloseAsync(), page).ConfigureAwait(false);
            try
            {
                if (request.Headers is { Count: > 0 } headers) await page.SetExtraHTTPHeadersAsync(headers).ConfigureAwait(false);

                IResponse? response;
                try
                {
                    response = await page.GotoAsync(request.Url, new PageGotoOptions
                    {
                        WaitUntil = _options.WaitUntil,
                        Timeout = (float)_options.NavigationTimeout.TotalMilliseconds,
                    }).ConfigureAwait(false);
                }
                catch (PlaywrightException ex) when (ex.Message.Contains("Download is starting", StringComparison.OrdinalIgnoreCase))
                {
                    // a file the browser would save: take it with the request API of the same context
                    return await FetchAsync(session, request, cancellationToken).ConfigureAwait(false);
                }
                if (response == null) throw new PlaywrightException($"No response for {request.Url}");

                if (response.Status is >= 200 and < 300)
                {
                    if (_options.WaitForSelector is { } selector)
                    {
                        await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
                        {
                            Timeout = (float)_options.SelectorTimeout.TotalMilliseconds,
                        }).ConfigureAwait(false);
                    }
                    if (_options.OnPageLoaded is { } onLoaded) await onLoaded(page, request, cancellationToken).ConfigureAwait(false);
                }

                var responseHeaders = await response.AllHeadersAsync().ConfigureAwait(false);
                var result = new Dictionary<string, string>(responseHeaders, StringComparer.OrdinalIgnoreCase);
                result.TryGetValue("content-type", out var contentType);

                if (contentType == null || contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    // the page as the browser has drawn it; the body is no longer what the headers describe
                    result.Remove("content-length");
                    result.Remove("content-encoding");
                    var html = await page.ContentAsync().ConfigureAwait(false);
                    return PageResponse.FromText(response.Status, page.Url, html, "text/html; charset=utf-8", result);
                }

                var body = await response.BodyAsync().ConfigureAwait(false);
                return new PageResponse(response.Status, response.Url, body, contentType, result);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is PlaywrightException or PlaywrightTimeoutException)
            {
                throw new OperationCanceledException("The load was cancelled", ex, cancellationToken);
            }
        }
        finally
        {
            await CloseQuietlyAsync(page).ConfigureAwait(false);
        }
    }

    /// <summary>A request through the context's request API: other methods than GET, and files.</summary>
    private async Task<PageResponse> FetchAsync(Session session, ScrapeRequest request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Body != null) headers["Content-Type"] = request.ContentType ?? "application/x-www-form-urlencoded";
        if (request.Headers != null)
        {
            foreach (var header in request.Headers) headers[header.Key] = header.Value;
        }

        var fetch = session.Context.APIRequest.FetchAsync(request.Url, new APIRequestContextOptions
        {
            Method = request.Method.Method,
            Data = request.Body,
            Headers = headers,
            Timeout = (float)_options.NavigationTimeout.TotalMilliseconds,
        });
        var response = await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var body = await response.BodyAsync().ConfigureAwait(false);
            var result = new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase);
            result.TryGetValue("content-type", out var contentType);
            return new PageResponse(response.Status, response.Url, body, contentType, result);
        }
        finally
        {
            await response.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task CloseQuietlyAsync(IPage page)
    {
        try
        {
            if (!page.IsClosed) await page.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // the context or the browser is gone already
        }
    }

    // ---------------------------------------------------------------- browser and sessions

    /// <summary>The context of the proxy; a new one when there is none or the browser has started again.</summary>
    private async Task<Session> GetSessionAsync(ProxyAddress key, CancellationToken cancellationToken)
    {
        while (true)
        {
            var lazy = _sessions.GetOrAdd(key, k => new Lazy<Task<Session>>(() => CreateSessionAsync(k)));
            Session session;
            try
            {
                session = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _sessions.TryRemove(new KeyValuePair<ProxyAddress, Lazy<Task<Session>>>(key, lazy));    // the next load tries again
                throw;
            }

            if (session.Generation == Volatile.Read(ref _browserGeneration) && _browser?.IsConnected == true && !session.Retired) return session;
            if (_sessions.TryRemove(new KeyValuePair<ProxyAddress, Lazy<Task<Session>>>(key, lazy)) && !session.Retired)
            {
                _ = CloseSessionAsync(session);     // its browser is gone: free the relay
            }
        }
    }

    private async Task<Session> CreateSessionAsync(ProxyAddress key)
    {
        var (browser, generation) = await GetBrowserAsync().ConfigureAwait(false);
        var options = new BrowserNewContextOptions { IgnoreHTTPSErrors = true };
        Socks5Relay? relay = null;
        if (NeedsRelay(key))
        {
            // browsers cannot log in to socks5 proxies: they talk to a local relay without a login, the relay logs in
            relay = new Socks5Relay(key, _logger);
            options.Proxy = new Proxy { Server = relay.Server };
        }
        else if (!ReferenceEquals(key, NoProxy))
        {
            options.Proxy = new Proxy
            {
                Server = $"{key.Scheme}://{key.Host}:{key.Port}",
                Username = key.UserName,
                Password = key.Password,
            };
        }
        if (_options.UserAgents is { } agents) options.UserAgent = agents.Next();
        _options.ConfigureContext?.Invoke(options);

        try
        {
            var context = await browser.NewContextAsync(options).ConfigureAwait(false);
            if (_options.BlockedResourceTypes.Count > 0)
            {
                var blocked = _options.BlockedResourceTypes;
                await context.RouteAsync("**/*", route =>
                    blocked.Contains(route.Request.ResourceType) ? route.AbortAsync() : route.FallbackAsync()).ConfigureAwait(false);
            }
            return new Session(key, context, generation, relay);
        }
        catch
        {
            if (relay != null) await relay.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A socks5 proxy with a login goes through a <see cref="Socks5Relay"/>.</summary>
    internal static bool NeedsRelay(ProxyAddress proxy) =>
        proxy.UserName != null && string.Equals(proxy.Scheme, "socks5", StringComparison.OrdinalIgnoreCase);

    private async Task<(IBrowser Browser, int Generation)> GetBrowserAsync()
    {
        await _browserLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_browser is { IsConnected: true } browser) return (browser, _browserGeneration);

            if (_browser != null)
            {
                await CloseBrowserQuietlyAsync(_browser).ConfigureAwait(false);
                _browser = null;
            }

            _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);
            var type = _options.Browser switch
            {
                PlaywrightBrowser.Firefox => _playwright.Firefox,
                PlaywrightBrowser.Webkit => _playwright.Webkit,
                _ => _playwright.Chromium,
            };
            try
            {
                _browser = await type.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = _options.Headless,
                    Channel = _options.Channel,
                    ExecutablePath = _options.ExecutablePath,
                    Args = _options.LaunchArgs,
                }).ConfigureAwait(false);
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
            {
                throw new NonRetryableException(
                    $"{_options.Browser} is not installed: call PlaywrightPageLoader.InstallBrowser(), run playwright.ps1 install, or set Channel = \"chrome\"", ex);
            }

            var generation = Interlocked.Increment(ref _browserGeneration);
            _logger.LogInformation("Browser {Browser} {Version} started", _options.Browser, _browser.Version);
            return (_browser, generation);
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private void Retire(Session session, string reason)
    {
        if (session.Retired) return;
        session.Retired = true;
        if (_sessions.TryGetValue(session.Key, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully && lazy.Value.Result == session)
        {
            _sessions.TryRemove(new KeyValuePair<ProxyAddress, Lazy<Task<Session>>>(session.Key, lazy));
        }
        _logger.LogInformation("Session {Proxy} is blocked ({Reason}): a new one starts", ReferenceEquals(session.Key, NoProxy) ? "without proxy" : session.Key, reason);
        if (Volatile.Read(ref session.OpenPages) == 0) _ = CloseSessionAsync(session);
    }

    private async Task ReleaseAsync(Session session)
    {
        if (Interlocked.Decrement(ref session.OpenPages) == 0 && session.Retired) await CloseSessionAsync(session).ConfigureAwait(false);
    }

    private static async Task CloseSessionAsync(Session session)
    {
        if (Interlocked.Exchange(ref session.Closed, 1) == 1) return;
        try
        {
            await session.Context.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // the browser is gone already
        }
        finally
        {
            if (session.Relay != null) await session.Relay.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task CloseBrowserQuietlyAsync(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _browserLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var open = _sessions.Values.Where(l => l.IsValueCreated && l.Value.IsCompletedSuccessfully).Select(l => l.Value.Result).ToList();
            _sessions.Clear();
            foreach (var session in open) await CloseSessionAsync(session).ConfigureAwait(false);
            if (_browser != null) await CloseBrowserQuietlyAsync(_browser).ConfigureAwait(false);
            _browser = null;
            _playwright?.Dispose();
            _playwright = null;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Session(ProxyAddress key, IBrowserContext context, int generation, Socks5Relay? relay)
    {
        public ProxyAddress Key { get; } = key;
        public IBrowserContext Context { get; } = context;
        public int Generation { get; } = generation;
        public Socks5Relay? Relay { get; } = relay;
        public volatile bool Retired;
        public int OpenPages;
        public int Closed;
    }

    private sealed record Served(Session Session, ProxyList.Entry? Proxy);
}
