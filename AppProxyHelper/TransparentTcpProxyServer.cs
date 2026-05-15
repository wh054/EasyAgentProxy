using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace AppProxyHelper;

internal sealed class TransparentTcpProxyServer : IAsyncDisposable
{
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(3);

    private readonly IPAddress _listenAddress;
    private readonly int _listenPort;
    private readonly ProxyEndpoint _proxy;
    private readonly TransparentConnectionTable _connections;
    private readonly TransparentInterceptionConfig _config;
    private readonly AppLogger _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly List<Task> _connectionTasks = new();
    private readonly List<TcpClient> _trackedClients = new();
    private readonly object _sync = new();
    private TcpListener? _listener;
    private Task? _acceptTask;

    public TransparentTcpProxyServer(
        IPAddress listenAddress,
        int listenPort,
        ProxyEndpoint proxy,
        TransparentConnectionTable connections,
        TransparentInterceptionConfig config,
        AppLogger logger)
    {
        _listenAddress = listenAddress;
        _listenPort = listenPort;
        _proxy = proxy;
        _connections = connections;
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(_listenAddress, _listenPort);
        _listener.Start(_config.ListenBacklog);
        _acceptTask = AcceptLoopAsync(_disposeCts.Token);

        _logger.Info($"透明 TCP 转发器已监听: {_listenAddress}:{_listenPort}");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        _listener?.Stop();
        CloseTrackedClients();

        var allStopped = true;
        if (_acceptTask is not null)
        {
            allStopped &= await WaitExpectedAsync(_acceptTask, "TCP 监听任务");
        }

        Task[] tasks;
        lock (_sync)
        {
            tasks = _connectionTasks.ToArray();
        }

        if (tasks.Length > 0)
        {
            allStopped &= await WaitExpectedAsync(Task.WhenAll(tasks), "TCP 转发连接");
        }

        if (allStopped)
        {
            _disposeCts.Dispose();
        }
        else
        {
            _logger.Warn("透明 TCP 转发器停止超时，已释放监听资源并返回界面控制权。");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug($"透明 TCP 转发器停止监听: {ex.SocketErrorCode}");
                return;
            }

            var task = HandleClientAsync(client, cancellationToken);
            lock (_sync)
            {
                _connectionTasks.Add(task);
            }

            _ = task.ContinueWith(
                completed =>
                {
                    lock (_sync)
                    {
                        _connectionTasks.Remove(completed);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        TrackClient(client);
        try
        {
            using var clientOwner = client;
            var remoteEndpoint = client.Client.RemoteEndPoint as IPEndPoint;
            if (remoteEndpoint is null)
            {
                _logger.Warn("透明 TCP 转发器收到未知远端端点连接，已关闭。");
                return;
            }

            var localPort = (ushort)remoteEndpoint.Port;
            if (!_connections.TryGetConnection(IpProtocols.Tcp, localPort, out var original)
                && !_connections.WaitForConnection(IpProtocols.Tcp, localPort, _config.ProxyLookupTimeoutMs))
            {
                _logger.Warn($"未找到透明连接映射，已关闭连接: localPort={localPort}");
                return;
            }

            if (!_connections.TryGetConnection(IpProtocols.Tcp, localPort, out original))
            {
                _logger.Warn($"透明连接映射尚未写入，已关闭连接: localPort={localPort}");
                return;
            }

            using var upstream = new TcpClient();
            TrackClient(upstream);
            upstream.NoDelay = true;
            client.NoDelay = true;
            Stream? upstreamStream = null;
            var hasOriginal = true;

            try
            {
                _logger.Info(
                    $"透明代理连接: pid-port={localPort}, original={original.RemoteAddress}:{original.RemotePort}, " +
                    $"proxy={_proxy.Scheme}://{_proxy.Host}:{_proxy.Port}");

                upstreamStream = await ConnectThroughProxyAsync(upstream, original, cancellationToken);
                await TunnelAsync(client.GetStream(), upstreamStream, upstream, original, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
            {
                _logger.Error(
                    $"透明代理连接失败: localPort={localPort}, original={original.RemoteAddress}:{original.RemotePort}",
                    ex);
            }
            finally
            {
                upstreamStream?.Dispose();
                UntrackClient(upstream);
                if (hasOriginal)
                {
                    _connections.ReleaseConnection(original);
                }
            }
        }
        finally
        {
            UntrackClient(client);
        }
    }

    private async Task<Stream> ConnectThroughProxyAsync(
        TcpClient upstream,
        OriginalConnection original,
        CancellationToken cancellationToken)
    {
        await ConnectWithTimeoutAsync(
            upstream,
            _proxy.Host,
            _proxy.Port,
            TimeSpan.FromMilliseconds(_config.ProxyConnectTimeoutMs),
            cancellationToken);

        Stream stream = upstream.GetStream();
        if (_proxy.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            var sslStream = new SslStream(stream, leaveInnerStreamOpen: true);
            await sslStream.AuthenticateAsClientAsync(_proxy.Host);
            stream = sslStream;
        }

        if (_proxy.IsSocks)
        {
            await EstablishSocks5Async(stream, original, cancellationToken);
            return stream;
        }

        if (_proxy.IsHttp)
        {
            await EstablishHttpConnectAsync(stream, original, cancellationToken);
            return stream;
        }

        throw new InvalidOperationException($"不支持的代理协议: {_proxy.Scheme}");
    }

    private async Task EstablishSocks5Async(
        Stream stream,
        OriginalConnection original,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_proxy.UserInfo))
        {
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
        }
        else
        {
            await stream.WriteAsync(new byte[] { 0x05, 0x02, 0x00, 0x02 }, cancellationToken);
        }

        var handshake = await ReadExactAsync(stream, 2, cancellationToken);
        if (handshake[0] != 0x05)
        {
            throw new InvalidOperationException($"SOCKS5 响应版本异常: 0x{handshake[0]:X2}");
        }

        if (handshake[1] == 0xFF)
        {
            throw new InvalidOperationException("SOCKS5 代理拒绝所有认证方式。");
        }

        if (handshake[1] == 0x02)
        {
            await AuthenticateSocks5Async(stream, cancellationToken);
        }
        else if (handshake[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 认证方法不支持: 0x{handshake[1]:X2}");
        }

        var request = BuildSocks5ConnectRequest(original.RemoteAddress, original.RemotePort);
        await stream.WriteAsync(request, cancellationToken);

        var response = await ReadExactAsync(stream, 4, cancellationToken);
        if (response[0] != 0x05)
        {
            throw new InvalidOperationException($"SOCKS5 CONNECT 响应版本异常: 0x{response[0]:X2}");
        }

        if (response[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 CONNECT 失败，reply=0x{response[1]:X2}");
        }

        await DrainSocks5BindAddressAsync(stream, response[3], cancellationToken);
    }

    private async Task AuthenticateSocks5Async(Stream stream, CancellationToken cancellationToken)
    {
        var separatorIndex = _proxy.UserInfo.IndexOf(':');
        var username = separatorIndex >= 0 ? _proxy.UserInfo[..separatorIndex] : _proxy.UserInfo;
        var password = separatorIndex >= 0 ? _proxy.UserInfo[(separatorIndex + 1)..] : "";

        var usernameBytes = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(username));
        var passwordBytes = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(password));
        if (usernameBytes.Length > 255 || passwordBytes.Length > 255)
        {
            throw new InvalidOperationException("SOCKS5 用户名和密码最多 255 字节。");
        }

        var request = new byte[3 + usernameBytes.Length + passwordBytes.Length];
        request[0] = 0x01;
        request[1] = (byte)usernameBytes.Length;
        Buffer.BlockCopy(usernameBytes, 0, request, 2, usernameBytes.Length);
        request[2 + usernameBytes.Length] = (byte)passwordBytes.Length;
        Buffer.BlockCopy(passwordBytes, 0, request, 3 + usernameBytes.Length, passwordBytes.Length);

        await stream.WriteAsync(request, cancellationToken);
        var response = await ReadExactAsync(stream, 2, cancellationToken);
        if (response[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 用户名密码认证失败: 0x{response[1]:X2}");
        }
    }

    private static byte[] BuildSocks5ConnectRequest(IPAddress address, ushort port)
    {
        var addressBytes = address.GetAddressBytes();
        var addressType = addressBytes.Length switch
        {
            4 => (byte)0x01,
            16 => (byte)0x04,
            _ => throw new InvalidOperationException($"SOCKS5 不支持的地址长度: {addressBytes.Length}")
        };

        var request = new byte[6 + addressBytes.Length];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = addressType;
        Buffer.BlockCopy(addressBytes, 0, request, 4, addressBytes.Length);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)(port & 0xFF);
        return request;
    }

    private async Task EstablishHttpConnectAsync(
        Stream stream,
        OriginalConnection original,
        CancellationToken cancellationToken)
    {
        var target = $"{FormatHost(original.RemoteAddress)}:{original.RemotePort}";
        var builder = new StringBuilder();
        builder.Append($"CONNECT {target} HTTP/1.1\r\n");
        builder.Append($"Host: {target}\r\n");
        builder.Append("Proxy-Connection: keep-alive\r\n");
        builder.Append("User-Agent: AppProxyHelper/1.0\r\n");

        if (!string.IsNullOrWhiteSpace(_proxy.UserInfo))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(_proxy.UserInfo)));
            builder.Append($"Proxy-Authorization: Basic {auth}\r\n");
        }

        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken);

        var response = await ReadHttpHeaderAsync(stream, cancellationToken);
        var statusLine = response.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
        if (!statusLine.Contains(" 200 ", StringComparison.Ordinal)
            && !statusLine.StartsWith("HTTP/1.1 2", StringComparison.Ordinal)
            && !statusLine.StartsWith("HTTP/1.0 2", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"HTTP CONNECT 响应异常: {statusLine}");
        }
    }

    private async Task TunnelAsync(
        Stream client,
        Stream upstream,
        TcpClient upstreamClient,
        OriginalConnection original,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var clientToProxy = CopyMeteredAsync(client, upstream, cancellationToken);
        var proxyToClient = CopyMeteredAsync(upstream, client, cancellationToken);
        var first = await Task.WhenAny(clientToProxy, proxyToClient);

        try
        {
            await first;
        }
        finally
        {
            CloseSocket(upstreamClient);
        }

        await IgnoreExpectedTunnelEndAsync(clientToProxy);
        await IgnoreExpectedTunnelEndAsync(proxyToClient);

        var c2p = clientToProxy.IsCompletedSuccessfully ? clientToProxy.Result : 0;
        var p2c = proxyToClient.IsCompletedSuccessfully ? proxyToClient.Result : 0;
        var elapsed = DateTimeOffset.Now - started;
        _logger.Info(
            $"透明代理连接结束: original={original.RemoteAddress}:{original.RemotePort}, " +
            $"upload={c2p}, download={p2c}, elapsedMs={(long)elapsed.TotalMilliseconds}");
    }

    private static async Task<long> CopyMeteredAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return total;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }
    }

    private static void CloseSocket(TcpClient client)
    {
        try
        {
            client.Client.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void TrackClient(TcpClient client)
    {
        lock (_sync)
        {
            _trackedClients.Add(client);
        }
    }

    private void UntrackClient(TcpClient client)
    {
        lock (_sync)
        {
            _trackedClients.Remove(client);
        }
    }

    private void CloseTrackedClients()
    {
        TcpClient[] clients;
        lock (_sync)
        {
            clients = _trackedClients.ToArray();
        }

        foreach (var client in clients)
        {
            CloseSocket(client);
            client.Dispose();
        }
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("连接被对端关闭。");
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task DrainSocks5BindAddressAsync(
        Stream stream,
        byte addressType,
        CancellationToken cancellationToken)
    {
        var addressLength = addressType switch
        {
            0x01 => 4,
            0x03 => (await ReadExactAsync(stream, 1, cancellationToken))[0],
            0x04 => 16,
            _ => throw new InvalidOperationException($"SOCKS5 地址类型异常: 0x{addressType:X2}")
        };

        await ReadExactAsync(stream, addressLength + 2, cancellationToken);
    }

    private static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var used = 0;

        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used, buffer.Length - used), cancellationToken);
            if (read == 0)
            {
                break;
            }

            used += read;
            var text = Encoding.ASCII.GetString(buffer, 0, used);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return text;
            }
        }

        throw new IOException("读取 HTTP 代理响应头失败。");
    }

    private static string FormatHost(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
    }

    private static async Task ConnectWithTimeoutAsync(
        TcpClient client,
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await client.ConnectAsync(host, port, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"连接代理超时: {host}:{port}, timeout={timeout.TotalMilliseconds:0}ms");
        }
    }

    private async Task<bool> WaitExpectedAsync(Task task, string name)
    {
        try
        {
            await task.WaitAsync(StopWaitTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            _logger.Warn($"{name}停止超过 {StopWaitTimeout.TotalSeconds:0} 秒，继续释放。");
            ObserveFault(task);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task IgnoreExpectedTunnelEndAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
    }
}
