using System.Net;

namespace AppProxyHelper;

internal readonly struct IPv4TcpPacket
{
    private const int MinIPv4HeaderLength = 20;
    private const int MinTcpHeaderLength = 20;

    private readonly byte[] _buffer;
    private readonly int _tcpOffset;

    private IPv4TcpPacket(byte[] buffer, int length, int ipHeaderLength)
    {
        _buffer = buffer;
        Length = length;
        IpHeaderLength = ipHeaderLength;
        _tcpOffset = ipHeaderLength;
    }

    public int Length { get; }
    public int IpHeaderLength { get; }
    public IPAddress SourceAddress => new(_buffer.AsSpan(12, 4));
    public IPAddress DestinationAddress => new(_buffer.AsSpan(16, 4));
    public ushort SourcePort => ReadUInt16(_tcpOffset);
    public ushort DestinationPort => ReadUInt16(_tcpOffset + 2);
    public bool IsSyn => (_buffer[_tcpOffset + 13] & 0x02) != 0;
    public bool IsAck => (_buffer[_tcpOffset + 13] & 0x10) != 0;
    public bool IsFinOrRst => (_buffer[_tcpOffset + 13] & 0x05) != 0;
    public bool IsInitialSyn => IsSyn && !IsAck;

    public static bool TryParse(byte[] buffer, int length, out IPv4TcpPacket packet)
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

        if (length < ipHeaderLength + MinTcpHeaderLength)
        {
            return false;
        }

        if (buffer[9] != IpProtocols.Tcp)
        {
            return false;
        }

        var fragment = ReadUInt16(buffer, 6);
        if ((fragment & 0x3FFF) != 0)
        {
            return false;
        }

        var tcpHeaderLength = (buffer[ipHeaderLength + 12] >> 4) * 4;
        if (tcpHeaderLength < MinTcpHeaderLength || length < ipHeaderLength + tcpHeaderLength)
        {
            return false;
        }

        packet = new IPv4TcpPacket(buffer, length, ipHeaderLength);
        return true;
    }

    public void SetSource(IPAddress address, ushort port)
    {
        WriteAddress(12, address);
        WriteUInt16(_tcpOffset, port);
    }

    public void SetDestination(IPAddress address, ushort port)
    {
        WriteAddress(16, address);
        WriteUInt16(_tcpOffset + 2, port);
    }

    private ushort ReadUInt16(int offset) => ReadUInt16(_buffer, offset);

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
