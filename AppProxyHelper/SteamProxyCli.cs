using System.Text;
using System.Security.Principal;

namespace AppProxyHelper;

internal static class SteamProxyCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        var action = args[0].ToLowerInvariant();
        if (args.Skip(1).Any(IsHelp))
        {
            PrintHelp();
            return 0;
        }

        ValidateArguments(action, args);
        var startupId = GetWorkerStartupId(args);
        var ownerSid = GetOptionArgument(args, "--owner-sid");
        if (ownerSid is not null)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!string.Equals(ownerSid, identity.User?.Value, StringComparison.Ordinal))
            {
                throw new CommandLineException("Steam 代理需要同一 Windows 用户提权，不支持切换管理员账户。");
            }
        }

        var controller = new SteamProxyProcessController();
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            switch (action)
            {
                case "start":
                    var upstream = args.Contains("--direct", StringComparer.OrdinalIgnoreCase)
                        ? null
                        : GetOptionArgument(args, "--proxy") ?? controller.LoadSettings().UpstreamProxyUri;
                    if (upstream is not null)
                    {
                        ValidateProxyUri(upstream);
                    }

                    await controller.StartAsync(upstream, cancellation.Token);
                    Console.WriteLine(FormatStatus(controller.GetStatus()));
                    return 0;
                case "launch":
                    var target = GetOptionArgument(args, "--target")
                        ?? throw new CommandLineException("steam launch 需要 --target 指定 Steam.exe。");
                    var launchProxy = args.Contains("--direct", StringComparer.OrdinalIgnoreCase)
                        ? null : GetOptionArgument(args, "--proxy") ?? controller.LoadSettings().UpstreamProxyUri;
                    return await SteamApplicationLauncher.LaunchAsync(target, launchProxy,
                        log: Console.WriteLine, cancellationToken: cancellation.Token);
                case "stop":
                    await controller.StopAsync(cancellation.Token);
                    Console.WriteLine(FormatStatus(controller.GetStatus()));
                    return 0;
                case "status":
                    Console.WriteLine(FormatStatus(controller.GetStatus()));
                    return 0;
                case "install-cert":
                    controller.InstallCertificate();
                    Console.WriteLine("已将 EasyProxy Steam CA 安装到当前用户的受信任根证书库。");
                    return 0;
                case "remove-cert":
                    controller.RemoveCertificate();
                    Console.WriteLine("已移除当前用户信任库中的 EasyProxy Steam CA。");
                    return 0;
                case "run":
                    return await controller.RunAsync(cancellation.Token, startupId);
                case "run-session":
                    if (!int.TryParse(GetOptionArgument(args, "--session-owner-pid"), out var sessionOwnerPid) || sessionOwnerPid <= 0
                        || !long.TryParse(GetOptionArgument(args, "--session-owner-start"), out var sessionOwnerStart) || sessionOwnerStart <= 0)
                        throw new CommandLineException("Steam 会话后台需要有效的会话进程身份。");
                    return await controller.RunAsync(cancellation.Token, startupId, sessionMode: true, sessionOwnerPid, sessionOwnerStart);
                case "recover":
                    return await controller.RecoverAsync(cancellation.Token);
                default:
                    throw new CommandLineException($"未知 Steam 代理命令: {action}。");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Steam 代理操作已取消。");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static string FormatStatus(SteamProxyStatus status)
    {
        var text = new StringBuilder();
        text.AppendLine($"Steam 代理: {(status.IsRunning ? "运行中" : "已停止")}");
        text.AppendLine("代理范围: 商店、社区等网页；游戏下载与更新保留原网络链路");
        if (status.ProcessId is { } processId)
        {
            text.AppendLine($"后台进程: {processId}");
        }
        if (status.IsRunning)
            text.AppendLine($"运行方式: {(status.IsSession ? "临时会话；Steam 完全退出后恢复" : "手动维护模式")}");

        var upstream = !status.IsRunning
            ? "未运行；Steam-Proxy 使用生成入口中的会话参数"
            : string.IsNullOrWhiteSpace(status.UpstreamProxyUri)
                ? "直连（通过独立 DNS 回源）"
                : UriFormatter.Sanitize(status.UpstreamProxyUri);
        text.AppendLine($"上游代理: {upstream}");
        text.AppendLine($"本地监听: {(status.IsRunning ? "127.0.0.1:80 / 127.0.0.1:443" : "未运行（默认端口 80 / 443）")}");
        text.AppendLine($"CA 信任: {(status.CertificateTrusted ? "已安装（当前用户）" : "未安装")}");
        text.AppendLine($"Hosts 映射: {(status.HostsInstalled ? "已写入 EasyProxy Steam 标记区" : "未写入")}");
        text.AppendLine($"数据目录: {status.DataDirectory}");
        text.AppendLine($"日志文件: {status.LogFilePath}");
        if (!status.IsRunning && status.HostsInstalled)
        {
            text.AppendLine("检测到未恢复的 Hosts 映射，请执行停止以恢复。");
        }

        return text.ToString().TrimEnd();
    }

    internal static void ValidateProxyUri(string proxyUri)
    {
        if (!Uri.TryCreate(proxyUri, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !ProxyEndpoint.IsSupportedScheme(uri.Scheme)
            || uri.Port == 0
            || uri.Fragment.Length > 0
            || uri.Query.Length > 0
            || uri.AbsolutePath is not "" and not "/")
        {
            throw new CommandLineException("Steam 上游代理需要有效的 http://、https://、socks:// 或 socks5:// 地址（不含路径、查询参数或片段）。");
        }
    }

    private static void ValidateArguments(string action, string[] args)
    {
        if (action is not ("start" or "launch" or "stop" or "status" or "install-cert" or "remove-cert" or "run" or "run-session" or "recover"))
        {
            throw new CommandLineException($"未知 Steam 代理命令: {action}。");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var option = args[i];
            var isProxy = option.Equals("--proxy", StringComparison.OrdinalIgnoreCase);
            var isDirect = option.Equals("--direct", StringComparison.OrdinalIgnoreCase);
            var isOwner = option.Equals("--owner-sid", StringComparison.OrdinalIgnoreCase);
            var isStartupId = option.Equals("--startup-id", StringComparison.OrdinalIgnoreCase);
            var isTarget = option.Equals("--target", StringComparison.OrdinalIgnoreCase);
            var isSessionOwner = option.Equals("--session-owner-pid", StringComparison.OrdinalIgnoreCase) || option.Equals("--session-owner-start", StringComparison.OrdinalIgnoreCase);
            if (!((action is "start" or "launch") && (isProxy || isDirect)
                  || action == "launch" && isTarget
                  || (action is "run" or "run-session" or "recover") && isOwner
                  || (action is "run" or "run-session") && isStartupId
                  || action == "run-session" && isSessionOwner))
            {
                throw new CommandLineException($"steam {action} 不支持参数: {option}。");
            }

            if (!seen.Add(option))
            {
                throw new CommandLineException($"参数不能重复: {option}。");
            }

            if (isProxy || isOwner || isStartupId || isTarget || isSessionOwner)
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(args[i]))
                {
                    throw new CommandLineException($"{option} 需要一个值。");
                }
            }
        }

        if (seen.Contains("--proxy") && seen.Contains("--direct"))
        {
            throw new CommandLineException("--proxy 和 --direct 不能同时使用。");
        }
    }

    private static string? GetWorkerStartupId(string[] args)
    {
        var value = GetOptionArgument(args, "--startup-id");
        if (value is null)
        {
            return null;
        }

        if (!Guid.TryParseExact(value, "N", out var startupId))
        {
            throw new CommandLineException("--startup-id 需要不含连字符的 32 位 GUID。");
        }

        return startupId.ToString("N");
    }

    private static string? GetOptionArgument(string[] args, string name)
    {
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool IsHelp(string value) => value is "help" or "--help" or "-h" or "/?";

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            EasyProxy Steam 代理

            用法:
              EasyProxy steam start [--proxy URI | --direct]
              EasyProxy steam stop
              EasyProxy steam status
              EasyProxy steam install-cert
              EasyProxy steam remove-cert

            start 首次安装独立的当前用户 EasyProxy Steam CA，后续复用，并提权启动后台服务、写入 Hosts 映射。
            不指定 --proxy 或 --direct 时沿用已保存的上游代理；首次默认 socks5://127.0.0.1:7890。
            stop 会停止后台服务、恢复 Hosts 并清理会话配置，保留 CA；remove-cert 可在停止后移除 CA 信任。
            仅代理 Steam 商店、社区及专用网页资源；游戏下载、更新、通用 CDN 和下载服务器目录 API 保留原网络链路。
            """);
    }
}
