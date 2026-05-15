using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace AppProxyHelper;

internal static class IpProtocols
{
    public const byte Tcp = 6;
    public const byte Udp = 17;
}

internal readonly record struct PortProtocolKey(byte Protocol, ushort LocalPort);

internal readonly record struct TrackedConnectionKey(
    byte Protocol,
    ushort LocalPort,
    IPAddress? RemoteAddress,
    ushort RemotePort)
{
    public bool HasRemoteEndpoint => RemoteAddress is not null && RemotePort != 0;

    public static TrackedConnectionKey Exact(
        byte protocol,
        ushort localPort,
        IPAddress remoteAddress,
        ushort remotePort)
    {
        return new TrackedConnectionKey(protocol, localPort, remoteAddress, remotePort);
    }

    public static TrackedConnectionKey Wildcard(byte protocol, ushort localPort)
    {
        return new TrackedConnectionKey(protocol, localPort, null, 0);
    }

    public static TrackedConnectionKey From(OriginalConnection connection)
    {
        return Exact(
            connection.Protocol,
            connection.LocalPort,
            connection.RemoteAddress,
            connection.RemotePort);
    }
}

internal readonly record struct ClassificationEndpointKey(string Source, ulong EndpointId);

internal readonly record struct UdpOriginalKey(
    IPAddress LocalAddress,
    ushort LocalPort,
    IPAddress RemoteAddress,
    ushort RemotePort)
{
    public static UdpOriginalKey From(OriginalConnection connection)
    {
        return new UdpOriginalKey(
            connection.LocalAddress,
            connection.LocalPort,
            connection.RemoteAddress,
            connection.RemotePort);
    }
}

internal readonly record struct UdpRelayKey(ushort RelayPort, ushort LocalPort);

internal sealed record OriginalConnection(
    byte Protocol,
    IPAddress LocalAddress,
    ushort LocalPort,
    IPAddress RemoteAddress,
    ushort RemotePort,
    uint IfIdx,
    uint SubIfIdx,
    DateTimeOffset StartedAt);

internal sealed class TransparentConnectionTable
{
    private static readonly TimeSpan TargetProcessCacheTtl = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<PortProtocolKey, OriginalConnection> _connections = new();
    private readonly ConcurrentDictionary<UdpOriginalKey, ushort> _udpRelayPorts = new();
    private readonly ConcurrentDictionary<UdpRelayKey, OriginalConnection> _udpRelayConnections = new();
    private readonly ConcurrentDictionary<ushort, byte> _udpRelayPortSet = new();
    private readonly Dictionary<TrackedConnectionKey, int> _trackedConnectionCounts = new();
    private readonly Dictionary<ClassificationEndpointKey, TrackedConnectionKey> _endpointToTrackedConnection = new();
    private readonly object _sync = new();
    private readonly AppLogger _logger;
    private readonly bool _trackChildProcesses;
    private readonly HashSet<string> _excludedChildProcessNames;
    private readonly Dictionary<uint, bool> _targetProcessCache = new();
    private readonly HashSet<uint> _loggedChildProcesses = new();
    private readonly HashSet<uint> _loggedExcludedChildProcesses = new();
    private int? _targetProcessId;
    private string? _targetPath;
    private DateTimeOffset _targetProcessCacheExpiresAt;

    public TransparentConnectionTable(
        AppLogger logger,
        bool trackChildProcesses,
        IEnumerable<string> excludedChildProcessNames)
    {
        _logger = logger;
        _trackChildProcesses = trackChildProcesses;
        _excludedChildProcessNames = excludedChildProcessNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void SetTargetProcess(int processId, string executablePath)
    {
        _targetProcessId = processId;
        _targetPath = executablePath;
        lock (_sync)
        {
            _targetProcessCache.Clear();
            _loggedChildProcesses.Clear();
            _loggedExcludedChildProcesses.Clear();
            _targetProcessCacheExpiresAt = DateTimeOffset.MinValue;
        }

        var childText = _trackChildProcesses ? "启用" : "关闭";
        _logger.Info($"透明拦截目标进程: pid={processId}, path={executablePath}, 子进程跟踪={childText}");
    }

    public bool IsTracked(byte protocol, ushort localPort, IPAddress remoteAddress, ushort remotePort)
    {
        var exactKey = TrackedConnectionKey.Exact(protocol, localPort, remoteAddress, remotePort);
        var wildcardKey = TrackedConnectionKey.Wildcard(protocol, localPort);
        var reversedAddress = ReverseIPv4Address(remoteAddress);
        var reversedKey = reversedAddress is null
            ? exactKey
            : TrackedConnectionKey.Exact(protocol, localPort, reversedAddress, remotePort);
        lock (_sync)
        {
            return _trackedConnectionCounts.ContainsKey(exactKey)
                || _trackedConnectionCounts.ContainsKey(reversedKey)
                || _trackedConnectionCounts.ContainsKey(wildcardKey);
        }
    }

    public bool WaitForConnection(byte protocol, ushort localPort, int timeoutMs)
    {
        if (TryGetConnection(protocol, localPort, out _))
        {
            return true;
        }

        if (timeoutMs <= 0)
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        lock (_sync)
        {
            while (!_connections.ContainsKey(new PortProtocolKey(protocol, localPort)))
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                Monitor.Wait(_sync, remaining);
            }
        }

        return true;
    }

    public OriginalConnection RegisterConnection(OriginalConnection connection)
    {
        var key = new PortProtocolKey(connection.Protocol, connection.LocalPort);
        var created = false;
        var value = _connections.GetOrAdd(
            key,
            _ =>
            {
                created = true;
                return connection;
            });

        if (created)
        {
            _logger.Info(
                $"透明连接映射: {connection.LocalAddress}:{connection.LocalPort} -> " +
                $"{connection.RemoteAddress}:{connection.RemotePort}");
        }

        lock (_sync)
        {
            Monitor.PulseAll(_sync);
        }

        return value;
    }

    public bool TryGetConnection(byte protocol, ushort localPort, out OriginalConnection connection)
    {
        return _connections.TryGetValue(new PortProtocolKey(protocol, localPort), out connection!);
    }

    public void ReleaseConnection(OriginalConnection connection)
    {
        _connections.TryRemove(new PortProtocolKey(connection.Protocol, connection.LocalPort), out _);
        RemoveTrackedConnection(TrackedConnectionKey.From(connection));
        RemoveTrackedConnection(TrackedConnectionKey.Wildcard(connection.Protocol, connection.LocalPort));
    }

    public void RegisterUdpRelay(OriginalConnection connection, ushort relayPort)
    {
        var originalKey = UdpOriginalKey.From(connection);
        var relayKey = new UdpRelayKey(relayPort, connection.LocalPort);
        _udpRelayPorts[originalKey] = relayPort;
        _udpRelayConnections[relayKey] = connection;
        _udpRelayPortSet[relayPort] = 0;

        _logger.Info(
            $"透明 UDP 映射: {connection.LocalAddress}:{connection.LocalPort} -> " +
            $"{connection.RemoteAddress}:{connection.RemotePort}, relayPort={relayPort}");
    }

    public bool TryGetUdpRelayPort(OriginalConnection connection, out ushort relayPort)
    {
        return _udpRelayPorts.TryGetValue(UdpOriginalKey.From(connection), out relayPort);
    }

    public bool TryGetUdpRelayConnection(ushort relayPort, ushort localPort, out OriginalConnection connection)
    {
        return _udpRelayConnections.TryGetValue(new UdpRelayKey(relayPort, localPort), out connection!);
    }

    public bool IsUdpRelayPort(ushort port)
    {
        return _udpRelayPortSet.ContainsKey(port);
    }

    public void ReleaseUdpRelay(OriginalConnection connection, ushort relayPort)
    {
        _udpRelayPorts.TryRemove(UdpOriginalKey.From(connection), out _);
        _udpRelayConnections.TryRemove(new UdpRelayKey(relayPort, connection.LocalPort), out _);
        _udpRelayPortSet.TryRemove(relayPort, out _);
    }

    public void HandleFlowEvent(WinDivertAddress address)
    {
        var flow = address.Data.Flow;
        HandleClassificationEvent(
            address.Event,
            flow.EndpointId,
            flow.ProcessId,
            flow.Protocol,
            flow.LocalPort,
            GetIPv4Address(flow.RemoteAddr0, flow.RemoteAddr1, flow.RemoteAddr2, flow.RemoteAddr3),
            flow.RemotePort,
            "flow");
    }

    public void HandleSocketEvent(WinDivertAddress address)
    {
        var socket = address.Data.Socket;
        HandleClassificationEvent(
            address.Event,
            socket.EndpointId,
            socket.ProcessId,
            socket.Protocol,
            socket.LocalPort,
            GetIPv4Address(socket.RemoteAddr0, socket.RemoteAddr1, socket.RemoteAddr2, socket.RemoteAddr3),
            socket.RemotePort,
            "socket");
    }

    private void HandleClassificationEvent(
        WinDivertEvent eventType,
        ulong endpointId,
        uint processId,
        byte protocol,
        ushort localPort,
        IPAddress? remoteAddress,
        ushort remotePort,
        string source)
    {
        if (protocol is not (IpProtocols.Tcp or IpProtocols.Udp))
        {
            return;
        }

        if (localPort == 0)
        {
            return;
        }

        if (eventType is WinDivertEvent.FlowDeleted or WinDivertEvent.SocketClose)
        {
            RemoveEndpoint(new ClassificationEndpointKey(source, endpointId), source);
            return;
        }

        if (!IsTrackableOpenEvent(protocol, eventType))
        {
            return;
        }

        if (!IsTargetProcess(processId))
        {
            return;
        }

        var key = remoteAddress is not null && remotePort != 0
            ? TrackedConnectionKey.Exact(protocol, localPort, remoteAddress, remotePort)
            : TrackedConnectionKey.Wildcard(protocol, localPort);
        lock (_sync)
        {
            var endpointKey = new ClassificationEndpointKey(source, endpointId);
            if (!_endpointToTrackedConnection.ContainsKey(endpointKey))
            {
                _endpointToTrackedConnection[endpointKey] = key;
                _trackedConnectionCounts.TryGetValue(key, out var count);
                _trackedConnectionCounts[key] = count + 1;
            }

            Monitor.PulseAll(_sync);
        }

        var protocolName = ProtocolName(protocol);
        var remoteText = remoteAddress is null || remotePort == 0
            ? "*"
            : $"{remoteAddress}:{remotePort}";
        _logger.Debug(
            $"已跟踪目标 {protocolName} {source} 事件: pid={processId}, endpoint={endpointId}, " +
            $"localPort={localPort}, remote={remoteText}, target={_targetPath}");
    }

    private void RemoveEndpoint(ClassificationEndpointKey endpointKey, string source)
    {
        TrackedConnectionKey key;
        lock (_sync)
        {
            if (!_endpointToTrackedConnection.Remove(endpointKey, out key))
            {
                return;
            }

            RemoveTrackedConnectionLocked(key);
            Monitor.PulseAll(_sync);
        }

        var remoteText = key.HasRemoteEndpoint
            ? $"{key.RemoteAddress}:{key.RemotePort}"
            : "*";
        _logger.Debug(
            $"已删除目标 {ProtocolName(key.Protocol)} {source} 映射: " +
            $"endpoint={endpointKey.EndpointId}, localPort={key.LocalPort}, remote={remoteText}");
    }

    private void RemoveTrackedConnection(TrackedConnectionKey key)
    {
        lock (_sync)
        {
            RemoveTrackedConnectionLocked(key);
            Monitor.PulseAll(_sync);
        }
    }

    private void RemoveTrackedConnectionLocked(TrackedConnectionKey key)
    {
        if (!_trackedConnectionCounts.TryGetValue(key, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _trackedConnectionCounts.Remove(key);
            return;
        }

        _trackedConnectionCounts[key] = count - 1;
    }

    private static bool IsTrackableOpenEvent(byte protocol, WinDivertEvent eventType)
    {
        if (eventType is WinDivertEvent.FlowEstablished or WinDivertEvent.SocketConnect)
        {
            return true;
        }

        return protocol == IpProtocols.Udp && eventType == WinDivertEvent.SocketBind;
    }

    private static IPAddress? GetIPv4Address(uint part0, uint part1, uint part2, uint part3)
    {
        if (part0 == 0 && part1 == 0 && part2 == 0 && part3 == 0)
        {
            return null;
        }

        if (part0 != 0 && part1 == 0 && part2 == 0 && part3 == 0)
        {
            return FromWinDivertIPv4Word(part0);
        }

        if (part0 == 0 && part1 == 0 && part3 != 0 && IsIPv4MappedMarker(part2))
        {
            return FromWinDivertIPv4Word(part3);
        }

        return part0 != 0 ? FromWinDivertIPv4Word(part0) : null;
    }

    private static IPAddress FromWinDivertIPv4Word(uint value)
    {
        return new IPAddress(BitConverter.GetBytes(value));
    }

    private static IPAddress? ReverseIPv4Address(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return null;
        }

        var bytes = address.GetAddressBytes();
        Array.Reverse(bytes);
        return new IPAddress(bytes);
    }

    private static bool IsIPv4MappedMarker(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        return bytes is [0x00, 0x00, 0xFF, 0xFF]
            or [0xFF, 0xFF, 0x00, 0x00];
    }

    private static string ProtocolName(byte protocol)
    {
        return protocol switch
        {
            IpProtocols.Tcp => "TCP",
            IpProtocols.Udp => "UDP",
            _ => protocol.ToString()
        };
    }

    private bool IsTargetProcess(uint processId)
    {
        if (_targetProcessId is not { } targetProcessId)
        {
            return false;
        }

        if (processId == (uint)targetProcessId)
        {
            return true;
        }

        if (!_trackChildProcesses)
        {
            return false;
        }

        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= _targetProcessCacheExpiresAt)
            {
                _targetProcessCache.Clear();
                _targetProcessCacheExpiresAt = now.Add(TargetProcessCacheTtl);
            }

            if (_targetProcessCache.TryGetValue(processId, out var cached))
            {
                return cached;
            }
        }

        var isChild = ProcessTreeSnapshot.IsDescendantOf(processId, (uint)targetProcessId);
        if (isChild && IsExcludedChildProcess(processId))
        {
            isChild = false;
        }

        lock (_sync)
        {
            _targetProcessCache[processId] = isChild;
        }

        if (isChild && MarkChildProcessLogged(processId))
        {
            _logger.Info($"透明拦截目标子进程: pid={processId}, rootPid={targetProcessId}");
        }

        return isChild;
    }

    private bool IsExcludedChildProcess(uint processId)
    {
        if (_excludedChildProcessNames.Count == 0)
        {
            return false;
        }

        var processName = ProcessTreeSnapshot.TryGetProcessName(processId);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var nameWithExtension = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : $"{processName}.exe";
        var excluded = _excludedChildProcessNames.Contains(processName)
            || _excludedChildProcessNames.Contains(nameWithExtension);
        if (excluded && MarkExcludedChildProcessLogged(processId))
        {
            _logger.Info($"透明拦截已排除子进程: pid={processId}, name={nameWithExtension}");
        }

        return excluded;
    }

    private bool MarkChildProcessLogged(uint processId)
    {
        lock (_sync)
        {
            return _loggedChildProcesses.Add(processId);
        }
    }

    private bool MarkExcludedChildProcessLogged(uint processId)
    {
        lock (_sync)
        {
            return _loggedExcludedChildProcesses.Add(processId);
        }
    }
}

internal static class ProcessTreeSnapshot
{
    private const uint Th32CsSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static bool IsDescendantOf(uint processId, uint ancestorProcessId)
    {
        if (processId == ancestorProcessId)
        {
            return true;
        }

        var parents = GetParentProcessMap();
        var seen = new HashSet<uint>();
        var current = processId;

        for (var depth = 0; depth < 64; depth++)
        {
            if (!parents.TryGetValue(current, out var parentProcessId) || parentProcessId == 0)
            {
                return false;
            }

            if (parentProcessId == ancestorProcessId)
            {
                return true;
            }

            if (!seen.Add(parentProcessId))
            {
                return false;
            }

            current = parentProcessId;
        }

        return false;
    }

    public static string? TryGetProcessName(uint processId)
    {
        if (processId > int.MaxValue)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<uint, uint> GetParentProcessMap()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return new Dictionary<uint, uint>();
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
                ExeFile = string.Empty
            };

            var parents = new Dictionary<uint, uint>();
            if (!Process32First(snapshot, ref entry))
            {
                return parents;
            }

            do
            {
                parents[entry.ProcessId] = entry.ParentProcessId;
            }
            while (Process32Next(snapshot, ref entry));

            return parents;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }
}
