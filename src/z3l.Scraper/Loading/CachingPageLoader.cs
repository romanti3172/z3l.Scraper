using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace z3l.Scraper.Loading;

/// <summary>A page kept by <see cref="CachingPageLoader"/>: what it writes and reads back.</summary>
/// <param name="Key">Key of the request: <see cref="ScrapeRequest.UniqueKey"/>, or the method, the URL and the body, as for duplicates.</param>
/// <param name="Label">Label of the request that loaded the page.</param>
/// <param name="RequestUrl">URL of that request.</param>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="Url">Final URL after redirects.</param>
/// <param name="ContentType">Content-Type header.</param>
/// <param name="Headers">Response headers.</param>
/// <param name="Body">Response body.</param>
/// <param name="IsText">
/// The body is the text of the page in UTF-8, whatever <paramref name="ContentType"/> says: a page drawn by a browser
/// (<see cref="PageResponse.FromText"/>).
/// </param>
/// <param name="SavedAt">When the page was kept.</param>
public sealed record CachedPage(string Key, string Label, string RequestUrl, int StatusCode, string Url, string? ContentType,
    IReadOnlyDictionary<string, string> Headers, byte[] Body, bool IsText, DateTimeOffset SavedAt)
{
    /// <summary>Takes what is needed from a response, which may be let go afterwards.</summary>
    public static CachedPage From(string key, ScrapeRequest request, PageResponse response, DateTimeOffset savedAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        return new CachedPage(key, request.Label, request.Url, response.StatusCode, response.Url, response.ContentType,
            new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase), response.Body, response.MadeFromText, savedAt);
    }

    /// <summary>The page as a response, the same as it was loaded.</summary>
    public PageResponse ToResponse() => IsText
        ? PageResponse.FromText(StatusCode, Url, Encoding.UTF8.GetString(Body), ContentType, Headers)
        : new PageResponse(StatusCode, Url, Body, ContentType, Headers);
}

/// <summary>
/// Keeps the pages the handlers took in a folder and gives them back instead of loading them again: run the scraper again and again
/// while you work on the handlers, without loading the site each time. Set it in <see cref="Scraper.CachingPageLoader"/>:
/// <code>scraper.CachingPageLoader = new CachingPageLoader { Path = "cache" };</code>
/// Only pages that passed the checks and that their handler took are kept, so block pages and cut-off pages do not get in. A kept page
/// that fails the checks later (the rules changed) or whose handler asks for a retry is forgotten, and its request is loaded.
/// <para>
/// <see cref="Path"/> gets a folder per host with two files per page: the body (<c>.html</c>, <c>.json</c>...) and its description
/// (<c>.meta.json</c>: the request, the status, the headers). To keep the pages elsewhere (a database...) with the same labels, filter and
/// limits, override <see cref="ReadAsync"/>, <see cref="WriteAsync"/>, <see cref="DeleteAsync"/> and <see cref="CountStored"/>; or
/// implement <see cref="ICachingPageLoader"/> yourself.
/// </para>
/// </summary>
public class CachingPageLoader : ICachingPageLoader
{
    private const string Format = "z3l.Scraper cached page";
    private const int Version = 1;

    private readonly object _lock = new();
    private bool _counted;
    private long _stored;

    /// <summary>Folder of the kept pages, made when the first page is kept. Not needed when the storage methods are overridden.</summary>
    public string? Path { get; set; }

    /// <summary>
    /// Labels whose pages are kept and given back (a <see cref="PageType"/> or a string); empty, the default, for every label.
    /// Pages of other labels are always loaded.
    /// </summary>
    public HashSet<ScrapeLabel> Labels { get; } = [];

    /// <summary>Which of those pages to keep, once their handler took them; null, the default, keeps them all.</summary>
    public Func<ScrapeRequest, PageResponse, bool>? Filter { get; set; }

    /// <summary>A kept page older than this is loaded again; zero, the default, keeps pages for ever.</summary>
    public TimeSpan MaxAge { get; set; }

    /// <summary>
    /// Pages to keep at most, those kept by earlier runs included; then new pages are not kept, and the kept ones are still given back.
    /// 0, the default, for no limit.
    /// </summary>
    public int MaxPages { get; set; }

    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <inheritdoc />
    public async ValueTask<PageResponse?> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Accepts(request)) return null;

        var key = KeyOf(request);
        var page = await ReadAsync(key, request, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        if (MaxAge > TimeSpan.Zero && Clock.GetUtcNow() - page.SavedAt > MaxAge)
        {
            await ForgetAsync(key, request, cancellationToken).ConfigureAwait(false);     // too old: the request loads it again
            return null;
        }
        return page.ToResponse();
    }

    /// <inheritdoc />
    public async ValueTask StoreAsync(ScrapeRequest request, PageResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        if (!Accepts(request) || (Filter != null && !Filter(request, response))) return;
        if (!TryTakePlace()) return;

        try
        {
            await WriteAsync(CachedPage.From(KeyOf(request), request, response, Clock.GetUtcNow()), request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            GivePlaceBack();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(ScrapeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Accepts(request)) await ForgetAsync(KeyOf(request), request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The label of the request is one of <see cref="Labels"/>, or there are none.</summary>
    protected bool Accepts(ScrapeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Labels.Count == 0 || Labels.Contains(new ScrapeLabel(request.Label));
    }

    /// <summary>
    /// Key of a request: <see cref="ScrapeRequest.UniqueKey"/>, or its method, URL without the fragment and body, as for duplicates.
    /// Pages are kept and found by it.
    /// </summary>
    protected virtual string KeyOf(ScrapeRequest request) => request.GetUniqueKey();

    private async ValueTask ForgetAsync(string key, ScrapeRequest request, CancellationToken cancellationToken)
    {
        if (await DeleteAsync(key, request, cancellationToken).ConfigureAwait(false)) GivePlaceBack();
    }

    // ---------------------------------------------------------------- limit

    /// <summary>A place for one more page under <see cref="MaxPages"/>; the pages kept earlier are counted on first use.</summary>
    private bool TryTakePlace()
    {
        if (MaxPages <= 0) return true;
        lock (_lock)
        {
            if (!_counted)
            {
                _stored = CountStored();
                _counted = true;
            }
            if (_stored >= MaxPages) return false;
            _stored++;
            return true;
        }
    }

    private void GivePlaceBack()
    {
        lock (_lock)
        {
            if (_counted && _stored > 0) _stored--;
        }
    }

    // ---------------------------------------------------------------- storage: a folder

    /// <summary>
    /// Reads the page kept under the key, or null when there is none. The folder keeps it in <c>{Path}/{host}/{hash of the key}.meta.json</c>
    /// and the body next to it.
    /// </summary>
    /// <param name="key">Key of the request (<see cref="KeyOf"/>).</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancelled when the run stops.</param>
    protected virtual async ValueTask<CachedPage?> ReadAsync(string key, ScrapeRequest request, CancellationToken cancellationToken)
    {
        var folder = FolderOf(request.Url);
        var meta = await PageFiles.ReadAsync(System.IO.Path.Combine(folder, NameOf(key) + PageFiles.MetaSuffix), cancellationToken).ConfigureAwait(false);
        if (meta is null) return null;

        // a description that is not ours, of another key or that cannot be read is no page: the request loads the page and keeps it again
        Description? description;
        try
        {
            description = Describe(meta);
        }
        catch (JsonException)
        {
            return null;
        }
        if (description is null || description.Page.Key != key) return null;

        var body = await PageFiles.ReadAsync(System.IO.Path.Combine(folder, description.BodyFile), cancellationToken).ConfigureAwait(false);
        return body is null ? null : description.Page with { Body = body };
    }

    /// <summary>Keeps a page, in place of the page kept under its key, if any.</summary>
    /// <param name="page">The page.</param>
    /// <param name="request">The request that loaded it.</param>
    /// <param name="cancellationToken">Cancelled when the run stops.</param>
    protected virtual async ValueTask WriteAsync(CachedPage page, ScrapeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var folder = FolderOf(request.Url);
        Directory.CreateDirectory(folder);

        var name = NameOf(page.Key);
        var bodyFile = name + PageFiles.ExtensionOf(page.ContentType, page.IsText);
        // the body first: a description without its body is no page
        await PageFiles.WriteAsync(System.IO.Path.Combine(folder, bodyFile), page.Body, cancellationToken).ConfigureAwait(false);
        var meta = PageFiles.Json(writer =>
        {
            writer.WriteString("format", Format);
            writer.WriteNumber("version", Version);
            writer.WriteString("key", page.Key);
            writer.WriteString("label", page.Label);
            writer.WriteString("requestUrl", page.RequestUrl);
            writer.WriteNumber("status", page.StatusCode);
            writer.WriteString("url", page.Url);
            writer.WriteString("contentType", page.ContentType);
            writer.WriteBoolean("isText", page.IsText);
            writer.WriteString("savedAt", page.SavedAt);
            writer.WriteString("body", bodyFile);
            writer.WriteNumber("length", page.Body.Length);
            PageFiles.WriteHeaders(writer, page.Headers);
        });
        await PageFiles.WriteAsync(System.IO.Path.Combine(folder, name + PageFiles.MetaSuffix), meta, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forgets the page kept under the key. Returns false when there was none.</summary>
    /// <param name="key">Key of the request (<see cref="KeyOf"/>).</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancelled when the run stops.</param>
    protected virtual async ValueTask<bool> DeleteAsync(string key, ScrapeRequest request, CancellationToken cancellationToken)
    {
        var folder = FolderOf(request.Url);
        var metaPath = System.IO.Path.Combine(folder, NameOf(key) + PageFiles.MetaSuffix);
        var meta = await PageFiles.ReadAsync(metaPath, cancellationToken).ConfigureAwait(false);
        if (meta is null) return false;

        // the description first: a body without its description is not found
        File.Delete(metaPath);
        string? bodyFile = null;
        try
        {
            bodyFile = Describe(meta)?.BodyFile;
        }
        catch (JsonException)
        {
        }
        if (bodyFile != null) File.Delete(System.IO.Path.Combine(folder, bodyFile));
        return true;
    }

    /// <summary>
    /// How many pages are kept now, for <see cref="MaxPages"/>: called once, before the first page is kept with a limit set.
    /// The folder counts its descriptions.
    /// </summary>
    protected virtual long CountStored()
    {
        var root = Path;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;
        return Directory.EnumerateFiles(root, "*" + PageFiles.MetaSuffix, SearchOption.AllDirectories).LongCount();
    }

    private string FolderOf(string url)
    {
        var root = Path is { Length: > 0 } path ? path : throw new InvalidOperationException("Set CachingPageLoader.Path: the folder of the kept pages");
        var uri = new Uri(url);
        return System.IO.Path.Combine(root, PageFiles.SafeName(uri.IsDefaultPort ? uri.Host : $"{uri.Host}_{uri.Port}"));
    }

    /// <summary>File name of a key: a hash, so any key makes a short valid name.</summary>
    private static string NameOf(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

    private sealed record Description(CachedPage Page, string BodyFile);

    /// <summary>Reads a description written by <see cref="WriteAsync"/>; null when it is not one.</summary>
    private static Description? Describe(byte[] meta)
    {
        using var document = JsonDocument.Parse(meta);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || StringOf(root, "format") != Format
            || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != Version
            || StringOf(root, "key") is not { } key
            || StringOf(root, "url") is not { } url
            || StringOf(root, "body") is not { } bodyFile
            || bodyFile != System.IO.Path.GetFileName(bodyFile)        // a file next to the description, nothing else
            || !root.TryGetProperty("status", out var status) || !status.TryGetInt32(out var statusCode)
            || !root.TryGetProperty("savedAt", out var saved) || !saved.TryGetDateTimeOffset(out var savedAt))
        {
            return null;
        }

        var isText = root.TryGetProperty("isText", out var text) && text.ValueKind == JsonValueKind.True;
        var page = new CachedPage(key, StringOf(root, "label") ?? ScrapeRequest.DefaultLabel, StringOf(root, "requestUrl") ?? url, statusCode,
            url, StringOf(root, "contentType"), PageFiles.ReadHeaders(root), [], isText, savedAt);
        return new Description(page, bodyFile);
    }

    private static string? StringOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
