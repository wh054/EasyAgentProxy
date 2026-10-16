using System.Net;

namespace AppProxyHelper;

public static class SteamDomainCatalog
{
    public static IReadOnlyList<string> Domains { get; } = Array.AsReadOnly(new[]
    {
        "steamuserimages-a.akamaihd.net", "community.akamai.steamstatic.com",
        "avatars.akamai.steamstatic.com", "store.akamai.steamstatic.com",
        "community.steamstatic.com", "steamcommunity.com",
        "www.steamcommunity.com", "store.steampowered.com",
        "help.steampowered.com", "login.steampowered.com", "checkout.steampowered.com",
        "img.youtube.com"
    });

    public static string Normalize(string host) => host.TrimEnd('.').ToLowerInvariant();
    public static bool IsSupported(string? host) => host is not null && Domains.Contains(Normalize(host), StringComparer.OrdinalIgnoreCase);

    // Watt's update/download groups and shared endpoints are not web interception rules.
    internal static bool IsLegacyNonWebDomain(string? host) => host is not null && Normalize(host) is
        ("steamcdn-a.akamaihd.net" or "cdn.akamai.steamstatic.com" or
         "media.steampowered.com" or "api.steampowered.com");
}

public static class SteamWebRequestPolicy
{
    public static bool IsDownloadPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var segments = new List<string>();
        foreach (var segment in Uri.UnescapeDataString(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(segment);
        }
        // SteamPipe depot manifests/chunks must never be sent to the web upstream.
        return segments.Count > 0 && segments[0].Equals("depot", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SteamProxyRule
{
    public string Domain { get; set; } = "";
    public string? ForwardHost { get; set; }
    public string? TlsServerName { get; set; }
    public string? UserAgent { get; set; }
    public string[] Addresses { get; set; } = [];
    public int Port { get; set; } = 443;
}

public sealed class SteamProxySettings
{
    public string? UpstreamProxyUri { get; set; }
    public string? SessionSteamExecutablePath { get; set; }
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public string[] DohEndpoints { get; set; } = ["https://dns.alidns.com/resolve", "https://dns.google/resolve"];
    public List<SteamProxyRule> Rules { get; set; } = SteamDomainCatalog.Domains.Select(domain => new SteamProxyRule { Domain = domain }).ToList();

    internal void ApplyWebOnlyPolicy()
    {
        // Migrate only the known old broad rules. Unknown rules still fail validation;
        // do not rewrite the encrypted settings or silently enable additional domains.
        if (Rules is not null)
            Rules = Rules.Where(rule => rule is null || !SteamDomainCatalog.IsLegacyNonWebDomain(rule.Domain)).ToList();
    }

    public void Validate()
    {
        if (HttpPort is < 1 or > 65535 || HttpsPort is < 1 or > 65535 || HttpPort == HttpsPort)
            throw new InvalidOperationException("Steam HTTP/HTTPS 监听端口无效或重复。");
        if (!string.IsNullOrWhiteSpace(UpstreamProxyUri))
        {
            var proxy = ProxyEndpoint.Parse(UpstreamProxyUri);
            if (proxy.PathAndQuery.Length != 0) throw new InvalidOperationException("Steam 上游代理地址不能包含路径或查询。");
            if (IPAddress.TryParse(proxy.Host, out var address) && IPAddress.IsLoopback(address) && (proxy.Port == HttpPort || proxy.Port == HttpsPort))
                throw new InvalidOperationException("Steam 上游代理不能指向自身监听端口。");
            if (proxy.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && (proxy.Port == HttpPort || proxy.Port == HttpsPort))
                throw new InvalidOperationException("Steam 上游代理不能指向自身监听端口。");
            if (SteamDomainCatalog.IsSupported(proxy.Host)) throw new InvalidOperationException("上游代理主机不能是被接管的 Steam 域名。");
        }
        if (Rules is null || Rules.Count == 0 || Rules.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.Domain))
            || Rules.Select(r => SteamDomainCatalog.Normalize(r.Domain)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Rules.Count)
            throw new InvalidOperationException("Steam 规则不能为空或包含重复域名。");
        foreach (var rule in Rules)
        {
            if (!SteamDomainCatalog.IsSupported(rule.Domain)) throw new InvalidOperationException($"不支持的 Steam 域名: {rule.Domain}");
            if (!string.IsNullOrWhiteSpace(rule.ForwardHost) && !SteamDomainCatalog.IsSupported(rule.ForwardHost))
                throw new InvalidOperationException("Steam 网页规则不能回源到下载或其他非网页域名。");
            if (rule.Port is < 1 or > 65535) throw new InvalidOperationException("Steam 回源端口无效。");
            foreach (var host in new[] { rule.ForwardHost, rule.TlsServerName }.Where(h => !string.IsNullOrWhiteSpace(h)))
                if (Uri.CheckHostName(host!) != UriHostNameType.Dns) throw new InvalidOperationException("Steam 回源主机/SNI 必须为 DNS 名称。");
            if (rule.UserAgent?.Any(c => c is '\r' or '\n') == true) throw new InvalidOperationException("User-Agent 不能包含换行。");
            if (rule.Addresses is null || rule.Addresses.Any(a => !IPAddress.TryParse(a, out var ip) || !SteamDnsResolver.IsPublicAddress(ip)))
                throw new InvalidOperationException("Steam 回源 IP 必须为公网地址。");
            if (!string.IsNullOrWhiteSpace(UpstreamProxyUri) && (rule.Addresses.Length > 0 || !string.IsNullOrWhiteSpace(rule.TlsServerName)))
                throw new InvalidOperationException("自定义回源 IP/SNI 仅适用于 Steam 直连模式；上游代理模式由代理解析并保留原始 SNI。");
        }
        if (DohEndpoints is null || DohEndpoints.Length == 0 || DohEndpoints.Any(e => !Uri.TryCreate(e, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || SteamDomainCatalog.IsSupported(uri.Host)))
            throw new InvalidOperationException("DoH 需要独立 HTTPS 解析端点。");
    }
}
