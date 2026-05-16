using System.Text.Json;

namespace AppProxyHelper;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public static LoadedConfig Load(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("配置文件不存在。", fullPath);
        }

        var json = File.ReadAllText(fullPath);
        var config = JsonSerializer.Deserialize<AppProxyConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException("配置文件为空或格式无效。");

        Normalize(config, Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
        Validate(config);
        return new LoadedConfig(config, fullPath);
    }

    public static void WriteExample(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        if (File.Exists(fullPath))
        {
            throw new IOException($"配置文件已存在: {fullPath}");
        }

        var example = new AppProxyConfig
        {
            Mode = "Transparent",
            TargetPath = @"C:\Path\To\App.exe",
            TargetProcessNames = Array.Empty<string>(),
            TargetArguments = new[] { "--example-arg" },
            WorkingDirectory = null,
            ProxyUri = "socks5://127.0.0.1:7890",
            LogDirectory = "logs",
            Diagnostics = new DiagnosticsConfig
            {
                TestProxyHandshake = true,
                TestProxyConnect = false,
                ConnectTestHost = "example.com",
                ConnectTestPort = 443
            }
        };

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, JsonSerializer.Serialize(example, JsonOptions));
    }

    public static void Save(string configPath, AppProxyConfig config)
    {
        Validate(config);

        var fullPath = Path.GetFullPath(configPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    private static void Validate(AppProxyConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Mode))
        {
            throw new InvalidOperationException("mode 不能为空。");
        }

        if (!config.Mode.Equals("Environment", StringComparison.OrdinalIgnoreCase)
            && !config.Mode.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("mode 只支持 Environment 或 Transparent。");
        }

        if (string.IsNullOrWhiteSpace(config.ProxyUri))
        {
            throw new InvalidOperationException("proxyUri 不能为空。");
        }

        var uri = ProxyEndpoint.Parse(config.ProxyUri);
        if (!ProxyEndpoint.IsSupportedScheme(uri.Scheme))
        {
            throw new InvalidOperationException("proxyUri 只支持 http、https、socks 或 socks5。");
        }

        if (config.Mode.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            ValidateTransparent(config.Transparent, uri);
        }
    }

    private static void Normalize(AppProxyConfig config, string baseDirectory)
    {
        config.TargetProcessNames = NormalizeProcessNames(
            config.TargetProcessNames,
            Array.Empty<string>());

        config.Transparent.ExcludedChildProcessNames = NormalizeProcessNames(
            config.Transparent.ExcludedChildProcessNames,
            new TransparentInterceptionConfig().ExcludedChildProcessNames);

        if (config.Transparent.FlowAssociationTimeoutMs == 750)
        {
            config.Transparent.FlowAssociationTimeoutMs = 0;
        }

        if (config.Transparent.DriverPath.Equals("drivers/WinDivert.dll", StringComparison.OrdinalIgnoreCase))
        {
            var configured = PathResolver.Resolve(config.Transparent.DriverPath, baseDirectory);
            var rootDriver = Path.Combine(baseDirectory, "WinDivert.dll");
            if (!File.Exists(configured) && File.Exists(rootDriver))
            {
                config.Transparent.DriverPath = "WinDivert.dll";
            }
        }

        if (config.Transparent.RedirectListenAddress.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            config.Transparent.RedirectListenAddress = "0.0.0.0";
        }

        if (config.Mode.Equals("Transparent", StringComparison.OrdinalIgnoreCase)
            && config.TargetArguments.Length == 0
            && IsChromiumShellTarget(config.TargetPath))
        {
            config.TargetArguments = new[] { "--disable-quic" };
        }
    }

    private static string[] NormalizeProcessNames(string[]? names, string[] fallback)
    {
        var source = names is { Length: > 0 } ? names : fallback;
        return source
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => Path.GetFileName(name.Trim()))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsChromiumShellTarget(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(targetPath);
        return fileName.Equals("Codex", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Cursor", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Antigravity", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateTransparent(TransparentInterceptionConfig config, ProxyEndpoint proxy)
    {
        if (!config.Provider.Equals("WinDivert", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("transparent.provider 当前只支持 WinDivert。");
        }

        if (string.IsNullOrWhiteSpace(config.DriverPath))
        {
            throw new InvalidOperationException("transparent.driverPath 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(config.RedirectListenAddress))
        {
            throw new InvalidOperationException("transparent.redirectListenAddress 不能为空。");
        }

        if (config.RedirectListenPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("transparent.redirectListenPort 必须在 1-65535 范围内。");
        }

        if (config.PacketBufferSize is < 1500 or > 1024 * 1024)
        {
            throw new InvalidOperationException("transparent.packetBufferSize 必须在 1500 到 1048576 范围内。");
        }

        if (config.FlowAssociationTimeoutMs is < 0 or > 60000)
        {
            throw new InvalidOperationException("transparent.flowAssociationTimeoutMs 必须在 0 到 60000 范围内。");
        }

        if (config.CaptureUdp && !proxy.IsSocks)
        {
            throw new InvalidOperationException("transparent.captureUdp=true 时，proxyUri 必须使用 socks:// 或 socks5://。");
        }

        if (config.UdpIdleTimeoutMs is < 1000 or > 24 * 60 * 60 * 1000)
        {
            throw new InvalidOperationException("transparent.udpIdleTimeoutMs 必须在 1000 到 86400000 范围内。");
        }
    }
}
