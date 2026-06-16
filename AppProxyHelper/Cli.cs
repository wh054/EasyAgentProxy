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
                "run" => await RunTargetAsync(args),
                "scripts" => CreateLauncherScripts(args),
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

    private static int CreateLauncherScripts(string[] args)
    {
        var configPath = GetOptionValue(args, "--config");
        AppProxyConfig? config = null;
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            config = ConfigLoader.Load(configPath).Value;
        }

        var proxyUri = GetOptionValue(args, "--proxy")
            ?? config?.ProxyUri
            ?? new AppProxyConfig().ProxyUri;
        var outputDirectory = GetOptionValue(args, "--output")
            ?? GetOptionValue(args, "--out");
        var appId = GetOptionValue(args, "--app") ?? "all";
        var scripts = new List<string>();

        if (appId.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            scripts.AddRange(ProxyLauncherScriptGenerator.CreateScriptsForInstalledApps(proxyUri, outputDirectory));
        }
        else
        {
            var app = TargetApplicationCatalog.FindInstalledById(appId)
                ?? throw new CommandLineException($"未找到应用: {appId}。可用值: all, codex, cursor, claude, antigravity。");
            scripts.Add(ProxyLauncherScriptGenerator.CreateScript(app.ExecutablePath, app.Name, proxyUri, outputDirectory));
        }

        var targetPath = GetOptionValue(args, "--target") ?? config?.TargetPath;
        if (!string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath))
        {
            var fullTargetPath = Path.GetFullPath(targetPath);
            var alreadyCreated = scripts.Any(script => Path.GetFileNameWithoutExtension(script)
                .StartsWith(TargetApplicationCatalog.GuessNameFromPath(fullTargetPath), StringComparison.OrdinalIgnoreCase));
            if (!alreadyCreated)
            {
                var displayName = GetOptionValue(args, "--name") ?? TargetApplicationCatalog.GuessNameFromPath(fullTargetPath);
                scripts.Add(ProxyLauncherScriptGenerator.CreateScript(fullTargetPath, displayName, proxyUri, outputDirectory));
            }
        }

        if (scripts.Count == 0)
        {
            throw new CommandLineException("没有找到 Codex、Cursor、Claude 或 Antigravity。也可以用 --target 指定 exe。");
        }

        Console.WriteLine("已生成代理启动脚本:");
        foreach (var script in scripts)
        {
            Console.WriteLine($"  {script}");
        }

        return 0;
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
            EasyProxy - Electron/Chromium 应用代理启动器

            用法:
              EasyProxy init [--config app-proxy.json]
              EasyProxy check --config app-proxy.json
              EasyProxy run --config app-proxy.json
              EasyProxy scripts [--config app-proxy.json] [--app all|codex|cursor|claude|antigravity|antigravity-ide] [--out DIR]
              EasyProxy ui [--config app-proxy.json]

            说明:
              EasyProxy 会启动目标 Electron/Chromium 应用，并自动注入 --disable-quic、
              --proxy-server、--proxy-bypass-list 以及 HTTP_PROXY/HTTPS_PROXY/ALL_PROXY
              等环境变量。它面向 Codex、Cursor、Claude、Antigravity 这类桌面 AI 应用，不再提供
              Transparent/WinDivert 透明拦截入口。
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
