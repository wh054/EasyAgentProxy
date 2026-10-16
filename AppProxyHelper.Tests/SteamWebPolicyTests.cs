using AppProxyHelper;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

internal static class SteamWebPolicyTests
{
    private static readonly string[] ExpectedWebDomains =
    [
        "steamuserimages-a.akamaihd.net", "community.akamai.steamstatic.com",
        "avatars.akamai.steamstatic.com", "store.akamai.steamstatic.com",
        "community.steamstatic.com", "steamcommunity.com", "www.steamcommunity.com",
        "store.steampowered.com", "help.steampowered.com", "login.steampowered.com",
        "checkout.steampowered.com", "img.youtube.com"
    ];

    private static readonly string[] RemovedLegacyDomains =
    [
        "steamcdn-a.akamaihd.net", "cdn.akamai.steamstatic.com",
        "media.steampowered.com", "api.steampowered.com"
    ];

    private static readonly string[] DownloadDomainExamples =
    [
        "steamcontent.com", "cache1.steamcontent.com", "cache1-hkg1.steamcontent.com",
        "depot.steamcontent.com", "content1.steampowered.com",
        "client-download.steampowered.com", "cdn.cloudflare.steamstatic.com"
    ];

    private static IEnumerable<string> ExcludedDomains => RemovedLegacyDomains.Concat(DownloadDomainExamples);

    public static IEnumerable<(string Name, Action Body)> Cases =>
    [
        ("Steam web-only defaults write only the twelve web hosts", DefaultHostsExcludeDownloads),
        ("Steam old sixteen-domain JSON migrates without changing proxy or web rules", LegacyJsonMigratesToWebRules),
        ("Steam web policy rejects unknown rules and download forwarding targets", UnknownRulesAndForwardTargetsAreRejected),
        ("Steam web-only CA rejects non-web domains before creating files", CertificateScopeFailsBeforeWrites),
        ("Steam web-only hosts reject non-web domains without file changes", HostsScopeFailsBeforeWrites),
        ("Steam download-path classification preserves ordinary web resources", DownloadPathsDoNotClassifyByExtension),
        ("Steam download requests never reach the injected origin", () => DownloadRequestsNeverReachOriginAsync().GetAwaiter().GetResult()),
        ("Steam sessions cannot reuse an obsolete web policy version", OldSessionPolicyCannotBeReused)
    ];

    private static void DefaultHostsExcludeDownloads()
    {
        EqualDomains(ExpectedWebDomains, SteamDomainCatalog.Domains, "explicit web domain catalog");
        var settings = new SteamProxySettings();
        settings.Validate();
        EqualDomains(ExpectedWebDomains, settings.Rules.Select(rule => rule.Domain), "default web rules");
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "hosts");
            var original = Encoding.UTF8.GetBytes("# original hosts\r\n127.0.0.1 localhost\r\n");
            File.WriteAllBytes(path, original);
            var hosts = new SteamHostsManager(path);
            hosts.Apply(settings.Rules.Select(rule => rule.Domain), createBackup: false);
            var text = File.ReadAllText(path);
            var managedDomains = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("127.0.0.1 ", StringComparison.Ordinal) && !line.EndsWith(" localhost", StringComparison.Ordinal))
                .Select(line => line["127.0.0.1 ".Length..].Trim());
            EqualDomains(ExpectedWebDomains, managedDomains, "managed hosts contain only the web allowlist");
            foreach (var domain in ExcludedDomains)
            {
                Equal(false, SteamDomainCatalog.IsSupported(domain), "excluded download/shared domain " + domain);
                Equal(false, text.Contains(domain, StringComparison.OrdinalIgnoreCase), "download/shared domain is not redirected by hosts " + domain);
            }
            hosts.Remove();
            Equal(true, original.SequenceEqual(File.ReadAllBytes(path)), "web-only hosts restore the original file");
        });
    }

    private static void LegacyJsonMigratesToWebRules()
    {
        var oldDomains = new[]
        {
            "steamcdn-a.akamaihd.net", "steamuserimages-a.akamaihd.net",
            "cdn.akamai.steamstatic.com", "community.akamai.steamstatic.com",
            "avatars.akamai.steamstatic.com", "store.akamai.steamstatic.com",
            "community.steamstatic.com", "media.steampowered.com", "steamcommunity.com",
            "www.steamcommunity.com", "store.steampowered.com", "api.steampowered.com",
            "help.steampowered.com", "login.steampowered.com", "checkout.steampowered.com", "img.youtube.com"
        };
        const string proxy = "socks5://127.0.0.1:7890";
        var json = JsonSerializer.Serialize(new
        {
            UpstreamProxyUri = proxy,
            Rules = oldDomains.Select(domain => new SteamProxyRule { Domain = domain, UserAgent = "web-policy-test-agent" }).ToArray()
        });
        var settings = JsonSerializer.Deserialize<SteamProxySettings>(json)!;
        Equal(16, settings.Rules.Count, "legacy fixture includes all original rules");
        settings.ApplyWebOnlyPolicy();
        settings.Validate();

        EqualDomains(ExpectedWebDomains, settings.Rules.Select(rule => rule.Domain), "migration removes only the four legacy shared domains");
        Equal(proxy, settings.UpstreamProxyUri, "migration keeps the saved upstream proxy");
        Equal(true, settings.Rules.All(rule => rule.UserAgent == "web-policy-test-agent"), "migration preserves surviving web rule settings");
        var migratedJson = JsonSerializer.Serialize(settings);
        settings.ApplyWebOnlyPolicy();
        Equal(migratedJson, JsonSerializer.Serialize(settings), "migration is idempotent");
    }

    private static void UnknownRulesAndForwardTargetsAreRejected()
    {
        foreach (var domain in DownloadDomainExamples.Append("unexpected.example"))
        {
            var settings = new SteamProxySettings();
            settings.Rules.Add(new SteamProxyRule { Domain = domain });
            settings.ApplyWebOnlyPolicy();
            Throws<InvalidOperationException>(settings.Validate);
        }
        foreach (var domain in ExcludedDomains.Append("unexpected.example"))
        {
            var settings = new SteamProxySettings();
            settings.Rules.Single(rule => rule.Domain == "store.steampowered.com").ForwardHost = domain;
            settings.ApplyWebOnlyPolicy();
            Throws<InvalidOperationException>(settings.Validate);
        }
        var valid = new SteamProxySettings();
        valid.Rules.Single(rule => rule.Domain == "steamcommunity.com").ForwardHost = "www.steamcommunity.com";
        valid.Validate();
    }

    private static void CertificateScopeFailsBeforeWrites()
    {
        WithTempDirectory(directory =>
        {
            foreach (var domain in ExcludedDomains)
            {
                var path = Path.Combine(directory, "uncreated-ca");
                Throws<ArgumentException>(() =>
                {
                    using var rejected = new SteamCertificateAuthority(path, ["steamcommunity.com", domain]);
                });
                Equal(false, Directory.Exists(path), "invalid custom CA allowlist creates no directory or key");
                using var authority = new SteamCertificateAuthority(path);
                Throws<ArgumentException>(() => authority.GetServerCertificate(domain.ToUpperInvariant() + "."));
                Equal(false, Directory.Exists(path), "default CA rejects excluded names before generating an identity");
            }
        });
    }

    private static void HostsScopeFailsBeforeWrites()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "hosts");
            var original = Encoding.UTF8.GetBytes("# preserve this file\r\n127.0.0.1 localhost\r\n");
            File.WriteAllBytes(path, original);
            var hosts = new SteamHostsManager(path);
            foreach (var domain in ExcludedDomains)
            {
                Throws<ArgumentException>(() => hosts.Validate(["steamcommunity.com", domain]));
                Throws<ArgumentException>(() => hosts.Apply(["steamcommunity.com", domain]));
                Equal(true, original.SequenceEqual(File.ReadAllBytes(path)), "rejected hosts scope leaves original bytes untouched");
                Equal(1, Directory.GetFiles(directory).Length, "rejected hosts scope creates no backup, lock or temporary file");
            }
        });
    }

    private static void DownloadPathsDoNotClassifyByExtension()
    {
        foreach (var path in new[] { "/depot/123/chunk/abc", "/DEPOT/123/manifest/abc", "/depot/123/" })
            Equal(true, SteamWebRequestPolicy.IsDownloadPath(path), "SteamPipe path is excluded " + path);
        foreach (var path in new string?[] { null, "", "/", "/app/570/", "/images/banner.zip", "/download/manual.zip", "/depot-guide/", "/community/chunk-guide/" })
            Equal(false, SteamWebRequestPolicy.IsDownloadPath(path), "ordinary web path is retained " + path);
    }

    private static async Task DownloadRequestsNeverReachOriginAsync()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var authority = new SteamCertificateAuthority(Path.Combine(directory, "ca"));
            var root = authority.LoadOrCreate();
            var settings = CreateLocalSettings();
            var originRequests = 0;
            var preservedRange = false;
            await using var server = new SteamProxyServer(settings, authority, clientFactory: _ =>
                new HttpMessageInvoker(new CountingOriginHandler(request =>
                {
                    Interlocked.Increment(ref originRequests);
                    preservedRange = request.Headers.Range?.ToString() == "bytes=0-63";
                })));
            await server.StartAsync(timeout.Token);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
            foreach (var host in ExcludedDomains)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{settings.HttpPort}/app/570/");
                request.Headers.Host = host;
                using var response = await http.SendAsync(request, timeout.Token);
                Rejected(response.StatusCode, "excluded HTTP Host " + host);
                Equal(0, originRequests, "excluded Host reaches no origin");
                await RejectDownloadSniAsync(settings.HttpsPort, host, timeout.Token);
                Equal(0, originRequests, "excluded TLS SNI reaches no origin");
            }

            var mismatchStatus = await SendDownloadHostOverWebTlsAsync(settings.HttpsPort, root,
                "cache1.steamcontent.com", timeout.Token);
            Equal(true, mismatchStatus.StartsWith("HTTP/1.1 421", StringComparison.Ordinal), "a download Host cannot use an allowed web TLS connection");
            Equal(0, originRequests, "download Host over web SNI reaches no origin");

            using var https = CreateWebTlsClient(settings.HttpsPort, root);
            foreach (var path in new[] { "/depot/123/chunk/abc", "/DEPOT/123/chunk/abc", "/%64epot/123/chunk/abc" })
            {
                using var response = await https.GetAsync($"https://store.steampowered.com:{settings.HttpsPort}" + path, timeout.Token);
                Rejected(response.StatusCode, "depot path over allowed web TLS " + path);
                Equal(0, originRequests, "game chunk path reaches no origin");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{settings.HttpPort}/depot/123/chunk/abc"))
            {
                request.Headers.Host = "store.steampowered.com";
                using var response = await http.SendAsync(request, timeout.Token);
                Rejected(response.StatusCode, "depot path rejected before HTTP redirect");
                Equal(0, originRequests, "HTTP game chunk path reaches no origin");
            }

            using var web = new HttpRequestMessage(HttpMethod.Get,
                $"https://store.steampowered.com:{settings.HttpsPort}/images/banner.zip?topic=chunk");
            web.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 63);
            using var webResponse = await https.SendAsync(web, timeout.Token);
            Equal(HttpStatusCode.OK, webResponse.StatusCode, "ordinary web request still forwards");
            Equal("web-policy-origin", await webResponse.Content.ReadAsStringAsync(timeout.Token), "positive control comes from the injected origin");
            Equal(1, originRequests, "only the ordinary web request reaches the origin");
            Equal(true, preservedRange, "a Range header does not turn ordinary web resources into a game download");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void OldSessionPolicyCannotBeReused()
    {
        var legacy = new SteamProxyStatus(true, 123, null, true, true, "test-log", "test-data", IsSession: true);
        Equal(0, legacy.WebPolicyVersion, "a status without a policy version represents the legacy worker");
        Equal(false, SteamProxyProcessController.CanReuseSessionPolicy(legacy), "legacy worker cannot be reused");
        var current = legacy with { WebPolicyVersion = SteamProxyProcessController.CurrentWebPolicyVersion };
        Equal(true, SteamProxyProcessController.CanReuseSessionPolicy(current), "matching web policy version can be reused");
        Equal(false, SteamProxyProcessController.CanReuseSessionPolicy(current with { WebPolicyVersion = current.WebPolicyVersion + 1 }), "different policy version cannot be reused");
    }

    private static async Task RejectDownloadSniAsync(int port, string host, CancellationToken token)
    {
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, port, token);
        using var tls = new SslStream(connection.GetStream(), false, (_, _, _, _) => true);
        try { await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, token); }
        catch (AuthenticationException) { return; }
        catch (IOException) { return; }
        throw new InvalidOperationException("TLS accepted excluded download/shared SNI " + host);
    }

    private static async Task<string> SendDownloadHostOverWebTlsAsync(int port, X509Certificate2 root, string host, CancellationToken token)
    {
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, port, token);
        using var tls = new SslStream(connection.GetStream(), false, (_, certificate, _, errors) => ValidateWebCertificate(certificate, errors, root));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "store.steampowered.com" }, token);
        await tls.WriteAsync(Encoding.ASCII.GetBytes("GET /app/570/ HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n"), token);
        using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, leaveOpen: true);
        return await reader.ReadLineAsync(token) ?? "";
    }

    private static HttpClient CreateWebTlsClient(int port, X509Certificate2 root) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        ConnectCallback = async (_, token) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port, token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) => ValidateWebCertificate(certificate, errors, root)
        }
    }) { Timeout = TimeSpan.FromSeconds(10), DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };

    private static bool ValidateWebCertificate(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2 root)
    {
        if (certificate is not X509Certificate2 leaf || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        return chain.Build(leaf);
    }

    private sealed class CountingOriginHandler(Action<HttpRequestMessage> received) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            received(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("web-policy-origin") });
        }
    }

    private static SteamProxySettings CreateLocalSettings()
    {
        var http = AllocatePort();
        var https = AllocatePort();
        while (https == http) https = AllocatePort();
        return new() { HttpPort = http, HttpsPort = https };
    }

    private static int AllocatePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "EasyProxySteamWebPolicyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WithTempDirectory(Action<string> body)
    {
        var path = CreateTempDirectory();
        try { body(path); }
        finally { Directory.Delete(path, recursive: true); }
    }

    private static void Rejected(HttpStatusCode status, string label) => Equal(true, (int)status is >= 400 and < 500, label);

    private static void EqualDomains(IEnumerable<string> expected, IEnumerable<string> actual, string label)
        => Equal(true, expected.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal), StringComparer.Ordinal), label);

    private static void Throws<T>(Action body) where T : Exception
    {
        try { body(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'");
    }
}
