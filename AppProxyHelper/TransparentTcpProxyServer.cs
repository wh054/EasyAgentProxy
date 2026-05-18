using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace AppProxyHelper;

internal sealed class TransparentTcpProxyServer : IAsyncDisposable
{
    private const int TlsFirstClientDataTimeoutMs = 30000;
    private const int TlsSniContinuationTimeoutMs = 500;
    private const int MaxTlsClientHelloBytes = 16 * 1024;
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ClientHalfCloseGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ClientHalfClosePostResponseIdle = TimeSpan.FromMilliseconds(750);

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
                var clientStream = client.GetStream();
                var initialClientData = await ReadInitialClientDataAsync(clientStream, original, cancellationToken);
                if (ShouldCloseIdleTlsProbe(original, initialClientData))
                {
                    _logger.Debug(
                        $"透明 TLS 连接未收到客户端首包，已关闭空闲预连接: " +
                        $"localPort={localPort}, original={original.RemoteAddress}:{original.RemotePort}");
                    return;
                }

                var connectTarget = GetProxyConnectTarget(original, initialClientData.SniHost);

                _logger.Info(
                    $"透明代理连接: pid-port={localPort}, original={original.RemoteAddress}:{original.RemotePort}, " +
                    $"target={connectTarget.Display}, proxy={_proxy.Scheme}://{_proxy.Host}:{_proxy.Port}");

                upstreamStream = await ConnectThroughProxyAsync(upstream, connectTarget, cancellationToken);
                await TunnelAsync(
                    client,
                    clientStream,
                    upstreamStream,
                    upstream,
                    original,
                    initialClientData.Bytes,
                    cancellationToken);
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
        ProxyConnectTarget target,
        CancellationToken cancellationToken)
    {
        var stream = await ConnectToProxyEndpointAsync(upstream, cancellationToken);
        await EstablishProxyTunnelAsync(stream, target, cancellationToken);
        return stream;
    }

    private async Task<Stream> ConnectToProxyEndpointAsync(
        TcpClient upstream,
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
            await EstablishSocks5HandshakeAsync(stream, cancellationToken);
            return stream;
        }

        if (_proxy.IsHttp)
        {
            return stream;
        }

        throw new InvalidOperationException($"不支持的代理协议: {_proxy.Scheme}");
    }

    private async Task EstablishProxyTunnelAsync(
        Stream stream,
        ProxyConnectTarget target,
        CancellationToken cancellationToken)
    {
        if (_proxy.IsSocks)
        {
            await EstablishSocks5ConnectAsync(stream, target, cancellationToken);
            return;
        }

        if (_proxy.IsHttp)
        {
            await EstablishHttpConnectAsync(stream, target, cancellationToken);
            return;
        }

        throw new InvalidOperationException($"不支持的代理协议: {_proxy.Scheme}");
    }

    private async Task EstablishSocks5HandshakeAsync(
        Stream stream,
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
    }

    private async Task EstablishSocks5ConnectAsync(
        Stream stream,
        ProxyConnectTarget target,
        CancellationToken cancellationToken)
    {
        var request = BuildSocks5ConnectRequest(target);
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

    private static byte[] BuildSocks5ConnectRequest(ProxyConnectTarget target)
    {
        byte[] addressBytes;
        byte addressType;
        var domainLengthField = 0;

        if (target.Address is { } address)
        {
            addressBytes = address.GetAddressBytes();
            addressType = addressBytes.Length switch
            {
                4 => (byte)0x01,
                16 => (byte)0x04,
                _ => throw new InvalidOperationException($"SOCKS5 不支持的地址长度: {addressBytes.Length}")
            };
        }
        else
        {
            addressBytes = Encoding.ASCII.GetBytes(target.Host);
            if (addressBytes.Length == 0 || addressBytes.Length > 255)
            {
                throw new InvalidOperationException("SOCKS5 domain length must be 1-255 bytes.");
            }

            addressType = 0x03;
            domainLengthField = 1;
        }

        var request = new byte[6 + domainLengthField + addressBytes.Length];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = addressType;
        var addressOffset = 4;
        if (domainLengthField == 1)
        {
            request[addressOffset++] = (byte)addressBytes.Length;
        }

        Buffer.BlockCopy(addressBytes, 0, request, addressOffset, addressBytes.Length);
        request[^2] = (byte)(target.Port >> 8);
        request[^1] = (byte)(target.Port & 0xFF);
        return request;
    }

    private async Task EstablishHttpConnectAsync(
        Stream stream,
        ProxyConnectTarget target,
        CancellationToken cancellationToken)
    {
        var connectTarget = $"{FormatHost(target)}:{target.Port}";
        var builder = new StringBuilder();
        builder.Append($"CONNECT {connectTarget} HTTP/1.1\r\n");
        builder.Append($"Host: {connectTarget}\r\n");
        builder.Append("Proxy-Connection: keep-alive\r\n");
        builder.Append("User-Agent: EasyProxy/1.0\r\n");

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
        TcpClient clientTcp,
        Stream clientStream,
        Stream upstreamStream,
        TcpClient upstreamClient,
        OriginalConnection original,
        byte[] initialUpload,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        long initialUploadBytes = 0;
        if (initialUpload.Length > 0)
        {
            await upstreamStream.WriteAsync(initialUpload, cancellationToken);
            initialUploadBytes = initialUpload.Length;
        }

        var proxyToClientProgress = new CopyProgress();
        using var tunnelCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clientToProxy = CopyMeteredAsync("client->proxy", clientStream, upstreamStream, tunnelCts.Token);
        var proxyToClient = CopyMeteredAsync(
            "proxy->client",
            upstreamStream,
            clientStream,
            tunnelCts.Token,
            proxyToClientProgress);
        var firstTask = await Task.WhenAny(clientToProxy, proxyToClient);
        var first = await firstTask;

        CopyResult second;
        if (ReferenceEquals(firstTask, clientToProxy))
        {
            second = await WaitForCopyAfterClientHalfCloseAsync(
                proxyToClient,
                proxyToClientProgress,
                upstreamClient,
                tunnelCts);
        }
        else
        {
            ShutdownSend(clientTcp);
            second = await clientToProxy;
        }

        CloseSocket(upstreamClient);
        CloseSocket(clientTcp);

        var c2p = initialUploadBytes
            + (first.Direction == "client->proxy" ? first.Bytes : second.Bytes);
        var p2c = first.Direction == "proxy->client" ? first.Bytes : second.Bytes;
        var elapsed = DateTimeOffset.Now - started;
        _logger.Info(
            $"透明代理连接结束: localPort={original.LocalPort}, original={original.RemoteAddress}:{original.RemotePort}, " +
            $"upload={c2p}, download={p2c}, first={first.Direction}/{first.EndReason}, " +
            $"second={second.Direction}/{second.EndReason}, elapsedMs={(long)elapsed.TotalMilliseconds}");
    }

    private static async Task<CopyResult> CopyMeteredAsync(
        string direction,
        Stream source,
        Stream destination,
        CancellationToken cancellationToken,
        CopyProgress? progress = null)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return new CopyResult(direction, total, "eof");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
                progress?.Report(total);
            }
        }
        catch (OperationCanceledException)
        {
            return new CopyResult(direction, total, "cancelled");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            return new CopyResult(direction, total, ex.GetType().Name);
        }
    }

    private static async Task<CopyResult> WaitForCopyAfterClientHalfCloseAsync(
        Task<CopyResult> proxyToClient,
        CopyProgress progress,
        TcpClient upstreamClient,
        CancellationTokenSource tunnelCts)
    {
        var startedAt = Environment.TickCount64;
        var endReason = "client-half-close-timeout";

        while (!proxyToClient.IsCompleted)
        {
            await Task.Delay(100);
            if (proxyToClient.IsCompleted)
            {
                break;
            }

            if (progress.TotalBytes > 0
                && Environment.TickCount64 - progress.LastProgressTick >= ClientHalfClosePostResponseIdle.TotalMilliseconds)
            {
                endReason = "client-half-close-response-idle";
                break;
            }

            if (Environment.TickCount64 - startedAt >= ClientHalfCloseGrace.TotalMilliseconds)
            {
                break;
            }
        }

        if (proxyToClient.IsCompleted)
        {
            return await proxyToClient;
        }

        tunnelCts.Cancel();
        CloseSocket(upstreamClient);
        var result = await proxyToClient;
        return result with { EndReason = $"{endReason}/{result.EndReason}" };
    }

    private static void ShutdownSend(TcpClient client)
    {
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
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

    private async Task<ClientInitialData> ReadInitialClientDataAsync(
        Stream clientStream,
        OriginalConnection original,
        CancellationToken cancellationToken)
    {
        if (!_config.EnableDomainSniffing
            || original.RemotePort != 443)
        {
            return ClientInitialData.Empty;
        }

        var buffer = new byte[MaxTlsClientHelloBytes];
        var used = 0;
        string? sniHost = null;
        var parseResult = TlsSniParseResult.NeedMoreData;

        while (used < buffer.Length)
        {
            int read;
            try
            {
                read = await ReadTlsProbeBytesAsync(
                    clientStream,
                    buffer.AsMemory(used, buffer.Length - used),
                    used == 0 ? TlsFirstClientDataTimeoutMs : TlsSniContinuationTimeoutMs,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            used += read;
            parseResult = TryReadTlsSni(buffer.AsSpan(0, used), out sniHost);
            if (parseResult != TlsSniParseResult.NeedMoreData)
            {
                break;
            }
        }

        if (used == 0)
        {
            _logger.Debug(
                $"透明 SNI 嗅探无首包: localPort={original.LocalPort}, original={original.RemoteAddress}:{original.RemotePort}, " +
                $"timeoutMs={TlsFirstClientDataTimeoutMs}");
            return ClientInitialData.Empty;
        }

        var normalizedSni = NormalizeSniHost(sniHost);
        if (normalizedSni is null)
        {
            _logger.Debug(
                $"透明 SNI 嗅探未得到域名: localPort={original.LocalPort}, original={original.RemoteAddress}:{original.RemotePort}, " +
                $"bytes={used}, result={parseResult}");
        }
        else
        {
            _logger.Debug(
                $"透明 SNI 嗅探命中: localPort={original.LocalPort}, original={original.RemoteAddress}:{original.RemotePort}, " +
                $"host={normalizedSni}, bytes={used}");
        }

        return new ClientInitialData(buffer[..used], normalizedSni);
    }

    private static async Task<int> ReadTlsProbeBytesAsync(
        Stream clientStream,
        Memory<byte> buffer,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        return await clientStream.ReadAsync(buffer, timeoutCts.Token);
    }

    private bool ShouldCloseIdleTlsProbe(OriginalConnection original, ClientInitialData initialClientData)
    {
        return ShouldProbeTls(original)
            && initialClientData.Bytes.Length == 0;
    }

    private bool ShouldProbeTls(OriginalConnection original)
    {
        return _config.EnableDomainSniffing
            && original.RemotePort == 443;
    }

    private static ProxyConnectTarget GetProxyConnectTarget(OriginalConnection original, string? sniHost)
    {
        return string.IsNullOrWhiteSpace(sniHost)
            ? ProxyConnectTarget.FromAddress(original.RemoteAddress, original.RemotePort)
            : ProxyConnectTarget.FromHost(sniHost, original.RemotePort);
    }

    private static TlsSniParseResult TryReadTlsSni(ReadOnlySpan<byte> data, out string? sniHost)
    {
        sniHost = null;
        if (data.Length < 5)
        {
            return TlsSniParseResult.NeedMoreData;
        }

        if (data[0] != 0x16)
        {
            return TlsSniParseResult.NotTls;
        }

        var recordLength = ReadUInt16(data, 3);
        if (recordLength <= 0)
        {
            return TlsSniParseResult.NoSni;
        }

        var recordEnd = 5 + recordLength;
        if (data.Length < recordEnd)
        {
            return recordEnd > MaxTlsClientHelloBytes
                ? TlsSniParseResult.NoSni
                : TlsSniParseResult.NeedMoreData;
        }

        var offset = 5;
        if (data[offset] != 0x01)
        {
            return TlsSniParseResult.NotTls;
        }

        if (recordEnd - offset < 4)
        {
            return TlsSniParseResult.NeedMoreData;
        }

        var handshakeLength = ReadUInt24(data, offset + 1);
        offset += 4;
        var handshakeEnd = offset + handshakeLength;
        if (handshakeEnd > recordEnd)
        {
            return data.Length < handshakeEnd && handshakeEnd <= MaxTlsClientHelloBytes
                ? TlsSniParseResult.NeedMoreData
                : TlsSniParseResult.NoSni;
        }

        if (!TrySkip(data, handshakeEnd, ref offset, 2 + 32))
        {
            return TlsSniParseResult.NoSni;
        }

        if (!TryReadLengthPrefixed(data, handshakeEnd, ref offset, 1, out _)
            || !TryReadLengthPrefixed(data, handshakeEnd, ref offset, 2, out _)
            || !TryReadLengthPrefixed(data, handshakeEnd, ref offset, 1, out _))
        {
            return TlsSniParseResult.NoSni;
        }

        if (!TryReadLengthPrefixed(data, handshakeEnd, ref offset, 2, out var extensions))
        {
            return TlsSniParseResult.NoSni;
        }

        var extensionOffset = 0;
        while (extensions.Length - extensionOffset >= 4)
        {
            var extensionType = ReadUInt16(extensions, extensionOffset);
            var extensionLength = ReadUInt16(extensions, extensionOffset + 2);
            extensionOffset += 4;
            if (extensionLength > extensions.Length - extensionOffset)
            {
                return TlsSniParseResult.NoSni;
            }

            var extensionData = extensions.Slice(extensionOffset, extensionLength);
            if (extensionType == 0x0000 && TryReadSniExtension(extensionData, out sniHost))
            {
                return TlsSniParseResult.Found;
            }

            extensionOffset += extensionLength;
        }

        return TlsSniParseResult.NoSni;
    }

    private static bool TryReadSniExtension(ReadOnlySpan<byte> extensionData, out string? sniHost)
    {
        sniHost = null;
        if (extensionData.Length < 2)
        {
            return false;
        }

        var listLength = ReadUInt16(extensionData, 0);
        if (listLength > extensionData.Length - 2)
        {
            return false;
        }

        var offset = 2;
        var end = 2 + listLength;
        while (end - offset >= 3)
        {
            var nameType = extensionData[offset++];
            var nameLength = ReadUInt16(extensionData, offset);
            offset += 2;
            if (nameLength > end - offset)
            {
                return false;
            }

            if (nameType == 0x00)
            {
                sniHost = Encoding.ASCII.GetString(extensionData.Slice(offset, nameLength));
                return true;
            }

            offset += nameLength;
        }

        return false;
    }

    private static bool TrySkip(ReadOnlySpan<byte> data, int end, ref int offset, int count)
    {
        if (count < 0 || offset > end - count)
        {
            return false;
        }

        offset += count;
        return true;
    }

    private static bool TryReadLengthPrefixed(
        ReadOnlySpan<byte> data,
        int end,
        ref int offset,
        int lengthBytes,
        out ReadOnlySpan<byte> value)
    {
        value = default;
        if (lengthBytes is < 1 or > 2 || offset > end - lengthBytes)
        {
            return false;
        }

        var length = lengthBytes == 1
            ? data[offset]
            : ReadUInt16(data, offset);
        offset += lengthBytes;
        if (length > end - offset)
        {
            return false;
        }

        value = data.Slice(offset, length);
        offset += length;
        return true;
    }

    private static int ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        return (data[offset] << 8) | data[offset + 1];
    }

    private static int ReadUInt24(ReadOnlySpan<byte> data, int offset)
    {
        return (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
    }

    private static string? NormalizeSniHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length is 0 or > 253 || IPAddress.TryParse(host, out _))
        {
            return null;
        }

        var previousWasDot = true;
        foreach (var ch in host)
        {
            var valid = ch is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '.';
            if (!valid)
            {
                return null;
            }

            if (ch == '.')
            {
                if (previousWasDot)
                {
                    return null;
                }

                previousWasDot = true;
            }
            else
            {
                previousWasDot = false;
            }
        }

        return previousWasDot ? null : host;
    }

    private static string FormatHost(ProxyConnectTarget target)
    {
        return target.Address is { } address
            ? FormatHost(address)
            : target.Host;
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

    private readonly record struct ClientInitialData(byte[] Bytes, string? SniHost)
    {
        public static ClientInitialData Empty { get; } = new(Array.Empty<byte>(), null);
    }

    private readonly record struct ProxyConnectTarget(string Host, ushort Port, IPAddress? Address)
    {
        public string Display => $"{Host}:{Port}";

        public static ProxyConnectTarget FromAddress(IPAddress address, ushort port)
        {
            return new ProxyConnectTarget(address.ToString(), port, address);
        }

        public static ProxyConnectTarget FromHost(string host, ushort port)
        {
            return new ProxyConnectTarget(host, port, null);
        }
    }

    private readonly record struct CopyResult(string Direction, long Bytes, string EndReason);

    private sealed class CopyProgress
    {
        private long _lastProgressTick = Environment.TickCount64;
        private long _totalBytes;

        public long LastProgressTick => Interlocked.Read(ref _lastProgressTick);

        public long TotalBytes => Interlocked.Read(ref _totalBytes);

        public void Report(long totalBytes)
        {
            Interlocked.Exchange(ref _totalBytes, totalBytes);
            Interlocked.Exchange(ref _lastProgressTick, Environment.TickCount64);
        }
    }

    private enum TlsSniParseResult
    {
        Found,
        NeedMoreData,
        NotTls,
        NoSni
    }
}
