namespace z3l.Scraper.Crawling;

/// <summary>
/// What a run takes, made once from <see cref="ScraperOptions"/> when it starts: <see cref="ScraperOptions.AllowedHosts"/> and
/// <see cref="ScraperOptions.UrlFilter"/> for the links found on pages, and <see cref="ScraperOptions.IgnoredQueryParameters"/>, dropped
/// from the URLs of all requests.
/// </summary>
internal sealed class CrawlScope
{
    // hosts as Uri.IdnHost writes them (xn-- for an IDN); a host is found with its subdomains by its suffixes
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _hostLookup;
    private readonly Func<Uri, bool>? _filter;
    private readonly QueryParameterSet _ignored;

    /// <exception cref="ArgumentException">An entry of AllowedHosts or IgnoredQueryParameters is not a host or a parameter name.</exception>
    public CrawlScope(ScraperOptions options)
    {
        foreach (var host in options.AllowedHosts) _hosts.Add(HostName(host));
        _hostLookup = _hosts.GetAlternateLookup<ReadOnlySpan<char>>();
        _filter = options.UrlFilter;
        _ignored = new QueryParameterSet(options.IgnoredQueryParameters);
    }

    /// <summary>The URL as the run takes it, without the ignored query parameters; the same instance when it has none of them.</summary>
    public Uri Clean(Uri url)
    {
        var absolute = url.AbsoluteUri;
        var clean = UrlHelper.WithoutParameters(absolute, _ignored);
        return ReferenceEquals(clean, absolute) ? url : new Uri(clean);
    }

    /// <summary>
    /// A link found on a page is in the scope: its host is one of the allowed ones or their subdomain (any host when none is named), and
    /// the filter takes it. The filter is the user's: what it throws goes to the caller.
    /// </summary>
    public bool Allows(Uri url) => (_hosts.Count == 0 || IsAllowedHost(url.IdnHost)) && (_filter == null || _filter(url));

    /// <summary>The host, or a host it is a subdomain of, is allowed: www.shop.example, then shop.example, then example.</summary>
    private bool IsAllowedHost(string host)
    {
        var name = host.AsSpan();
        while (true)
        {
            if (_hostLookup.Contains(name)) return true;
            var dot = name.IndexOf('.');
            if (dot < 0) return false;
            name = name[(dot + 1)..];
        }
    }

    /// <summary>An entry of <see cref="ScraperOptions.AllowedHosts"/> as <see cref="Uri.IdnHost"/> writes it: lowercase, an IDN in xn-- form.</summary>
    /// <exception cref="ArgumentException">The entry is not a host name: a URL, a port, a path, a '*'.</exception>
    internal static string HostName(string? entry)
    {
        var value = entry?.Trim();
        // an IPv6 address comes in brackets; any other ':' is a scheme or a port
        if (!string.IsNullOrEmpty(value) && value.AsSpan().IndexOfAny("/\\?#@* ") < 0 && (!value.Contains(':') || value.StartsWith('['))
            && Uri.TryCreate($"http://{value}/", UriKind.Absolute, out var uri) && uri.IsDefaultPort && uri.AbsolutePath == "/" && uri.IdnHost.Length > 0)
        {
            return uri.IdnHost;
        }
        throw new ArgumentException(
            $"AllowedHosts takes host names, like \"shop.example\" (its subdomains are taken too), without a scheme, a port, a path or '*': \"{entry}\"",
            nameof(ScraperOptions.AllowedHosts));
    }
}
