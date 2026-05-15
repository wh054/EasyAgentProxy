using System.Net;

namespace AppProxyHelper;

internal readonly struct IPv4UdpPacket
{
    private const int MinIPv4HeaderLength = 20;
    private const int UdpHeaderLength = 8;

    private readonly byte[] _buffer;
    private readonly int _udpOffset;

    private IPv4UdpPacket(byte[] buffer, int length, int ipHeaderLength)
    {
        _buffer = buffer;
        Length = length;
        IpHeaderLength = ipHeaderLength;
        _udpOffset = ipHeaderLength;
    }

    public int Length { get; }
    public int IpHeaderLength { get; }
    public IPAddress SourceAddress => new(_buffer.AsSpan(12, 4));
    public IPAddress DestinationAddress => new(_buffer.AsSpan(16, 4));
    public ushort SourcePort => ReadUInt16(_buffer, _udpOffset);
    public ushort DestinationPort => ReadUInt16(_buffer, _udpOffset + 2);

    public static bool TryParse(byte[] buffer, int length, out IPv4UdpPacket packet)
    {
        packet = default;

        if (length < MinIPv4HeaderLength)
        {
            return false;
        }

        var version = buffer[0] >> 4;
        var ipHeaderLength = (buffer[0] & 0x0F) * 4;
        if (version != 4 || ipHeaderLength < MinIPv4HeaderLength)
        {
            return false;
        }

        if (length < ipHeaderLength + UdpHeaderLength)
        {
            return false;
        }

        if (buffer[9] != IpProtocols.Udp)
        {
            return false;
        }

        var fragment = ReadUInt16(buffer, 6);
        if ((fragment & 0x3FFF) != 0)
        {
            return false;
        }

        var udpLength = ReadUInt16(buffer, ipHeaderLength + 4);
        if (udpLength < UdpHeaderLength || length < ipHeaderLength + udpLength)
        {
            return false;
        }

        packet = new IPv4UdpPacket(buffer, length, ipHeaderLength);
        return true;
    }

    public void SetSource(IPAddress address, ushort port)
    {
        WriteAddress(12, address);
        WriteUInt16(_udpOffset, port);
    }

    public void SetDestination(IPAddress address, ushort port)
    {
        WriteAddress(16, address);
        WriteUInt16(_udpOffset + 2, port);
    }

    private static ushort ReadUInt16(byte[] buffer, int offset)
    {
        return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    }

    private void WriteUInt16(int offset, ushort value)
    {
        _buffer[offset] = (byte)(value >> 8);
        _buffer[offset + 1] = (byte)(value & 0xFF);
    }

    private void WriteAddress(int offset, IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            throw new InvalidOperationException("当前 WinDivert 透明代理实现仅支持 IPv4 地址。");
        }

        Buffer.BlockCopy(bytes, 0, _buffer, offset, 4);
    }
}
