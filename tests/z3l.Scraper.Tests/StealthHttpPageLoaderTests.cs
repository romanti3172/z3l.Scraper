using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>StealthHttpPageLoader: HTTP/2 and the headers of the browser on the wire; ParentUrl of the requests.</summary>
[TestClass]
public sealed class StealthHttpPageLoaderTests
{
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Headers_GoOnTheWire_InTheBrowsersOrder()
    {
        await using var server = new RawServer();
        var options = new StealthHttpPageLoaderOptions { DefaultHeaders = { ["Accept-Language"] = "de-DE,de;q=0.9", ["X-Own"] = "1" } };
        using var loader = new StealthHttpPageLoader(options);
        var request = new ScrapeRequest(server.Url("/p/1"), PageType.Item) { ParentUrl = server.Url("/catalog?page=2") };

        var response = await loader.LoadAsync(request, CancellationToken.None);

        Assert.AreEqual(200, response.StatusCode);
        var lines = server.Heads.Single().Split("\r\n");
        Assert.AreEqual("GET /p/1 HTTP/1.1", lines[0], "an http:// site is asked over HTTP/1.1");
        var headers = lines.Skip(1).Select(l => (Name: l[..l.IndexOf(':', StringComparison.Ordinal)], Value: l[(l.IndexOf(':', StringComparison.Ordinal) + 2)..])).ToArray();
        CollectionAssert.AreEqual(new[]
        {
            "Host", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform", "Upgrade-Insecure-Requests", "User-Agent", "Accept",
            "Sec-Fetch-Site", "Sec-Fetch-Mode", "Sec-Fetch-User", "Sec-Fetch-Dest", "Referer", "Accept-Encoding", "Accept-Language", "Priority", "X-Own",
        }, headers.Select(h => h.Name).ToArray());
        var values = headers.ToDictionary(h => h.Name, h => h.Value);
        Assert.AreEqual(BrowserHeaders.Brands(142, "Google Chrome"), values["sec-ch-ua"]);
        Assert.AreEqual(options.UserAgent, values["User-Agent"]);
        Assert.AreEqual(server.Url("/catalog?page=2"), values["Referer"]);
        Assert.AreEqual("same-origin", values["Sec-Fetch-Site"]);
        Assert.AreEqual("gzip, deflate, br", values["Accept-Encoding"]);
        Assert.AreEqual("de-DE,de;q=0.9", values["Accept-Language"], "a default header replaces the browser's in its place");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task UserAgentOfTheRequest_IsTheBrowserToLookLike()
    {
        await using var server = new RawServer();
        using var loader = new StealthHttpPageLoader();
        const string firefox = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0";
        var request = new ScrapeRequest(server.Url("/")) { Headers = new Dictionary<string, string> { ["User-Agent"] = firefox } };

        await loader.LoadAsync(request, CancellationToken.None);

        var head = server.Heads.Single();
        StringAssert.Contains(head, "User-Agent: " + firefox);
        Assert.IsFalse(head.Contains("sec-ch-ua", StringComparison.Ordinal), "Firefox sends no client hints");
        StringAssert.Contains(head, "Sec-Fetch-Site: none");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Http2_IsAskedFor_WhereTheServerHasIt()
    {
        using var certificate = SelfSigned();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(certificate);
        }));
        await using var app = builder.Build();
        app.MapGet("/", (HttpContext http) => Results.Text($"<html><body>{http.Request.Protocol}</body></html>", "text/html"));
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";

        using var stealth = new TrustingStealthLoader();
        var page = await stealth.LoadAsync(new ScrapeRequest(url), CancellationToken.None);

        using var trusting = new HttpClient(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } });
        using var plain = new HttpPageLoader(trusting);
        var plainPage = await plain.LoadAsync(new ScrapeRequest(url), CancellationToken.None);

        StringAssert.Contains(page.Text, "HTTP/2");
        StringAssert.Contains(plainPage.Text, "HTTP/1.1", "HttpPageLoader stays on HTTP/1.1");
        await app.StopAsync();
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ParentUrl_IsThePageTheLinkWasFoundOn_AfterItsRedirects()
    {
        var loader = new DelegateLoader((r, _) =>
            Task.FromResult(PageResponse.FromText(200, r.Url.EndsWith("/list", StringComparison.Ordinal) ? r.Url + "?moved" : r.Url, ScraperTests.Html("x"))));
        var parents = new ConcurrentDictionary<string, string?>();
        var scraper = new Scraper(loader, ScraperTests.FastOptions()).Route("default", ctx =>
        {
            parents[ctx.Request.Url] = ctx.Request.ParentUrl;
            if (ctx.Request.Depth == 0) ctx.Enqueue("item");
        });

        await scraper.RunToListAsync("https://s.test/list");

        Assert.IsNull(parents["https://s.test/list"], "a start request has no parent");
        Assert.AreEqual("https://s.test/list?moved", parents["https://s.test/item"]);
    }

    /// <summary>The test's own certificate is taken, as a derived loader may change the handler.</summary>
    private sealed class TrustingStealthLoader : StealthHttpPageLoader
    {
        protected override HttpMessageHandler CreateHandler(IWebProxy? proxy)
        {
            var handler = (SocketsHttpHandler)base.CreateHandler(proxy);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            return handler;
        }
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));     // server authentication
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // through PFX, so that SslStream on Windows can use the key too
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A server that keeps the raw head of every request and answers a small page, one request per connection.</summary>
    private sealed class RawServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;

        public RawServer()
        {
            _listener.Start();
            _accepting = Task.Run(AcceptAsync);
        }

        public ConcurrentQueue<string> Heads { get; } = new();

        public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}";

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                var head = new StringBuilder();
                while (true)
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0) return;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    var end = head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end < 0) continue;
                    Heads.Enqueue(head.ToString(0, end));
                    break;
                }
                const string body = "<html><body>ok</body></html>";
                var answer = $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(answer));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accepting;
            _stop.Dispose();
        }
    }
}
