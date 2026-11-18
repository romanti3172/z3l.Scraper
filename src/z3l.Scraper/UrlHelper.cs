using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace z3l.Scraper;

internal static class UrlHelper
{
    /// <summary>
    /// Makes an absolute http(s) URL. A value without a scheme is relative to <paramref name="baseUrl"/>.
    /// The value is parsed as relative explicitly: on Windows "//host/x" parses as an absolute UNC path and on Unix
    /// "/x" as an absolute file path, so <c>Uri.TryCreate(value, UriKind.Absolute)</c> cannot tell links apart.
    /// </summary>
    public static bool TryResolve(string? baseUrl, string? url, out string absolute)
    {
        if (!TryResolveUri(baseUrl, url, out var uri))
        {
            absolute = string.Empty;
            return false;
        }
        absolute = uri.AbsoluteUri;
        return true;
    }

    /// <summary>Makes an absolute http(s) URL, as <see cref="TryResolve(string?, string?, out string)"/> does, and gives it parsed.</summary>
    public static bool TryResolveUri(string? baseUrl, string? url, [NotNullWhen(true)] out Uri? absolute)
    {
        absolute = null;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var value = url.Trim();
        Uri? result;
        if (HasScheme(value))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out result)) return false;
        }
        else
        {
            if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || !IsHttp(baseUri)) return false;
            if (!Uri.TryCreate(value, UriKind.Relative, out var relative) || !Uri.TryCreate(baseUri, relative, out result)) return false;
        }

        if (!IsHttp(result) || string.IsNullOrEmpty(result.Host)) return false;

        absolute = result;
        return true;
    }

    /// <summary>Checks and normalizes an absolute http(s) URL.</summary>
    public static bool TryNormalize(string? url, out string absolute) => TryResolve(null, url, out absolute);

    /// <summary>Checks and normalizes an absolute http(s) URL, and gives it parsed.</summary>
    public static bool TryNormalizeUri(string? url, [NotNullWhen(true)] out Uri? absolute) => TryResolveUri(null, url, out absolute);

    /// <summary>
    /// The URL without the query parameters of <paramref name="ignored"/>; the same string when it has none of them. The other parameters
    /// stay as they are, in their order, and so does the fragment; a query left with nothing loses its '?'.
    /// </summary>
    public static string WithoutParameters(string url, QueryParameterSet ignored)
    {
        if (ignored.IsEmpty) return url;
        // the first '#' starts the fragment, and the query is the part from the first '?' before it
        var end = url.IndexOf('#');
        if (end < 0) end = url.Length;
        var start = url.IndexOf('?', 0, end);
        if (start < 0) return url;

        var query = url.AsSpan(start + 1, end - start - 1);
        if (!HasAny(query, ignored)) return url;

        var kept = new StringBuilder(url.Length);
        kept.Append(url, 0, start);
        var first = true;
        foreach (var range in query.Split('&'))
        {
            var parameter = query[range];
            if (parameter.IsEmpty || ignored.Contains(Name(parameter))) continue;
            kept.Append(first ? '?' : '&').Append(parameter);
            first = false;
        }
        kept.Append(url, end, url.Length - end);
        return kept.ToString();
    }

    private static bool HasAny(ReadOnlySpan<char> query, QueryParameterSet ignored)
    {
        foreach (var range in query.Split('&'))
        {
            var parameter = query[range];
            if (!parameter.IsEmpty && ignored.Contains(Name(parameter))) return true;
        }
        return false;
    }

    /// <summary>
    /// The URL with its query parameters sorted by name, for the key of duplicates: <c>?b=2&amp;a=1</c> and <c>?a=1&amp;b=2</c> are one
    /// page. Parameters of one name keep their order (<c>?id=2&amp;id=1</c> may be a list), and empty ones go (<c>?a=1&amp;&amp;b=2</c>,
    /// a lone '?'). Takes a URL without its fragment; gives the same string when it needs no change.
    /// </summary>
    public static string SortQuery(string url)
    {
        var start = url.IndexOf('?');
        if (start < 0) return url;
        var query = url.AsSpan(start + 1);
        if (IsSorted(query)) return url;

        var parameters = new List<string>();
        foreach (var range in query.Split('&'))
        {
            var parameter = query[range];
            if (!parameter.IsEmpty) parameters.Add(parameter.ToString());
        }
        if (parameters.Count == 0) return url[..start];

        // OrderBy is stable: parameters of one name keep their order
        return string.Concat(url.AsSpan(0, start + 1), string.Join('&', parameters.OrderBy(ParameterName, StringComparer.Ordinal)));
    }

    /// <summary>Sorted by name, with no empty parameter: a lone '?' is not.</summary>
    private static bool IsSorted(ReadOnlySpan<char> query)
    {
        if (query.IsEmpty) return false;
        var previous = ReadOnlySpan<char>.Empty;
        var first = true;
        foreach (var range in query.Split('&'))
        {
            var parameter = query[range];
            if (parameter.IsEmpty) return false;
            var name = Name(parameter);
            if (!first && name.CompareTo(previous, StringComparison.Ordinal) < 0) return false;
            previous = name;
            first = false;
        }
        return true;
    }

    /// <summary>The name of a query parameter: what comes before its '='.</summary>
    private static ReadOnlySpan<char> Name(ReadOnlySpan<char> parameter)
    {
        var equals = parameter.IndexOf('=');
        return equals < 0 ? parameter : parameter[..equals];
    }

    private static string ParameterName(string parameter)
    {
        var equals = parameter.IndexOf('=');
        return equals < 0 ? parameter : parameter[..equals];
    }

    /// <summary>RFC 3986: scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) ":"</summary>
    private static bool HasScheme(string value)
    {
        if (value.Length == 0 || !char.IsAsciiLetter(value[0])) return false;
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == ':') return true;
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.')) return false;
        }
        return false;
    }

    private static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}

/// <summary>
/// Names of query parameters (<see cref="ScraperOptions.IgnoredQueryParameters"/>): whole names, and prefixes written with a '*' at the
/// end (<c>utm_*</c>). Names ignore case.
/// </summary>
internal sealed class QueryParameterSet
{
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly string[] _prefixes;

    public QueryParameterSet(IEnumerable<string> entries)
    {
        var prefixes = new List<string>();
        foreach (var entry in entries)
        {
            var name = Check(entry);
            if (name.EndsWith('*')) prefixes.Add(name[..^1]);
            else _names.Add(name);
        }
        _prefixes = [.. prefixes];
        _lookup = _names.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public bool IsEmpty => _names.Count == 0 && _prefixes.Length == 0;

    public bool Contains(ReadOnlySpan<char> name)
    {
        if (_lookup.Contains(name)) return true;
        foreach (var prefix in _prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>An entry of <see cref="ScraperOptions.IgnoredQueryParameters"/>, trimmed.</summary>
    /// <exception cref="ArgumentException">Empty, a '*' that is not at the end, or a character no parameter name has here.</exception>
    internal static string Check(string? entry)
    {
        var name = entry?.Trim();
        if (string.IsNullOrEmpty(name) || name == "*" || name.AsSpan(0, name.Length - 1).Contains('*') || name.AsSpan().IndexOfAny("=&?#/ ") >= 0)
        {
            throw new ArgumentException(
                $"IgnoredQueryParameters takes names of query parameters (\"sessionid\") or prefixes that end in '*' (\"utm_*\"), not \"{entry}\"",
                nameof(ScraperOptions.IgnoredQueryParameters));
        }
        return name;
    }
}
