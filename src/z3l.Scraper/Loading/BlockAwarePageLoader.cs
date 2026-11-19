using System.Net;
using System.Runtime.CompilerServices;
using z3l.Scraper.Diagnostics;

namespace z3l.Scraper.Loading;

/// <summary>
/// A session of a loader on a proxy: one proxy, one user agent, and whatever the loader keeps for them in <see cref="State"/> (an
/// HttpClient with its cookies, a browser context...). A session serves one load at a time. It stays while its loads do well; a
/// failure, a block or a bad response throws it away whole, and the next load opens a new session on another proxy, with another
/// user agent and nothing of the old one. Made by <see cref="BlockAwarePageLoader"/>.
/// </summary>
public sealed class ProxySession
{
    internal ProxySession(ProxyList.Entry proxy, string? userAgent)
    {
        Proxy = proxy;
        UserAgent = userAgent;
    }

    /// <summary>The proxy of the session.</summary>
    public ProxyList.Entry Proxy { get; }

    /// <summary>The user agent of every load of the session; null for none.</summary>
    public string? UserAgent { get; }

    /// <summary>
    /// What the loader keeps for the session, made on its first load (an HttpClient with its cookies, a browser context...) and let go
    /// in <see cref="BlockAwarePageLoader.CloseSession"/>. Only one load uses a session at a time.
    /// </summary>
    public object? State { get; set; }

    /// <summary>Loads the session took.</summary>
    public long Loads => Interlocked.Read(ref _loads);

    internal long _loads;

    /// <summary>Where the session is: loading, waiting for the verdict of its answer, free, or closed.</summary>
    internal SessionState Is;

    /// <summary>A block or a bad response came while the session was loading again: it is closed when that load ends.</summary>
    internal bool Doomed;

    /// <summary>When the session's last answer started to wait for its verdict.</summary>
    internal DateTimeOffset WaitingSince;

    /// <summary>The proxy and the user agent; nothing of the state.</summary>
    public override string ToString() => UserAgent == null ? Proxy.ToString() : $"{Proxy} as {UserAgent}";
}

internal enum SessionState
{
    Loading,
    Waiting,
    Free,
    Closed,
}

/// <summary>
/// Base of the loaders that load pages themselves, through <see cref="ProxyList"/> proxies or without them. With proxies a load goes
/// through a session (<see cref="ProxySession"/>): one proxy, one user agent and the derived class's client with its cookies. A session
/// that did well serves the next load, before any new proxy is taken; a failure (no answer, 407, a timeout), a block or a bad response
/// throws the session away whole, the proxy rests as the list says, and the next load opens a new session on another proxy with another
/// user agent and an empty cookie jar. There are as many sessions as loads at once, and a session serves one load at a time. A derived
/// class loads one page through the session it is given, in <see cref="LoadThroughAsync"/>: <see cref="HttpClientPageLoader"/> does it
/// with HttpClient, a loader of your own can do it with a browser, curl or anything else.
/// <para>
/// An answer stays with its session until the caller says what it was: <see cref="ReportGoodResponse"/> and <see cref="ReportDone"/>
/// free the session for the next load, <see cref="ReportBlocked"/> and <see cref="ReportBadResponse"/> throw it away. The scraper always
/// says it; code that calls <see cref="LoadAsync"/> itself calls one of them for every answer (a session nobody hears about is freed
/// after <see cref="VerdictTimeout"/>).
/// </para>
/// </summary>
public abstract class BlockAwarePageLoader : IBlockAwarePageLoader, IPublishesMetrics
{
    /// <summary>A run with metrics uses the loader: its proxy list publishes them.</summary>
    void IPublishesMetrics.PublishMetrics() => Proxies?.PublishMetrics();

    private readonly object _lock = new();

    /// <summary>Sessions free for a load, the one freed last at the end.</summary>
    private readonly List<ProxySession> _free = [];

    /// <summary>Sessions whose answer waits for its verdict.</summary>
    private readonly List<ProxySession> _waiting = [];

    /// <summary>Sessions loading now.</summary>
    private readonly HashSet<ProxySession> _loading = [];

    /// <summary>Which session loaded a response, and which of its loads it was.</summary>
    private readonly ConditionalWeakTable<PageResponse, Served> _servedBy = [];

    /// <summary>The proxy of the last load of a request, for <see cref="ReportTimeout"/> (a timed-out load has no response).</summary>
    private readonly ConditionalWeakTable<ScrapeRequest, ProxyList.Entry> _lastProxy = [];

    private sealed record Served(ProxySession Session, long Load);

    /// <summary>Creates the loader.</summary>
    /// <param name="proxies">Proxies to load through, a session on each; null loads without them (or through the one proxy the derived class knows).</param>
    /// <param name="userAgents">
    /// User agents to rotate: with <paramref name="proxies"/> every session takes the next one and keeps it, without them every load takes
    /// the next one.
    /// </param>
    /// <param name="userAgent">The user agent when <paramref name="userAgents"/> is null; null for none.</param>
    protected BlockAwarePageLoader(ProxyList? proxies, UserAgentList? userAgents, string? userAgent)
    {
        Proxies = proxies;
        UserAgents = userAgents;
        UserAgent = userAgent;
    }

    /// <summary>Proxies to load through; null for none.</summary>
    protected ProxyList? Proxies { get; }

    /// <summary>User agents to rotate; null for <see cref="UserAgent"/>.</summary>
    protected UserAgentList? UserAgents { get; }

    /// <summary>The user agent when there are no <see cref="UserAgents"/>.</summary>
    protected string? UserAgent { get; }

    /// <summary>
    /// How long an answer waits for its verdict (<see cref="ReportGoodResponse"/>, <see cref="ReportDone"/>...) before its session is
    /// freed without one. The scraper always gives the verdict; this is for code that loads pages itself and forgets to.
    /// </summary>
    internal TimeSpan VerdictTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Sessions open now: loading, waiting for a verdict, free.</summary>
    internal (int Loading, int Waiting, int Free) Sessions
    {
        get { lock (_lock) return (_loading.Count, _waiting.Count, _free.Count); }
    }

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

        var session = await TakeSessionAsync(proxies, uri.Host, cancellationToken).ConfigureAwait(false);
        var load = Interlocked.Increment(ref session._loads);
        _lastProxy.AddOrUpdate(request, session.Proxy);
        PageResponse response;
        try
        {
            response = await LoadThroughAsync(request, uri, session, session.UserAgent, cancellationToken).ConfigureAwait(false);
        }
        catch (NonRetryableException)
        {
            EndLoad(session, SessionState.Free);                // the page's fault (too big...), not the session's
            throw;
        }
        catch (Exception ex)
        {
            // no answer through this session: a timeout is reported by ReportTimeout, a stopped run is nobody's failure
            if (ex is not OperationCanceledException) proxies.ReportFailure(session.Proxy, ex.Message);
            EndLoad(session, SessionState.Closed);
            throw;
        }

        _servedBy.AddOrUpdate(response, new Served(session, load));
        if (response.StatusCode == (int)HttpStatusCode.ProxyAuthenticationRequired)
        {
            proxies.ReportFailure(session.Proxy, "407 Proxy Authentication Required");
            EndLoad(session, SessionState.Closed);
        }
        else
        {
            proxies.ReportSuccess(session.Proxy);
            EndLoad(session, SessionState.Waiting);
        }
        return response;
    }

    /// <summary>
    /// Loads one page. Any HTTP status is a normal result; throw only when there is no response (a network error, a proxy failure): the
    /// session is thrown away, its proxy is told of the failure, and the scraper retries the request. <see cref="NonRetryableException"/>
    /// fails the request at once and keeps the session.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="uri">Its URL, an absolute http(s) one.</param>
    /// <param name="session">
    /// The session to load through: its proxy, and its <see cref="ProxySession.State"/> to make on the first load and keep (a client
    /// with its cookies); null without <see cref="Proxies"/>.
    /// </param>
    /// <param name="userAgent">The user agent of this load (the session's one with proxies); null for none.</param>
    /// <param name="cancellationToken">Cancelled when the load runs out of time or the run stops.</param>
    protected abstract Task<PageResponse> LoadThroughAsync(ScrapeRequest request, Uri uri, ProxySession? session, string? userAgent,
        CancellationToken cancellationToken);

    /// <summary>
    /// A session is thrown away (after a failure, a block or a bad response) or the loader is closed: let go of what the session kept in
    /// <see cref="ProxySession.State"/>. Called once per session, never while it loads (but for <see cref="CloseSessions"/>).
    /// </summary>
    /// <param name="session">The session.</param>
    protected virtual void CloseSession(ProxySession session)
    {
    }

    /// <summary>The scraper saw a block in this response: the session that loaded it is thrown away and its proxy rests for the site.</summary>
    public virtual void ReportBlocked(ScrapeRequest request, PageResponse response, string reason)
    {
        if (Proxies is not { } proxies || !_servedBy.TryGetValue(response, out var served)) return;
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)) proxies.ReportBlocked(served.Session.Proxy, uri.Host);
        Discard(served.Session);
    }

    /// <summary>
    /// The response was not the page: the session that loaded it is thrown away, and after bad responses in a row its proxy rests for
    /// the site.
    /// </summary>
    public virtual void ReportBadResponse(ScrapeRequest request, PageResponse response, string reason)
    {
        if (Proxies is not { } proxies || !_servedBy.TryGetValue(response, out var served)) return;
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)) proxies.ReportBadResponse(served.Session.Proxy, uri.Host, reason);
        Discard(served.Session);
    }

    /// <summary>The last load of the request timed out: a failure of its proxy (its session is thrown away already).</summary>
    public virtual void ReportTimeout(ScrapeRequest request)
    {
        if (Proxies is { } proxies && _lastProxy.TryGetValue(request, out var proxy)) proxies.ReportFailure(proxy, "timeout");
    }

    /// <summary>
    /// The response was the page: the bad responses in a row of its proxy for the site start again, and the session that loaded it
    /// serves the next load.
    /// </summary>
    public virtual void ReportGoodResponse(ScrapeRequest request, PageResponse response)
    {
        if (Proxies is not { } proxies || !_servedBy.TryGetValue(response, out var served)) return;
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)) proxies.ReportGoodResponse(served.Session.Proxy, uri.Host);
        Free(served);
    }

    /// <summary>
    /// The caller is done with the response and has said all it will about it (a 404, a 429, a handler that failed...): unless it was
    /// reported blocked or bad, its session serves the next load.
    /// </summary>
    public virtual void ReportDone(ScrapeRequest request, PageResponse response)
    {
        if (Proxies != null && _servedBy.TryGetValue(response, out var served)) Free(served);
    }

    /// <summary>Closes every session (the loader is disposed); one that is loading now is closed with it.</summary>
    protected void CloseSessions()
    {
        List<ProxySession> closed;
        lock (_lock)
        {
            closed = [.. _free, .. _waiting, .. _loading];
            _free.Clear();
            _waiting.Clear();
            _loading.Clear();
            foreach (var session in closed) session.Is = SessionState.Closed;
        }
        foreach (var session in closed) Close(session);
    }

    // ---------------------------------------------------------------- sessions

    /// <summary>A free session that may load from the site, the one freed last first; or a new session on the next proxy.</summary>
    private async Task<ProxySession> TakeSessionAsync(ProxyList proxies, string site, CancellationToken cancellationToken)
    {
        List<ProxySession>? dropped = null;
        lock (_lock)
        {
            FreeLateVerdicts(proxies);
            for (var i = _free.Count - 1; i >= 0; i--)
            {
                var session = _free[i];
                if (!proxies.MayLoad(session.Proxy, site))
                {
                    // a proxy dropped from the list goes; one resting for the site keeps its session for other sites
                    if (!session.Proxy.Listed)
                    {
                        _free.RemoveAt(i);
                        session.Is = SessionState.Closed;
                        (dropped ??= []).Add(session);
                    }
                    continue;
                }
                _free.RemoveAt(i);
                session.Is = SessionState.Loading;
                _loading.Add(session);
                proxies.CountLoad(session.Proxy);
                CloseAll(dropped);
                return session;
            }
        }
        CloseAll(dropped);

        var proxy = await proxies.AcquireForSessionAsync(site, cancellationToken).ConfigureAwait(false);
        var opened = new ProxySession(proxy, UserAgents?.Next() ?? UserAgent) { Is = SessionState.Loading };
        lock (_lock) _loading.Add(opened);
        return opened;

        void CloseAll(List<ProxySession>? sessions)
        {
            if (sessions == null) return;
            foreach (var session in sessions) Close(session);
        }
    }

    /// <summary>Answers that waited for their verdict longer than <see cref="VerdictTimeout"/> free their sessions. Under the lock.</summary>
    private void FreeLateVerdicts(ProxyList proxies)
    {
        if (_waiting.Count == 0) return;
        var late = proxies.Clock.GetUtcNow() - VerdictTimeout;
        for (var i = 0; i < _waiting.Count; i++)
        {
            var session = _waiting[i];
            if (session.WaitingSince > late) continue;
            _waiting.RemoveAt(i--);
            session.Is = SessionState.Free;
            _free.Insert(0, session);           // after the sessions that got their verdict
        }
    }

    /// <summary>
    /// The load of the session ended: its answer waits for its verdict, it is free (a load that failed by the page's fault), or it is
    /// closed. A session doomed while it loaded, or closed with the loader, is closed (once).
    /// </summary>
    private void EndLoad(ProxySession session, SessionState next)
    {
        lock (_lock)
        {
            _loading.Remove(session);
            if (session.Is == SessionState.Closed) return;          // closed with the loader
            if (session.Doomed) next = SessionState.Closed;
            session.Is = next;
            switch (next)
            {
                case SessionState.Waiting:
                    session.WaitingSince = Proxies!.Clock.GetUtcNow();
                    _waiting.Add(session);
                    return;
                case SessionState.Free:
                    _free.Add(session);
                    return;
            }
        }
        Close(session);
    }

    /// <summary>The verdict of the session's answer came and was not bad: the session is free, if that answer is its last one.</summary>
    private void Free(Served served)
    {
        lock (_lock)
        {
            var session = served.Session;
            if (session.Is != SessionState.Waiting || Interlocked.Read(ref session._loads) != served.Load) return;
            _waiting.Remove(session);
            session.Is = SessionState.Free;
            _free.Add(session);
        }
    }

    /// <summary>The session is thrown away: at once, or when the load it serves now ends.</summary>
    private void Discard(ProxySession session)
    {
        lock (_lock)
        {
            switch (session.Is)
            {
                case SessionState.Closed:
                    return;
                case SessionState.Loading:
                    // a stale answer of a session that serves another load now: it is closed when that load ends
                    session.Doomed = true;
                    return;
                case SessionState.Waiting:
                    _waiting.Remove(session);
                    break;
                case SessionState.Free:
                    _free.Remove(session);
                    break;
            }
            session.Is = SessionState.Closed;
        }
        Close(session);
    }

    private void Close(ProxySession session)
    {
        Proxies?.CloseSession(session.Proxy);
        CloseSession(session);
    }
}
