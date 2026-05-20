using System.Text;
using System.Reflection;
using System.Runtime.InteropServices;

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
        if (AntigravityCloudCodeRelay.IsAntigravityExecutable(fullExecutablePath))
        {
            var shimPath = AntigravityLanguageServerShim.GetCurrentExecutablePath();
            if (!string.IsNullOrWhiteSpace(shimPath))
            {
                AntigravityLanguageServerShim.InstallForAntigravity(fullExecutablePath, shimPath);
            }
        }

        File.WriteAllText(
            scriptPath,
            BuildScript(fullExecutablePath, profile, relayScriptPath),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (AntigravityCloudCodeRelay.IsAntigravityExecutable(fullExecutablePath))
        {
            PointAntigravityShortcutsToScript(scriptPath, fullExecutablePath);
        }

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
            : Environment.NewLine + BuildAntigravityRelayLaunchLine(
                executablePath,
                antigravityRelayScriptPath,
                profile.HttpProxyUri);

        return $"""
            @echo off
            setlocal
            {environmentLines}
            {preLaunchLines}
            start "" "{EscapePath(executablePath)}" {joinedArguments}
            endlocal
            """;
    }

    private static string BuildAntigravityRelayLaunchLine(
        string executablePath,
        string relayScriptPath,
        string httpProxyUri)
    {
        var escapedRelayPath = relayScriptPath.Replace("'", "''", StringComparison.Ordinal);
        var escapedWorkingDirectory = Path.GetDirectoryName(relayScriptPath)?.Replace("'", "''", StringComparison.Ordinal) ?? ".";
        var escapedProxyUri = httpProxyUri.Replace("'", "''", StringComparison.Ordinal);
        var shimPath = AntigravityLanguageServerShim.GetCurrentExecutablePath();
        var realLanguageServerPath = AntigravityLanguageServerShim.GetRealLanguageServerPath(executablePath);
        var shimLines = string.IsNullOrWhiteSpace(shimPath)
            ? string.Empty
            : "set \"CODEIUM_LANGUAGE_SERVER_BIN=" + EscapeSetValue(shimPath) + "\"" + Environment.NewLine
                + "set \"EASYPROXY_ANTIGRAVITY_REAL_LS=" + EscapeSetValue(realLanguageServerPath) + "\"" + Environment.NewLine;
        return "set \"EASYPROXY_HTTP_PROXY=" + EscapeSetValue(httpProxyUri) + "\"" + Environment.NewLine
            + shimLines
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
            + "'; Start-Sleep -Milliseconds 800; $existing = Get-NetTCPConnection -LocalPort 18990 -ErrorAction SilentlyContinue | Select-Object -First 1; if (-not $existing) { Write-Error 'Antigravity CloudCode relay failed to listen on 127.0.0.1:18990.'; exit 1 } }\"" + Environment.NewLine
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

    private static void PointAntigravityShortcutsToScript(string scriptPath, string executablePath)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        foreach (var shortcutPath in new[]
        {
            string.IsNullOrWhiteSpace(desktop) ? null : Path.Combine(desktop, "Antigravity.lnk"),
            string.IsNullOrWhiteSpace(programs) ? null : Path.Combine(programs, "Antigravity.lnk")
        })
        {
            if (string.IsNullOrWhiteSpace(shortcutPath))
            {
                continue;
            }

            TryWriteShortcut(shortcutPath, scriptPath, Path.GetDirectoryName(scriptPath) ?? "", executablePath + ",0");
        }
    }

    private static void TryWriteShortcut(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string iconLocation)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath });
            if (shortcut is null)
            {
                return;
            }

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "" });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { iconLocation });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>());
        }
        catch
        {
            // Shortcut repair is best-effort; script generation is still useful without it.
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
