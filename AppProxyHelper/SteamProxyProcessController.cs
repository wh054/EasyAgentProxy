using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AppProxyHelper;

public sealed record SteamProxyStatus(bool IsRunning, int? ProcessId, string? UpstreamProxyUri,
    bool CertificateTrusted, bool HostsInstalled, string LogFilePath, string DataDirectory, bool IsSession = false, int WebPolicyVersion = 0);

/// <summary>Owns an elevated worker via a same-user named pipe; never terminates a process by saved PID.</summary>
public sealed class SteamProxyProcessController
{
    public const int CurrentWebPolicyVersion = 1;
    public static string DefaultDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyProxy", "SteamProxy");
    public string DataDirectory { get; }
    private readonly string? _hostsPath;
    public string LogFilePath => Path.Combine(DataDirectory, "steam-proxy.log");
    private string SettingsPath => Path.Combine(DataDirectory, "settings.dat");
    private string SessionSettingsPath => Path.Combine(DataDirectory, "session-settings.dat");
    private string SessionCertificateDirectory => Path.Combine(DataDirectory, "session-certificate");
    private string ErrorPath => Path.Combine(DataDirectory, "startup-error.txt");
    private string StartupLeasePath => Path.Combine(DataDirectory, "startup.lease");
    private string PipeName => "EasyProxy.Steam." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        WindowsIdentity.GetCurrent().User!.Value + "|" + Path.GetFullPath(DataDirectory).ToUpperInvariant())))[..24];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SteamProxyProcessController(string? dataDirectory = null, string? hostsPath = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? DefaultDataDirectory);
        _hostsPath = hostsPath;
    }

    public SteamProxySettings LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return new SteamProxySettings { UpstreamProxyUri = new AppProxyConfig().ProxyUri };
        return ReadSettings(SettingsPath);
    }

    public SteamProxyStatus GetStatus()
    {
        try
        {
            var status = QueryAsync("status", TimeSpan.FromMilliseconds(800), CancellationToken.None).GetAwaiter().GetResult();
            if (status is not null) return status;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException) { }
        using var authority = new SteamCertificateAuthority(DataDirectory);
        using var sessionAuthority = new SteamCertificateAuthority(SessionCertificateDirectory);
        var hosts = new SteamHostsManager(_hostsPath);
        return new(false, null, null, authority.IsTrusted || sessionAuthority.IsTrusted, hosts.HasManagedEntries, LogFilePath, DataDirectory);
    }

    public async Task<SteamProxyStatus> EnsureSessionStartedAsync(string steamExecutablePath, string? upstreamProxyUri, CancellationToken cancellationToken = default)
    {
        var status = await Task.Run(GetStatus, cancellationToken);
        if (status.IsRunning)
        {
            if (!status.IsSession) throw new InvalidOperationException("已有独立 Steam 代理运行，请先停止后再使用 Steam-Proxy 会话入口。");
            var saved = ReadSettings(SessionSettingsPath);
            if (!SteamProcessLifetime.IsMatchingSteamProcessPath(steamExecutablePath, saved.SessionSteamExecutablePath))
                throw new InvalidOperationException("另一个 Steam 安装目录的代理会话正在运行，请先退出该 Steam 后再启动。");
            if (CanReuseSessionPolicy(status) && status.CertificateTrusted && status.HostsInstalled && SameProxy(saved.UpstreamProxyUri, upstreamProxyUri)) return status;
            await StopAsync(cancellationToken);
        }
        return await StartWorkerAsync(upstreamProxyUri, sessionMode: true, cancellationToken, steamExecutablePath);
    }

    internal static bool SameProxy(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
        static string Normalize(string value)
        {
            var endpoint = ProxyEndpoint.Parse(value);
            return endpoint.ToUriString(endpoint.IsSocks ? "socks5" : null);
        }
        return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
    }

    internal static bool CanReuseSessionPolicy(SteamProxyStatus status) => status.WebPolicyVersion == CurrentWebPolicyVersion;

    public Task<SteamProxyStatus> StartAsync(string? upstreamProxyUri, CancellationToken cancellationToken = default)
        => StartWorkerAsync(upstreamProxyUri, sessionMode: false, cancellationToken);

    private SteamProxySettings ReadSettings(string path)
    {
        var bytes = SteamDataProtection.Unprotect(File.ReadAllBytes(path));
        try
        {
            var value = JsonSerializer.Deserialize<SteamProxySettings>(bytes, JsonOptions) ?? throw new InvalidDataException("Steam 代理配置为空。");
            value.ApplyWebOnlyPolicy();
            value.Validate();
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<SteamProxyStatus> StartWorkerAsync(string? upstreamProxyUri, bool sessionMode, CancellationToken cancellationToken, string? steamExecutablePath = null)
    {
        var existing = GetStatus();
        if (existing.IsRunning) throw new InvalidOperationException("Steam 代理已运行；更换上游前请先停止。");
        var settings = sessionMode ? new SteamProxySettings() : LoadSettings();
        settings.UpstreamProxyUri = string.IsNullOrWhiteSpace(upstreamProxyUri) ? null : upstreamProxyUri;
        if (sessionMode) settings.SessionSteamExecutablePath = Path.GetFullPath(steamExecutablePath ?? throw new ArgumentNullException(nameof(steamExecutablePath)));
        settings.Validate();
        SteamDataProtection.EnsurePrivateDirectory(DataDirectory);
        using var operation = AcquireOperationLock();
        if (IsWorkerActive()) throw new InvalidOperationException("Steam 后台进程正在启动或停止，请稍后刷新状态。");
        if (sessionMode) CleanupSessionFiles();
        var settingsPath = sessionMode ? SessionSettingsPath : SettingsPath;
        var startupId = Guid.NewGuid().ToString("N");
        try
        {
            var protectedSettings = SteamDataProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions));
            var temporary = settingsPath + ".tmp";
            File.WriteAllBytes(temporary, protectedSettings);
            File.Move(temporary, settingsPath, overwrite: true);
            if (File.Exists(ErrorPath)) File.Delete(ErrorPath);
            File.WriteAllText(StartupLeasePath, startupId);
            using var process = StartWorker(sessionMode ? "run-session" : "run", startupId);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromMinutes(2))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await TryQueryAsync("confirm", cancellationToken);
                if (status?.IsRunning == true) return status;
                if (process.HasExited) throw new InvalidOperationException(ReadStartupError("Steam 代理后台进程已退出。请使用同一 Windows 用户完成提权，不支持切换管理员账户。"));
                await Task.Delay(150, cancellationToken);
            }
            throw new TimeoutException("Steam 代理未在 2 分钟内就绪，已撤销启动请求，请检查 Windows 证书确认、UAC 及日志。");
        }
        catch
        {
            // Also cover the narrow race where a confirmation reached the worker but its
            // response was interrupted. This only addresses our authenticated pipe.
            try { await TryQueryAsync("stop", CancellationToken.None); }
            catch (UnauthorizedAccessException) { /* Keep the original error; the revoked lease also stops the worker. */ }
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(StartupLeasePath) && File.ReadAllText(StartupLeasePath) == startupId) File.Delete(StartupLeasePath);
            }
            finally
            {
                if (sessionMode && !IsWorkerActive()) CleanupSessionFiles();
            }
        }
    }

    public async Task<SteamProxyStatus> StopAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(StartupLeasePath)) File.Delete(StartupLeasePath);
        var status = await TryQueryAsync("stop", cancellationToken);
        if (status is not null || IsWorkerActive())
        {
            for (var i = 0; i < 75; i++)
            {
                await Task.Delay(150, cancellationToken);
                if (await TryQueryAsync("status", cancellationToken) is null && !IsWorkerActive())
                {
                    var stopped = GetStatus();
                    if (stopped.HostsInstalled) throw new IOException("Steam 后台已退出，但 Hosts 恢复失败；请再次点击停止以恢复残留条目。");
                    CleanupSessionFilesWhenIdle();
                    return GetStatus();
                }
            }
            throw new TimeoutException("Steam 代理尚未停止，请检查日志。");
        }
        if (new SteamHostsManager(_hostsPath).HasManagedEntries)
        {
            SteamDataProtection.EnsurePrivateDirectory(DataDirectory);
            if (File.Exists(ErrorPath)) File.Delete(ErrorPath);
            using var process = StartWorker("recover");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new IOException(ReadStartupError("Steam Hosts 恢复失败。"));
        }
        CleanupSessionFilesWhenIdle();
        return GetStatus();
    }

    public void InstallCertificate()
    {
        using var authority = new SteamCertificateAuthority(DataDirectory);
        authority.LoadOrCreate();
        authority.InstallTrust();
    }

    public void RemoveCertificate()
    {
        if (!Directory.Exists(DataDirectory)) return;
        using var operation = AcquireOperationLock();
        if (GetStatus().IsRunning || IsWorkerActive()) throw new InvalidOperationException("请先停止 Steam 代理，再移除证书。");
        // Also remove a temporary CA left by older versions, using its exact identity.
        CleanupSessionFiles();
        using var authority = new SteamCertificateAuthority(DataDirectory);
        authority.RemoveTrust();
    }

    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (await TryQueryAsync("status", cancellationToken) is not null)
            throw new InvalidOperationException("Steam 代理正在运行，不能恢复 Hosts。");
        RequireAdministrator();
        try
        {
            using var workerLock = AcquireWorkerLock();
            new SteamHostsManager(_hostsPath).Remove();
        }
        catch (Exception ex)
        {
            File.WriteAllText(ErrorPath, $"Steam Hosts 恢复失败: {ex.Message}");
            throw;
        }
        FlushDns();
        return 0;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default, string? startupId = null,
        bool sessionMode = false, int? sessionOwnerPid = null, long? sessionOwnerStartTicks = null)
    {
        RequireAdministrator();
        SteamDataProtection.EnsurePrivateDirectory(DataDirectory);
        // File locks survive async thread switches and are automatically released on a crash.
        using var workerLock = AcquireWorkerLock();
        using var output = new StreamWriter(new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var logSync = new object();
        void Log(string value) { lock (logSync) output.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {value}"); }
        var hosts = new SteamHostsManager(_hostsPath);
        // Reuse one per-user CA across sessions. Trust is explicitly removed only by
        // steam remove-cert, so normal Steam exit never requires a certificate prompt.
        using var authority = new SteamCertificateAuthority(DataDirectory);
        SteamProxyServer? server = null;
        var hostsApplied = false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var watchStop = new CancellationTokenSource();
        var confirmed = startupId is null ? 1 : 0;
        var watch = startupId is null ? Task.CompletedTask : WatchStartupLeaseAsync(startupId, stop, () => Volatile.Read(ref confirmed) != 0, watchStop.Token);
        var ownerWatch = sessionMode && sessionOwnerPid is int ownerPid && sessionOwnerStartTicks is long ownerTicks
            ? WatchSessionOwnerAsync(ownerPid, ownerTicks, stop, watchStop.Token) : Task.CompletedTask;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            var settings = sessionMode ? ReadSettings(SessionSettingsPath) : LoadSettings();
            // Stale entries are ours, so they can be removed without restoring an old file snapshot.
            if (hosts.HasManagedEntries) { hosts.Remove(); FlushDns(); }
            hosts.Validate(settings.Rules.Select(r => r.Domain));
            authority.LoadOrCreate();
            server = new SteamProxyServer(settings, authority, Log);
            authority.InstallTrust();
            stop.Token.ThrowIfCancellationRequested();
            await server.StartAsync(stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            hostsApplied = true;
            hosts.Apply(settings.Rules.Select(r => r.Domain), createBackup: !sessionMode);
            FlushDns();
            Log($"Steam 已启动，回源: {(settings.UpstreamProxyUri is null ? "直连 / 独立 DoH" : UriFormatter.Sanitize(settings.UpstreamProxyUri))}");
            var liveStatus = new SteamProxyStatus(true, Environment.ProcessId,
                settings.UpstreamProxyUri is null ? null : UriFormatter.Sanitize(settings.UpstreamProxyUri), true, true, LogFilePath, DataDirectory, sessionMode, CurrentWebPolicyVersion);
            await ServeControlAsync(liveStatus, stop, () => Interlocked.Exchange(ref confirmed, 1));
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            Log($"Steam 代理失败: {ex.GetType().Name}: {ex.Message}");
            File.WriteAllText(ErrorPath, $"Steam 代理失败: {ex.Message}");
            return 1;
        }
        finally
        {
            watchStop.Cancel();
            var cleanupErrors = new List<Exception>();
            void Failure(string action, Exception error)
            {
                cleanupErrors.Add(error);
                Log($"{action}失败: {error.Message}");
            }
            try { await watch; } catch (Exception ex) { Failure("停止启动监测", ex); }
            try { await ownerWatch; } catch (Exception ex) { Failure("停止会话监测", ex); }
            // Restore DNS routing before releasing the listening sockets.
            if (hostsApplied)
            {
                try { hosts.Remove(); FlushDns(); Log("已恢复 Steam Hosts。"); }
                catch (Exception ex) { Failure("恢复 Hosts，请再次执行 steam stop", ex); }
            }
            if (server is not null)
            {
                try { await server.DisposeAsync(); } catch (Exception ex) { Failure("关闭代理监听", ex); }
            }
            if (sessionMode)
            {
                try { CleanupSessionFiles(); } catch (Exception ex) { Failure("清理 Steam 临时会话", ex); }
            }
            Log("Steam 代理已停止。");
            if (cleanupErrors.Count > 0)
            {
                File.WriteAllText(ErrorPath, "Steam 会话清理失败，请执行停止并恢复并查看日志。");
                throw new AggregateException("Steam 代理恢复未全部完成。", cleanupErrors);
            }
        }
    }

    private async Task ServeControlAsync(SteamProxyStatus status, CancellationTokenSource stop, Action confirm)
    {
        while (!stop.IsCancellationRequested)
        {
            using var pipe = CreateControlPipe();
            await pipe.WaitForConnectionAsync(stop.Token);
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                var command = await reader.ReadLineAsync(requestTimeout.Token);
                await HandleControlCommandAsync(command, () =>
                {
                    using var authority = new SteamCertificateAuthority(DataDirectory);
                    return status with { CertificateTrusted = authority.IsTrusted, HostsInstalled = new SteamHostsManager(_hostsPath).HasManagedEntries };
                }, current => writer.WriteLineAsync(JsonSerializer.Serialize(current)), confirm, stop);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }

    internal static async Task HandleControlCommandAsync(string? command, Func<SteamProxyStatus> readStatus,
        Func<SteamProxyStatus, Task> writeResponse, Action confirm, CancellationTokenSource stop)
    {
        if (command is not ("status" or "stop" or "confirm")) return;
        // A stop request must reach shutdown even when status inspection fails or the
        // requesting process disconnects before receiving its acknowledgement.
        if (command == "stop") stop.Cancel();
        if (command == "confirm") confirm();
        var current = readStatus();
        await writeResponse(current);
    }

    private async Task<SteamProxyStatus?> TryQueryAsync(string command, CancellationToken cancellationToken)
    {
        try { return await QueryAsync(command, TimeSpan.FromMilliseconds(500), cancellationToken); }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException) { return null; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private async Task<SteamProxyStatus?> QueryAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(limit.Token).ConfigureAwait(false);
        using var identity = WindowsIdentity.GetCurrent();
        if (!Equals(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)), identity.User))
            throw new UnauthorizedAccessException("Steam 控制管道不属于当前 Windows 用户。");
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
        await writer.WriteLineAsync(command.AsMemory(), limit.Token).ConfigureAwait(false);
        var response = await reader.ReadLineAsync(limit.Token).ConfigureAwait(false);
        return response is null ? null : JsonSerializer.Deserialize<SteamProxyStatus>(response);
    }

    private NamedPipeServerStream CreateControlPipe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("无法确定 Steam 代理用户身份。");
        // CurrentUserOnly also checks elevation on Windows, preventing the unelevated
        // launcher from controlling its elevated worker. Use the exact user SID instead.
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    private async Task WatchStartupLeaseAsync(string startupId, CancellationTokenSource stop, Func<bool> isConfirmed, CancellationToken cancellationToken)
    {
        try
        {
            while (!isConfirmed() && !cancellationToken.IsCancellationRequested)
            {
                if (!File.Exists(StartupLeasePath) || File.ReadAllText(StartupLeasePath) != startupId)
                {
                    stop.Cancel();
                    return;
                }
                await Task.Delay(200, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { stop.Cancel(); }
    }

    private static async Task WatchSessionOwnerAsync(int processId, long startTicks, CancellationTokenSource stop, CancellationToken cancellationToken)
    {
        try
        {
            using var owner = Process.GetProcessById(processId);
            if (owner.StartTime.ToUniversalTime().Ticks != startTicks) { stop.Cancel(); return; }
            await owner.WaitForExitAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested) stop.Cancel();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            stop.Cancel();
        }
    }

    private void CleanupSessionFiles()
    {
        // Only dedicated runtime files and the old temporary-CA layout belong here.
        // The shared CA in DataDirectory and its trust persist until manual removal.
        if (Directory.Exists(SessionCertificateDirectory) && (File.GetAttributes(SessionCertificateDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Steam 会话证书目录不能是目录联接或符号链接。");
        using var authority = new SteamCertificateAuthority(SessionCertificateDirectory);
        authority.RemoveTrust();
        if (File.Exists(SessionSettingsPath)) File.Delete(SessionSettingsPath);
        if (File.Exists(SessionSettingsPath + ".tmp")) File.Delete(SessionSettingsPath + ".tmp");
        if (!Directory.Exists(SessionCertificateDirectory)) return;
        foreach (var name in new[] { "steam-root-ca.dpapi", "steam-root-ca.cer", "steam-root-ca.dpapi.lock" })
            File.Delete(Path.Combine(SessionCertificateDirectory, name));
        if (!Directory.EnumerateFileSystemEntries(SessionCertificateDirectory).Any()) Directory.Delete(SessionCertificateDirectory);
    }

    private void CleanupSessionFilesWhenIdle()
    {
        if (!Directory.Exists(DataDirectory)) return;
        using var operation = AcquireOperationLock();
        if (IsWorkerActive() || File.Exists(StartupLeasePath)) return;
        CleanupSessionFiles();
    }

    private Process StartWorker(string command, string? startupId = null)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位 EasyProxy 程序。");
        if (_hostsPath is not null || !string.Equals(DataDirectory, DefaultDataDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("后台进程只支持默认 Steam 数据目录。");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = IsAdministrator() ? "" : "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(SteamProxyProcessController).Assembly.Location);
        info.ArgumentList.Add("steam");
        info.ArgumentList.Add(command);
        info.ArgumentList.Add("--owner-sid");
        info.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
        if (command == "run-session")
        {
            using var owner = Process.GetCurrentProcess();
            info.ArgumentList.Add("--session-owner-pid");
            info.ArgumentList.Add(owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--session-owner-start");
            info.ArgumentList.Add(owner.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (startupId is not null)
        {
            info.ArgumentList.Add("--startup-id");
            info.ArgumentList.Add(startupId);
        }
        return Process.Start(info) ?? throw new InvalidOperationException("无法启动 Steam 代理后台进程。");
    }

    private string ReadStartupError(string fallback) => File.Exists(ErrorPath) ? File.ReadAllText(ErrorPath) : fallback;
    private FileStream AcquireOperationLock() => new(Path.Combine(DataDirectory, "control.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    private FileStream AcquireWorkerLock() => new(Path.Combine(DataDirectory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    private bool IsWorkerActive()
    {
        var path = Path.Combine(DataDirectory, "worker.lock");
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
    }
    private static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static void RequireAdministrator()
    {
        if (!IsAdministrator()) throw new InvalidOperationException("Steam Hosts 模式需要管理员权限，请通过 steam start 启动。");
    }

    private static void FlushDns() => DnsFlushResolverCache();
    [System.Runtime.InteropServices.DllImport("dnsapi.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();
}
