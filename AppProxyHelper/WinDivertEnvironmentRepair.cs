using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace AppProxyHelper;

internal static class WinDivertEnvironmentRepair
{
    private const string WinDivertServiceName = "WinDivert";
    private const string BaseFilteringEngineServiceName = "BFE";
    private const int ServiceDisabled = 4;
    private const int ServiceDemandStart = 3;
    private const int ServiceAutoStart = 2;
    private const int ServiceRunning = 4;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceDoesNotExist = 1060;

    public static async Task<int> CheckAndRepairAsync(
        LoadedConfig loadedConfig,
        AppLogger logger,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            logger.Error("环境修复仅支持 Windows。");
            return 3;
        }

        if (!loadedConfig.Value.Mode.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            logger.Info("当前不是 Transparent 模式，无需检查 WinDivert 环境。");
            return 0;
        }

        var hasIssue = false;
        var repaired = false;
        var isAdministrator = IsAdministrator();
        if (!isAdministrator)
        {
            logger.Error("当前进程不是管理员权限，无法修复 Windows 驱动服务。请以管理员身份运行后重试。");
            return 3;
        }

        logger.Info("开始检查 WinDivert 透明拦截运行环境。");

        var driverPath = CheckWinDivertFiles(loadedConfig, logger, ref hasIssue);
        cancellationToken.ThrowIfCancellationRequested();

        var bfe = WindowsServiceManager.GetSnapshot(BaseFilteringEngineServiceName);
        LogServiceSnapshot(logger, "Base Filtering Engine", bfe);
        if (!bfe.Exists)
        {
            logger.Error("未找到 Base Filtering Engine 服务，WinDivert 无法工作。请检查系统网络组件是否完整。");
            hasIssue = true;
        }
        else
        {
            repaired |= TryRepairBaseFilteringEngine(bfe, logger, ref hasIssue);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var winDivert = WindowsServiceManager.GetSnapshot(WinDivertServiceName);
        LogServiceSnapshot(logger, "WinDivert", winDivert);
        if (!winDivert.Exists)
        {
            logger.Info("WinDivert 服务当前不存在，这是正常状态；首次打开驱动时会自动创建。");
        }
        else
        {
            repaired |= TryRepairWinDivertService(winDivert, logger, ref hasIssue);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(driverPath))
        {
            hasIssue |= !TestWinDivertOpen(driverPath, loadedConfig.Value.Transparent.WinDivertPriority, logger);
        }

        if (hasIssue)
        {
            logger.Warn("环境检测/修复完成，但仍存在需要人工处理的问题。");
            return 3;
        }

        logger.Info(repaired
            ? "环境检测/修复完成，已修复可自动处理的问题。"
            : "环境检测完成，未发现需要修复的问题。");
        return 0;
    }

    private static string? CheckWinDivertFiles(LoadedConfig loadedConfig, AppLogger logger, ref bool hasIssue)
    {
        var configuredDriverPath = PathResolver.Resolve(
            loadedConfig.Value.Transparent.DriverPath,
            loadedConfig.BaseDirectory);
        var driverPath = WinDivertInterceptionSession.ResolveWinDivertDriverPath(
            loadedConfig.Value.Transparent.DriverPath,
            loadedConfig.BaseDirectory);

        if (driverPath is null)
        {
            logger.Error($"找不到 WinDivert.dll。当前配置路径: {configuredDriverPath}");
            hasIssue = true;
            return null;
        }

        logger.Info($"WinDivert.dll: {driverPath}");

        var driverDirectory = Path.GetDirectoryName(driverPath) ?? AppContext.BaseDirectory;
        var sysFileName = Environment.Is64BitProcess ? "WinDivert64.sys" : "WinDivert32.sys";
        var sysPath = Path.Combine(driverDirectory, sysFileName);
        if (!File.Exists(sysPath))
        {
            logger.Error($"找不到 {sysFileName}。请确认它和 WinDivert.dll 在同一目录: {driverDirectory}");
            hasIssue = true;
            return driverPath;
        }

        logger.Info($"{sysFileName}: {sysPath}");
        return driverPath;
    }

    private static bool TryRepairBaseFilteringEngine(
        WindowsServiceSnapshot snapshot,
        AppLogger logger,
        ref bool hasIssue)
    {
        var repaired = false;

        if (snapshot.StartValue == ServiceDisabled)
        {
            if (WindowsServiceManager.TrySetStartType(BaseFilteringEngineServiceName, ServiceAutoStart, out var error))
            {
                logger.Warn("Base Filtering Engine 服务被禁用，已改为自动启动。");
                repaired = true;
            }
            else
            {
                logger.Error($"无法修改 Base Filtering Engine 启动类型: {DescribeWin32Error(error)}");
                hasIssue = true;
                return repaired;
            }
        }

        var refreshed = WindowsServiceManager.GetSnapshot(BaseFilteringEngineServiceName);
        if (refreshed.State != ServiceRunning)
        {
            if (WindowsServiceManager.TryStart(BaseFilteringEngineServiceName, out var error)
                || error == ErrorServiceAlreadyRunning)
            {
                logger.Warn("Base Filtering Engine 服务未运行，已尝试启动。");
                repaired = true;
            }
            else
            {
                logger.Error($"无法启动 Base Filtering Engine 服务: {DescribeWin32Error(error)}");
                hasIssue = true;
            }
        }

        return repaired;
    }

    private static bool TryRepairWinDivertService(
        WindowsServiceSnapshot snapshot,
        AppLogger logger,
        ref bool hasIssue)
    {
        if (snapshot.StartValue == ServiceDisabled)
        {
            logger.Warn("WinDivert 服务已被禁用。为避免影响其他软件，环境检查不会自动修改它；请在高级修复中确认后处理。");
            hasIssue = true;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ImagePath)
            && !snapshot.ImagePath.Contains("WinDivert", StringComparison.OrdinalIgnoreCase))
        {
            logger.Warn($"WinDivert 服务路径看起来异常: {snapshot.ImagePath}");
            logger.Warn("如果后续仍然失败，请在高级修复中确认是否关闭或卸载当前 WinDivert 服务。");
            hasIssue = true;
        }

        return false;
    }

    private static bool TestWinDivertOpen(string driverPath, int priority, AppLogger logger)
    {
        try
        {
            using var native = new WinDivertNative(driverPath);
            try
            {
                using var existingHandle = native.Open(
                    "tcp",
                    WinDivertLayer.Flow,
                    (short)priority,
                    WinDivertFlags.Sniff | WinDivertFlags.RecvOnly | WinDivertFlags.NoInstall);
                logger.Info("WinDivert Flow 层打开测试通过：已连接系统已有驱动。");
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorServiceDoesNotExist)
            {
                logger.Info("系统中未检测到已有 WinDivert 驱动，继续测试应用目录自带驱动。");
            }

            using var bundledHandle = native.Open("tcp", WinDivertLayer.Flow, (short)priority, WinDivertFlags.Sniff | WinDivertFlags.RecvOnly);
            logger.Info("WinDivert Flow 层打开测试通过：已使用应用目录自带驱动。");
            return true;
        }
        catch (Win32Exception ex)
        {
            logger.Error($"WinDivert 打开测试失败: Win32Error={ex.NativeErrorCode}: {WinDivertNative.ExplainError(ex.NativeErrorCode)}");
            if (ex.NativeErrorCode is 577 or 1275)
            {
                logger.Warn("这通常表示驱动被系统安全策略或安全软件阻止，需要在目标电脑上放行 WinDivert 驱动。");
            }
            else if (ex.NativeErrorCode == 1058)
            {
                logger.Warn("这通常表示 WinDivert 服务仍处于禁用状态，或关联驱动设备没有被系统允许启动。");
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error("WinDivert 打开测试发生异常。", ex);
            return false;
        }
    }

    private static void LogServiceSnapshot(AppLogger logger, string displayName, WindowsServiceSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            logger.Info($"{displayName}: 未安装");
            return;
        }

        logger.Info(
            $"{displayName}: Start={FormatStartType(snapshot.StartValue)}, State={FormatServiceState(snapshot.State)}, ImagePath={snapshot.ImagePath ?? "-"}");
    }

    private static string FormatStartType(int? startValue)
    {
        return startValue switch
        {
            0 => "Boot",
            1 => "System",
            ServiceAutoStart => "Auto",
            ServiceDemandStart => "Manual",
            ServiceDisabled => "Disabled",
            null => "Unknown",
            _ => startValue.Value.ToString()
        };
    }

    private static string FormatServiceState(int? state)
    {
        return state switch
        {
            1 => "Stopped",
            2 => "StartPending",
            3 => "StopPending",
            ServiceRunning => "Running",
            5 => "ContinuePending",
            6 => "PausePending",
            7 => "Paused",
            null => "Unknown",
            _ => state.Value.ToString()
        };
    }

    private static string DescribeWin32Error(int error)
    {
        return error == 0 ? "成功" : $"Win32Error={error}: {new Win32Exception(error).Message}";
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private sealed record WindowsServiceSnapshot(
        string Name,
        bool Exists,
        int? StartValue,
        int? State,
        string? ImagePath);

    private static class WindowsServiceManager
    {
        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceStart = 0x0010;
        private const uint ServiceChangeConfig = 0x0002;
        private const uint ServiceNoChange = 0xFFFFFFFF;

        public static WindowsServiceSnapshot GetSnapshot(string serviceName)
        {
            var exists = TryReadServiceRegistry(
                serviceName,
                out var startValue,
                out var imagePath);

            int? state = null;
            if (exists && TryQueryState(serviceName, out var serviceState, out _))
            {
                state = serviceState;
            }

            return new WindowsServiceSnapshot(serviceName, exists, startValue, state, imagePath);
        }

        public static bool TrySetStartType(string serviceName, int startType, out int error)
        {
            return WithService(
                serviceName,
                ServiceChangeConfig,
                serviceHandle => ChangeServiceConfig(
                    serviceHandle,
                    ServiceNoChange,
                    (uint)startType,
                    ServiceNoChange,
                    null,
                    null,
                    IntPtr.Zero,
                    null,
                    null,
                    null,
                    null),
                out error);
        }

        public static bool TryStart(string serviceName, out int error)
        {
            return WithService(
                serviceName,
                ServiceStart | ServiceQueryStatus,
                serviceHandle => StartService(serviceHandle, 0, IntPtr.Zero),
                out error);
        }

        private static bool TryQueryState(string serviceName, out int state, out int error)
        {
            state = 0;
            var queriedState = 0;
            var ok = WithService(
                serviceName,
                ServiceQueryStatus,
                serviceHandle =>
                {
                    if (!QueryServiceStatus(serviceHandle, out var status))
                    {
                        return false;
                    }

                    queriedState = (int)status.CurrentState;
                    return true;
                },
                out error);
            state = queriedState;
            return ok;
        }

        private static bool WithService(
            string serviceName,
            uint desiredAccess,
            Func<IntPtr, bool> operation,
            out int error)
        {
            error = 0;
            var serviceControlManager = OpenSCManager(null, null, ScManagerConnect);
            if (serviceControlManager == IntPtr.Zero)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                var service = OpenService(serviceControlManager, serviceName, desiredAccess);
                if (service == IntPtr.Zero)
                {
                    error = Marshal.GetLastWin32Error();
                    if (error == ErrorServiceDoesNotExist)
                    {
                        return false;
                    }

                    return false;
                }

                try
                {
                    if (operation(service))
                    {
                        return true;
                    }

                    error = Marshal.GetLastWin32Error();
                    return false;
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(serviceControlManager);
            }
        }

        private static bool TryReadServiceRegistry(
            string serviceName,
            out int? startValue,
            out string? imagePath)
        {
            startValue = null;
            imagePath = null;

            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key is null)
            {
                return false;
            }

            startValue = key.GetValue("Start") is int start ? start : null;
            imagePath = key.GetValue("ImagePath")?.ToString();
            return true;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(
            string? machineName,
            string? databaseName,
            uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(
            IntPtr serviceControlManager,
            string serviceName,
            uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatus(
            IntPtr service,
            out ServiceStatus serviceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StartService(
            IntPtr service,
            int serviceArgsCount,
            IntPtr serviceArgs);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeServiceConfig(
            IntPtr service,
            uint serviceType,
            uint startType,
            uint errorControl,
            string? binaryPathName,
            string? loadOrderGroup,
            IntPtr tagId,
            string? dependencies,
            string? serviceStartName,
            string? password,
            string? displayName);

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }
    }
}
