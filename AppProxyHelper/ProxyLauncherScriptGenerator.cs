using System.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AppProxyHelper;

internal static class ProxyLauncherScriptGenerator
{
    public static string GetDefaultOutputDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Environment.CurrentDirectory
            : Path.Combine(localAppData, "EasyProxy", "Launchers");
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
            ? GetDefaultOutputDirectory(fullExecutablePath, displayName)
            : Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);

        var isAntigravity = AntigravityCloudCodeRelay.IsAntigravityExecutable(fullExecutablePath);
        var safeName = GetLauncherScriptName(fullExecutablePath, displayName);
        var scriptPath = Path.Combine(directory, safeName);
        var hiddenLauncherPath = Path.ChangeExtension(scriptPath, ".vbs");
        var antigravityRelayUrl = isAntigravity
            ? AntigravityCloudCodeRelay.RelayUrl
            : null;
        var relayScriptPath = antigravityRelayUrl is null
            ? null
            : AntigravityCloudCodeRelay.WriteRelayScript(directory);
        KnownEditorProxySettings.WriteForExecutable(fullExecutablePath, profile.HttpProxyUri, antigravityRelayUrl);
        if (isAntigravity)
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
        File.WriteAllText(
            hiddenLauncherPath,
            BuildHiddenLauncherScript(scriptPath),
            Encoding.ASCII);
        PointShortcutsToScript(fullExecutablePath, displayName, hiddenLauncherPath);

        return scriptPath;
    }

    private static string GetDefaultOutputDirectory(string executablePath, string displayName)
    {
        if (IsCodexExecutable(executablePath, displayName)
            && TryGetWindowsAppPackageLocalCache(executablePath, out var codexDirectory))
        {
            return Path.Combine(codexDirectory, "EasyProxy");
        }

        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (!string.IsNullOrWhiteSpace(executableDirectory)
            && !IsWindowsAppsPath(executableDirectory))
        {
            return Path.Combine(executableDirectory, "EasyProxy");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Environment.CurrentDirectory
            : Path.Combine(localAppData, "EasyProxy", MakeSafeFileName(displayName));
    }

    private static string GetLauncherScriptName(string executablePath, string displayName)
    {
        if (AntigravityCloudCodeRelay.IsAntigravityExecutable(executablePath))
        {
            return "AntigravityProxy.cmd";
        }

        if (IsAntigravityIdeExecutable(executablePath, displayName))
        {
            return "AntigravityIDEProxy.cmd";
        }

        if (IsCursorExecutable(executablePath, displayName))
        {
            return "CursorProxy.cmd";
        }

        if (IsCodexExecutable(executablePath, displayName))
        {
            return "CodexProxy.cmd";
        }

        return MakeSafeFileName($"{displayName}Proxy.cmd");
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
        var escapedRelayFileName = Path.GetFileName(relayScriptPath).Replace("'", "''", StringComparison.Ordinal);
        var escapedWorkingDirectory = Path.GetDirectoryName(relayScriptPath)?.Replace("'", "''", StringComparison.Ordinal) ?? ".";
        var escapedProxyUri = httpProxyUri.Replace("'", "''", StringComparison.Ordinal);
        var escapedHealthUrl = AntigravityCloudCodeRelay.HealthUrl.Replace("'", "''", StringComparison.Ordinal);
        var shimPath = AntigravityLanguageServerShim.GetCurrentExecutablePath();
        var realLanguageServerPath = AntigravityLanguageServerShim.GetRealLanguageServerPath(executablePath);
        var shimLines = string.IsNullOrWhiteSpace(shimPath)
            ? string.Empty
            : "set \"CODEIUM_LANGUAGE_SERVER_BIN=" + EscapeSetValue(shimPath) + "\"" + Environment.NewLine
                + "set \"EASYPROXY_ANTIGRAVITY_REAL_LS=" + EscapeSetValue(realLanguageServerPath) + "\"" + Environment.NewLine;
        return "set \"EASYPROXY_HTTP_PROXY=" + EscapeSetValue(httpProxyUri) + "\"" + Environment.NewLine
            + shimLines
            + "powershell -NoProfile -ExecutionPolicy Bypass -Command \""
            + "$relay = '"
            + escapedRelayPath
            + "'; $relayFull = [System.IO.Path]::GetFullPath($relay); $relayName = '"
            + escapedRelayFileName
            + "'; $healthUrl = '"
            + escapedHealthUrl
            + "'; "
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
            + "$healthy = $false; $existingHealthy = $false; "
            + "$listener = Get-NetTCPConnection -LocalPort 18990 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1; "
            + "if ($listener) { "
            + "$proc = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $listener.OwningProcess) -ErrorAction SilentlyContinue; "
            + "$cmd = if ($proc) { [string]$proc.CommandLine } else { '' }; "
            + "$isRelay = $cmd -like ('*' + $relayName + '*'); "
            + "$isCurrentRelay = $cmd -like ('*' + $relayFull + '*'); "
            + "$procInfo = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue; "
            + "$scriptInfo = Get-Item -LiteralPath $relayFull -ErrorAction SilentlyContinue; "
            + "$isOld = $procInfo -and $scriptInfo -and $procInfo.StartTime -lt $scriptInfo.LastWriteTime; "
            + "try { $response = Invoke-WebRequest -UseBasicParsing -Uri $healthUrl -TimeoutSec 3; $healthy = ($response.StatusCode -eq 200 -and $response.Content -match 'easyproxy-antigravity-relay') } catch { $healthy = $false }; "
            + "if ($isRelay -and (-not $isCurrentRelay -or -not $healthy -or $isOld)) { Stop-Process -Id $listener.OwningProcess -Force; Start-Sleep -Milliseconds 300 } "
            + "elseif ($healthy) { $existingHealthy = $true } "
            + "else { Write-Error ('Port 18990 is occupied by pid ' + $listener.OwningProcess + ': ' + $cmd); exit 1 } "
            + "}; "
            + "if (-not $existingHealthy) { $env:EASYPROXY_HTTP_PROXY = '"
            + escapedProxyUri
            + "'; Start-Process -WindowStyle Hidden -FilePath $node -WorkingDirectory '"
            + escapedWorkingDirectory
            + "' -ArgumentList (([char]34) + $relayFull + ([char]34)); $healthy = $false; for ($i = 0; $i -lt 20 -and -not $healthy; $i++) { Start-Sleep -Milliseconds 250; try { $response = Invoke-WebRequest -UseBasicParsing -Uri $healthUrl -TimeoutSec 3; $healthy = ($response.StatusCode -eq 200 -and $response.Content -match 'easyproxy-antigravity-relay') } catch { $healthy = $false } }; "
            + "if (-not $healthy) { Write-Error 'Antigravity CloudCode relay failed health check on 127.0.0.1:18990.'; exit 1 } }\"" + Environment.NewLine
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

    private static string BuildHiddenLauncherScript(string scriptPath)
    {
        return "Set shell = CreateObject(\"WScript.Shell\")" + Environment.NewLine
            + "scriptPath = Left(WScript.ScriptFullName, Len(WScript.ScriptFullName) - 4) & \".cmd\"" + Environment.NewLine
            + "shell.Run Chr(34) & scriptPath & Chr(34), 0, False" + Environment.NewLine;
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

    private static void PointShortcutsToScript(string executablePath, string displayName, string scriptPath)
    {
        var shortcutName = GetShortcutName(executablePath, displayName);
        if (string.IsNullOrWhiteSpace(shortcutName))
        {
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        foreach (var shortcutPath in new[]
        {
            string.IsNullOrWhiteSpace(desktop) ? null : Path.Combine(desktop, shortcutName),
            string.IsNullOrWhiteSpace(programs) ? null : Path.Combine(programs, shortcutName)
        })
        {
            if (string.IsNullOrWhiteSpace(shortcutPath))
            {
                continue;
            }

            TryWriteShortcut(shortcutPath, scriptPath, Path.GetDirectoryName(scriptPath) ?? "", executablePath + ",0");
        }
    }

    private static string? GetShortcutName(string executablePath, string displayName)
    {
        if (AntigravityCloudCodeRelay.IsAntigravityExecutable(executablePath))
        {
            return "Antigravity-Proxy.lnk";
        }

        if (IsAntigravityIdeExecutable(executablePath, displayName))
        {
            return "Antigravity IDE-Proxy.lnk";
        }

        if (IsCursorExecutable(executablePath, displayName))
        {
            return "Cursor-Proxy.lnk";
        }

        if (IsCodexExecutable(executablePath, displayName))
        {
            return "Codex-Proxy.lnk";
        }

        return string.IsNullOrWhiteSpace(displayName)
            ? null
            : MakeSafeFileName(displayName + "-Proxy") + ".lnk";
    }

    private static bool IsCursorExecutable(string executablePath, string displayName)
    {
        return Path.GetFileNameWithoutExtension(executablePath).Equals("Cursor", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("Cursor", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAntigravityIdeExecutable(string executablePath, string displayName)
    {
        return Path.GetFileName(executablePath).Equals("Antigravity IDE.exe", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("Antigravity IDE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodexExecutable(string executablePath, string displayName)
    {
        return Path.GetFileNameWithoutExtension(executablePath).Equals("Codex", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("Codex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsAppsPath(string path)
    {
        return path.Contains(
            Path.Combine("Program Files", "WindowsApps"),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetWindowsAppPackageLocalCache(string executablePath, out string localCacheDirectory)
    {
        localCacheDirectory = "";
        var directory = Path.GetDirectoryName(executablePath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var name = Path.GetFileName(directory);
            var separatorIndex = name.IndexOf("__", StringComparison.Ordinal);
            if (separatorIndex > 0)
            {
                var packageNameEnd = name.IndexOf('_', StringComparison.Ordinal);
                if (packageNameEnd > 0)
                {
                    var packageFamily = name[..packageNameEnd] + "_" + name[(separatorIndex + 2)..];
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (!string.IsNullOrWhiteSpace(localAppData))
                    {
                        localCacheDirectory = Path.Combine(localAppData, "Packages", packageFamily, "LocalCache");
                        return true;
                    }
                }
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
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
