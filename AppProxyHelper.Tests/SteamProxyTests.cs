using AppProxyHelper;
using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

internal static class SteamProxyTests
{
    public static IEnumerable<(string Name, Action Body)> Cases =>
    [
        ("Steam hosts lifecycle preserves original bytes and is idempotent", HostsLifecyclePreservesOriginalBytes),
        ("Steam session hosts leaves preexisting backup untouched and no temporary locks", SessionHostsPreservesBackupAndLeavesNoLocks),
        ("Steam hosts removal preserves later user additions", HostsRemovalPreservesLaterAdditions),
        ("Steam hosts rejects conflicting aliases without changing the file", HostsRejectsConflictingAliases),
        ("Steam hosts rejects malformed or repeated managed markers", HostsRejectsMalformedMarkers),
        ("Steam CA issues a valid scoped leaf and persists its identity", CertificateAuthorityIssuesScopedLeaf),
        ("Steam CA recovers a missing public identity without changing local files", CertificateAuthorityRecoversMissingPublicIdentity),
        ("Steam CA recovers expired trust identities and rejects unrelated protected certificates", CertificateAuthorityRecoversExpiredTrustIdentity),
        ("Steam CA rejects unrelated and malformed certificate names", CertificateAuthorityRejectsUnknownNames),
        ("Steam origin validation keeps both certificate-chain and original-domain checks", OriginValidationRejectsInvalidCertificates),
        ("Steam proxy settings reject local destinations and proxy loops", SettingsRejectLocalDestinationsAndLoops),
        ("Steam initial status inspection does not create a CA or data directory", InitialStatusInspectionHasNoFiles),
        ("Steam resolver rejects addresses that can loop back into local networks", ResolverRejectsLocalAddresses),
        ("Steam HTTP listener redirects only supported hosts", () => HttpListenerRedirectsOnlySupportedHostsAsync().GetAwaiter().GetResult()),
        ("Steam TLS reverse proxy forwards bodies and preserves upstream responses", () => TlsReverseProxyForwardsAsync().GetAwaiter().GetResult()),
        ("Steam TLS reverse proxy relays a WebSocket upgrade and text frame", () => TlsReverseProxyRelaysWebSocketAsync().GetAwaiter().GetResult()),
        ("Steam HTTP CONNECT upstream receives the Steam host and rejects an untrusted origin", () => UpstreamProxyRejectsUntrustedOriginAsync(socks: false).GetAwaiter().GetResult()),
        ("Steam SOCKS5 upstream receives the Steam host and rejects an untrusted origin", () => UpstreamProxyRejectsUntrustedOriginAsync(socks: true).GetAwaiter().GetResult()),
        ("Steam TLS listener rejects an unknown SNI", () => TlsListenerRejectsUnknownSniAsync().GetAwaiter().GetResult())
    ];

    private static void HostsLifecyclePreservesOriginalBytes()
    {
        WithTempDirectory(directory =>
        {
            var hostsPath = Path.Combine(directory, "hosts");
            var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
                "# 用户注释\r\n127.0.0.1 localhost\r\n10.20.30.40 intranet.example # keep spacing  \r\n# steamcommunity.com is only a comment")).ToArray();
            File.WriteAllBytes(hostsPath, original);
            var hosts = new SteamHostsManager(hostsPath);

            hosts.Apply(["steamcommunity.com", "store.steampowered.com"]);
            Equal(true, hosts.HasManagedEntries, "managed section exists");
            Equal(true, original.SequenceEqual(File.ReadAllBytes(hosts.BackupPath)), "backup preserves all original bytes");
            var applied = File.ReadAllBytes(hostsPath);
            hosts.Apply(["steamcommunity.com", "store.steampowered.com"]);
            Equal(true, applied.SequenceEqual(File.ReadAllBytes(hostsPath)), "repeated apply is idempotent");
            Equal(true, original.SequenceEqual(File.ReadAllBytes(hosts.BackupPath)), "repeated apply keeps the original backup");

            hosts.Remove();
            Equal(false, hosts.HasManagedEntries, "managed section removed");
            Equal(true, original.SequenceEqual(File.ReadAllBytes(hostsPath)), "original bytes restored");
            hosts.Remove();
            Equal(true, original.SequenceEqual(File.ReadAllBytes(hostsPath)), "repeated removal preserves bytes");
        });
    }

    private static void HostsRemovalPreservesLaterAdditions()
    {
        WithTempDirectory(directory =>
        {
            var hostsPath = Path.Combine(directory, "hosts");
            const string original = "# existing\n127.0.0.1 localhost\n";
            const string addition = "192.0.2.24 added-after-start.example # user-owned\n";
            File.WriteAllText(hostsPath, original, new UTF8Encoding(false));
            var hosts = new SteamHostsManager(hostsPath);
            hosts.Apply(["steamcommunity.com"]);
            File.AppendAllText(hostsPath, addition, new UTF8Encoding(false));

            hosts.Remove();

            Equal(original + addition, File.ReadAllText(hostsPath), "post-start changes preserved");
        });
    }

    private static void SessionHostsPreservesBackupAndLeavesNoLocks()
    {
        WithTempDirectory(directory =>
        {
            var hostsPath = Path.Combine(directory, "hosts");
            var original = Encoding.UTF8.GetBytes("# user hosts\r\n127.0.0.1 localhost\r\n");
            var previousBackup = new byte[] { 0, 42, 255, 18, 19, 20 };
            File.WriteAllBytes(hostsPath, original);
            var hosts = new SteamHostsManager(hostsPath);
            File.WriteAllBytes(hosts.BackupPath, previousBackup);
            var timestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(hosts.BackupPath, timestamp);

            hosts.Apply(["steamcommunity.com"], createBackup: false);
            Equal(true, hosts.HasManagedEntries, "temporary session mapping installed");
            Equal(true, previousBackup.SequenceEqual(File.ReadAllBytes(hosts.BackupPath)), "session preserves prior backup bytes");
            Equal(timestamp, File.GetLastWriteTimeUtc(hosts.BackupPath), "session preserves prior backup timestamp");
            Equal(false, File.Exists(hostsPath + ".easyproxy-steam.lock"), "apply releases and removes its lock file");
            hosts.Remove();
            Equal(true, original.SequenceEqual(File.ReadAllBytes(hostsPath)), "temporary session restores original hosts bytes");
            Equal(true, previousBackup.SequenceEqual(File.ReadAllBytes(hosts.BackupPath)), "session removal preserves prior backup bytes");
            Equal(false, Directory.GetFiles(directory, "*.lock").Any(), "no temporary host lock remains");
            Equal(false, Directory.GetFiles(directory, "*.tmp").Any(), "no temporary replacement file remains");

            File.Delete(hosts.BackupPath);
            hosts.Apply(["steamcommunity.com"], createBackup: false);
            hosts.Remove();
            Equal(false, File.Exists(hosts.BackupPath), "temporary session does not create a new backup");
            Equal(true, original.SequenceEqual(File.ReadAllBytes(hostsPath)), "session without a prior backup also restores hosts");
            Equal(1, Directory.GetFiles(directory).Length, "only the original hosts file remains");
        });
    }

    private static void HostsRejectsConflictingAliases()
    {
        WithTempDirectory(directory =>
        {
            var hostsPath = Path.Combine(directory, "hosts");
            var original = Encoding.UTF8.GetBytes("127.0.0.1 localhost\r\n203.0.113.42 unrelated.example STEAMCOMMUNITY.COM # existing alias\r\n");
            File.WriteAllBytes(hostsPath, original);
            var hosts = new SteamHostsManager(hostsPath);

            Throws<InvalidOperationException>(() => hosts.Apply(["steamcommunity.com"]));

            Equal(true, original.SequenceEqual(File.ReadAllBytes(hostsPath)), "conflicting hosts file unchanged");
            Equal(false, hosts.HasManagedEntries, "no partial managed entries");
        });
    }

    private static void HostsRejectsMalformedMarkers()
    {
        WithTempDirectory(directory =>
        {
            var hostsPath = Path.Combine(directory, "hosts");
            var malformed = new[]
            {
                "# existing\n" + SteamHostsManager.StartMarker + "\n127.0.0.1 steamcommunity.com\n",
                "# existing\n" + SteamHostsManager.EndMarker + "\n",
                SteamHostsManager.StartMarker + "\n" + SteamHostsManager.StartMarker + "\n" + SteamHostsManager.EndMarker + "\n",
                SteamHostsManager.StartMarker + "\n" + SteamHostsManager.EndMarker + "\n" + SteamHostsManager.StartMarker + "\n" + SteamHostsManager.EndMarker + "\n"
            };
            foreach (var original in malformed)
            {
                File.WriteAllText(hostsPath, original, new UTF8Encoding(false));
                var hosts = new SteamHostsManager(hostsPath);
                Throws<InvalidDataException>(() => hosts.Apply(["steamcommunity.com"]));
                Equal(original, File.ReadAllText(hostsPath), "malformed file unchanged after apply");
                Throws<InvalidDataException>(hosts.Remove);
                Equal(original, File.ReadAllText(hostsPath), "malformed file unchanged after remove");
            }
        });
    }

    private static void CertificateAuthorityIssuesScopedLeaf()
    {
        WithTempDirectory(directory =>
        {
            string rootThumbprint;
            using (var authority = new SteamCertificateAuthority(directory))
            {
                var root = authority.LoadOrCreate();
                var leaf = authority.GetServerCertificate("steamcommunity.com");
                rootThumbprint = root.Thumbprint;
                Equal(true, root.HasPrivateKey, "root has a signing key");
                Equal(true, leaf.HasPrivateKey, "leaf has a TLS key");
                Equal(root.Subject, leaf.Issuer, "root issues the leaf");
                Equal(leaf.Thumbprint, authority.GetServerCertificate("STEAMCOMMUNITY.COM").Thumbprint, "domain cache is case insensitive");
                Equal("steamcommunity.com", string.Join(",", ReadDnsSubjectAlternativeNames(leaf)), "SAN restricted to the requested domain");

                var constraints = leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single();
                Equal(false, constraints.CertificateAuthority, "leaf cannot issue certificates");
                Equal(true, leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single()
                    .EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"), "leaf permits TLS server authentication");
                Equal(true, leaf.NotAfter <= root.NotAfter, "leaf expires before its root");

                using var publicRoot = X509CertificateLoader.LoadCertificate(root.Export(X509ContentType.Cert));
                using var publicLeaf = X509CertificateLoader.LoadCertificate(leaf.Export(X509ContentType.Cert));
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(publicRoot);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.DisableCertificateDownloads = true;
                Equal(true, chain.Build(publicLeaf), "leaf verifies using only the private test root: " + string.Join(",", chain.ChainStatus.Select(status => status.Status)));
                Equal(2, chain.ChainElements.Count, "leaf has exactly one issuing root");
            }

            using var reopened = new SteamCertificateAuthority(directory);
            Equal(rootThumbprint, reopened.LoadOrCreate().Thumbprint, "reopening retains the same root");
        });
    }

    private static void CertificateAuthorityRecoversMissingPublicIdentity()
    {
        WithTempDirectory(directory =>
        {
            string expectedThumbprint;
            using (var authority = new SteamCertificateAuthority(directory))
            {
                expectedThumbprint = authority.LoadOrCreate().Thumbprint;
                File.Delete(authority.RootCertificatePath);
            }

            using var reopened = new SteamCertificateAuthority(directory);
            var protectedBytes = File.ReadAllBytes(reopened.PrivateKeyPath);
            var keyTimestamp = File.GetLastWriteTimeUtc(reopened.PrivateKeyPath);
            var existingFiles = string.Join("\n", Directory.GetFiles(directory).Order(StringComparer.Ordinal));
            var originalAcl = new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorSddlForm(
                AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
            using var identity = ReadPublicTrustIdentity(reopened);
            Equal(expectedThumbprint, identity?.Thumbprint, "missing CER recovers the exact persisted CA identity");
            Equal(false, identity!.HasPrivateKey, "identity inspection returns a public-only certificate");
            Equal(false, File.Exists(reopened.RootCertificatePath), "identity inspection does not recreate the missing CER");
            Equal(true, protectedBytes.SequenceEqual(File.ReadAllBytes(reopened.PrivateKeyPath)), "identity inspection does not rewrite the DPAPI key");
            Equal(keyTimestamp, File.GetLastWriteTimeUtc(reopened.PrivateKeyPath), "identity inspection preserves the key timestamp");
            Equal(existingFiles, string.Join("\n", Directory.GetFiles(directory).Order(StringComparer.Ordinal)), "identity inspection creates no lock or temporary files");
            Equal(originalAcl, new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorSddlForm(
                AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group), "identity inspection does not change directory ACLs");

            File.Delete(reopened.PrivateKeyPath);
            using var absent = ReadPublicTrustIdentity(reopened);
            Equal<X509Certificate2?>(null, absent, "no identity is created when both certificate files are missing");
        });
    }

    private static void CertificateAuthorityRecoversExpiredTrustIdentity()
    {
        WithTempDirectory(directory =>
        {
            using var authority = new SteamCertificateAuthority(directory);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=EasyProxy Steam Root CA, O=EasyProxy", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, critical: true));
            using var expired = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));
            WriteProtectedIdentity(expired);
            using var recovered = ReadPublicTrustIdentity(authority);
            Equal(expired.Thumbprint, recovered?.Thumbprint, "an expired CA identity remains available for exact trust removal");
            Equal(false, recovered!.HasPrivateKey, "expired identity recovery does not expose a private key");
            Equal(false, File.Exists(authority.RootCertificatePath), "expired identity recovery does not create a CER");

            var otherRequest = new CertificateRequest("CN=Another Root, O=Another Application", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            otherRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, critical: true));
            using var unrelated = otherRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            WriteProtectedIdentity(unrelated);
            Throws<CryptographicException>(() => { using var _ = ReadPublicTrustIdentity(authority); });

            var leafRequest = new CertificateRequest("CN=EasyProxy Steam Root CA, O=EasyProxy", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
            using var leaf = leafRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            WriteProtectedIdentity(leaf);
            Throws<CryptographicException>(() => { using var _ = ReadPublicTrustIdentity(authority); });

            void WriteProtectedIdentity(X509Certificate2 certificate)
            {
                var bytes = certificate.Export(X509ContentType.Pkcs12);
                try { File.WriteAllBytes(authority.PrivateKeyPath, SteamDataProtection.Protect(bytes)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        });
    }

    private static X509Certificate2? ReadPublicTrustIdentity(SteamCertificateAuthority authority)
    {
        var method = typeof(SteamCertificateAuthority).GetMethod("ReadPublicTrustIdentity",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        try { return (X509Certificate2?)method.Invoke(authority, null); }
        catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static void CertificateAuthorityRejectsUnknownNames()
    {
        WithTempDirectory(directory =>
        {
            using var authority = new SteamCertificateAuthority(directory);
            foreach (var name in new[] { "www.example.com", "steamcommunity.com.attacker.example", "*.steamcommunity.com", "steamcommunity.com:443", "127.0.0.1", "", "steamcommunity.com\r\nCN=attacker" })
            {
                Throws<ArgumentException>(() => authority.GetServerCertificate(name));
            }
        });
    }

    private static void ResolverRejectsLocalAddresses()
    {
        foreach (var address in new[] { "0.0.0.0", "127.0.0.1", "127.255.255.255", "10.1.2.3", "172.16.1.2", "192.168.4.5", "169.254.1.1", "224.0.0.1", "255.255.255.255", "::", "::1", "fe80::1", "fc00::1", "ff02::1", "::ffff:127.0.0.1" })
            Equal(false, SteamDnsResolver.IsPublicAddress(IPAddress.Parse(address)), "refuse local destination " + address);
        foreach (var address in new[] { "1.1.1.1", "223.5.5.5", "2606:4700:4700::1111" })
            Equal(true, SteamDnsResolver.IsPublicAddress(IPAddress.Parse(address)), "accept public destination " + address);
    }

    private static void OriginValidationRejectsInvalidCertificates()
    {
        WithTempDirectory(directory =>
        {
            using var authority = new SteamCertificateAuthority(directory);
            var community = authority.GetServerCertificate("steamcommunity.com");
            var store = authority.GetServerCertificate("store.steampowered.com");
            Equal(true, SteamProxyServer.ValidateOriginCertificate("steamcommunity.com", community, SslPolicyErrors.None), "correct trusted origin certificate accepted");
            Equal(true, SteamProxyServer.ValidateOriginCertificate("steamcommunity.com", community, SslPolicyErrors.RemoteCertificateNameMismatch), "fake SNI still authenticates the original Steam SAN");
            Equal(false, SteamProxyServer.ValidateOriginCertificate("steamcommunity.com", store, SslPolicyErrors.RemoteCertificateNameMismatch), "different Steam SAN rejected");
            Equal(false, SteamProxyServer.ValidateOriginCertificate("steamcommunity.com", store, SslPolicyErrors.None), "SAN checked even when callback supplied no name error");
            Equal(false, SteamProxyServer.ValidateOriginCertificate("steamcommunity.com", community, SslPolicyErrors.RemoteCertificateChainErrors), "untrusted chain rejected");
            Equal(false, SteamProxyServer.ValidateOriginCertificate("steamcommunity.com", community, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors), "fake SNI cannot bypass chain errors");
            Equal(false, SteamProxyServer.ValidateOriginCertificate("steamcommunity.com", null, SslPolicyErrors.RemoteCertificateNotAvailable), "missing certificate rejected");
        });
    }

    private static void SettingsRejectLocalDestinationsAndLoops()
    {
        foreach (var address in new[] { "127.0.0.1", "::1", "192.168.1.1", "0.0.0.0" })
        {
            var settings = CreateLocalSettings();
            settings.Rules[0].Addresses = [address];
            Throws<InvalidOperationException>(settings.Validate);
        }
        foreach (var proxyHost in new[] { "127.0.0.1", "localhost", "[::1]" })
        {
            var settings = CreateLocalSettings();
            settings.UpstreamProxyUri = $"http://{proxyHost}:{settings.HttpsPort}";
            Throws<InvalidOperationException>(settings.Validate);
        }
        var unknown = CreateLocalSettings();
        unknown.Rules.Add(new SteamProxyRule { Domain = "steamcommunity.com.attacker.example" });
        Throws<InvalidOperationException>(unknown.Validate);
        var dnsLoop = CreateLocalSettings();
        dnsLoop.DohEndpoints = ["https://steamcommunity.com/resolve"];
        Throws<InvalidOperationException>(dnsLoop.Validate);
    }

    private static void InitialStatusInspectionHasNoFiles()
    {
        WithTempDirectory(directory =>
        {
            var dataDirectory = Path.Combine(directory, "not-created");
            var hostsPath = Path.Combine(directory, "hosts");
            File.WriteAllText(hostsPath, "127.0.0.1 localhost\n", new UTF8Encoding(false));
            var controller = new SteamProxyProcessController(dataDirectory, hostsPath);
            var settings = controller.LoadSettings();
            settings.Validate();
            var status = controller.GetStatus();
            Equal(false, status.IsRunning, "no worker exists for the fresh data directory");
            Equal(false, status.CertificateTrusted, "fresh status does not create a trusted root");
            Equal(false, status.HostsInstalled, "fresh test hosts has no managed mappings");
            Equal(false, Directory.Exists(dataDirectory), "read-only inspection creates no files or CA keys");
            Equal(false, File.Exists(hostsPath + ".easyproxy-steam.lock"), "read-only status creates no hosts lock file");
        });
    }

    private static async Task HttpListenerRedirectsOnlySupportedHostsAsync()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var authority = new SteamCertificateAuthority(directory);
            var settings = CreateLocalSettings();
            await using var server = new SteamProxyServer(settings, authority, clientFactory: _ =>
            {
                return new HttpMessageInvoker(new SocketsHttpHandler { UseProxy = false });
            });
            await server.StartAsync();
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
            using var allowed = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{settings.HttpPort}/profiles/test?l=schinese");
            allowed.Headers.Host = "steamcommunity.com";
            using var allowedResponse = await client.SendAsync(allowed);
            Equal(HttpStatusCode.Found, allowedResponse.StatusCode, "HTTP redirect status");
            Equal($"https://steamcommunity.com:{settings.HttpsPort}/profiles/test?l=schinese", allowedResponse.Headers.Location?.AbsoluteUri, "redirect preserves host, HTTPS port and URL");

            using var unknown = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{settings.HttpPort}/");
            unknown.Headers.Host = "steamcommunity.com.attacker.example";
            using var unknownResponse = await client.SendAsync(unknown);
            Equal(true, (int)unknownResponse.StatusCode >= 400 && (int)unknownResponse.StatusCode < 500, "unknown HTTP Host is rejected");
            Equal<Uri?>(null, unknownResponse.Headers.Location, "unknown host cannot create an open redirect");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task TlsReverseProxyForwardsAsync()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var authority = new SteamCertificateAuthority(directory);
            var root = authority.LoadOrCreate();
            var settings = CreateLocalSettings();
            using var origin = new TcpListener(IPAddress.Loopback, 0);
            origin.Start();
            var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
            var originTask = ServeOriginOnceAsync(origin, timeout.Token);
            using var rewrite = new LocalOriginHandler(originPort);
            await using var server = new SteamProxyServer(settings, authority, clientFactory: _ => new HttpMessageInvoker(rewrite, disposeHandler: false));
            await server.StartAsync(timeout.Token);

            using var client = CreateTlsClient(settings.HttpsPort, root);
            const string body = "session=test&message=你好 Steam";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://steamcommunity.com:{settings.HttpsPort}/comment/add?l=schinese")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            request.Headers.TryAddWithoutValidation("X-Keep-Request", "original-value");
            request.Headers.TryAddWithoutValidation("X-Remove-Request", "must-not-reach-origin");
            request.Headers.Connection.Add("X-Remove-Request");
            request.Headers.TryAddWithoutValidation("Cookie", "steamLoginSecure=client-cookie");
            using var response = await client.SendAsync(request, timeout.Token);
            var received = await originTask;

            Equal(HttpStatusCode.Found, response.StatusCode, "upstream redirect is not followed");
            Equal("https://store.steampowered.com/next", response.Headers.Location?.AbsoluteUri, "upstream Location preserved");
            Equal("origin-response-你好", await response.Content.ReadAsStringAsync(timeout.Token), "response body forwarded");
            Equal(2, response.Headers.GetValues("Set-Cookie").Count(), "all Set-Cookie headers preserved");
            Equal(false, response.Headers.Contains("X-Remove-Response"), "connection-specific response header removed");
            Equal(true, received.Headers.StartsWith("POST /comment/add?l=schinese HTTP/1.1\r\n", StringComparison.Ordinal), "origin receives method, path and query");
            Equal(body, received.Body, "UTF8 request body forwarded");
            Equal(true, received.Headers.Contains("X-Keep-Request: original-value", StringComparison.OrdinalIgnoreCase), "end-to-end header forwarded");
            Equal(true, received.Headers.Contains("Cookie: steamLoginSecure=client-cookie", StringComparison.OrdinalIgnoreCase), "client cookies forwarded");
            Equal(false, received.Headers.Contains("X-Remove-Request", StringComparison.OrdinalIgnoreCase), "connection-specific request header removed");
            Equal("steamcommunity.com", rewrite.OriginalUri?.Host, "rule destination retains original host before fixture transport");
            Equal(true, rewrite.OriginalHost?.StartsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase) == true, "original HTTP Host forwarded");

            using var mismatchSocket = new TcpClient();
            await mismatchSocket.ConnectAsync(IPAddress.Loopback, settings.HttpsPort, timeout.Token);
            using var mismatchTls = new SslStream(mismatchSocket.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await mismatchTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "steamcommunity.com" }, timeout.Token);
            await mismatchTls.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: store.steampowered.com\r\nConnection: close\r\n\r\n"), timeout.Token);
            var mismatchStatus = new byte[128];
            var read = await mismatchTls.ReadAsync(mismatchStatus, timeout.Token);
            Equal(true, Encoding.ASCII.GetString(mismatchStatus, 0, read).StartsWith("HTTP/1.1 421", StringComparison.Ordinal), "different allowed Host cannot reuse another domain's TLS connection");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task TlsListenerRejectsUnknownSniAsync()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var authority = new SteamCertificateAuthority(directory);
            var settings = CreateLocalSettings();
            await using var server = new SteamProxyServer(settings, authority);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.StartAsync(timeout.Token);
            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, settings.HttpsPort, timeout.Token);
            using var tls = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            try
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "www.example.com" }, timeout.Token);
            }
            catch (AuthenticationException) { return; }
            catch (IOException) { return; }
            throw new InvalidOperationException("TLS accepted an unrelated SNI and issued a certificate.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task TlsReverseProxyRelaysWebSocketAsync()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var authority = new SteamCertificateAuthority(directory);
            var root = authority.LoadOrCreate();
            var settings = CreateLocalSettings();
            using var origin = new TcpListener(IPAddress.Loopback, 0);
            origin.Start();
            var originTask = ServeWebSocketOriginOnceAsync(origin, timeout.Token);
            using var rewrite = new LocalOriginHandler(((IPEndPoint)origin.LocalEndpoint).Port);
            await using var server = new SteamProxyServer(settings, authority, clientFactory: _ => new HttpMessageInvoker(rewrite, disposeHandler: false));
            await server.StartAsync(timeout.Token);
            using var transport = new HttpMessageInvoker(CreateTlsHandler(settings.HttpsPort, root));
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"wss://steamcommunity.com:{settings.HttpsPort}/websocket"), transport, timeout.Token);
            var message = Encoding.UTF8.GetBytes("Steam chat 你好");
            await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
            var response = new byte[256];
            var received = await socket.ReceiveAsync(response, timeout.Token);
            Equal(WebSocketMessageType.Text, received.MessageType, "WebSocket reply type");
            Equal(true, received.EndOfMessage, "WebSocket reply is complete");
            Equal("Steam chat 你好", Encoding.UTF8.GetString(response, 0, received.Count), "WebSocket payload preserved");
            Equal("Steam chat 你好", await originTask, "origin receives a masked client frame");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task UpstreamProxyRejectsUntrustedOriginAsync(bool socks)
    {
        var directory = CreateTempDirectory();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var authority = new SteamCertificateAuthority(Path.Combine(directory, "proxy-ca"));
            using var originAuthority = new SteamCertificateAuthority(Path.Combine(directory, "untrusted-origin-ca"));
            var root = authority.LoadOrCreate();
            var originCertificate = originAuthority.GetServerCertificate("steamcommunity.com");
            var settings = CreateLocalSettings();
            using var proxy = new TcpListener(IPAddress.Loopback, 0);
            proxy.Start();
            var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
            settings.UpstreamProxyUri = $"{(socks ? "socks5" : "http")}://127.0.0.1:{proxyPort}";
            var proxyTask = ServeUntrustedProxyOriginOnceAsync(proxy, socks, originCertificate, timeout.Token);
            await using var server = new SteamProxyServer(settings, authority);
            await server.StartAsync(timeout.Token);
            using var client = CreateTlsClient(settings.HttpsPort, root);

            using var response = await client.GetAsync($"https://steamcommunity.com:{settings.HttpsPort}/private-profile", timeout.Token);
            var received = await proxyTask;

            Equal("steamcommunity.com:443", received.Target, "upstream receives the original Steam DNS name and port");
            Equal(false, received.AcceptedApplicationData, "untrusted origin receives no decrypted Steam request");
            Equal(HttpStatusCode.BadGateway, response.StatusCode, "production transport rejects the untrusted origin certificate");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<(string Target, bool AcceptedApplicationData)> ServeUntrustedProxyOriginOnceAsync(
        TcpListener listener, bool socks, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        string target;
        if (socks)
        {
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, cancellationToken);
            Equal((byte)5, greeting[0], "SOCKS version");
            var methods = new byte[greeting[1]];
            await stream.ReadExactlyAsync(methods, cancellationToken);
            Equal(true, methods.Contains((byte)0), "SOCKS supports anonymous authentication");
            await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken);
            var connect = new byte[5];
            await stream.ReadExactlyAsync(connect, cancellationToken);
            Equal((byte)5, connect[0], "SOCKS CONNECT version");
            Equal((byte)1, connect[1], "SOCKS CONNECT command");
            Equal((byte)3, connect[3], "SOCKS receives a DNS name rather than OS-resolved loopback");
            var address = new byte[connect[4]];
            await stream.ReadExactlyAsync(address, cancellationToken);
            var port = new byte[2];
            await stream.ReadExactlyAsync(port, cancellationToken);
            target = Encoding.ASCII.GetString(address) + ":" + ((port[0] << 8) | port[1]);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, cancellationToken);
        }
        else
        {
            var headers = await ReadHeadersAsync(stream, cancellationToken);
            var request = headers.Split("\r\n", StringSplitOptions.None)[0].Split(' ');
            Equal("CONNECT", request[0], "HTTP proxy creates an authenticated TLS tunnel");
            target = request[1];
            await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), cancellationToken);
        }

        using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                ApplicationProtocols = [SslApplicationProtocol.Http11]
            }, cancellationToken);
            var firstApplicationByte = new byte[1];
            var received = await tls.ReadAsync(firstApplicationByte, cancellationToken);
            if (received > 0)
            {
                await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancellationToken);
                return (target, true);
            }
        }
        catch (AuthenticationException) { }
        catch (IOException) { }
        return (target, false);
    }

    private static SteamProxySettings CreateLocalSettings()
    {
        var httpPort = AllocateLocalPort();
        var httpsPort = AllocateLocalPort();
        while (httpsPort == httpPort) httpsPort = AllocateLocalPort();
        return new() { HttpPort = httpPort, HttpsPort = httpsPort, UpstreamProxyUri = null };
    }

    private static int AllocateLocalPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static HttpClient CreateTlsClient(int port, X509Certificate2 root)
    {
        return new HttpClient(CreateTlsHandler(port, root)) { Timeout = TimeSpan.FromSeconds(10), DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
    }

    private static SocketsHttpHandler CreateTlsHandler(int port, X509Certificate2 root) =>
        new()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
                        return false;
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(root);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.DisableCertificateDownloads = true;
                    return chain.Build((X509Certificate2)certificate);
                }
            }
        };

    private static async Task<(string Headers, string Body)> ServeOriginOnceAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        var headers = await ReadHeadersAsync(stream, cancellationToken);
        var contentLengthLine = headers.Split("\r\n", StringSplitOptions.None).Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var bodyBytes = new byte[int.Parse(contentLengthLine[(contentLengthLine.IndexOf(':') + 1)..].Trim())];
        await stream.ReadExactlyAsync(bodyBytes, cancellationToken);
        var responseBody = Encoding.UTF8.GetBytes("origin-response-你好");
        var responseHeaders = Encoding.ASCII.GetBytes(
            "HTTP/1.1 302 Found\r\nLocation: https://store.steampowered.com/next\r\n" +
            "Set-Cookie: first=1; Path=/; Secure\r\nSet-Cookie: second=2; Path=/; HttpOnly\r\n" +
            "Connection: close, X-Remove-Response\r\nX-Remove-Response: must-not-reach-client\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\nContent-Length: " + responseBody.Length + "\r\n\r\n");
        await stream.WriteAsync(responseHeaders, cancellationToken);
        await stream.WriteAsync(responseBody, cancellationToken);
        return (headers, Encoding.UTF8.GetString(bodyBytes));
    }

    private static async Task<string> ServeWebSocketOriginOnceAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        var headers = await ReadHeadersAsync(stream, cancellationToken);
        var keyHeader = headers.Split("\r\n", StringSplitOptions.None).Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
        var key = keyHeader[(keyHeader.IndexOf(':') + 1)..].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), cancellationToken);
        var prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        Equal((byte)0x81, prefix[0], "client sends one complete text frame");
        Equal(true, (prefix[1] & 0x80) != 0, "client frame is masked");
        var length = prefix[1] & 0x7f;
        Equal(true, length < 126, "small test frame length");
        var mask = new byte[4];
        await stream.ReadExactlyAsync(mask, cancellationToken);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
        await stream.WriteAsync(new byte[] { 0x81, (byte)payload.Length }, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var headerBytes = new MemoryStream();
        var singleByte = new byte[1];
        while (headerBytes.Length < 64 * 1024)
        {
            if (await stream.ReadAsync(singleByte, cancellationToken) == 0)
                throw new IOException("Origin connection closed before headers completed.");
            headerBytes.WriteByte(singleByte[0]);
            if (headerBytes.Length >= 4 && headerBytes.GetBuffer().AsSpan((int)headerBytes.Length - 4, 4).SequenceEqual("\r\n\r\n"u8))
                return Encoding.ASCII.GetString(headerBytes.ToArray());
        }
        throw new IOException("Origin request headers exceeded the test limit.");
    }

    private sealed class LocalOriginHandler(int originPort) : DelegatingHandler(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        public Uri? OriginalUri { get; private set; }
        public string? OriginalHost { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            OriginalUri = request.RequestUri;
            OriginalHost = request.Headers.Host;
            request.RequestUri = new Uri($"http://127.0.0.1:{originPort}" + request.RequestUri!.PathAndQuery);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EasyProxySteamTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IReadOnlyList<string> ReadDnsSubjectAlternativeNames(X509Certificate2 certificate)
    {
        var san = certificate.Extensions.Cast<X509Extension>().Single(extension => extension.Oid?.Value == "2.5.29.17");
        var reader = new AsnReader(san.RawData, AsnEncodingRules.DER);
        var names = reader.ReadSequence();
        var result = new List<string>();
        while (names.HasData)
        {
            Equal(new Asn1Tag(TagClass.ContextSpecific, 2), names.PeekTag(), "SAN contains DNS names only");
            result.Add(names.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
        }
        reader.ThrowIfNotEmpty();
        return result;
    }

    private static void WithTempDirectory(Action<string> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "EasyProxySteamTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { body(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'");
    }

    private static void Throws<T>(Action body) where T : Exception
    {
        try { body(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
