using System.Net;

namespace AppProxyHelper;

internal enum PacketRewriteKind
{
    None,
    Modified,
    Drop
}

internal sealed class WinDivertPacketRewriter
{
    private readonly TransparentConnectionTable _connections;
    private readonly TransparentUdpProxyServer? _udpProxy;
    private readonly AppLogger _logger;
    private readonly bool _blockQuicUdp443;
    private readonly IPAddress _redirectAddress;
    private readonly ushort _redirectPort;
    private int _blockedUdp443Count;

    public WinDivertPacketRewriter(
        TransparentConnectionTable connections,
        IPAddress redirectAddress,
        int redirectPort,
        TransparentUdpProxyServer? udpProxy,
        bool blockQuicUdp443,
        AppLogger logger)
    {
        if (redirectPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("transparent.redirectListenPort 必须在 1-65535 范围内。");
        }

        _connections = connections;
        _udpProxy = udpProxy;
        _blockQuicUdp443 = blockQuicUdp443;
        _logger = logger;
        _redirectAddress = redirectAddress;
        _redirectPort = (ushort)redirectPort;
    }

    public PacketRewriteKind Rewrite(byte[] buffer, int length, ref WinDivertAddress address)
    {
        if (IPv4TcpPacket.TryParse(buffer, length, out var tcpPacket))
        {
            if (tcpPacket.SourcePort == _redirectPort)
            {
                return RestoreTcpProxyResponse(tcpPacket, ref address);
            }

            return address.Outbound
                ? RedirectTcpTargetRequest(tcpPacket, ref address)
                : PacketRewriteKind.None;
        }

        if (IPv4UdpPacket.TryParse(buffer, length, out var udpPacket))
        {
            if (_connections.IsUdpRelayPort(udpPacket.SourcePort))
            {
                return RestoreUdpProxyResponse(udpPacket, ref address);
            }

            return address.Outbound
                ? RedirectUdpTargetRequest(udpPacket, ref address)
                : PacketRewriteKind.None;
        }

        return PacketRewriteKind.None;
    }

    private PacketRewriteKind RedirectTcpTargetRequest(IPv4TcpPacket packet, ref WinDivertAddress address)
    {
        if (ShouldBypassDestination(packet.DestinationAddress, packet.DestinationPort))
        {
            return PacketRewriteKind.None;
        }

        if (packet.DestinationPort == _redirectPort && packet.DestinationAddress.Equals(_redirectAddress))
        {
            return PacketRewriteKind.None;
        }

        if (!_connections.IsTracked(
                IpProtocols.Tcp,
                packet.SourcePort,
                packet.DestinationAddress,
                packet.DestinationPort))
        {
            return PacketRewriteKind.None;
        }

        var originalLocalAddress = packet.SourceAddress;
        var originalLocalPort = packet.SourcePort;
        var originalRemoteAddress = packet.DestinationAddress;
        var originalRemotePort = packet.DestinationPort;

        var connection = new OriginalConnection(
            IpProtocols.Tcp,
            originalLocalAddress,
            originalLocalPort,
            originalRemoteAddress,
            originalRemotePort,
            address.Data.Network.IfIdx,
            address.Data.Network.SubIfIdx,
            DateTimeOffset.Now);

        _connections.RegisterConnection(connection);
        packet.SetSource(originalRemoteAddress, originalLocalPort);
        packet.SetDestination(GetRedirectAddress(originalLocalAddress), _redirectPort);
        address.SetOutbound(false);
        address.SetLoopback(false);
        address.SetImpostor(false);
        return PacketRewriteKind.Modified;
    }

    private PacketRewriteKind RestoreTcpProxyResponse(IPv4TcpPacket packet, ref WinDivertAddress address)
    {
        if (packet.SourcePort != _redirectPort)
        {
            return PacketRewriteKind.None;
        }

        if (!_connections.TryGetConnection(IpProtocols.Tcp, packet.DestinationPort, out var connection))
        {
            return PacketRewriteKind.None;
        }

        packet.SetSource(connection.RemoteAddress, connection.RemotePort);
        packet.SetDestination(connection.LocalAddress, connection.LocalPort);

        address.SetOutbound(false);
        address.SetLoopback(false);
        address.SetImpostor(false);
        address.Data.Network.IfIdx = connection.IfIdx;
        address.Data.Network.SubIfIdx = connection.SubIfIdx;
        return PacketRewriteKind.Modified;
    }

    private PacketRewriteKind RedirectUdpTargetRequest(IPv4UdpPacket packet, ref WinDivertAddress address)
    {
        if (ShouldBypassDestination(packet.DestinationAddress, packet.DestinationPort))
        {
            return PacketRewriteKind.None;
        }

        if (packet.DestinationAddress.Equals(_redirectAddress)
            && _connections.IsUdpRelayPort(packet.DestinationPort))
        {
            return PacketRewriteKind.None;
        }

        if (!_connections.IsTracked(
                IpProtocols.Udp,
                packet.SourcePort,
                packet.DestinationAddress,
                packet.DestinationPort))
        {
            return PacketRewriteKind.None;
        }

        if (_blockQuicUdp443 && packet.DestinationPort == 443)
        {
            LogBlockedUdp443(packet);
            return PacketRewriteKind.Drop;
        }

        if (_udpProxy is null)
        {
            return PacketRewriteKind.None;
        }

        var originalLocalAddress = packet.SourceAddress;
        var originalLocalPort = packet.SourcePort;
        var originalRemoteAddress = packet.DestinationAddress;
        var originalRemotePort = packet.DestinationPort;

        var connection = new OriginalConnection(
            IpProtocols.Udp,
            originalLocalAddress,
            originalLocalPort,
            originalRemoteAddress,
            originalRemotePort,
            address.Data.Network.IfIdx,
            address.Data.Network.SubIfIdx,
            DateTimeOffset.Now);

        if (!_udpProxy.TryGetOrCreateRelayPort(connection, out var relayPort))
        {
            return PacketRewriteKind.None;
        }

        packet.SetSource(originalRemoteAddress, originalLocalPort);
        packet.SetDestination(GetRedirectAddress(originalLocalAddress), relayPort);
        address.SetOutbound(false);
        address.SetLoopback(false);
        address.SetImpostor(false);
        return PacketRewriteKind.Modified;
    }

    private void LogBlockedUdp443(IPv4UdpPacket packet)
    {
        var count = Interlocked.Increment(ref _blockedUdp443Count);
        if (count <= 10 || count % 100 == 0)
        {
            _logger.Info(
                $"已阻断目标 UDP/443 以强制回落 TCP: " +
                $"{packet.SourceAddress}:{packet.SourcePort} -> {packet.DestinationAddress}:{packet.DestinationPort}, count={count}");
        }
    }

    private PacketRewriteKind RestoreUdpProxyResponse(IPv4UdpPacket packet, ref WinDivertAddress address)
    {
        if (!_connections.TryGetUdpRelayConnection(packet.SourcePort, packet.DestinationPort, out var connection))
        {
            return PacketRewriteKind.None;
        }

        packet.SetSource(connection.RemoteAddress, connection.RemotePort);
        packet.SetDestination(connection.LocalAddress, connection.LocalPort);

        address.SetOutbound(false);
        address.SetLoopback(false);
        address.SetImpostor(false);
        address.Data.Network.IfIdx = connection.IfIdx;
        address.Data.Network.SubIfIdx = connection.SubIfIdx;
        return PacketRewriteKind.Modified;
    }

    private IPAddress GetRedirectAddress(IPAddress originalLocalAddress)
    {
        return _redirectAddress.Equals(IPAddress.Any) ? originalLocalAddress : _redirectAddress;
    }

    private static bool ShouldBypassDestination(IPAddress address, ushort port)
    {
        return IPAddress.IsLoopback(address);
    }
}
