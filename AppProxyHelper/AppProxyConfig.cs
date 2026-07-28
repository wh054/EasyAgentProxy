using System.Text.Json.Serialization;

namespace AppProxyHelper;

public sealed class AppProxyConfig
{
    public string Mode { get; set; } = "Environment";
    public string TargetPath { get; set; } = "";

    [JsonIgnore]
    public string[] TargetProcessNames { get; set; } = Array.Empty<string>();
    public string[] TargetArguments { get; set; } = Array.Empty<string>();
    public string? WorkingDirectory { get; set; }
    public string ProxyUri { get; set; } = "socks5://127.0.0.1:7890";
    public string LogDirectory { get; set; } = "logs";
    public string MinimumLogLevel { get; set; } = "Information";
    public bool CaptureChildOutput { get; set; } = true;
    public bool InjectProxyEnvironment { get; set; } = true;
    public bool WaitForExit { get; set; } = true;
    public bool RunDiagnosticsBeforeLaunch { get; set; } = true;
    public bool AbortLaunchWhenDiagnosticsFail { get; set; }
    public bool EnableCodexQqSkin { get; set; }
    public string[] NoProxy { get; set; } = { "localhost", "127.0.0.1", "::1" };
    public DiagnosticsConfig Diagnostics { get; set; } = new();

    [JsonIgnore]
    public TransparentInterceptionConfig Transparent { get; set; } = new();
}

public sealed class DiagnosticsConfig
{
    public int TcpConnectTimeoutMs { get; set; } = 3000;
    public bool TestProxyHandshake { get; set; } = true;
    public bool TestProxyConnect { get; set; }
    public bool TestProxyTlsHandshake { get; set; } = true;
    public string ConnectTestHost { get; set; } = "example.com";
    public int ConnectTestPort { get; set; } = 443;
}

public sealed class TransparentInterceptionConfig
{
    public string Provider { get; set; } = "WinDivert";
    public string DriverPath { get; set; } = "WinDivert.dll";
    public string RedirectListenAddress { get; set; } = "0.0.0.0";
    public int RedirectListenPort { get; set; } = 19080;
    public int WinDivertPriority { get; set; }
    public int FlowAssociationTimeoutMs { get; set; }
    public int ProxyLookupTimeoutMs { get; set; } = 3000;
    public int ProxyConnectTimeoutMs { get; set; } = 10000;
    public int ListenBacklog { get; set; } = 512;
    public int PacketBufferSize { get; set; } = 65535;
    public int QueueLength { get; set; } = 4096;
    public int QueueTimeMs { get; set; } = 2048;
    public int QueueSizeBytes { get; set; } = 4 * 1024 * 1024;
    public bool CaptureUdp { get; set; }
    public bool BlockQuicUdp443 { get; set; } = true;
    public bool EnableDomainSniffing { get; set; } = true;
    public int UdpIdleTimeoutMs { get; set; } = 60000;
    public bool TrackChildProcesses { get; set; } = true;
    public string[] ExcludedChildProcessNames { get; set; } =
    {
        "chrome.exe",
        "msedge.exe",
        "firefox.exe",
        "brave.exe",
        "opera.exe",
        "vivaldi.exe",
        "iexplore.exe"
    };
}

public sealed class LoadedConfig
{
    public LoadedConfig(AppProxyConfig value, string configPath)
    {
        Value = value;
        ConfigPath = Path.GetFullPath(configPath);
        BaseDirectory = Path.GetDirectoryName(ConfigPath) ?? Directory.GetCurrentDirectory();
    }

    public AppProxyConfig Value { get; }
    public string ConfigPath { get; }
    public string BaseDirectory { get; }
}
