using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Playwright.Tests;

[TestClass]
public sealed class Socks5RelayTests
{
    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task LogsInUpstream_SendsTheHostName_CopiesBytesBothWays()
    {
        await using var echo = TcpEcho.Start();
        await using var upstream = FakeSocks5.Start("user", "päss", echo.Port);
        await using var relay = new Socks5Relay(new ProxyAddress("socks5", "127.0.0.1", upstream.Port, "user", "päss"));

        using var client = await ConnectThroughAsync(relay.Port, "shop.test", 443);
        var stream = client.GetStream();
        var reply = await ReadAsync(stream, 10);
        Assert.AreEqual(0, reply[1], "succeeded");

        await stream.WriteAsync("hello"u8.ToArray());
        CollectionAssert.AreEqual("hello"u8.ToArray(), await ReadAsync(stream, 5));
        Assert.AreEqual("shop.test:443", upstream.Targets.Single());
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task AWrongPassword_TheBrowserGetsNotAllowed()
    {
        await using var upstream = FakeSocks5.Start("user", "right", 1);
        await using var relay = new Socks5Relay(new ProxyAddress("socks5", "127.0.0.1", upstream.Port, "user", "wrong"));

        using var client = await ConnectThroughAsync(relay.Port, "shop.test", 80);
        var reply = await ReadAsync(client.GetStream(), 10);

        Assert.AreEqual(2, reply[1], "connection not allowed");
        Assert.AreEqual(0, upstream.Targets.Count);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task AProxyThatIsDown_TheBrowserGetsHostUnreachable()
    {
        var port = FreePort();
        await using var relay = new Socks5Relay(new ProxyAddress("socks5", "127.0.0.1", port, "user", "pass"));

        using var client = await ConnectThroughAsync(relay.Port, "shop.test", 80);
        var reply = await ReadAsync(client.GetStream(), 10);

        Assert.AreEqual(4, reply[1]);
    }

    [TestMethod]
    public void OnlySocks5WithALoginNeedsTheRelay()
    {
        Assert.IsTrue(PlaywrightPageLoader.NeedsRelay(new ProxyAddress("socks5", "h", 1, "u", "p")));
        Assert.IsFalse(PlaywrightPageLoader.NeedsRelay(new ProxyAddress("socks5", "h", 1)));
        Assert.IsFalse(PlaywrightPageLoader.NeedsRelay(new ProxyAddress("http", "h", 1, "u", "p")));
        Assert.ThrowsExactly<ArgumentException>(() => new Socks5Relay(new ProxyAddress("http", "h", 1, "u", "p")));
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task DisposeClosesOpenConnections()
    {
        await using var echo = TcpEcho.Start();
        await using var upstream = FakeSocks5.Start("user", "pass", echo.Port);
        var relay = new Socks5Relay(new ProxyAddress("socks5", "127.0.0.1", upstream.Port, "user", "pass"));
        using var client = await ConnectThroughAsync(relay.Port, "shop.test", 80);
        await ReadAsync(client.GetStream(), 10);

        await relay.DisposeAsync();

        int read;
        try
        {
            read = await client.GetStream().ReadAsync(new byte[1]);
        }
        catch (IOException)
        {
            read = 0;                                   // closed with a reset
        }
        Assert.AreEqual(0, read, "the relay closed the connection");
        await Assert.ThrowsExactlyAsync<SocketException>(async () =>
        {
            using var again = new TcpClient();
            await again.ConnectAsync(IPAddress.Loopback, relay.Port);
        });
    }

    /// <summary>Greeting without a login and a CONNECT to a host name, as Chromium sends them.</summary>
    internal static async Task<TcpClient> ConnectThroughAsync(int relayPort, string host, int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relayPort);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        var choice = await ReadAsync(stream, 2);
        CollectionAssert.AreEqual(new byte[] { 5, 0 }, choice);
        var name = Encoding.ASCII.GetBytes(host);
        await stream.WriteAsync((byte[])[5, 1, 0, 3, (byte)name.Length, .. name, (byte)(port >> 8), (byte)port]);
        return client;
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>A SOCKS5 server that wants a login and sends every CONNECT to one local port, whatever the target.</summary>
internal sealed class FakeSocks5 : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _user, _password;
    private readonly int _targetPort;

    private FakeSocks5(string user, string password, int targetPort)
    {
        (_user, _password, _targetPort) = (user, password, targetPort);
    }

    public int Port { get; private set; }
    public ConcurrentQueue<string> Targets { get; } = new();

    public static FakeSocks5 Start(string user, string password, int targetPort)
    {
        var server = new FakeSocks5(user, password, targetPort);
        server._listener.Start();
        server.Port = ((IPEndPoint)server._listener.LocalEndpoint).Port;
        _ = server.AcceptAsync();
        return server;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = ServeAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var s = client.GetStream();
            var head = await Read(s, 2);
            var methods = await Read(s, head[1]);
            if (Array.IndexOf(methods, (byte)2) < 0)
            {
                await s.WriteAsync(new byte[] { 5, 0xFF });
                return;
            }
            await s.WriteAsync(new byte[] { 5, 2 });

            var ver = await Read(s, 2);
            var user = Encoding.UTF8.GetString(await Read(s, ver[1]));
            var password = Encoding.UTF8.GetString(await Read(s, (await Read(s, 1))[0]));
            var ok = user == _user && password == _password;
            await s.WriteAsync(new byte[] { 1, (byte)(ok ? 0 : 1) });
            if (!ok) return;

            var request = await Read(s, 4);
            var host = request[3] switch
            {
                3 => Encoding.ASCII.GetString(await Read(s, (await Read(s, 1))[0])),
                1 => new IPAddress(await Read(s, 4)).ToString(),
                _ => new IPAddress(await Read(s, 16)).ToString(),
            };
            var portBytes = await Read(s, 2);
            Targets.Enqueue($"{host}:{(portBytes[0] << 8) | portBytes[1]}");

            using var target = new TcpClient();
            await target.ConnectAsync(IPAddress.Loopback, _targetPort);
            await s.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 });
            var t = target.GetStream();
            await Task.WhenAny(s.CopyToAsync(t), t.CopyToAsync(s));
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
        {
        }
    }

    private static async Task<byte[]> Read(Stream s, int count)
    {
        var buffer = new byte[count];
        await s.ReadExactlyAsync(buffer);
        return buffer;
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A TCP server that sends back what it gets.</summary>
internal sealed class TcpEcho : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public int Port { get; private set; }

    public static TcpEcho Start()
    {
        var echo = new TcpEcho();
        echo._listener.Start();
        echo.Port = ((IPEndPoint)echo._listener.LocalEndpoint).Port;
        _ = echo.AcceptAsync();
        return echo;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using var c = client;
                    try { await c.GetStream().CopyToAsync(c.GetStream()); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                });
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
