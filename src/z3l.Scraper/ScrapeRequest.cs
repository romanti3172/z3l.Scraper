namespace z3l.Scraper;

/// <summary>Request priority: higher priority requests are taken from the queue first.</summary>
public enum ScrapePriority
{
    /// <summary>Taken before <see cref="Normal"/> and <see cref="Low"/>.</summary>
    High = 0,
    /// <summary>Default priority.</summary>
    Normal = 1,
    /// <summary>Taken last.</summary>
    Low = 2,
}

/// <summary>
/// A page to load. <see cref="Label"/> selects the handler registered with <see cref="Scraper.Route(ScrapeLabel, Func{ScrapeContext, Task})"/>;
/// <see cref="Data"/> goes to a handler registered with <see cref="Scraper.Route{TData}(ScrapeLabel, Func{ScrapeContext, TData, Task})"/>.
/// </summary>
/// <remarks>A plain string converts to a request with the default label.</remarks>
public sealed record ScrapeRequest
{
    /// <summary>Label used when none is given.</summary>
    public const string DefaultLabel = ScrapeLabel.DefaultName;

    /// <summary>Creates a request.</summary>
    /// <param name="url">Absolute URL.</param>
    /// <param name="label">Handler label: a <see cref="PageType"/> or a string; the default label when omitted.</param>
    public ScrapeRequest(string url, ScrapeLabel label = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Url = url;
        Label = label.Name;
    }

    /// <summary>
    /// A request from a <see cref="Scraper.RequestsNeeded"/> source by its URL alone. The URL is not checked here: the run fails
    /// an empty or bad one with <see cref="FailureReason.BadUrl"/> instead of throwing.
    /// </summary>
    internal ScrapeRequest(string? url, ScrapeLabel label, bool skipDuplicateCheck)
    {
        Url = url ?? "";
        Label = label.Name;
        SkipDuplicateCheck = skipDuplicateCheck;
    }

    /// <summary>Creates a request with data for a typed handler.</summary>
    /// <param name="url">Absolute URL.</param>
    /// <param name="label">Handler label.</param>
    /// <param name="data">Data of the page, passed to the handler registered with <c>Route&lt;TData&gt;</c>.</param>
    public static ScrapeRequest For<TData>(string url, ScrapeLabel label, TData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new ScrapeRequest(url, label) { Data = data };
    }

    /// <summary>Absolute URL.</summary>
    public string Url { get; init; }

    /// <summary>Selects the handler and the response rules (a <see cref="PageType"/> name or your own string).</summary>
    public string Label { get; init; }

    /// <summary>HTTP method, GET by default.</summary>
    public HttpMethod Method { get; init; } = HttpMethod.Get;

    /// <summary>Request body (POST, PUT...).</summary>
    public string? Body { get; init; }

    /// <summary>Content type of <see cref="Body"/>; <c>application/x-www-form-urlencoded</c> when omitted.</summary>
    public string? ContentType { get; init; }

    /// <summary>Extra headers for this request.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Queue priority.</summary>
    public ScrapePriority Priority { get; init; } = ScrapePriority.Normal;

    /// <summary>
    /// Data of the page (category, parent item...). A handler registered with <c>Route&lt;TData&gt;</c> gets it typed;
    /// other handlers read it here.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>Overrides <see cref="ScraperOptions.MaxRetries"/> for this request.</summary>
    public int? MaxRetries { get; init; }

    /// <summary>
    /// Key used to skip duplicates. When omitted it is made of the method, the URL without the fragment and with its query parameters
    /// sorted by name, and the body: POST requests to one URL with different bodies are different pages, and <c>?b=2&amp;a=1</c> is the
    /// page of <c>?a=1&amp;b=2</c> (parameters of one name keep their order). The order changes only the key: the URL is loaded as it
    /// was found, less the <see cref="ScraperOptions.IgnoredQueryParameters"/>.
    /// </summary>
    public string? UniqueKey { get; init; }

    /// <summary>
    /// Load the page even if a request with the same key was already queued in this run: normally such a request is skipped
    /// as a duplicate. The request is not remembered either, so a later request with the same key is queued too.
    /// </summary>
    public bool SkipDuplicateCheck { get; init; }

    /// <summary>
    /// The file the handler saves this page to (an image, a PDF...). With <see cref="ScraperOptions.ReuseSavedFiles"/> a file already there
    /// (saved by a run before and not used up yet) is given as the response instead of a load, with
    /// <see cref="PageResponse.FromSavedFile"/>: it goes through the response checks and the handler as a loaded one, without an attempt
    /// or a load slot. A file that fails the checks, or that the handler asks to retry, is deleted and the page is loaded.
    /// </summary>
    public string? SavePath { get; init; }

    /// <summary>
    /// Number of load attempts made so far (1 in the first handler call). A load a scraping service refused for its limits is no
    /// attempt (<see cref="ScraperOptions.RateLimitRetries"/>).
    /// </summary>
    public int Attempts { get; internal set; }

    /// <summary>Link depth: 0 for start requests, one more than the page whose handler queued it.</summary>
    public int Depth { get; internal set; }

    /// <summary>
    /// URL of the page whose handler queued this request, after the redirects of that page; null for start requests and for those of
    /// <see cref="Scraper.RequestsNeeded"/>. <see cref="Loading.StealthHttpPageLoader"/> makes the Referer of it, as a browser does, and
    /// it shows where a broken link was found.
    /// </summary>
    public string? ParentUrl { get; internal set; }

    internal int NotFoundCount { get; set; }

    /// <summary>Loads a scraping service refused for its limits that took no attempt (<see cref="ScraperOptions.RateLimitRetries"/>).</summary>
    internal int RateLimitCount { get; set; }

    /// <summary>The request already holds a load slot of its throttled host (it waited in the host's line).</summary>
    internal bool HasThrottleSlot { get; set; }

    /// <summary><see cref="Scraper.CachingPageLoader"/> was asked for this request: its next loads go to the loader.</summary>
    internal bool SkipCache { get; set; }

    /// <summary>The file of <see cref="SavePath"/> was tried for this request: its next loads go to the loader.</summary>
    internal bool SkipSavedFile { get; set; }

    /// <summary>With a state file: items of this page the consumer has not taken yet; the page stays in the file until it is 0.</summary>
    internal int UnackedItems;

    /// <summary>The key of duplicates (<see cref="UniqueKey"/>), <see cref="Url"/> parsed for it.</summary>
    internal string GetUniqueKey() => GetUniqueKey(parsed: null);

    /// <summary>
    /// The key of duplicates (<see cref="UniqueKey"/>) of <see cref="Url"/> parsed already: a link found on a page is not parsed once
    /// more for its key. The Uri is taken only when it was parsed from <see cref="Url"/> itself: a parse that changed its string, escaping
    /// a broken '%' as %25, may keep the other escapes as it found them (<c>%7e</c>), and the parse of <see cref="Url"/> makes them
    /// canonical (<c>~</c>), as the key always had them.
    /// </summary>
    /// <param name="parsed">The URL as a Uri; null or another one parses <see cref="Url"/>.</param>
    internal string GetUniqueKey(Uri? parsed)
    {
        if (!string.IsNullOrEmpty(UniqueKey)) return UniqueKey;

        var uri = parsed is { IsAbsoluteUri: true } && parsed.OriginalString == Url ? parsed
            : Uri.TryCreate(Url, UriKind.Absolute, out var again) ? again : null;
        // the order of the query parameters makes no other page; a URL with them sorted keeps the key it always had
        var url = uri == null ? Url : UrlHelper.SortQuery(uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped));
        return string.IsNullOrEmpty(Body) ? $"{Method.Method} {url}" : $"{Method.Method} {url} {Body}";
    }

    /// <summary>Creates a GET request with the <see cref="DefaultLabel"/>.</summary>
    public static implicit operator ScrapeRequest(string url) => new(url);
}
