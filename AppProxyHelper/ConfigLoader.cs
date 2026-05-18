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
            Mode = "Environment",
            TargetPath = @"C:\Path\To\App.exe",
            TargetArguments = BuildDefaultChromiumProxyArguments("socks5://127.0.0.1:7890"),
            WorkingDirectory = null,
            ProxyUri = "socks5://127.0.0.1:7890",
            LogDirectory = "logs",
            Diagnostics = new DiagnosticsConfig
            {
                TestProxyHandshake = true,
                TestProxyConnect = false,
                TestProxyTlsHandshake = true,
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
        var fullPath = Path.GetFullPath(configPath);
        Normalize(config, Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
        Validate(config);

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    private static void Validate(AppProxyConfig config)
    {
        if (!config.Mode.Equals("Environment", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("mode 只支持 Environment；Transparent 已移除。");
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
    }

    private static void Normalize(AppProxyConfig config, string baseDirectory)
    {
        config.Mode = "Environment";
        config.TargetProcessNames = Array.Empty<string>();

        config.TargetArguments = EnsureChromiumProxyArguments(config.TargetArguments, config.ProxyUri);
    }

    internal static string[] BuildDefaultChromiumProxyArguments(string proxyUri)
    {
        return new[]
        {
            "--disable-quic",
            $"--proxy-server={proxyUri}",
            "--proxy-bypass-list=localhost;127.0.0.1;::1;<local>"
        };
    }

    private static string[] EnsureChromiumProxyArguments(string[]? arguments, string proxyUri)
    {
        var extras = (arguments ?? Array.Empty<string>())
            .Where(static argument => !string.IsNullOrWhiteSpace(argument))
            .Where(static argument => !argument.Equals("--disable-quic", StringComparison.OrdinalIgnoreCase))
            .Where(static argument => !argument.StartsWith("--proxy-server=", StringComparison.OrdinalIgnoreCase))
            .Where(static argument => !argument.StartsWith("--proxy-bypass-list=", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return BuildDefaultChromiumProxyArguments(proxyUri)
            .Concat(extras)
            .ToArray();
    }
}
