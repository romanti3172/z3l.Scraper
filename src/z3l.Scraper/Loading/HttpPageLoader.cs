namespace z3l.Scraper.Loading;

/// <summary>
/// Settings of <see cref="HttpPageLoader"/>: those of <see cref="HttpClientPageLoaderOptions"/>, with an Accept for pages and an
/// Accept-Language among the <see cref="HttpClientPageLoaderOptions.DefaultHeaders"/>.
/// </summary>
public sealed class HttpPageLoaderOptions : HttpClientPageLoaderOptions
{
    /// <summary>Creates the settings, with Accept and Accept-Language (en-US) in the default headers.</summary>
    public HttpPageLoaderOptions()
    {
        DefaultHeaders["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
        DefaultHeaders["Accept-Language"] = "en-US,en;q=0.9";
    }
}

/// <summary>
/// Loads pages with <see cref="HttpClient"/> in sessions (<see cref="LoaderSession"/>), directly or through proxies, sending the request as it is: HTTP/1.1, the default
/// headers, the headers of the request and the user agent. Thread-safe; dispose it when the scraping is over.
/// <see cref="StealthHttpPageLoader"/> asks for pages the way a browser does.
/// </summary>
public sealed class HttpPageLoader : HttpClientPageLoader
{
    /// <summary>Creates a loader with its own <see cref="HttpClient"/> (decompression, cookies, connection pooling).</summary>
    /// <exception cref="ArgumentException">Both <see cref="HttpClientPageLoaderOptions.Proxy"/> and <see cref="HttpClientPageLoaderOptions.Proxies"/> are set.</exception>
    public HttpPageLoader(HttpPageLoaderOptions? options = null)
        : base(options ?? new HttpPageLoaderOptions())
    {
    }

    /// <summary>Uses an existing client (for example from <c>IHttpClientFactory</c>); it is not disposed by the loader.</summary>
    /// <exception cref="ArgumentException"><see cref="HttpClientPageLoaderOptions.Proxies"/> is set: proxy sessions need clients of the loader.</exception>
    public HttpPageLoader(HttpClient client, HttpPageLoaderOptions? options = null)
        : base(client, options ?? new HttpPageLoaderOptions())
    {
    }

    /// <inheritdoc />
    protected override HttpRequestMessage CreateMessage(ScrapeRequest request, Uri uri, string? userAgent)
    {
        var message = NewMessage(request, uri);
        foreach (var header in Options.DefaultHeaders) SetHeader(message, header.Key, header.Value);
        if (request.Headers != null)
        {
            foreach (var header in request.Headers) SetHeader(message, header.Key, header.Value);
        }
        // the loader's user agent, unless the default headers or the request set one
        if (userAgent != null && !message.Headers.Contains("User-Agent")) message.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return message;
    }
}
