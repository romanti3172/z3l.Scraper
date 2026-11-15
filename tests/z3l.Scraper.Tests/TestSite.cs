using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace z3l.Scraper.Tests;

/// <summary>
/// A small shop on Kestrel with the troubles real sites have: a server error, a cut-off page, 429 with Retry-After,
/// a Cloudflare-like 503, a missing page, windows-1251, a redirect, a slow page, gzip, a soft block, a JSON API.
/// </summary>
internal sealed class TestSite : IAsyncDisposable
{
    static TestSite() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _hits = new();

    private TestSite(WebApplication app) => _app = app;

    public string BaseUrl { get; private set; } = "";
    public string Url(string path) => BaseUrl + path;
    public int Hits(string path) => _hits.TryGetValue(path, out var hits) ? hits.Count : 0;
    public DateTime[] HitTimes(string path) => _hits.TryGetValue(path, out var hits) ? [.. hits] : [];

    public static async Task<TestSite> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var site = new TestSite(app);

        app.Use(async (http, next) =>
        {
            site._hits.GetOrAdd(http.Request.Path + http.Request.QueryString, _ => new()).Enqueue(DateTime.UtcNow);
            await next(http);
        });

        app.MapGet("/catalog", (int page) => HtmlResult(page switch
        {
            // relative links of every kind, plus links that must be skipped
            1 => Page("""
                 <a href="/p/1">1</a> <a href="/p/2">2</a> <a href="p/3">3</a> <a href="../p/4">4</a>
                 <a href="#top">top</a> <a href="mailto:shop@s.test">mail</a> <a href="javascript:void(0)">js</a>
                 <a class="next" href="?page=2">next</a>
                 """),
            2 => Page("""
                 <a href="/p/5">5</a> <a href="/p/6">6</a> <a href="/p/7">7</a> <a href="/p/8">8</a> <a href="/p/4">4 again</a>
                 <a class="next" href="?page=3">next</a>
                 """),
            _ => Page("""<a href="/p/9">9</a> <a href="/p/10">10</a> <a href="/p/11">11</a> <a href="/p/12">12</a>"""),
        }));

        app.MapGet("/p/{id:int}", async (int id, HttpContext http) =>
        {
            var first = site.Hits($"/p/{id}") == 1;
            switch (id)
            {
                case 3 when first:
                    return HtmlResult("<html><body>Internal error</body></html>", 500);
                case 4 when first:
                    return HtmlResult("<html><body><h1>Product 4</h1><span cla");                    // the connection broke
                case 5 when first:
                    http.Response.Headers.RetryAfter = "1";
                    return HtmlResult("<html><body>Too many requests</body></html>", 429);
                case 6 when first:
                    return HtmlResult("<html><head><title>Just a moment...</title></head><body></body></html>", 503);
                case 7:
                    return HtmlResult("<html><body>No such product</body></html>", 404);
                case 8:
                    return Results.Bytes(Encoding.GetEncoding("windows-1251").GetBytes(Product(8, "Чай", "99,00")), "text/html; charset=windows-1251");
                case 9:
                    return Results.Redirect("/p/9/new", permanent: true);
                case 10 when first:
                    await Task.Delay(TimeSpan.FromSeconds(5), http.RequestAborted);                    // the scraper times out first
                    break;
                case 11:
                    http.Response.Headers.ContentEncoding = "gzip";
                    return Results.Bytes(Gzip(Product(11, "Product 11", "11.00")), "text/html; charset=utf-8");
                case 12 when first:
                    return HtmlResult("<html><body><h1>Product 12</h1>Prices are shown to people only</body></html>");
            }
            return HtmlResult(Product(id, $"Product {id}", $"{id}.00"));
        });

        app.MapGet("/p/9/new", () => HtmlResult(Product(9, "Product 9", "9.00")));

        app.MapPost("/api/stock", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            var id = int.Parse(form["id"].ToString());
            return Results.Content($$"""{"id":{{id}},"stock":{{id * 10}}}""", "application/json");
        });

        // loader checks
        app.MapMethods("/echo", ["GET", "POST"], async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var text = $"ua={request.Headers.UserAgent}|x={request.Headers["X-Test"]}|ct={request.ContentType}|lang={request.Headers.AcceptLanguage}|body={body}";
            return Results.Content($"<html><body>{text}</body></html>", "text/html");
        });
        app.MapGet("/big", () => Results.Bytes(new byte[2 * 1024 * 1024], "application/octet-stream"));
        app.MapGet("/cookie/set", (HttpResponse response) =>
        {
            response.Cookies.Append("session", "abc123");
            return HtmlResult(Page("set"));
        });
        app.MapGet("/cookie/get", (HttpRequest request) => HtmlResult(Page($"session={request.Cookies["session"]}")));
        app.MapGet("/latin1", () => Results.Bytes([(byte)'1', (byte)'0', 0x80], "text/html; charset=iso-8859-1"));

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        site.BaseUrl = address.TrimEnd('/');
        return site;
    }

    private static string Page(string body) => $"<html><head><meta charset=\"utf-8\"></head><body>{body}</body></html>";

    private static string Product(int id, string name, string price) =>
        $"<html><head><meta charset=\"utf-8\"></head><body><div class=\"product\" data-id=\"{id}\"><h1>{name}</h1><span class=\"price\">{price}</span></div></body></html>";

    private static IResult HtmlResult(string html, int status = 200) => Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }
        return output.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
