using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace AppProxyHelper;

/// <summary>Loopback-only Steam HTTPS reverse proxy; no CONNECT or arbitrary-host forwarding.</summary>
public sealed class SteamProxyServer : IAsyncDisposable
{
    private readonly SteamProxySettings _settings;
    private readonly SteamCertificateAuthority _authority;
    private readonly Action<string> _log;
    private readonly SteamDnsResolver _dns;
    private readonly Dictionary<string, (SteamProxyRule Rule, HttpMessageInvoker Client)> _routes;
    private WebApplication? _app;

    public SteamProxyServer(SteamProxySettings settings, SteamCertificateAuthority authority,
        Action<string>? log = null, Func<SteamProxyRule, HttpMessageInvoker>? clientFactory = null)
    {
        settings.Validate();
        _settings = settings;
        _authority = authority;
        _log = log ?? (_ => { });
        _dns = new SteamDnsResolver(settings);
        _routes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in settings.Rules)
            _routes.Add(SteamDomainCatalog.Normalize(rule.Domain), (rule, clientFactory?.Invoke(rule) ?? CreateClient(rule)));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null) throw new InvalidOperationException("Steam 代理已启动。");
        _authority.LoadOrCreate();
        // Do not read ASPNETCORE_URLS, appsettings or command line from the GUI's environment.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpForwarder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = null;
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.MinResponseDataRate = null;
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            options.Listen(IPAddress.Loopback, _settings.HttpPort, listen => listen.Protocols = HttpProtocols.Http1);
            options.Listen(IPAddress.Loopback, _settings.HttpsPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificateSelector = (_, name) => name is not null && _routes.ContainsKey(SteamDomainCatalog.Normalize(name))
                        ? _authority.GetServerCertificate(name)
                        : throw new AuthenticationException("Steam 代理拒绝未配置的 TLS SNI。");
                });
            });
        });
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(ForwardAsync);
        try
        {
            await app.StartAsync(cancellationToken);
            _app = app;
            _log($"Steam 代理监听 127.0.0.1:{_settings.HttpPort}/{_settings.HttpsPort}，域名 {_routes.Count} 个。");
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private async Task ForwardAsync(HttpContext context)
    {
        var host = SteamDomainCatalog.Normalize(context.Request.Host.Host);
        if (!_routes.TryGetValue(host, out var route) || HttpMethods.IsConnect(context.Request.Method)
            || SteamWebRequestPolicy.IsDownloadPath(context.Request.Path.Value))
        {
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }
        if (!context.Request.IsHttps)
        {
            var port = _settings.HttpsPort == 443 ? "" : $":{_settings.HttpsPort}";
            context.Response.Redirect($"https://{host}{port}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}");
            return;
        }
        var sni = context.Features.Get<ITlsHandshakeFeature>()?.HostName;
        if (!string.Equals(host, sni is null ? null : SteamDomainCatalog.Normalize(sni), StringComparison.OrdinalIgnoreCase)
            || context.Request.Host.Port is int portValue && portValue != 443 && portValue != _settings.HttpsPort)
        {
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }
        // Direct connections already return a TLS stream from ConnectCallback. Using http here
        // avoids a second TLS handshake; the transport to the origin is always authenticated TLS.
        var scheme = string.IsNullOrWhiteSpace(_settings.UpstreamProxyUri) ? "http" : "https";
        var destination = $"{scheme}://{route.Rule.ForwardHost ?? host}:{route.Rule.Port}/";
        var forwarder = context.RequestServices.GetRequiredService<IHttpForwarder>();
        var error = await forwarder.SendAsync(context, destination, route.Client, new ForwarderRequestConfig
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            ActivityTimeout = TimeSpan.FromMinutes(5)
        }, new SteamTransformer(route.Rule, host));
        if (error != ForwarderError.None)
        {
            var exception = context.Features.Get<IForwarderErrorFeature>()?.Exception?.GetBaseException();
            var detail = exception is null ? "" : $" {exception.GetType().Name}: {exception.Message}";
            if (!string.IsNullOrWhiteSpace(_settings.UpstreamProxyUri))
                detail = detail.Replace(_settings.UpstreamProxyUri, UriFormatter.Sanitize(_settings.UpstreamProxyUri), StringComparison.OrdinalIgnoreCase);
            _log($"Steam {context.Request.Method} {host} 转发失败: {error}.{detail}");
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
        else _log($"Steam {context.Request.Method} {host} → {context.Response.StatusCode}");
    }

    private HttpMessageInvoker CreateClient(SteamProxyRule rule)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(12), PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            RequestHeaderEncodingSelector = (_, _) => System.Text.Encoding.UTF8,
            ResponseHeaderEncodingSelector = (_, _) => System.Text.Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(_settings.UpstreamProxyUri))
        {
            var endpoint = ProxyEndpoint.Parse(_settings.UpstreamProxyUri);
            var uri = new Uri(endpoint.ToUriString(endpoint.IsSocks ? "socks5" : null));
            var proxy = new WebProxy(new UriBuilder(uri) { UserName = "", Password = "" }.Uri) { BypassProxyOnLocal = false };
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "");
            }
            handler.UseProxy = true;
            handler.Proxy = proxy;
        }
        else
        {
            handler.UseProxy = false;
            handler.ConnectCallback = (_, token) => ConnectDirectAsync(rule, token);
        }
        return new HttpMessageInvoker(handler, disposeHandler: true);
    }

    private async ValueTask<Stream> ConnectDirectAsync(SteamProxyRule rule, CancellationToken token)
    {
        var addresses = rule.Addresses.Length > 0 ? rule.Addresses.Select(IPAddress.Parse).ToArray()
            : await _dns.ResolveAsync(rule.ForwardHost ?? rule.Domain, token);
        var failures = new List<Exception>();
        foreach (var address in addresses)
        {
            if (!SteamDnsResolver.IsPublicAddress(address)) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            SslStream? tls = null;
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, rule.Port), timeout.Token);
                tls = new SslStream(new NetworkStream(socket, ownsSocket: true), false,
                    (_, cert, _, errors) => ValidateOriginCertificate(rule.Domain, cert, errors));
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = rule.TlsServerName ?? rule.Domain,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    CertificateRevocationCheckMode = X509RevocationMode.Online
                }, timeout.Token);
                return tls;
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException)
            {
                tls?.Dispose();
                socket.Dispose();
                token.ThrowIfCancellationRequested();
                failures.Add(ex);
            }
        }
        throw new IOException($"Steam 回源 {rule.Domain} 的所有 IP 建连失败。", new AggregateException(failures));
    }

    internal static bool ValidateOriginCertificate(string originalHost, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) != SslPolicyErrors.None) return false;
        using var cert = new X509Certificate2(certificate);
        // A fake SNI may differ, but the certificate must still authenticate the original Steam host.
        return cert.MatchesHostname(originalHost, allowWildcards: true, allowCommonName: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(stop.Token); }
            finally { await _app.DisposeAsync(); _app = null; }
        }
        foreach (var route in _routes.Values) route.Client.Dispose();
        _dns.Dispose();
    }

    private sealed class SteamTransformer(SteamProxyRule rule, string host) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(context, request, destinationPrefix, cancellationToken);
            request.Headers.Host = host;
            foreach (var header in ConnectionHeaders(context.Request.Headers.Connection))
            {
                request.Headers.Remove(header);
                request.Content?.Headers.Remove(header);
            }
            request.Headers.Remove("Proxy-Authorization");
            request.Headers.Remove("Forwarded");
            foreach (var header in request.Headers.Where(h => h.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)).Select(h => h.Key).ToArray())
                request.Headers.Remove(header);
            if (!string.IsNullOrEmpty(rule.UserAgent))
            {
                var agent = rule.UserAgent.Replace("${origin}", context.Request.Headers.UserAgent.ToString(), StringComparison.OrdinalIgnoreCase);
                request.Headers.Remove("User-Agent");
                request.Headers.TryAddWithoutValidation("User-Agent", agent);
            }
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext context, HttpResponseMessage? response, CancellationToken cancellationToken)
        {
            var forwardBody = await base.TransformResponseAsync(context, response, cancellationToken);
            if (response is not null)
                foreach (var header in ConnectionHeaders(response.Headers.Connection)) context.Response.Headers.Remove(header);
            return forwardBody;
        }

        private static IEnumerable<string> ConnectionHeaders(IEnumerable<string?> values) => values
            .Where(v => v is not null).SelectMany(v => v!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(v => !v.Equals("Upgrade", StringComparison.OrdinalIgnoreCase));
    }
}
