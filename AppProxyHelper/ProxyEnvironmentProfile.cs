using System.Net;

namespace AppProxyHelper;

internal sealed class ProxyEnvironmentProfile
{
    private ProxyEnvironmentProfile(
        ProxyEndpoint endpoint,
        string httpProxyUri,
        string allProxyUri,
        string noProxyValue)
    {
        Endpoint = endpoint;
        ChromiumProxyUri = endpoint.ToUriString();
        HttpProxyUri = httpProxyUri;
        AllProxyUri = allProxyUri;
        NoProxyValue = noProxyValue;
    }

    public ProxyEndpoint Endpoint { get; }
    public string ChromiumProxyUri { get; }
    public string HttpProxyUri { get; }
    public string AllProxyUri { get; }
    public string NoProxyValue { get; }

    public static ProxyEnvironmentProfile Create(string proxyUri, IEnumerable<string>? noProxy = null)
    {
        return Create(ProxyEndpoint.Parse(proxyUri), noProxy);
    }

    public static ProxyEnvironmentProfile Create(ProxyEndpoint endpoint, IEnumerable<string>? noProxy = null)
    {
        var noProxyValue = string.Join(
            ",",
            (noProxy ?? new[] { "localhost", "127.0.0.1", "::1" })
                .Where(static value => !string.IsNullOrWhiteSpace(value)));

        return new ProxyEnvironmentProfile(
            endpoint,
            BuildHttpCompatibleProxyUri(endpoint),
            endpoint.ToUriString(),
            noProxyValue);
    }

    public IEnumerable<KeyValuePair<string, string>> GetEnvironmentVariables()
    {
        yield return new KeyValuePair<string, string>("HTTP_PROXY", HttpProxyUri);
        yield return new KeyValuePair<string, string>("HTTPS_PROXY", HttpProxyUri);
        yield return new KeyValuePair<string, string>("ALL_PROXY", AllProxyUri);
        yield return new KeyValuePair<string, string>("http_proxy", HttpProxyUri);
        yield return new KeyValuePair<string, string>("https_proxy", HttpProxyUri);
        yield return new KeyValuePair<string, string>("all_proxy", AllProxyUri);
        yield return new KeyValuePair<string, string>("GRPC_PROXY", HttpProxyUri);
        yield return new KeyValuePair<string, string>("grpc_proxy", HttpProxyUri);

        if (!string.IsNullOrWhiteSpace(NoProxyValue))
        {
            yield return new KeyValuePair<string, string>("NO_PROXY", NoProxyValue);
            yield return new KeyValuePair<string, string>("no_proxy", NoProxyValue);
            yield return new KeyValuePair<string, string>("NO_GRPC_PROXY", NoProxyValue);
            yield return new KeyValuePair<string, string>("no_grpc_proxy", NoProxyValue);
        }

        if (TryGetCloudSdkProxyValues(out var type, out var address, out var port))
        {
            yield return new KeyValuePair<string, string>("CLOUDSDK_PROXY_TYPE", type);
            yield return new KeyValuePair<string, string>("CLOUDSDK_PROXY_ADDRESS", address);
            yield return new KeyValuePair<string, string>("CLOUDSDK_PROXY_PORT", port);
        }
    }

    public IEnumerable<KeyValuePair<string, string>> GetHttpProxyEnvironmentVariables()
    {
        yield return new KeyValuePair<string, string>("HTTP_PROXY", HttpProxyUri);
        yield return new KeyValuePair<string, string>("HTTPS_PROXY", HttpProxyUri);
        yield return new KeyValuePair<string, string>("http_proxy", HttpProxyUri);
        yield return new KeyValuePair<string, string>("https_proxy", HttpProxyUri);
        yield return new KeyValuePair<string, string>("GRPC_PROXY", HttpProxyUri);
        yield return new KeyValuePair<string, string>("grpc_proxy", HttpProxyUri);

        if (!string.IsNullOrWhiteSpace(NoProxyValue))
        {
            yield return new KeyValuePair<string, string>("NO_PROXY", NoProxyValue);
            yield return new KeyValuePair<string, string>("no_proxy", NoProxyValue);
            yield return new KeyValuePair<string, string>("NO_GRPC_PROXY", NoProxyValue);
            yield return new KeyValuePair<string, string>("no_grpc_proxy", NoProxyValue);
        }
    }

    private bool TryGetCloudSdkProxyValues(
        out string type,
        out string address,
        out string port)
    {
        type = "";
        address = "";
        port = "";

        if (!Uri.TryCreate(HttpProxyUri, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        type = uri.Scheme;
        address = uri.Host;
        port = uri.Port.ToString();
        return true;
    }

    private static string BuildHttpCompatibleProxyUri(ProxyEndpoint endpoint)
    {
        if (endpoint.IsSocks && IsLoopbackHost(endpoint.Host))
        {
            return endpoint.ToUriString("http");
        }

        return endpoint.ToUriString();
    }

    private static bool IsLoopbackHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.Ordinal)
            || host.Equals("::1", StringComparison.Ordinal)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
