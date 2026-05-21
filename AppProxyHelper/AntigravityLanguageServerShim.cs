using System.Diagnostics;
using System.Text;

namespace AppProxyHelper;

internal static class AntigravityLanguageServerShim
{
    private const string RealLanguageServerEnv = "EASYPROXY_ANTIGRAVITY_REAL_LS";
    private const string OriginalLanguageServerName = "language_server.easyproxy-original.exe";
    private const string PristineLanguageServerName = "language_server.easyproxy-pristine.exe";
    private const string IdeLanguageServerName = "language_server_windows_x64.exe";
    private const string IdePristineLanguageServerName = "language_server_windows_x64.easyproxy-pristine.exe";
    private const string IdePatchedLanguageServerName = "language_server_windows_x64.easyproxy-patched.exe";

    public static bool ShouldRun(string[] args)
    {
        return IsLanguageServerInvocation(args)
            && !string.IsNullOrWhiteSpace(ResolveRealLanguageServerPath());
    }

    public static int Run(string[] args)
    {
        var realLanguageServer = ResolveRealLanguageServerPath()
            ?? throw new InvalidOperationException(
                $"{RealLanguageServerEnv} is not set and no sibling Antigravity language server was found.");
        if (!File.Exists(realLanguageServer))
        {
            throw new FileNotFoundException("Antigravity real language server was not found.", realLanguageServer);
        }

        var rewrittenArgs = args.Any(arg => arg.Equals("--standalone", StringComparison.OrdinalIgnoreCase))
            ? RewriteArgs(args)
            : args;
        var startInfo = new ProcessStartInfo(realLanguageServer)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in rewrittenArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }
        ApplyProxyEnvironment(startInfo);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Antigravity real language server.");

        var stdin = ForwardInputAsync(process);
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        var stderr = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());

        process.WaitForExit();
        Task.WaitAll(
            new[] { stdin, stdout, stderr },
            TimeSpan.FromSeconds(2));
        return process.ExitCode;
    }

    public static string? GetCurrentExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(processPath))
        {
            return processPath;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "EasyProxy.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var commandPath = Environment.GetCommandLineArgs().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(commandPath))
        {
            try
            {
                var fullCommandPath = Path.GetFullPath(commandPath);
                if (fullCommandPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(fullCommandPath))
                {
                    return fullCommandPath;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    public static string GetRealLanguageServerPath(string antigravityExecutablePath)
    {
        var binDirectory = GetBinDirectory(antigravityExecutablePath);
        var patchedOriginal = Path.Combine(binDirectory, OriginalLanguageServerName);
        return File.Exists(patchedOriginal)
            ? patchedOriginal
            : Path.Combine(binDirectory, "language_server.exe");
    }

    public static void InstallForAntigravity(string antigravityExecutablePath, string shimExecutablePath)
    {
        var binDirectory = GetBinDirectory(antigravityExecutablePath);
        var languageServerPath = Path.Combine(binDirectory, "language_server.exe");
        var patchedOriginalPath = Path.Combine(binDirectory, OriginalLanguageServerName);
        var pristineOriginalPath = Path.Combine(binDirectory, PristineLanguageServerName);

        if (!File.Exists(pristineOriginalPath))
        {
            File.Copy(
                File.Exists(patchedOriginalPath) ? patchedOriginalPath : languageServerPath,
                pristineOriginalPath,
                overwrite: false);
        }

        File.Copy(pristineOriginalPath, patchedOriginalPath, overwrite: true);
        PatchCloudCodeUrls(patchedOriginalPath);
        File.Copy(shimExecutablePath, languageServerPath, overwrite: true);
    }

    public static string InstallForAntigravityIde(string antigravityIdeExecutablePath)
    {
        var binDirectory = GetIdeBinDirectory(antigravityIdeExecutablePath);
        var languageServerPath = Path.Combine(binDirectory, IdeLanguageServerName);
        var pristineOriginalPath = Path.Combine(binDirectory, IdePristineLanguageServerName);
        var patchedLanguageServerPath = Path.Combine(binDirectory, IdePatchedLanguageServerName);

        if (!File.Exists(languageServerPath))
        {
            throw new FileNotFoundException("Antigravity IDE language server was not found.", languageServerPath);
        }

        if (!File.Exists(pristineOriginalPath))
        {
            File.Copy(languageServerPath, pristineOriginalPath, overwrite: false);
        }

        File.Copy(pristineOriginalPath, patchedLanguageServerPath, overwrite: true);
        PatchCloudCodeUrls(patchedLanguageServerPath);
        return patchedLanguageServerPath;
    }

    public static IEnumerable<KeyValuePair<string, string>> GetEnvironmentVariables(
        string antigravityExecutablePath,
        string shimExecutablePath)
    {
        yield return new KeyValuePair<string, string>("CODEIUM_LANGUAGE_SERVER_BIN", shimExecutablePath);
        yield return new KeyValuePair<string, string>(
            RealLanguageServerEnv,
            GetRealLanguageServerPath(antigravityExecutablePath));
    }

    private static string GetBinDirectory(string antigravityExecutablePath)
    {
        var appDirectory = Path.GetDirectoryName(Path.GetFullPath(antigravityExecutablePath))
            ?? throw new InvalidOperationException("Unable to resolve Antigravity app directory.");
        return Path.Combine(appDirectory, "resources", "bin");
    }

    private static string GetIdeBinDirectory(string antigravityIdeExecutablePath)
    {
        var appDirectory = Path.GetDirectoryName(Path.GetFullPath(antigravityIdeExecutablePath))
            ?? throw new InvalidOperationException("Unable to resolve Antigravity IDE app directory.");
        return Path.Combine(appDirectory, "resources", "app", "extensions", "antigravity", "bin");
    }

    private static string[] RewriteArgs(string[] args)
    {
        var rewritten = args.ToArray();
        for (var i = 0; i < rewritten.Length - 1; i++)
        {
            if (rewritten[i].Equals("--cloud_code_endpoint", StringComparison.OrdinalIgnoreCase))
            {
                rewritten[i + 1] = AntigravityCloudCodeRelay.RelayUrl;
                i++;
            }
        }

        return rewritten;
    }

    private static bool IsLanguageServerInvocation(string[] args)
    {
        return args.Any(arg =>
            arg.Equals("--standalone", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("--stamp", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveRealLanguageServerPath()
    {
        var environmentValue = Environment.GetEnvironmentVariable(RealLanguageServerEnv);
        if (!string.IsNullOrWhiteSpace(environmentValue) && File.Exists(environmentValue))
        {
            return environmentValue;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            processPath = Environment.GetCommandLineArgs().FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(processPath)
            || !Path.GetFileName(processPath).Equals("language_server.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(processPath));
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var sibling = Path.Combine(directory, OriginalLanguageServerName);
        return File.Exists(sibling) ? sibling : null;
    }

    private static void PatchCloudCodeUrls(string filePath)
    {
        var pairs = new[]
        {
            new UrlPatch(
                "https://daily-cloudcode-pa.googleapis.com",
                "http://xxxxxxxxxxxxxxxxxx@127.0.0.1:18990"),
            new UrlPatch(
                "https://cloudcode-pa.googleapis.com",
                "http://xxxxxxxxxxxx@127.0.0.1:18990"),
            new UrlPatch(
                "https://www.googleapis.com/oauth2",
                "http://xxx@127.0.0.1:18990/oauth2"),
            new UrlPatch(
                "https://www.googleapis.com/drive",
                "http://xxx@127.0.0.1:18990/drive"),
            new UrlPatch(
                "https://www.googleapis.com/upload",
                "http://xxx@127.0.0.1:18990/upload")
        };
        var bytes = File.ReadAllBytes(filePath);
        var encoding = Encoding.ASCII;
        foreach (var pair in pairs)
        {
            var source = encoding.GetBytes(pair.Source);
            var target = encoding.GetBytes(pair.Target);
            if (source.Length != target.Length)
            {
                throw new InvalidOperationException("Antigravity URL patch length mismatch.");
            }

            ReplaceAll(bytes, source, target);
        }

        File.WriteAllBytes(filePath, bytes);
    }

    private static void ReplaceAll(byte[] bytes, byte[] source, byte[] target)
    {
        for (var i = 0; i <= bytes.Length - source.Length; i++)
        {
            var match = true;
            for (var j = 0; j < source.Length; j++)
            {
                if (bytes[i + j] != source[j])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            Buffer.BlockCopy(target, 0, bytes, i, target.Length);
            i += source.Length - 1;
        }
    }

    private static void ApplyProxyEnvironment(ProcessStartInfo startInfo)
    {
        var httpProxy = Environment.GetEnvironmentVariable("EASYPROXY_HTTP_PROXY");
        if (string.IsNullOrWhiteSpace(httpProxy))
        {
            httpProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
                ?? "http://127.0.0.1:7890";
        }

        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy", "GRPC_PROXY", "grpc_proxy" })
        {
            startInfo.Environment[name] = httpProxy;
        }

        var noProxy = "localhost,127.0.0.1,::1";
        foreach (var name in new[] { "NO_PROXY", "no_proxy", "NO_GRPC_PROXY", "no_grpc_proxy" })
        {
            startInfo.Environment[name] = noProxy;
        }
    }

    private static async Task ForwardInputAsync(Process process)
    {
        try
        {
            await Console.OpenStandardInput().CopyToAsync(process.StandardInput.BaseStream);
        }
        catch
        {
        }

        try
        {
            process.StandardInput.Close();
        }
        catch
        {
        }
    }

    private sealed record UrlPatch(string Source, string Target);
}
