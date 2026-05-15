using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AppProxyHelper;

internal sealed class WinDivertInterceptionSession : IInterceptionSession
{
    private const uint ErrorOperationAborted = 995;
    private const int DefaultPacketBufferSize = 0xFFFF;
    private static readonly TimeSpan PumpStopTimeout = TimeSpan.FromSeconds(3);

    private readonly LoadedConfig _loadedConfig;
    private readonly TransparentInterceptionConfig _config;
    private readonly AppLogger _logger;
    private readonly TransparentConnectionTable _connections;
    private readonly CancellationTokenSource _disposeCts = new();
    private WinDivertNative? _native;
    private WinDivertHandle? _networkHandle;
    private WinDivertHandle? _flowHandle;
    private WinDivertHandle? _socketHandle;
    private TransparentTcpProxyServer? _tcpProxy;
    private TransparentUdpProxyServer? _udpProxy;
    private Task? _networkTask;
    private Task? _flowTask;
    private Task? _socketTask;
    private CancellationTokenRegistration _externalCancellationRegistration;
    private int _networkRewriteErrorCount;

    public WinDivertInterceptionSession(LoadedConfig loadedConfig, AppLogger logger)
    {
        _loadedConfig = loadedConfig;
        _config = loadedConfig.Value.Transparent;
        _logger = logger;
        _connections = new TransparentConnectionTable(
            logger,
            _config.TrackChildProcesses,
            _config.ExcludedChildProcessNames);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Provider.Equals("WinDivert", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("transparent.provider 当前只支持 WinDivert。WFP 需要单独的内核 callout 驱动实现。");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WinDivert 透明代理只支持 Windows。");
        }

        if (!IsAdministrator())
        {
            _logger.Warn("当前进程不是管理员权限；WinDivert 驱动通常会打开失败。请用管理员终端运行。");
        }

        var configuredDriverPath = PathResolver.Resolve(_config.DriverPath, _loadedConfig.BaseDirectory);
        var driverPath = ResolveWinDivertDriverPath(_config.DriverPath, _loadedConfig.BaseDirectory);
        if (driverPath is null)
        {
            throw new FileNotFoundException(
                "找不到 WinDivert.dll。请把 WinDivert.dll 和 WinDivert64.sys/WinDivert32.sys 放到程序目录根目录，或重新运行 build-exe.bat 自动打包。",
                configuredDriverPath);
        }

        var redirectAddress = ParseIPv4Address(_config.RedirectListenAddress);
        var proxy = ProxyEndpoint.Parse(_loadedConfig.Value.ProxyUri);
        if (_config.CaptureUdp && !proxy.IsSocks)
        {
            throw new InvalidOperationException("transparent.captureUdp=true 时，proxyUri 必须使用 socks:// 或 socks5://。");
        }

        _logger.Info($"Transparent 模式启动: provider=WinDivert, driverPath={driverPath}");
        _logger.Info($"透明转发监听点: {redirectAddress}:{_config.RedirectListenPort}");

        _native = new WinDivertNative(driverPath);
        var transportFilter = _config.CaptureUdp ? "(tcp or udp)" : "tcp";
        _flowHandle = _native.Open(transportFilter, WinDivertLayer.Flow, (short)_config.WinDivertPriority, WinDivertFlags.Sniff | WinDivertFlags.RecvOnly);
        _socketHandle = _native.Open(transportFilter, WinDivertLayer.Socket, (short)_config.WinDivertPriority, WinDivertFlags.Sniff | WinDivertFlags.RecvOnly);
        _networkHandle = _native.Open($"outbound and ip and {transportFilter} and !impostor", WinDivertLayer.Network, (short)_config.WinDivertPriority, 0);

        ConfigureQueue(_networkHandle);

        _tcpProxy = new TransparentTcpProxyServer(
            redirectAddress,
            _config.RedirectListenPort,
            proxy,
            _connections,
            _config,
            _logger);
        _tcpProxy.StartAsync(cancellationToken).GetAwaiter().GetResult();

        if (_config.CaptureUdp)
        {
            _udpProxy = new TransparentUdpProxyServer(
                redirectAddress,
                proxy,
                _connections,
                _config,
                _logger);
            _udpProxy.StartAsync(cancellationToken).GetAwaiter().GetResult();
        }

        if (cancellationToken.CanBeCanceled)
        {
            _externalCancellationRegistration = cancellationToken.Register(static state =>
            {
                ((CancellationTokenSource)state!).Cancel();
            }, _disposeCts);
        }

        var token = _disposeCts.Token;
        var rewriter = new WinDivertPacketRewriter(
            _connections,
            redirectAddress,
            _config.RedirectListenPort,
            _udpProxy);

        _flowTask = Task.Run(() => PumpFlowAsync(token), CancellationToken.None);
        _socketTask = Task.Run(() => PumpSocketAsync(token), CancellationToken.None);
        _networkTask = Task.Run(() => PumpNetworkAsync(rewriter, token), CancellationToken.None);

        var protocols = _config.CaptureUdp ? "IPv4 TCP/UDP" : "IPv4 TCP";
        _logger.Info($"WinDivert 透明拦截已启动。当前实现支持目标进程 {protocols} 透明代理。");

        return Task.CompletedTask;
    }

    public void SetTargetProcess(int processId, string executablePath)
    {
        _connections.SetTargetProcess(processId, executablePath);
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        _networkHandle?.Shutdown();
        _flowHandle?.Shutdown();
        _socketHandle?.Shutdown();
        _networkHandle?.Dispose();
        _flowHandle?.Dispose();
        _socketHandle?.Dispose();

        if (_tcpProxy is not null)
        {
            await _tcpProxy.DisposeAsync();
        }

        if (_udpProxy is not null)
        {
            await _udpProxy.DisposeAsync();
        }

        var pumpsStopped = true;
        pumpsStopped &= await WaitForPumpAsync(_networkTask, "network");
        pumpsStopped &= await WaitForPumpAsync(_flowTask, "flow");
        pumpsStopped &= await WaitForPumpAsync(_socketTask, "socket");

        _externalCancellationRegistration.Dispose();
        if (pumpsStopped)
        {
            _native?.Dispose();
            _disposeCts.Dispose();
        }
        else
        {
            _logger.Warn("WinDivert 后台接收任务停止超时，已返回界面控制权；相关原生资源将由进程退出时回收。");
        }
    }

    private async Task PumpNetworkAsync(WinDivertPacketRewriter rewriter, CancellationToken cancellationToken)
    {
        if (_native is null || _networkHandle is null)
        {
            return;
        }

        var packetBufferSize = _config.PacketBufferSize is > 0 ? _config.PacketBufferSize : DefaultPacketBufferSize;
        var managedBuffer = new byte[packetBufferSize];
        var packet = Marshal.AllocHGlobal(packetBufferSize);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var address = new WinDivertAddress();
                if (!_native.Receive(_networkHandle, packet, (uint)packetBufferSize, out var recvLen, ref address))
                {
                    if (ShouldStopAfterNativeFailure(cancellationToken))
                    {
                        return;
                    }

                    _logger.Warn($"WinDivert network 接收失败: {LastWin32ErrorText()}");
                    continue;
                }

                if (recvLen == 0)
                {
                    continue;
                }

                Marshal.Copy(packet, managedBuffer, 0, (int)recvLen);
                var rewriteKind = PacketRewriteKind.None;
                try
                {
                    rewriteKind = rewriter.Rewrite(managedBuffer, (int)recvLen, ref address);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (Interlocked.Increment(ref _networkRewriteErrorCount) <= 10)
                    {
                        _logger.Error("WinDivert network 包改写失败，已按原包放行。", ex);
                    }
                }

                if (rewriteKind == PacketRewriteKind.Modified)
                {
                    Marshal.Copy(managedBuffer, 0, packet, (int)recvLen);
                    if (!_native.CalcChecksums(packet, recvLen, ref address))
                    {
                        _logger.Warn($"WinDivert checksum 重算失败: {LastWin32ErrorText()}");
                    }
                }

                if (!_native.Send(_networkHandle, packet, recvLen, out _, ref address)
                    && !ShouldStopAfterNativeFailure(cancellationToken))
                {
                    _logger.Warn($"WinDivert network 发送失败: {LastWin32ErrorText()}");
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(packet);
            await Task.CompletedTask;
        }
    }

    private async Task PumpFlowAsync(CancellationToken cancellationToken)
    {
        if (_native is null || _flowHandle is null)
        {
            return;
        }

        await PumpClassificationAsync(
            _flowHandle,
            cancellationToken,
            address => _connections.HandleFlowEvent(address),
            "flow");
    }

    private async Task PumpSocketAsync(CancellationToken cancellationToken)
    {
        if (_native is null || _socketHandle is null)
        {
            return;
        }

        await PumpClassificationAsync(
            _socketHandle,
            cancellationToken,
            address => _connections.HandleSocketEvent(address),
            "socket");
    }

    private async Task PumpClassificationAsync(
        WinDivertHandle handle,
        CancellationToken cancellationToken,
        Action<WinDivertAddress> handleAddress,
        string name)
    {
        if (_native is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var address = new WinDivertAddress();
            if (!_native.Receive(handle, IntPtr.Zero, 0, out _, ref address))
            {
                if (ShouldStopAfterNativeFailure(cancellationToken))
                {
                    return;
                }

                _logger.Warn($"WinDivert {name} 接收失败: {LastWin32ErrorText()}");
                continue;
            }

            handleAddress(address);
        }

        await Task.CompletedTask;
    }

    private void ConfigureQueue(WinDivertHandle handle)
    {
        if (_native is null)
        {
            return;
        }

        TrySetParam(handle, WinDivertParam.QueueLength, (ulong)Math.Max(1, _config.QueueLength));
        TrySetParam(handle, WinDivertParam.QueueTime, (ulong)Math.Max(1, _config.QueueTimeMs));
        TrySetParam(handle, WinDivertParam.QueueSize, (ulong)Math.Max(1, _config.QueueSizeBytes));
    }

    private static string? ResolveWinDivertDriverPath(string configuredPath, string configBaseDirectory)
    {
        foreach (var candidate in GetWinDivertDriverCandidates(configuredPath, configBaseDirectory))
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
                // Keep probing other likely locations.
            }
        }

        return null;
    }

    private static IEnumerable<string> GetWinDivertDriverCandidates(string configuredPath, string configBaseDirectory)
    {
        yield return PathResolver.Resolve(configuredPath, configBaseDirectory);

        if (!Path.IsPathFullyQualified(configuredPath))
        {
            yield return Path.Combine(AppContext.BaseDirectory, configuredPath);
            yield return Path.Combine(Directory.GetCurrentDirectory(), configuredPath);
        }

        yield return Path.Combine(AppContext.BaseDirectory, "drivers", "WinDivert.dll");
        yield return Path.Combine(AppContext.BaseDirectory, "WinDivert.dll");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "drivers", "WinDivert.dll");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "WinDivert.dll");
    }

    private void TrySetParam(WinDivertHandle handle, WinDivertParam param, ulong value)
    {
        try
        {
            _native?.SetParam(handle, param, value);
        }
        catch (Exception ex)
        {
            _logger.Warn($"设置 WinDivert 队列参数失败，继续使用默认值: {param}={value}, {ex.Message}");
        }
    }

    private bool ShouldStopAfterNativeFailure(CancellationToken cancellationToken)
    {
        var error = Marshal.GetLastWin32Error();
        return cancellationToken.IsCancellationRequested
            || _disposeCts.IsCancellationRequested
            || error == ErrorOperationAborted;
    }

    private static string LastWin32ErrorText()
    {
        var error = Marshal.GetLastWin32Error();
        return $"Win32Error={error}: {WinDivertNative.ExplainError(error)}";
    }

    private async Task<bool> WaitForPumpAsync(Task? task, string name)
    {
        if (task is null)
        {
            return true;
        }

        try
        {
            await task.WaitAsync(PumpStopTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            _logger.Warn($"WinDivert {name} 接收任务停止超过 {PumpStopTimeout.TotalSeconds:0} 秒，继续释放。");
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

    private static IPAddress ParseIPv4Address(string value)
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            throw new InvalidOperationException($"transparent.redirectListenAddress 不是有效 IP 地址: {value}");
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException("当前 WinDivert 透明代理实现只支持 IPv4 redirectListenAddress。");
        }

        return address;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
