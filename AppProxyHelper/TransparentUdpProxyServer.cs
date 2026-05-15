using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AppProxyHelper;

internal sealed class TransparentUdpProxyServer : IAsyncDisposable
{
    private readonly IPAddress _listenAddress;
    private readonly ProxyEndpoint _proxy;
    private readonly TransparentConnectionTable _connections;
    private readonly TransparentInterceptionConfig _config;
    private readonly AppLogger _logger;
    private readonly Dictionary<UdpOriginalKey, TransparentUdpRelay> _relays = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _cleanupTask;

    public TransparentUdpProxyServer(
        IPAddress listenAddress,
        ProxyEndpoint proxy,
        TransparentConnectionTable connections,
        TransparentInterceptionConfig config,
        AppLogger logger)
    {
        _listenAddress = listenAddress;
        _proxy = proxy;
        _connections = connections;
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_proxy.IsSocks)
        {
            throw new InvalidOperationException("transparent.captureUdp=true 时，proxyUri 必须使用 socks:// 或 socks5://。");
        }

        _cleanupTask = Task.Run(() => CleanupLoopAsync(_disposeCts.Token), CancellationToken.None);
        _logger.Info("透明 UDP relay 已启用，UDP 将通过 SOCKS5 UDP ASSOCIATE 转发。");
        return Task.CompletedTask;
    }

    public bool TryGetOrCreateRelayPort(OriginalConnection original, out ushort relayPort)
    {
        if (_connections.TryGetUdpRelayPort(original, out relayPort))
        {
            return true;
        }

        lock (_sync)
        {
            if (_connections.TryGetUdpRelayPort(original, out relayPort))
            {
                return true;
            }

            var key = UdpOriginalKey.From(original);
            if (_relays.TryGetValue(key, out var existing))
            {
                relayPort = existing.LocalPort;
                return true;
            }

            try
            {
                var relay = new TransparentUdpRelay(
                    _listenAddress,
                    original,
                    _proxy,
                    _config,
                    _logger);

                _relays[key] = relay;
                _connections.RegisterUdpRelay(original, relay.LocalPort);
                relay.Start();
                relayPort = relay.LocalPort;
                return true;
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                _logger.Error(
                    $"创建透明 UDP relay 失败: local={original.LocalAddress}:{original.LocalPort}, " +
                    $"remote={original.RemoteAddress}:{original.RemotePort}",
                    ex);
                relayPort = 0;
                return false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();

        if (_cleanupTask is not null)
        {
            await IgnoreExpectedAsync(_cleanupTask);
        }

        TransparentUdpRelay[] relays;
        lock (_sync)
        {
            relays = _relays.Values.ToArray();
            _relays.Clear();
        }

        foreach (var relay in relays)
        {
            _connections.ReleaseUdpRelay(relay.Original, relay.LocalPort);
            await relay.DisposeAsync();
        }

        _disposeCts.Dispose();
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.UdpIdleTimeoutMs));
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds / 4, 1000, 10000));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var idle = new List<TransparentUdpRelay>();
            lock (_sync)
            {
                foreach (var pair in _relays.ToArray())
                {
                    if (!pair.Value.IsIdle(now, timeout))
                    {
                        continue;
                    }

                    _relays.Remove(pair.Key);
                    idle.Add(pair.Value);
                }
            }

            foreach (var relay in idle)
            {
                _connections.ReleaseUdpRelay(relay.Original, relay.LocalPort);
                await relay.DisposeAsync();
                _logger.Debug(
                    $"透明 UDP relay 空闲释放: local={relay.Original.LocalAddress}:{relay.Original.LocalPort}, " +
                    $"remote={relay.Original.RemoteAddress}:{relay.Original.RemotePort}, relayPort={relay.LocalPort}");
            }
        }
    }

    private static async Task IgnoreExpectedAsync(Task task)
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
    }
}

internal sealed class TransparentUdpRelay : IAsyncDisposable
{
    private const byte Socks5Version = 0x05;
    private const byte Socks5CommandUdpAssociate = 0x03;
    private const byte Socks5AddressTypeIPv4 = 0x01;
    private const byte Socks5AddressTypeDomain = 0x03;
    private const byte Socks5AddressTypeIPv6 = 0x04;

    private readonly IPAddress _listenAddress;
    private readonly ProxyEndpoint _proxy;
    private readonly TransparentInterceptionConfig _config;
    private readonly AppLogger _logger;
    private readonly UdpClient _localSocket;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _associationLock = new(1, 1);
    private readonly object _clientSync = new();
    private TcpClient? _controlClient;
    private Stream? _controlStream;
    private UdpClient? _proxySocket;
    private IPEndPoint? _socksRelayEndpoint;
    private IPEndPoint? _lastClientEndpoint;
    private Task? _localReceiveTask;
    private Task? _proxyReceiveTask;
    private long _lastActivityMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private bool _disposed;

    public TransparentUdpRelay(
        IPAddress listenAddress,
        OriginalConnection original,
        ProxyEndpoint proxy,
        TransparentInterceptionConfig config,
        AppLogger logger)
    {
        _listenAddress = listenAddress;
        Original = original;
        _proxy = proxy;
        _config = config;
        _logger = logger;
        _localSocket = new UdpClient(new IPEndPoint(_listenAddress, 0));
        LocalPort = (ushort)((IPEndPoint)_localSocket.Client.LocalEndPoint!).Port;
    }

    public OriginalConnection Original { get; }
    public ushort LocalPort { get; }

    public void Start()
    {
        _localReceiveTask = Task.Run(() => LocalReceiveLoopAsync(_disposeCts.Token), CancellationToken.None);
    }

    public bool IsIdle(DateTimeOffset now, TimeSpan timeout)
    {
        var last = DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _lastActivityMs));
        return now - last >= timeout;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        _localSocket.Dispose();
        _proxySocket?.Dispose();
        _controlStream?.Dispose();
        _controlClient?.Dispose();

        await IgnoreExpectedAsync(_localReceiveTask);
        await IgnoreExpectedAsync(_proxyReceiveTask);

        _associationLock.Dispose();
        _disposeCts.Dispose();
    }

    private async Task LocalReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _localSocket.ReceiveAsync(cancellationToken);
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
                _logger.Debug($"透明 UDP relay 停止接收本地包: {ex.SocketErrorCode}");
                return;
            }

            Touch();
            lock (_clientSync)
            {
                _lastClientEndpoint = new IPEndPoint(_listenAddress, received.RemoteEndPoint.Port);
            }

            try
            {
                var relayEndpoint = await EnsureSocksUdpAssociationAsync(cancellationToken);
                var proxySocket = _proxySocket
                    ?? throw new InvalidOperationException("SOCKS5 UDP socket 尚未初始化。");
                var packet = BuildSocks5UdpPacket(Original.RemoteAddress, Original.RemotePort, received.Buffer);
                await proxySocket.SendAsync(packet, packet.Length, relayEndpoint).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException or TimeoutException)
            {
                _logger.Error(
                    $"透明 UDP 转发失败: local={Original.LocalAddress}:{Original.LocalPort}, " +
                    $"remote={Original.RemoteAddress}:{Original.RemotePort}",
                    ex);
                ResetSocksAssociation();
            }
        }
    }

    private async Task ProxyReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_proxySocket is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _proxySocket.ReceiveAsync(cancellationToken);
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
                _logger.Debug($"透明 UDP relay 停止接收 SOCKS5 回包: {ex.SocketErrorCode}");
                return;
            }

            Touch();
            if (!TryParseSocks5UdpPacket(received.Buffer, out var payloadOffset, out var payloadLength))
            {
                _logger.Warn("收到无效 SOCKS5 UDP 回包，已丢弃。");
                continue;
            }

            IPEndPoint? clientEndpoint;
            lock (_clientSync)
            {
                clientEndpoint = _lastClientEndpoint;
            }

            clientEndpoint ??= new IPEndPoint(_listenAddress, Original.LocalPort);
            var payload = received.Buffer.AsSpan(payloadOffset, payloadLength).ToArray();
            try
            {
                await _localSocket.SendAsync(payload, payload.Length, clientEndpoint).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _logger.Debug($"透明 UDP relay 写回目标进程失败: {ex.Message}");
            }
        }
    }

    private async Task<IPEndPoint> EnsureSocksUdpAssociationAsync(CancellationToken cancellationToken)
    {
        if (_socksRelayEndpoint is not null)
        {
            return _socksRelayEndpoint;
        }

        await _associationLock.WaitAsync(cancellationToken);
        try
        {
            if (_socksRelayEndpoint is not null)
            {
                return _socksRelayEndpoint;
            }

            _controlClient = new TcpClient();
            await ConnectWithTimeoutAsync(
                _controlClient,
                _proxy.Host,
                _proxy.Port,
                TimeSpan.FromMilliseconds(_config.ProxyConnectTimeoutMs),
                cancellationToken);

            _controlStream = _controlClient.GetStream();
            await NegotiateSocks5Async(_controlStream, cancellationToken);

            var request = new byte[]
            {
                Socks5Version,
                Socks5CommandUdpAssociate,
                0x00,
                Socks5AddressTypeIPv4,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00
            };

            await _controlStream.WriteAsync(request, cancellationToken);
            var relayEndpoint = await ReadSocks5ReplyEndpointAsync(_controlStream, cancellationToken);
            if (relayEndpoint.Address.Equals(IPAddress.Any) || relayEndpoint.Address.Equals(IPAddress.IPv6Any))
            {
                relayEndpoint = new IPEndPoint(
                    await ResolveProxyAddressAsync(relayEndpoint.Address.AddressFamily, cancellationToken),
                    relayEndpoint.Port);
            }

            _proxySocket = new UdpClient(relayEndpoint.AddressFamily);
            _socksRelayEndpoint = relayEndpoint;
            _proxyReceiveTask = Task.Run(() => ProxyReceiveLoopAsync(_disposeCts.Token), CancellationToken.None);
            _logger.Debug(
                $"SOCKS5 UDP ASSOCIATE 已建立: relay={relayEndpoint}, " +
                $"original={Original.RemoteAddress}:{Original.RemotePort}, localRelayPort={LocalPort}");

            return relayEndpoint;
        }
        finally
        {
            _associationLock.Release();
        }
    }

    private void ResetSocksAssociation()
    {
        _socksRelayEndpoint = null;
        _proxySocket?.Dispose();
        _proxySocket = null;
        _controlStream?.Dispose();
        _controlStream = null;
        _controlClient?.Dispose();
        _controlClient = null;
    }

    private async Task NegotiateSocks5Async(Stream stream, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_proxy.UserInfo))
        {
            await stream.WriteAsync(new byte[] { Socks5Version, 0x01, 0x00 }, cancellationToken);
        }
        else
        {
            await stream.WriteAsync(new byte[] { Socks5Version, 0x02, 0x00, 0x02 }, cancellationToken);
        }

        var handshake = await ReadExactAsync(stream, 2, cancellationToken);
        if (handshake[0] != Socks5Version)
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

    private async Task AuthenticateSocks5Async(Stream stream, CancellationToken cancellationToken)
    {
        var separatorIndex = _proxy.UserInfo.IndexOf(':');
        var username = separatorIndex >= 0 ? _proxy.UserInfo[..separatorIndex] : _proxy.UserInfo;
        var password = separatorIndex >= 0 ? _proxy.UserInfo[(separatorIndex + 1)..] : "";

        var usernameBytes = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(username));
        var passwordBytes = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(password));
        if (usernameBytes.Length > 255 || passwordBytes.Length > 255)
        {
            throw new InvalidOperationException("SOCKS5 用户名和密码最大 255 字节。");
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

    private async Task<IPEndPoint> ReadSocks5ReplyEndpointAsync(Stream stream, CancellationToken cancellationToken)
    {
        var response = await ReadExactAsync(stream, 4, cancellationToken);
        if (response[0] != Socks5Version)
        {
            throw new InvalidOperationException($"SOCKS5 UDP ASSOCIATE 响应版本异常: 0x{response[0]:X2}");
        }

        if (response[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 UDP ASSOCIATE 失败，reply=0x{response[1]:X2}");
        }

        var address = await ReadSocksAddressAsync(stream, response[3], cancellationToken);
        var portBytes = await ReadExactAsync(stream, 2, cancellationToken);
        var port = (portBytes[0] << 8) | portBytes[1];
        return new IPEndPoint(address, port);
    }

    private async Task<IPAddress> ReadSocksAddressAsync(
        Stream stream,
        byte addressType,
        CancellationToken cancellationToken)
    {
        return addressType switch
        {
            Socks5AddressTypeIPv4 => new IPAddress(await ReadExactAsync(stream, 4, cancellationToken)),
            Socks5AddressTypeIPv6 => new IPAddress(await ReadExactAsync(stream, 16, cancellationToken)),
            Socks5AddressTypeDomain => await ResolveDomainReplyAsync(stream, cancellationToken),
            _ => throw new InvalidOperationException($"SOCKS5 地址类型异常: 0x{addressType:X2}")
        };
    }

    private async Task<IPAddress> ResolveDomainReplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = (await ReadExactAsync(stream, 1, cancellationToken))[0];
        var domain = Encoding.ASCII.GetString(await ReadExactAsync(stream, length, cancellationToken));
        var addresses = await Dns.GetHostAddressesAsync(domain, cancellationToken);
        return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"无法解析 SOCKS5 UDP relay 域名: {domain}");
    }

    private async Task<IPAddress> ResolveProxyAddressAsync(
        AddressFamily preferredFamily,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(_proxy.Host, out var address))
        {
            return address;
        }

        var addresses = await Dns.GetHostAddressesAsync(_proxy.Host, cancellationToken);
        return addresses.FirstOrDefault(item => item.AddressFamily == preferredFamily)
            ?? addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"无法解析 SOCKS5 代理地址: {_proxy.Host}");
    }

    private static byte[] BuildSocks5UdpPacket(IPAddress destinationAddress, ushort destinationPort, byte[] payload)
    {
        var addressBytes = destinationAddress.GetAddressBytes();
        var addressType = addressBytes.Length switch
        {
            4 => Socks5AddressTypeIPv4,
            16 => Socks5AddressTypeIPv6,
            _ => throw new InvalidOperationException($"SOCKS5 不支持的地址长度: {addressBytes.Length}")
        };

        var packet = new byte[6 + addressBytes.Length + payload.Length];
        packet[0] = 0x00;
        packet[1] = 0x00;
        packet[2] = 0x00;
        packet[3] = addressType;
        Buffer.BlockCopy(addressBytes, 0, packet, 4, addressBytes.Length);
        var portOffset = 4 + addressBytes.Length;
        packet[portOffset] = (byte)(destinationPort >> 8);
        packet[portOffset + 1] = (byte)(destinationPort & 0xFF);
        Buffer.BlockCopy(payload, 0, packet, portOffset + 2, payload.Length);
        return packet;
    }

    private static bool TryParseSocks5UdpPacket(byte[] packet, out int payloadOffset, out int payloadLength)
    {
        payloadOffset = 0;
        payloadLength = 0;

        if (packet.Length < 10 || packet[0] != 0x00 || packet[1] != 0x00 || packet[2] != 0x00)
        {
            return false;
        }

        var offset = 4;
        var addressLength = packet[3] switch
        {
            Socks5AddressTypeIPv4 => 4,
            Socks5AddressTypeIPv6 => 16,
            Socks5AddressTypeDomain when packet.Length >= 5 => packet[4],
            _ => -1
        };

        if (addressLength < 0)
        {
            return false;
        }

        if (packet[3] == Socks5AddressTypeDomain)
        {
            offset++;
        }

        if (packet.Length < offset + addressLength + 2)
        {
            return false;
        }

        payloadOffset = offset + addressLength + 2;
        payloadLength = packet.Length - payloadOffset;
        return payloadLength >= 0;
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

    private void Touch()
    {
        Interlocked.Exchange(ref _lastActivityMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static async Task IgnoreExpectedAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

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
        catch (SocketException)
        {
        }
    }
}
