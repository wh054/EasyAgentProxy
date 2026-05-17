namespace AppProxyHelper;

public static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var command = args[0].ToLowerInvariant();
            return command switch
            {
                "init" => InitializeConfig(args),
                "check" => await CheckAsync(args),
                "repair-env" => await RepairEnvironmentAsync(args),
                "run" => await RunTargetAsync(args),
                _ => UnknownCommand(command)
            };
        }
        catch (CommandLineException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintHelp();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int InitializeConfig(string[] args)
    {
        var configPath = GetOptionValue(args, "--config")
            ?? (args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "app-proxy.json");

        ConfigLoader.WriteExample(configPath);
        Console.WriteLine($"已创建配置文件: {Path.GetFullPath(configPath)}");
        Console.WriteLine("编辑 targetPath 和 proxyUri 后运行 check 或 run。");
        return 0;
    }

    private static async Task<int> CheckAsync(string[] args)
    {
        var loadedConfig = LoadRequiredConfig(args);
        using var logger = AppLogger.Create(loadedConfig);

        logger.Info($"配置文件: {loadedConfig.ConfigPath}");
        logger.Info($"日志文件: {logger.LogFilePath}");
        logger.Info($"代理: {UriFormatter.Sanitize(loadedConfig.Value.ProxyUri)}");

        var ok = await ProxyDiagnostics.CheckAsync(loadedConfig.Value, logger, CancellationToken.None);
        return ok ? 0 : 3;
    }

    private static Task<int> RepairEnvironmentAsync(string[] args)
    {
        var loadedConfig = LoadRequiredConfig(args);
        using var logger = AppLogger.Create(loadedConfig);

        logger.Info($"配置文件: {loadedConfig.ConfigPath}");
        logger.Info($"日志文件: {logger.LogFilePath}");
        logger.Info($"运行模式: {loadedConfig.Value.Mode}");

        return WinDivertEnvironmentRepair.CheckAndRepairAsync(loadedConfig, logger, CancellationToken.None);
    }

    private static async Task<int> RunTargetAsync(string[] args)
    {
        var loadedConfig = LoadRequiredConfig(args);
        using var logger = AppLogger.Create(loadedConfig);
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
            logger.Warn("收到 Ctrl+C，正在停止等待流程。目标进程默认不会被强制结束。");
        };

        logger.Info($"配置文件: {loadedConfig.ConfigPath}");
        logger.Info($"日志文件: {logger.LogFilePath}");
        logger.Info($"运行模式: {loadedConfig.Value.Mode}");

        if (loadedConfig.Value.RunDiagnosticsBeforeLaunch)
        {
            var diagnosticsOk = await ProxyDiagnostics.CheckAsync(loadedConfig.Value, logger, cts.Token);
            if (!diagnosticsOk && loadedConfig.Value.AbortLaunchWhenDiagnosticsFail)
            {
                logger.Error("代理诊断失败，已按配置取消启动目标应用。");
                return 3;
            }

            if (!diagnosticsOk)
            {
                logger.Warn("代理诊断失败，但配置允许继续启动目标应用。");
            }
        }

        await using var interception = TrafficInterceptionFactory.Create(loadedConfig, logger);
        await interception.StartAsync(cts.Token);

        var launcher = new ProcessProxyLauncher(loadedConfig, logger, interception);
        return await launcher.RunAsync(cts.Token);
    }

    private static LoadedConfig LoadRequiredConfig(string[] args)
    {
        var configPath = GetOptionValue(args, "--config")
            ?? throw new CommandLineException("缺少 --config 参数。");

        return ConfigLoader.Load(configPath);
    }

    private static string? GetOptionValue(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new CommandLineException($"{optionName} 需要一个值。");
            }

            return args[i + 1];
        }

        return null;
    }

    private static bool IsHelp(string value)
    {
        return value is "-h" or "--help" or "help" or "/?";
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"未知命令: {command}");
        Console.Error.WriteLine();
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            AppProxyHelper - 按应用启动代理辅助工具

            用法:
              AppProxyHelper init [--config app-proxy.json]
              AppProxyHelper check --config app-proxy.json
              AppProxyHelper repair-env --config app-proxy.json
              AppProxyHelper run --config app-proxy.json
              AppProxyHelper ui [--config app-proxy.json] [--repair-env]

            说明:
              Transparent 是默认模式。它使用 WinDivert 驱动按目标 PID 分类连接，并把 IPv4 TCP
              透明转发到本机监听器，再通过 socks5/http CONNECT 上游代理转发；启用
              captureUdp 后会通过 SOCKS5 UDP ASSOCIATE 转发 IPv4 UDP。
              需要管理员权限，以及 WinDivert.dll + WinDivert64.sys/WinDivert32.sys。

              Environment 模式会启动目标程序并注入 HTTP_PROXY/HTTPS_PROXY/ALL_PROXY 等环境变量。
              这能覆盖尊重代理环境变量的应用和库，但不是内核级全流量拦截。
            """);
    }
}

public sealed class CommandLineException : Exception
{
    public CommandLineException(string message)
        : base(message)
    {
    }
}
