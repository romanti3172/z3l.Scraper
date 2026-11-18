using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using z3l.Scraper.Diagnostics;

namespace z3l.Scraper.Loading;

/// <summary>
/// Base of the loaders that load pages themselves, through rotating <see cref="ProxyList"/> proxies or without them. It takes the
/// proxy of every load, tells the list how the proxy did (no answer, 407, a block, an answer that was not the page, the page), and
/// keeps one user agent per proxy, so a site sees one browser per address. A derived class loads one page through the proxy it is
/// given, in <see cref="LoadThroughAsync"/>: <see cref="HttpClientPageLoader"/> does it with HttpClient, a loader of your own can do it
/// with a browser, curl or anything else.
/// </summary>
public abstract class BlockAwarePageLoader : IBlockAwarePageLoader, IPublishesMetrics
{
    /// <summary>A run with metrics uses the loader: its proxy list publishes them.</summary>
    void IPublishesMetrics.PublishMetrics() => Proxies?.PublishMetrics();

    private readonly ConcurrentDictionary<string, string> _proxyUserAgents = new();

    /// <summary>Which proxy loaded a response, so a block reported for it rests the right proxy.</summary>
    private readonly ConditionalWeakTable<PageResponse, ProxyList.Entry> _servedBy = [];

    /// <summary>The proxy of the last load of a request, for <see cref="ReportTimeout"/> (a timed-out load has no response).</summary>
    private readonly ConditionalWeakTable<ScrapeRequest, ProxyList.Entry> _lastProxy = [];

    /// <summary>Creates the loader.</summary>
    /// <param name="proxies">Proxies to rotate; null loads without them (or through the one proxy the derived class knows).</param>
    /// <param name="userAgents">
    /// User agents to rotate: with <paramref name="proxies"/> every proxy takes one and keeps it while the loader lives, without them every
    /// load takes the next one.
    /// </param>
    /// <param name="userAgent">The user agent when <paramref name="userAgents"/> is null; null for none.</param>
    protected BlockAwarePageLoader(ProxyList? proxies, UserAgentList? userAgents, string? userAgent)
    {
        Proxies = proxies;
        UserAgents = userAgents;
        UserAgent = userAgent;
    }

    /// <summary>Proxies to rotate; null for none.</summary>
    protected ProxyList? Proxies { get; }

    /// <summary>User agents to rotate; null for <see cref="UserAgent"/>.</summary>
    protected UserAgentList? UserAgents { get; }

    /// <summary>The user agent when there are no <see cref="UserAgents"/>.</summary>
    protected string? UserAgent { get; }

    /// <inheritdoc />
    /// <exception cref="NonRetryableException">The URL is not an absolute http(s) URL.</exception>
    public async Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new NonRetryableException($"Not an http(s) URL: \"{request.Url}\"");
        }

        if (Proxies is not { } proxies)
        {
            return await LoadThroughAsync(request, uri, null, UserAgents?.Next() ?? UserAgent, cancellationToken).ConfigureAwait(false);
        }

        var proxy = await proxies.AcquireAsync(uri.Host, cancellationToken).ConfigureAwait(false);
        _lastProxy.AddOrUpdate(request, proxy);
        var userAgent = UserAgents is { } agents
            ? _proxyUserAgents.GetOrAdd(proxy.Address.Key, static (_, list) => list.Next(), agents)
            : UserAgent;
        PageResponse response;
        try
        {
            response = await LoadThroughAsync(request, uri, proxy, userAgent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or NonRetryableException))
        {
            proxies.ReportFailure(proxy, ex.Message);       // no answer through this proxy
            throw;
        }

        if (response.StatusCode == (int)HttpStatusCode.ProxyAuthenticationRequired) proxies.ReportFailure(proxy, "407 Proxy Authentication Required");
        else proxies.ReportSuccess(proxy);
        _servedBy.AddOrUpdate(response, proxy);
        return response;
    }

    /// <summary>
    /// Loads one page. Any HTTP status is a normal result; throw only when there is no response (a network error, a proxy failure): the
    /// proxy is told of the failure, and the scraper retries the request. <see cref="NonRetryableException"/> fails the request at once.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="uri">Its URL, an absolute http(s) one.</param>
    /// <param name="proxy">The proxy to load through; null without <see cref="Proxies"/>.</param>
    /// <param name="userAgent">The user agent of this load; null for none.</param>
    /// <param name="cancellationToken">Cancelled when the load runs out of time or the run stops.</param>
    protected abstract Task<PageResponse> LoadThroughAsync(ScrapeRequest request, Uri uri, ProxyList.Entry? proxy, string? userAgent,
        CancellationToken cancellationToken);

    /// <summary>The scraper saw a block in this response: the proxy that loaded it rests for the site.</summary>
    public virtual void ReportBlocked(ScrapeRequest request, PageResponse response, string reason)
    {
        if (Proxies is { } proxies && _servedBy.TryGetValue(response, out var proxy) && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            proxies.ReportBlocked(proxy, uri.Host);
        }
    }

    /// <summary>The response was not the page: after bad responses in a row the proxy that loaded it rests for the site.</summary>
    public virtual void ReportBadResponse(ScrapeRequest request, PageResponse response, string reason)
    {
        if (Proxies is { } proxies && _servedBy.TryGetValue(response, out var proxy) && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            proxies.ReportBadResponse(proxy, uri.Host, reason);
        }
    }

    /// <summary>The last load of the request timed out: a failure of its proxy.</summary>
    public virtual void ReportTimeout(ScrapeRequest request)
    {
        if (Proxies is { } proxies && _lastProxy.TryGetValue(request, out var proxy)) proxies.ReportFailure(proxy, "timeout");
    }

    /// <summary>The response was the page: the bad responses in a row of its proxy for the site start again.</summary>
    public virtual void ReportGoodResponse(ScrapeRequest request, PageResponse response)
    {
        if (Proxies is { } proxies && _servedBy.TryGetValue(response, out var proxy) && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            proxies.ReportGoodResponse(proxy, uri.Host);
        }
    }
}
