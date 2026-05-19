using System.Text;

namespace AppProxyHelper;

internal static class ProxyLauncherScriptGenerator
{
    public static string GetDefaultOutputDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop)
            ? Environment.CurrentDirectory
            : desktop;
    }

    public static string CreateScript(
        string executablePath,
        string displayName,
        string proxyUri,
        string? outputDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("目标 exe 不能为空。");
        }

        var fullExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullExecutablePath))
        {
            throw new FileNotFoundException("目标 exe 不存在。", fullExecutablePath);
        }

        var profile = ProxyEnvironmentProfile.Create(proxyUri);
        if (!ProxyEndpoint.IsSupportedScheme(profile.Endpoint.Scheme))
        {
            throw new InvalidOperationException("proxyUri 只支持 http、https、socks 或 socks5。");
        }

        var directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? GetDefaultOutputDirectory()
            : Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);

        var safeName = MakeSafeFileName($"{displayName} Proxy.cmd");
        var scriptPath = Path.Combine(directory, safeName);
        var antigravityRelayUrl = AntigravityCloudCodeRelay.IsAntigravityExecutable(fullExecutablePath)
            ? AntigravityCloudCodeRelay.RelayUrl
            : null;
        var relayScriptPath = antigravityRelayUrl is null
            ? null
            : AntigravityCloudCodeRelay.WriteRelayScript(directory);
        KnownEditorProxySettings.WriteForExecutable(fullExecutablePath, profile.HttpProxyUri, antigravityRelayUrl);
        File.WriteAllText(
            scriptPath,
            BuildScript(fullExecutablePath, profile, relayScriptPath),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return scriptPath;
    }

    public static IReadOnlyList<string> CreateScriptsForInstalledApps(string proxyUri, string? outputDirectory = null)
    {
        var scripts = new List<string>();
        foreach (var app in TargetApplicationCatalog.FindInstalled())
        {
            scripts.Add(CreateScript(app.ExecutablePath, app.Name, proxyUri, outputDirectory));
        }

        return scripts;
    }

    private static string BuildScript(
        string executablePath,
        ProxyEnvironmentProfile profile,
        string? antigravityRelayScriptPath)
    {
        var arguments = ConfigLoader.BuildDefaultChromiumProxyArguments(profile.ChromiumProxyUri)
            .Select(EscapeCommandArgument);
        var joinedArguments = string.Join(" ", arguments);
        var environmentLines = string.Join(
            Environment.NewLine,
            profile.GetEnvironmentVariables()
                .Select(pair => "set \"" + pair.Key + "=" + EscapeSetValue(pair.Value) + "\""));
        var preLaunchLines = string.IsNullOrWhiteSpace(antigravityRelayScriptPath)
            ? string.Empty
            : Environment.NewLine + BuildAntigravityRelayLaunchLine(antigravityRelayScriptPath, profile.HttpProxyUri);

        return $"""
            @echo off
            setlocal
            {environmentLines}
            {preLaunchLines}
            start "" "{EscapePath(executablePath)}" {joinedArguments}
            endlocal
            """;
    }

    private static string BuildAntigravityRelayLaunchLine(string relayScriptPath, string httpProxyUri)
    {
        var escapedRelayPath = relayScriptPath.Replace("'", "''", StringComparison.Ordinal);
        var escapedWorkingDirectory = Path.GetDirectoryName(relayScriptPath)?.Replace("'", "''", StringComparison.Ordinal) ?? ".";
        var escapedProxyUri = httpProxyUri.Replace("'", "''", StringComparison.Ordinal);
        return "set \"EASYPROXY_HTTP_PROXY=" + EscapeSetValue(httpProxyUri) + "\"" + Environment.NewLine
            + "powershell -NoProfile -ExecutionPolicy Bypass -Command \""
            + "$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source; "
            + "if (-not $node) { "
            + "Write-Host 'Node.js is required for the Antigravity CloudCode relay. Installing Node.js LTS with winget...'; "
            + "$winget = (Get-Command winget.exe -ErrorAction SilentlyContinue).Source; "
            + "if (-not $winget) { Write-Error 'winget.exe was not found. Install Node.js LTS from https://nodejs.org, then run this launcher again.'; exit 1 }; "
            + "& $winget install --id OpenJS.NodeJS.LTS -e --accept-source-agreements --accept-package-agreements; "
            + "$nodeCandidates = @($env:ProgramFiles + '\\nodejs\\node.exe', $env:LOCALAPPDATA + '\\Programs\\nodejs\\node.exe'); "
            + "$node = ($nodeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1); "
            + "if (-not $node) { $node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source }; "
            + "if (-not $node) { Write-Error 'Node.js installation finished, but node.exe was not found. Reopen this launcher after Windows refreshes PATH.'; exit 1 } "
            + "}; "
            + "$existing = Get-NetTCPConnection -LocalPort 18990 -ErrorAction SilentlyContinue | Select-Object -First 1; if (-not $existing) { $env:EASYPROXY_HTTP_PROXY = '"
            + escapedProxyUri
            + "'; Start-Process -WindowStyle Hidden -FilePath $node -ArgumentList @('"
            + escapedRelayPath
            + "') -WorkingDirectory '"
            + escapedWorkingDirectory
            + "' }\"" + Environment.NewLine
            + "if errorlevel 1 (" + Environment.NewLine
            + "  echo Failed to prepare the Antigravity CloudCode relay." + Environment.NewLine
            + "  pause" + Environment.NewLine
            + "  exit /b 1" + Environment.NewLine
            + ")";
    }

    private static string EscapeCommandArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string EscapePath(string path)
    {
        return path.Replace("\"", "\"\"", StringComparison.Ordinal);
    }

    private static string EscapeSetValue(string value)
    {
        return value.Replace("%", "%%", StringComparison.Ordinal);
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }
}
