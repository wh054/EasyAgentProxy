using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AppProxyHelper;

internal enum WinDivertLayer
{
    Network = 0,
    NetworkForward = 1,
    Flow = 2,
    Socket = 3,
    Reflect = 4
}

internal enum WinDivertEvent
{
    NetworkPacket = 0,
    FlowEstablished = 1,
    FlowDeleted = 2,
    SocketBind = 3,
    SocketConnect = 4,
    SocketListen = 5,
    SocketAccept = 6,
    SocketClose = 7,
    ReflectOpen = 8,
    ReflectClose = 9
}

internal enum WinDivertParam
{
    QueueLength = 0,
    QueueTime = 1,
    QueueSize = 2
}

internal static class WinDivertFlags
{
    public const ulong Sniff = 0x0001;
    public const ulong RecvOnly = 0x0004;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WinDivertAddress
{
    private const int SniffedBit = 16;
    private const int OutboundBit = 17;
    private const int LoopbackBit = 18;
    private const int ImpostorBit = 19;
    private const int IPv6Bit = 20;

    public long Timestamp;
    public uint Bits;
    public uint Reserved2;
    public WinDivertAddressData Data;

    public WinDivertLayer Layer => (WinDivertLayer)(Bits & 0xFF);
    public WinDivertEvent Event => (WinDivertEvent)((Bits >> 8) & 0xFF);
    public bool Sniffed => GetBit(SniffedBit);
    public bool Outbound => GetBit(OutboundBit);
    public bool Loopback => GetBit(LoopbackBit);
    public bool Impostor => GetBit(ImpostorBit);
    public bool IPv6 => GetBit(IPv6Bit);

    public void SetOutbound(bool value) => SetBit(OutboundBit, value);

    public void SetLoopback(bool value) => SetBit(LoopbackBit, value);

    public void SetImpostor(bool value) => SetBit(ImpostorBit, value);

    private bool GetBit(int bit) => (Bits & (1u << bit)) != 0;

    private void SetBit(int bit, bool value)
    {
        var mask = 1u << bit;
        Bits = value ? Bits | mask : Bits & ~mask;
    }
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct WinDivertAddressData
{
    [FieldOffset(0)]
    public WinDivertNetworkData Network;

    [FieldOffset(0)]
    public WinDivertFlowData Flow;

    [FieldOffset(0)]
    public WinDivertSocketData Socket;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WinDivertNetworkData
{
    public uint IfIdx;
    public uint SubIfIdx;
}

[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct WinDivertFlowData
{
    public ulong EndpointId;
    public ulong ParentEndpointId;
    public uint ProcessId;
    public uint LocalAddr0;
    public uint LocalAddr1;
    public uint LocalAddr2;
    public uint LocalAddr3;
    public uint RemoteAddr0;
    public uint RemoteAddr1;
    public uint RemoteAddr2;
    public uint RemoteAddr3;
    public ushort LocalPort;
    public ushort RemotePort;
    public byte Protocol;
}

[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct WinDivertSocketData
{
    public ulong EndpointId;
    public ulong ParentEndpointId;
    public uint ProcessId;
    public uint LocalAddr0;
    public uint LocalAddr1;
    public uint LocalAddr2;
    public uint LocalAddr3;
    public uint RemoteAddr0;
    public uint RemoteAddr1;
    public uint RemoteAddr2;
    public uint RemoteAddr3;
    public ushort LocalPort;
    public ushort RemotePort;
    public byte Protocol;
}

internal sealed class WinDivertNative : IDisposable
{
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private readonly IntPtr _library;
    private readonly WinDivertOpenDelegate _open;
    private readonly WinDivertRecvDelegate _recv;
    private readonly WinDivertSendDelegate _send;
    private readonly WinDivertShutdownDelegate _shutdown;
    private readonly WinDivertCloseDelegate _close;
    private readonly WinDivertSetParamDelegate _setParam;
    private readonly WinDivertHelperCalcChecksumsDelegate _calcChecksums;
    private bool _disposed;

    public WinDivertNative(string dllPath)
    {
        _library = NativeLibrary.Load(dllPath);
        _open = Load<WinDivertOpenDelegate>("WinDivertOpen");
        _recv = Load<WinDivertRecvDelegate>("WinDivertRecv");
        _send = Load<WinDivertSendDelegate>("WinDivertSend");
        _shutdown = Load<WinDivertShutdownDelegate>("WinDivertShutdown");
        _close = Load<WinDivertCloseDelegate>("WinDivertClose");
        _setParam = Load<WinDivertSetParamDelegate>("WinDivertSetParam");
        _calcChecksums = Load<WinDivertHelperCalcChecksumsDelegate>("WinDivertHelperCalcChecksums");
    }

    public WinDivertHandle Open(string filter, WinDivertLayer layer, short priority, ulong flags)
    {
        var handle = _open(filter, layer, priority, flags);
        if (handle == IntPtr.Zero || handle == InvalidHandleValue)
        {
            throw CreateWin32Exception($"WinDivertOpen 失败: layer={layer}, filter={filter}");
        }

        return new WinDivertHandle(this, handle);
    }

    public void SetParam(WinDivertHandle handle, WinDivertParam param, ulong value)
    {
        if (!_setParam(handle.DangerousHandle, param, value))
        {
            throw CreateWin32Exception($"WinDivertSetParam 失败: {param}={value}");
        }
    }

    public bool Receive(WinDivertHandle handle, IntPtr packet, uint packetLen, out uint recvLen, ref WinDivertAddress address)
    {
        return _recv(handle.DangerousHandle, packet, packetLen, out recvLen, ref address);
    }

    public bool Send(WinDivertHandle handle, IntPtr packet, uint packetLen, out uint sendLen, ref WinDivertAddress address)
    {
        return _send(handle.DangerousHandle, packet, packetLen, out sendLen, ref address);
    }

    public bool CalcChecksums(IntPtr packet, uint packetLen, ref WinDivertAddress address)
    {
        return _calcChecksums(packet, packetLen, ref address, 0);
    }

    public void Shutdown(WinDivertHandle handle)
    {
        _shutdown(handle.DangerousHandle, 0x3);
    }

    public void Close(IntPtr handle)
    {
        _close(handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        NativeLibrary.Free(_library);
        _disposed = true;
    }

    public static Exception CreateWin32Exception(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{message}。Win32Error={error}: {ExplainError(error)}");
    }

    public static string ExplainError(int error)
    {
        return error switch
        {
            2 => "找不到 WinDivert32.sys/WinDivert64.sys 或相关文件，请确认它们和 WinDivert.dll 在同一目录。",
            5 => "权限不足，请用管理员权限运行。",
            87 => "WinDivert 参数或过滤表达式无效。",
            577 => "驱动签名无效或被系统策略拒绝。",
            654 => "系统中已有不兼容版本的 WinDivert 驱动。",
            1060 => "WinDivert 服务不存在，且当前模式未安装驱动。",
            1275 => "驱动被系统或安全软件阻止。",
            1753 => "Base Filtering Engine 服务不可用。",
            _ => new Win32Exception(error).Message
        };
    }

    private T Load<T>(string name)
        where T : Delegate
    {
        var address = NativeLibrary.GetExport(_library, name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
    private delegate IntPtr WinDivertOpenDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string filter,
        WinDivertLayer layer,
        short priority,
        ulong flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool WinDivertRecvDelegate(
        IntPtr handle,
        IntPtr packet,
        uint packetLen,
        out uint recvLen,
        ref WinDivertAddress address);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool WinDivertSendDelegate(
        IntPtr handle,
        IntPtr packet,
        uint packetLen,
        out uint sendLen,
        ref WinDivertAddress address);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool WinDivertShutdownDelegate(IntPtr handle, uint how);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool WinDivertCloseDelegate(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool WinDivertSetParamDelegate(IntPtr handle, WinDivertParam param, ulong value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool WinDivertHelperCalcChecksumsDelegate(
        IntPtr packet,
        uint packetLen,
        ref WinDivertAddress address,
        ulong flags);
}

internal sealed class WinDivertHandle : IDisposable
{
    private readonly WinDivertNative _native;
    private bool _disposed;

    public WinDivertHandle(WinDivertNative native, IntPtr handle)
    {
        _native = native;
        DangerousHandle = handle;
    }

    public IntPtr DangerousHandle { get; }

    public void Shutdown()
    {
        if (!_disposed)
        {
            _native.Shutdown(this);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _native.Close(DangerousHandle);
        _disposed = true;
    }
}
