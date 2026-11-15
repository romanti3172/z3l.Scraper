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
/// A page to load. <see cref="Label"/> selects the handler registered with
/// <see cref="Scraper{TItem}.Route(string, Func{ScrapeContext{TItem}, Task})"/>.
/// </summary>
/// <remarks>A plain string converts to a request with the <see cref="DefaultLabel"/>.</remarks>
public sealed record ScrapeRequest
{
    /// <summary>Label used when none is given.</summary>
    public const string DefaultLabel = "default";

    /// <summary>Creates a request.</summary>
    /// <param name="url">Absolute URL.</param>
    /// <param name="label">Handler label; <see cref="DefaultLabel"/> when omitted.</param>
    public ScrapeRequest(string url, string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Url = url;
        Label = string.IsNullOrWhiteSpace(label) ? DefaultLabel : label;
    }

    /// <summary>Absolute URL.</summary>
    public string Url { get; init; }

    /// <summary>Selects the handler and the response rules.</summary>
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

    /// <summary>Any data to pass to the handler (category name, parent item...).</summary>
    public object? UserData { get; init; }

    /// <summary>Overrides <see cref="ScraperOptions.MaxRetries"/> for this request.</summary>
    public int? MaxRetries { get; init; }

    /// <summary>
    /// Key used to skip duplicates. When omitted it is made of the method, the URL without the fragment
    /// and the body, so POST requests to one URL with different bodies are different pages.
    /// </summary>
    public string? UniqueKey { get; init; }

    /// <summary>Load the page even if a request with the same key was already queued.</summary>
    public bool SkipDuplicateCheck { get; init; }

    /// <summary>Number of load attempts made so far (1 in the first handler call).</summary>
    public int Attempts { get; internal set; }

    internal int NotFoundCount { get; set; }

    internal string GetUniqueKey()
    {
        if (!string.IsNullOrEmpty(UniqueKey)) return UniqueKey;

        var url = Url;
        if (Uri.TryCreate(Url, UriKind.Absolute, out var uri))
        {
            url = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        }
        return string.IsNullOrEmpty(Body) ? $"{Method.Method} {url}" : $"{Method.Method} {url} {Body}";
    }

    /// <summary>Creates a GET request with the <see cref="DefaultLabel"/>.</summary>
    public static implicit operator ScrapeRequest(string url) => new(url);
}
