using System.Net;
using System.Runtime.CompilerServices;
using z3l.Scraper.Diagnostics;

namespace z3l.Scraper.Loading;

/// <summary>
/// A session of a loader: a client of its own with its cookies (whatever the loader keeps in <see cref="State"/>: an HttpClient, a
/// browser context...), one user agent and, with a proxy list, one proxy. The user agent and the proxy are taken apart, each by the rules
/// of its list, and stay together while the session lives. A session serves one load at a time and goes on while its loads do well; when
/// it fails too many times in a row (<see cref="BlockAwarePageLoader.SessionMaxFailuresInARow"/>) it is thrown away whole, and the next
/// load opens a new session with a new client, user agent and proxy. Made by <see cref="BlockAwarePageLoader"/>.
/// </summary>
public sealed class LoaderSession
{
    internal LoaderSession(ProxyList.Entry? proxy, UserAgentList.Entry? agent, string? userAgent)
    {
        Proxy = proxy;
        Agent = agent;
        UserAgent = userAgent;
    }

    /// <summary>The proxy of the session; null without a proxy list.</summary>
    public ProxyList.Entry? Proxy { get; }

    /// <summary>The user agent of every load of the session; null for none.</summary>
    public string? UserAgent { get; }

    /// <summary>The user agent's entry in its list; null without one.</summary>
    internal UserAgentList.Entry? Agent { get; }

    /// <summary>
    /// What the loader keeps for the session, made on its first load (an HttpClient with its cookies, a browser context...) and let go
    /// in <see cref="BlockAwarePageLoader.CloseSession"/>. Only one load uses a session at a time.
    /// </summary>
    public object? State { get; set; }

    /// <summary>Loads the session took.</summary>
    public long Loads => Interlocked.Read(ref _loads);

    /// <summary>Answers of the session that were the page.</summary>
    public long Successes { get; internal set; }

    /// <summary>Failures, blocks and bad responses since its last good answer.</summary>
    public int FailuresInARow { get; internal set; }

    internal long _loads;

    /// <summary>Where the session is: loading, waiting for the verdict of its answer, free, or closed.</summary>
    internal SessionState Is;

    /// <summary>The session was thrown away while it was loading again: it is closed when that load ends.</summary>
    internal bool Doomed;

    /// <summary>When the session's last answer started to wait for its verdict.</summary>
    internal DateTimeOffset WaitingSince;

    /// <summary>The proxy and the user agent; nothing of the state.</summary>
    public override string ToString() => $"{Proxy?.ToString() ?? "direct"}{(UserAgent == null ? "" : $" as {UserAgent}")}";
}

internal enum SessionState
{
    Loading,
    Waiting,
    Free,
    Closed,
}

/// <summary>What went wrong with a load of a session.</summary>
internal enum SessionFailure
{
    /// <summary>No answer: a network error, a timeout, 407.</summary>
    Failure,

    /// <summary>The site blocked the answer.</summary>
    Blocked,

    /// <summary>The answer was not the page.</summary>
    BadResponse,
}

/// <summary>
/// Base of the loaders that load pages themselves, through <see cref="ProxyList"/> proxies or without them. Every load goes through a
/// session (<see cref="LoaderSession"/>): the derived class's client with its cookies, one user agent of <see cref="UserAgents"/> and,
/// with proxies, one proxy, each taken by the rules of its list. A session that did well serves the next load with the same client,
/// cookies, user agent and proxy, and free sessions go before new ones. A failure (no answer, 407, a timeout), a block or a bad response
/// is forgiven once to a session that already had <see cref="SessionTrustedAfter"/> good answers, never twice in a row; otherwise the
/// session is thrown away whole, its proxy and its user agent are told and rest as their lists say, and the next load opens a new
/// session. There are as many sessions as loads at once, and a session serves one load at a time. A derived class loads one page
/// through the session it is given, in <see cref="LoadThroughAsync"/>: <see cref="HttpClientPageLoader"/> does it with HttpClient, a
/// loader of your own can do it with a browser, curl or anything else.
/// <para>
/// An answer stays with its session until the caller says what it was: <see cref="ReportGoodResponse"/> and <see cref="ReportDone"/>
/// free the session for the next load, <see cref="ReportBlocked"/> and <see cref="ReportBadResponse"/> count a failure. The scraper
/// always says it; code that calls <see cref="LoadAsync"/> itself calls one of them for every answer (a session nobody hears about is
/// freed after 2 minutes).
/// </para>
/// </summary>
public abstract class BlockAwarePageLoader : IBlockAwarePageLoader, IPublishesMetrics
{
    /// <summary>A run with metrics uses the loader: its lists publish them.</summary>
    void IPublishesMetrics.PublishMetrics()
    {
        Proxies?.PublishMetrics();
        UserAgents?.PublishMetrics();
    }

    private readonly object _lock = new();

    /// <summary>Sessions free for a load, the one freed last at the end.</summary>
    private readonly List<LoaderSession> _free = [];

    /// <summary>Sessions whose answer waits for its verdict.</summary>
    private readonly List<LoaderSession> _waiting = [];

    /// <summary>Sessions loading now.</summary>
    private readonly HashSet<LoaderSession> _loading = [];

    /// <summary>Which session loaded a response, and which of its loads it was.</summary>
    private readonly ConditionalWeakTable<PageResponse, Served> _servedBy = [];

    /// <summary>The session of a load that was cancelled, and whether it was thrown away, for <see cref="ReportTimeout"/>.</summary>
    private readonly ConditionalWeakTable<ScrapeRequest, Cancelled> _cancelled = [];

    private sealed record Served(LoaderSession Session, long Load);

    private sealed record Cancelled(LoaderSession Session, bool Discarded, string Site);

    private int _trustedAfter = 1;
    private int _maxFailuresInARow = 2;

    /// <summary>Creates the loader.</summary>
    /// <param name="proxies">Proxies to load through, one per session; null loads without them (or through the one proxy the derived class knows).</param>
    /// <param name="userAgents">User agents, one per session; null for <paramref name="userAgent"/>.</param>
    /// <param name="userAgent">The user agent when <paramref name="userAgents"/> is null; null for none.</param>
    protected BlockAwarePageLoader(ProxyList? proxies, UserAgentList? userAgents, string? userAgent)
    {
        Proxies = proxies;
        UserAgents = userAgents;
        UserAgent = userAgent;
    }

    /// <summary>Proxies to load through; null for none.</summary>
    protected ProxyList? Proxies { get; }

    /// <summary>User agents of the sessions; null for <see cref="UserAgent"/>.</summary>
    protected UserAgentList? UserAgents { get; }

    /// <summary>The user agent when there are no <see cref="UserAgents"/>.</summary>
    protected string? UserAgent { get; }

    /// <summary>
    /// Good answers after which a session is forgiven a failure, a block or a bad response (up to <see cref="SessionMaxFailuresInARow"/>
    /// in a row). Before them, the first one throws it away. 1 by default: a session that has loaded one page gets a second chance.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 0.</exception>
    public int SessionTrustedAfter
    {
        get => _trustedAfter;
        protected set => _trustedAfter = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Must be 0 or more");
    }

    /// <summary>
    /// Failures, blocks and bad responses in a row that throw a trusted session away (<see cref="SessionTrustedAfter"/>); a good answer
    /// starts the count again. 2 by default; 1 throws every session away at its first one.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int SessionMaxFailuresInARow
    {
        get => _maxFailuresInARow;
        protected set => _maxFailuresInARow = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Must be 1 or more");
    }

    /// <summary>
    /// How long an answer waits for its verdict (<see cref="ReportGoodResponse"/>, <see cref="ReportDone"/>...) before its session is
    /// freed without one. The scraper always gives the verdict; this is for code that loads pages itself and forgets to.
    /// </summary>
    internal TimeSpan VerdictTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Time source; that of the lists unless a test sets one.</summary>
    internal TimeProvider? Clock { get; set; }

    private DateTimeOffset Now => (Clock ?? Proxies?.Clock ?? UserAgents?.Clock ?? TimeProvider.System).GetUtcNow();

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

        var site = uri.Host;
        var session = await TakeSessionAsync(site, cancellationToken).ConfigureAwait(false);
        var load = Interlocked.Increment(ref session._loads);
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
        catch (OperationCanceledException)
        {
            // a timeout (ReportTimeout tells the lists) or a stopped run
            var discarded = Fail(session);
            _cancelled.AddOrUpdate(request, new Cancelled(session, discarded, site));
            EndLoad(session, discarded ? SessionState.Closed : SessionState.Free);
            throw;
        }
        catch (Exception ex)
        {
            var discarded = Fail(session);
            Tell(session, SessionFailure.Failure, site, ex.Message, discarded);
            EndLoad(session, discarded ? SessionState.Closed : SessionState.Free);
            throw;
        }

        _servedBy.AddOrUpdate(response, new Served(session, load));
        if (response.StatusCode == (int)HttpStatusCode.ProxyAuthenticationRequired)
        {
            var discarded = Fail(session);
            Tell(session, SessionFailure.Failure, site, "407 Proxy Authentication Required", discarded);
            EndLoad(session, discarded ? SessionState.Closed : SessionState.Free);
        }
        else
        {
            if (session.Proxy is { } proxy) Proxies!.ReportSuccess(proxy);
            if (session.Agent is { } agent) UserAgents!.ReportSuccess(agent);
            EndLoad(session, SessionState.Waiting);
        }
        return response;
    }

    /// <summary>
    /// Loads one page. Any HTTP status is a normal result; throw only when there is no response (a network error, a proxy failure): it
    /// counts as a failure of the session, and the scraper retries the request. <see cref="NonRetryableException"/> fails the request at
    /// once and is not the session's fault.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="uri">Its URL, an absolute http(s) one.</param>
    /// <param name="session">
    /// The session to load through: its proxy (null without proxies), its user agent, and its <see cref="LoaderSession.State"/> to make
    /// on the first load and keep (a client with its cookies).
    /// </param>
    /// <param name="userAgent">The user agent of the session; null for none.</param>
    /// <param name="cancellationToken">Cancelled when the load runs out of time or the run stops.</param>
    protected abstract Task<PageResponse> LoadThroughAsync(ScrapeRequest request, Uri uri, LoaderSession session, string? userAgent,
        CancellationToken cancellationToken);

    /// <summary>
    /// A session is thrown away or the loader is closed: let go of what the session kept in <see cref="LoaderSession.State"/>. Called
    /// once per session, never while it loads (but for <see cref="CloseSessions"/>).
    /// </summary>
    /// <param name="session">The session.</param>
    protected virtual void CloseSession(LoaderSession session)
    {
    }

    /// <summary>
    /// The scraper saw a block in this response: a failure of the session that loaded it. Thrown away, its proxy and user agent rest for
    /// the site as their lists say.
    /// </summary>
    public virtual void ReportBlocked(ScrapeRequest request, PageResponse response, string reason) =>
        Judge(request, response, SessionFailure.Blocked, reason);

    /// <summary>
    /// The response was not the page: a failure of the session that loaded it. Thrown away, its proxy and user agent go after the clean
    /// ones for the site, and after bad responses in a row rest for it.
    /// </summary>
    public virtual void ReportBadResponse(ScrapeRequest request, PageResponse response, string reason) =>
        Judge(request, response, SessionFailure.BadResponse, reason);

    /// <summary>The last load of the request timed out: a failure of its session, counted when the load ended.</summary>
    public virtual void ReportTimeout(ScrapeRequest request)
    {
        if (request != null && _cancelled.TryGetValue(request, out var cancelled))
        {
            _cancelled.Remove(request);
            Tell(cancelled.Session, SessionFailure.Failure, cancelled.Site, "timeout", cancelled.Discarded);
        }
    }

    /// <summary>
    /// The response was the page: the session that loaded it starts its failures in a row again and serves the next load, and the bad
    /// responses in a row of its proxy and user agent for the site start again.
    /// </summary>
    public virtual void ReportGoodResponse(ScrapeRequest request, PageResponse response)
    {
        if (!_servedBy.TryGetValue(response, out var served)) return;
        var session = served.Session;
        lock (_lock)
        {
            session.Successes++;
            session.FailuresInARow = 0;
        }
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            if (session.Proxy is { } proxy) Proxies!.ReportGoodResponse(proxy, uri.Host);
            if (session.Agent is { } agent) UserAgents!.ReportGoodResponse(agent, uri.Host);
        }
        Free(served);
    }

    /// <summary>
    /// The caller is done with the response and has said all it will about it (a 404, a 429, a handler that failed...): unless it was
    /// a failure that threw the session away, the session serves the next load.
    /// </summary>
    public virtual void ReportDone(ScrapeRequest request, PageResponse response)
    {
        if (_servedBy.TryGetValue(response, out var served)) Free(served);
    }

    /// <summary>Closes every session (the loader is disposed); one that is loading now is closed with it.</summary>
    protected void CloseSessions()
    {
        List<LoaderSession> closed;
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

    // ---------------------------------------------------------------- verdicts

    /// <summary>A block or a bad response of an answer: a failure of its session, which is thrown away or goes on.</summary>
    private void Judge(ScrapeRequest request, PageResponse response, SessionFailure failure, string reason)
    {
        if (!_servedBy.TryGetValue(response, out var served)) return;
        var site = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ? uri.Host : "";
        var discarded = Fail(served.Session);
        Tell(served.Session, failure, site, reason, discarded);
        if (discarded) Discard(served.Session);
        else Free(served);
    }

    /// <summary>
    /// Counts a failure of the session: true when it is thrown away. A session with fewer than <see cref="SessionTrustedAfter"/> good
    /// answers goes at its first failure, a trusted one at <see cref="SessionMaxFailuresInARow"/> in a row.
    /// </summary>
    private bool Fail(LoaderSession session)
    {
        lock (_lock)
        {
            session.FailuresInARow++;
            var allowed = session.Successes >= SessionTrustedAfter ? SessionMaxFailuresInARow : 1;
            return session.FailuresInARow >= allowed;
        }
    }

    /// <summary>
    /// Tells the proxy and the user agent of the session what happened: as a failure, a block or a bad response that rests them when the
    /// session is thrown away, as a count only when it is forgiven. A failure (no answer) is the proxy's only: the site never saw the user
    /// agent.
    /// </summary>
    private void Tell(LoaderSession session, SessionFailure failure, string site, string reason, bool discarded)
    {
        if (!discarded)
        {
            if (session.Proxy is { } forgivenProxy) Proxies!.CountForgiven(forgivenProxy, failure);
            if (session.Agent is { } forgivenAgent) UserAgents!.CountForgiven(forgivenAgent, failure);
            return;
        }
        if (session.Proxy is { } proxy)
        {
            switch (failure)
            {
                case SessionFailure.Failure: Proxies!.ReportFailure(proxy, reason); break;
                case SessionFailure.Blocked when site.Length > 0: Proxies!.ReportBlocked(proxy, site); break;
                case SessionFailure.BadResponse when site.Length > 0: Proxies!.ReportBadResponse(proxy, site, reason); break;
            }
        }
        if (session.Agent is { } agent)
        {
            switch (failure)
            {
                case SessionFailure.Blocked when site.Length > 0: UserAgents!.ReportBlocked(agent, site); break;
                case SessionFailure.BadResponse when site.Length > 0: UserAgents!.ReportBadResponse(agent, site, reason); break;
            }
        }
    }

    // ---------------------------------------------------------------- sessions

    /// <summary>A free session that may load from the site, the one freed last first; or a new session.</summary>
    private async Task<LoaderSession> TakeSessionAsync(string site, CancellationToken cancellationToken)
    {
        List<LoaderSession>? dropped = null;
        LoaderSession? taken = null;
        lock (_lock)
        {
            FreeLateVerdicts();
            for (var i = _free.Count - 1; i >= 0 && taken == null; i--)
            {
                var session = _free[i];
                var proxyOk = session.Proxy is not { } proxy || Proxies!.MayLoad(proxy, site);
                var agentOk = session.Agent is not { } agent || UserAgents!.MayLoad(agent, site);
                if (!proxyOk || !agentOk)
                {
                    // gone from its list: closed; resting for the site: kept for other sites
                    if (session.Proxy is { Health.Listed: false } || session.Agent is { Health.Listed: false })
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
                taken = session;
            }
        }
        if (dropped != null)
        {
            foreach (var session in dropped) Close(session);
        }
        if (taken != null)
        {
            if (taken.Proxy is { } proxy) Proxies!.CountLoad(proxy);
            if (taken.Agent is { } agent) UserAgents!.CountLoad(agent);
            return taken;
        }

        var newProxy = Proxies is { } proxies ? await proxies.AcquireForSessionAsync(site, cancellationToken).ConfigureAwait(false) : null;
        var newAgent = UserAgents?.AcquireForSession(site);
        var opened = new LoaderSession(newProxy, newAgent, newAgent?.UserAgent ?? UserAgent) { Is = SessionState.Loading };
        lock (_lock) _loading.Add(opened);
        return opened;
    }

    /// <summary>Answers that waited for their verdict longer than <see cref="VerdictTimeout"/> free their sessions. Under the lock.</summary>
    private void FreeLateVerdicts()
    {
        if (_waiting.Count == 0) return;
        var late = Now - VerdictTimeout;
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
    /// The load of the session ended: its answer waits for its verdict, it is free, or it is closed. A session thrown away while it
    /// loaded, or closed with the loader, is closed (once).
    /// </summary>
    private void EndLoad(LoaderSession session, SessionState next)
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
                    session.WaitingSince = Now;
                    _waiting.Add(session);
                    return;
                case SessionState.Free:
                    _free.Add(session);
                    return;
            }
        }
        Close(session);
    }

    /// <summary>The verdict of the session's answer came and kept it: the session is free, if that answer is its last one.</summary>
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
    private void Discard(LoaderSession session)
    {
        lock (_lock)
        {
            switch (session.Is)
            {
                case SessionState.Closed:
                    return;
                case SessionState.Loading:
                    // a verdict on an older answer of a session that serves another load now: closed when that load ends
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

    private void Close(LoaderSession session)
    {
        if (session.Proxy is { } proxy) Proxies?.CloseSession(proxy);
        if (session.Agent is { } agent) UserAgents?.CloseSession(agent);
        CloseSession(session);
    }
}
