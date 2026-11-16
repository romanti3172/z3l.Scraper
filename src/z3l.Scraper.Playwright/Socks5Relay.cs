using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Playwright;

/// <summary>
/// A SOCKS5 proxy without a login on 127.0.0.1 that sends every connection on to a SOCKS5 proxy with a login (RFC 1928, RFC 1929).
/// Browsers cannot log in to SOCKS5 proxies; they talk to the relay instead. Only CONNECT is relayed (no UDP, no BIND). The host
/// name goes to the upstream proxy as the browser sent it, so names are resolved there. Bytes after the handshake are copied
/// as they are: TLS passes through untouched.
/// </summary>
internal sealed class Socks5Relay : IAsyncDisposable
{
    private const byte Version = 5;
    private const byte NoAuth = 0x00, UserPassword = 0x02, NoAcceptableMethod = 0xFF;
    private const byte Connect = 0x01;
    private const byte IPv4 = 0x01, DomainName = 0x03, IPv6 = 0x04;
    private const byte GeneralFailure = 0x01, NotAllowed = 0x02, HostUnreachable = 0x04, CommandNotSupported = 0x07, AddressNotSupported = 0x08;

    private readonly ProxyAddress _upstream;
    private readonly byte[] _login;
    private readonly ILogger _logger;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Socket, byte> _open = new();
    private readonly Task _acceptLoop;
    private int _disposed;

    /// <summary>Starts listening on a free port of 127.0.0.1.</summary>
    /// <exception cref="ArgumentException">The proxy is not socks5, has no login, or the login is longer than 255 bytes.</exception>
    public Socks5Relay(ProxyAddress upstream, ILogger? logger = null, TimeSpan? connectTimeout = null)
    {
        if (!string.Equals(upstream.Scheme, "socks5", StringComparison.OrdinalIgnoreCase) || upstream.UserName == null)
        {
            throw new ArgumentException("The relay is for socks5 proxies with a login", nameof(upstream));
        }
        var user = Encoding.UTF8.GetBytes(upstream.UserName);
        var password = Encoding.UTF8.GetBytes(upstream.Password ?? "");
        if (user.Length is 0 or > 255 || password.Length > 255) throw new ArgumentException("SOCKS5 logins and passwords are 1 to 255 bytes", nameof(upstream));

        // RFC 1929: VER=1, ULEN, UNAME, PLEN, PASSWD
        _login = [1, (byte)user.Length, .. user, (byte)password.Length, .. password];
        _upstream = upstream;
        _logger = logger ?? NullLogger.Instance;
        ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(30);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(512);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>The local port: give the browser <c>socks5://127.0.0.1:{Port}</c>.</summary>
    public int Port { get; }

    /// <summary>The proxy address for the browser.</summary>
    public string Server => $"socks5://127.0.0.1:{Port}";

    /// <summary>Time to connect to the upstream proxy and finish its handshake.</summary>
    public TimeSpan ConnectTimeout { get; }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException && _stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "SOCKS5 relay: accept failed");
                continue;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(Socket client)
    {
        Socket? upstream = null;
        client.NoDelay = true;
        _open.TryAdd(client, 0);
        try
        {
            using var clientStream = new NetworkStream(client, ownsSocket: false);
            var request = await ReadClientRequestAsync(clientStream).ConfigureAwait(false);
            if (request == null) return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(ConnectTimeout);
            byte[] reply;
            try
            {
                upstream = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                _open.TryAdd(upstream, 0);
                await upstream.ConnectAsync(_upstream.Host, _upstream.Port, timeout.Token).ConfigureAwait(false);
                reply = await UpstreamHandshakeAsync(upstream, request, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or RelayException)
            {
                var code = ex is RelayException relay ? relay.Reply : HostUnreachable;
                _logger.LogDebug(ex, "SOCKS5 relay: {Proxy} failed", _upstream);
                await SendReplyAsync(clientStream, code).ConfigureAwait(false);
                return;
            }

            await clientStream.WriteAsync(reply).ConfigureAwait(false);
            if (reply[1] != 0) return;                                     // the upstream proxy refused the target: its answer is passed on

            using var upstreamStream = new NetworkStream(upstream, ownsSocket: false);
            var toUpstream = PumpAsync(clientStream, upstream, upstreamStream);
            var toClient = PumpAsync(upstreamStream, client, clientStream);
            await Task.WhenAll(toUpstream, toClient).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // the browser or the proxy went away
        }
        finally
        {
            Close(client);
            if (upstream != null) Close(upstream);
        }
    }

    /// <summary>Greeting and request of the browser; null when it was answered with an error.</summary>
    private static async Task<byte[]?> ReadClientRequestAsync(NetworkStream client)
    {
        var head = new byte[2];
        await client.ReadExactlyAsync(head).ConfigureAwait(false);
        if (head[0] != Version) return null;
        var methods = new byte[head[1]];
        await client.ReadExactlyAsync(methods).ConfigureAwait(false);
        if (Array.IndexOf(methods, NoAuth) < 0)
        {
            await client.WriteAsync(new byte[] { Version, NoAcceptableMethod }).ConfigureAwait(false);
            return null;
        }
        await client.WriteAsync(new byte[] { Version, NoAuth }).ConfigureAwait(false);

        // VER CMD RSV ATYP DST.ADDR DST.PORT
        var fixedPart = new byte[4];
        await client.ReadExactlyAsync(fixedPart).ConfigureAwait(false);
        if (fixedPart[0] != Version) return null;
        var addressLength = fixedPart[3] switch
        {
            IPv4 => 4,
            IPv6 => 16,
            DomainName => await ReadByteAsync(client).ConfigureAwait(false),
            _ => -1,
        };
        if (addressLength < 0)
        {
            await SendReplyAsync(client, AddressNotSupported).ConfigureAwait(false);
            return null;
        }
        var prefix = fixedPart[3] == DomainName ? 1 : 0;
        var request = new byte[4 + prefix + addressLength + 2];
        fixedPart.CopyTo(request, 0);
        if (prefix == 1) request[4] = (byte)addressLength;
        await client.ReadExactlyAsync(request.AsMemory(4 + prefix)).ConfigureAwait(false);

        if (fixedPart[1] != Connect)
        {
            await SendReplyAsync(client, CommandNotSupported).ConfigureAwait(false);
            return null;
        }
        return request;
    }

    /// <summary>Logs in to the upstream proxy, sends the browser's request and returns the proxy's whole reply.</summary>
    private async Task<byte[]> UpstreamHandshakeAsync(Socket upstream, byte[] request, CancellationToken cancellationToken)
    {
        await using var stream = new NetworkStream(upstream, ownsSocket: false);
        await stream.WriteAsync(new byte[] { Version, 1, UserPassword }, cancellationToken).ConfigureAwait(false);
        var choice = new byte[2];
        await stream.ReadExactlyAsync(choice, cancellationToken).ConfigureAwait(false);
        if (choice[0] != Version || choice[1] != UserPassword) throw new RelayException(GeneralFailure, $"the proxy chose auth method {choice[1]}");

        await stream.WriteAsync(_login, cancellationToken).ConfigureAwait(false);
        var status = new byte[2];
        await stream.ReadExactlyAsync(status, cancellationToken).ConfigureAwait(false);
        if (status[1] != 0) throw new RelayException(NotAllowed, "the proxy refused the login");

        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        var head = new byte[4];
        await stream.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
        if (head[0] != Version) throw new RelayException(GeneralFailure, "not a SOCKS5 reply");
        var addressLength = head[3] switch
        {
            IPv4 => 4,
            IPv6 => 16,
            DomainName => 1 + await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => throw new RelayException(GeneralFailure, $"address type {head[3]} in the reply"),
        };
        var reply = new byte[4 + addressLength + 2];
        head.CopyTo(reply, 0);
        if (head[3] == DomainName)
        {
            reply[4] = (byte)(addressLength - 1);
            await stream.ReadExactlyAsync(reply.AsMemory(5), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await stream.ReadExactlyAsync(reply.AsMemory(4), cancellationToken).ConfigureAwait(false);
        }
        return reply;
    }

    private static async Task PumpAsync(NetworkStream from, Socket toSocket, NetworkStream to)
    {
        try
        {
            await from.CopyToAsync(to, 64 * 1024).ConfigureAwait(false);
            toSocket.Shutdown(SocketShutdown.Send);                        // half close: the other way may still be talking
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            try { toSocket.Shutdown(SocketShutdown.Both); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        }
    }

    private static async Task SendReplyAsync(NetworkStream client, byte code)
    {
        try
        {
            await client.WriteAsync(new byte[] { Version, code, 0, IPv4, 0, 0, 0, 0, 0, 0 }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
        }
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, cancellationToken).ConfigureAwait(false);
        return one[0];
    }

    private void Close(Socket socket)
    {
        _open.TryRemove(socket, out _);
        socket.Dispose();
    }

    /// <summary>Stops listening and closes every relayed connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach (var socket in _open.Keys) socket.Dispose();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        _stop.Dispose();
    }

    private sealed class RelayException(byte reply, string message) : Exception(message)
    {
        public byte Reply { get; } = reply;
    }
}
