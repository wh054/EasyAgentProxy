namespace AppProxyHelper;

public sealed class ProxyEndpoint
{
    private ProxyEndpoint(Uri uri, int port)
    {
        Scheme = uri.Scheme.ToLowerInvariant();
        Host = uri.Host;
        Port = port;
        UserInfo = uri.UserInfo;
        PathAndQuery = uri.PathAndQuery == "/" ? "" : uri.PathAndQuery;
    }

    public string Scheme { get; }
    public string Host { get; }
    public int Port { get; }
    public string UserInfo { get; }
    public string PathAndQuery { get; }
    public bool IsSocks => Scheme is "socks" or "socks5";
    public bool IsHttp => Scheme is "http" or "https";

    public static ProxyEndpoint Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"proxyUri 不是有效 URI: {value}");
        }

        if (!IsSupportedScheme(uri.Scheme))
        {
            throw new InvalidOperationException($"不支持的代理协议: {uri.Scheme}");
        }

        var port = uri.IsDefaultPort || uri.Port <= 0 ? DefaultPort(uri.Scheme) : uri.Port;
        return new ProxyEndpoint(uri, port);
    }

    public static bool IsSupportedScheme(string scheme)
    {
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("socks", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase);
    }

    public string ToUriString()
    {
        var host = Host.Contains(":", StringComparison.Ordinal) && !Host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{Host}]"
            : Host;
        var authority = string.IsNullOrWhiteSpace(UserInfo)
            ? $"{host}:{Port}"
            : $"{UserInfo}@{host}:{Port}";

        return $"{Scheme}://{authority}{PathAndQuery}";
    }

    private static int DefaultPort(string scheme)
    {
        return scheme.ToLowerInvariant() switch
        {
            "http" => 8080,
            "https" => 443,
            "socks" or "socks5" => 1080,
            _ => throw new InvalidOperationException($"不支持的代理协议: {scheme}")
        };
    }
}

public static class UriFormatter
{
    public static string Sanitize(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
        {
            return value;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = "***",
            Password = "***"
        };

        return builder.Uri.ToString();
    }
}
