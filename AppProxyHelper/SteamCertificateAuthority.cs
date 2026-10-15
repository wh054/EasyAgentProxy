using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AppProxyHelper;

/// <summary>
/// An independent, per-user EasyProxy CA. Private PKCS#12 bytes are protected with
/// Windows DPAPI CurrentUser before reaching disk; only allowlisted DNS names are signed.
/// Returned certificates are owned by this instance and live until it is disposed.
/// </summary>
public sealed class SteamCertificateAuthority : IDisposable
{
    private const string RootSubject = "CN=EasyProxy Steam Root CA, O=EasyProxy";
    private readonly object _sync = new();
    private readonly HashSet<string> _allowedDomains;
    private readonly Dictionary<string, X509Certificate2> _serverCertificates = new(StringComparer.OrdinalIgnoreCase);
    private X509Certificate2? _root;
    private bool _disposed;

    public SteamCertificateAuthority(string dataDirectory, IEnumerable<string>? allowedDomains = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        _allowedDomains = new HashSet<string>((allowedDomains ?? SteamDomainCatalog.Domains).Select(NormalizeHost), StringComparer.OrdinalIgnoreCase);
        if (_allowedDomains.Count == 0)
            throw new ArgumentException("Steam 证书域名白名单不能为空。", nameof(allowedDomains));
    }

    public string DataDirectory { get; }
    public string PrivateKeyPath => Path.Combine(DataDirectory, "steam-root-ca.dpapi");
    public string RootCertificatePath => Path.Combine(DataDirectory, "steam-root-ca.cer");

    public X509Certificate2 LoadOrCreate()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_root is not null)
                return _root;
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Steam CA 私钥使用 Windows CurrentUser DPAPI 保护。请在 Windows 上运行。");

            SteamDataProtection.EnsurePrivateDirectory(DataDirectory);
            using var processLock = new FileStream(PrivateKeyPath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            if (File.Exists(PrivateKeyPath))
            {
                byte[]? decrypted = null;
                try
                {
                    decrypted = SteamDataProtection.Unprotect(File.ReadAllBytes(PrivateKeyPath));
                    _root = X509CertificateLoader.LoadPkcs12(decrypted, password: null,
                        X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
                    ValidateRoot(_root);
                }
                catch
                {
                    _root?.Dispose();
                    _root = null;
                    throw;
                }
                finally
                {
                    if (decrypted is not null)
                        CryptographicOperations.ZeroMemory(decrypted);
                }
            }
            else
            {
                if (File.Exists(RootCertificatePath))
                    throw new CryptographicException("Steam CA 公钥证书存在，但 DPAPI 私钥文件缺失；未覆盖现有身份。请移除原证书信任后重置 Steam 证书文件。");
                _root = CreateRoot();
                byte[]? privateBytes = null;
                try
                {
                    privateBytes = _root.Export(X509ContentType.Pkcs12);
                    var protectedBytes = SteamDataProtection.Protect(privateBytes);
                    WriteAtomic(PrivateKeyPath, protectedBytes);
                }
                catch
                {
                    _root.Dispose();
                    _root = null;
                    throw;
                }
                finally
                {
                    if (privateBytes is not null)
                        CryptographicOperations.ZeroMemory(privateBytes);
                }
            }

            try
            {
                // Keep a public-only copy for inspection and manual trust import if required.
                if (!File.Exists(RootCertificatePath) || !File.ReadAllBytes(RootCertificatePath).AsSpan().SequenceEqual(_root.RawData))
                    WriteAtomic(RootCertificatePath, _root.RawData);
                return _root;
            }
            catch
            {
                _root.Dispose();
                _root = null;
                throw;
            }
        }
    }

    public X509Certificate2 GetServerCertificate(string host)
    {
        var normalized = NormalizeHost(host);
        if (!_allowedDomains.Contains(normalized))
            throw new ArgumentException($"拒绝为不在 Steam 白名单中的域名签发证书：{normalized}", nameof(host));

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var root = LoadOrCreate();
            if (_serverCertificates.TryGetValue(normalized, out var cached) && cached.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddHours(1))
                return cached;

            using var key = RSA.Create(2048);
            var request = new CertificateRequest(new X500DistinguishedName("CN=" + normalized), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, critical: false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(normalized);
            request.CertificateExtensions.Add(san.Build());
            var notAfter = new DateTimeOffset(root.NotAfter.ToUniversalTime()).AddMinutes(-1);
            var preferredExpiry = DateTimeOffset.UtcNow.AddDays(90);
            if (preferredExpiry < notAfter)
                notAfter = preferredExpiry;
            if (notAfter <= DateTimeOffset.UtcNow.AddHours(1))
                throw new InvalidOperationException("EasyProxy Steam CA 即将过期，请先移除其信任并重新创建证书。");

            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F;
            serial[0] |= 1;
            using var publicCertificate = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-5), notAfter, serial);
            using var withKey = publicCertificate.CopyWithPrivateKey(key);
            // Windows Schannel server credentials require a CNG/CAPI key container; a
            // CopyWithPrivateKey / EphemeralKeySet leaf can fail with SEC_E_NO_CREDENTIALS.
            // UserKeySet imports a temporary per-user container removed on certificate
            // disposal. PersistKeySet is deliberately omitted; no plaintext PFX is saved.
            var privateBytes = withKey.Export(X509ContentType.Pkcs12);
            X509Certificate2 certificate;
            try
            {
                certificate = X509CertificateLoader.LoadPkcs12(privateBytes, password: null, X509KeyStorageFlags.UserKeySet);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateBytes);
            }
            // Keep retired certificates alive until Dispose: in-flight TLS handshakes may
            // still be using a previously returned certificate during a long-running session.
            if (cached is not null)
                _retiredCertificates.Add(cached);
            _serverCertificates[normalized] = certificate;
            return certificate;
        }
    }

    public bool IsTrusted
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // Status must remain entirely read-only, including before first setup and
                // from the unelevated GUI: do not create directories, locks, keys or ACLs.
                using var root = ReadPublicTrustIdentity();
                if (root is null)
                    return false;
                using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
                var matches = FindOwnedCertificates(store, root);
                var found = matches.Count > 0;
                foreach (var certificate in matches) certificate.Dispose();
                return found;
            }
        }
    }

    public void InstallTrust()
    {
        lock (_sync)
        {
            var root = LoadOrCreate();
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            var matches = FindOwnedCertificates(store, root);
            var found = matches.Count > 0;
            foreach (var certificate in matches) certificate.Dispose();
            if (!found)
            {
                using var publicRoot = X509CertificateLoader.LoadCertificate(root.RawData);
                store.Add(publicRoot);
            }
        }
    }

    public void RemoveTrust()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Removal also works for an expired root or a lost private key. It must not
            // generate a new identity simply to remove a previous certificate's trust.
            using var root = ReadPublicTrustIdentity();
            if (root is null)
                return;
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            // Match the actual certificate, never just the common name. Other applications'
            // roots and EasyProxy identities in other profiles remain untouched.
            foreach (var certificate in FindOwnedCertificates(store, root))
            {
                using (certificate)
                    store.Remove(certificate);
            }
        }
    }

    private readonly List<X509Certificate2> _retiredCertificates = new();

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var certificate in _serverCertificates.Values) certificate.Dispose();
            foreach (var certificate in _retiredCertificates) certificate.Dispose();
            _serverCertificates.Clear();
            _retiredCertificates.Clear();
            _root?.Dispose();
            _root = null;
        }
    }

    private static X509Certificate2 CreateRoot()
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest(new X500DistinguishedName(RootSubject), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var bytes = generated.Export(X509ContentType.Pkcs12);
        try
        {
            return X509CertificateLoader.LoadPkcs12(bytes, password: null,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateRoot(X509Certificate2 root)
    {
        if (!root.HasPrivateKey || root.Subject != new X500DistinguishedName(RootSubject).Name ||
            root.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true)
            throw new CryptographicException("保存的 Steam CA 不包含有效的 EasyProxy CA 私钥；未自动覆盖该文件。");
        if (root.NotBefore.ToUniversalTime() > DateTime.UtcNow || root.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(1))
            throw new CryptographicException("保存的 EasyProxy Steam CA 已过期或尚未生效；未自动更换信任身份。");
    }

    private static X509Certificate2Collection FindOwnedCertificates(X509Store store, X509Certificate2 root)
    {
        return store.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, validOnly: false);
    }

    private X509Certificate2? ReadPublicTrustIdentity()
    {
        if (_root is not null)
            return X509CertificateLoader.LoadCertificate(_root.RawData);
        X509Certificate2 root;
        if (File.Exists(RootCertificatePath))
        {
            root = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(RootCertificatePath));
        }
        else
        {
            if (!File.Exists(PrivateKeyPath))
                return null;

            // The public copy may have been removed independently. Recover only the
            // existing identity, without creating files, changing ACLs or persisting a
            // key container. Expired identities must remain removable from the store.
            byte[]? privateBytes = null;
            try
            {
                privateBytes = SteamDataProtection.Unprotect(File.ReadAllBytes(PrivateKeyPath));
                using var privateRoot = X509CertificateLoader.LoadPkcs12(privateBytes, password: null, X509KeyStorageFlags.EphemeralKeySet);
                root = X509CertificateLoader.LoadCertificate(privateRoot.RawData);
            }
            finally
            {
                if (privateBytes is not null)
                    CryptographicOperations.ZeroMemory(privateBytes);
            }
        }
        try
        {
            if (root.Subject != new X500DistinguishedName(RootSubject).Name ||
                root.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true)
                throw new CryptographicException("保存的 Steam 根证书不是 EasyProxy CA；未修改证书信任库。");
            return root;
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    private static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var normalized = new IdnMapping().GetAscii(host.Trim().TrimEnd('.')).ToLowerInvariant();
        if (normalized.Length > 253 || Uri.CheckHostName(normalized) != UriHostNameType.Dns || normalized.Contains('*') || normalized.Contains('_') ||
            normalized.Split('.').Any(label => label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'))
            throw new ArgumentException($"无效的 TLS DNS 名称：{host}", nameof(host));
        return normalized;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
