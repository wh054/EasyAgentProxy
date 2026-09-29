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

    public static IReadOnlyList<string> CreateScripts(
        string executablePath,
        string displayName,
        string proxyUri,
        string? outputDirectory = null,
        bool createShortcuts = true)
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
        var usesAntigravityRelay = AntigravityCloudCodeRelay.UsesCloudCodeRelay(fullExecutablePath);
        var antigravityRelayUrl = usesAntigravityRelay
            ? AntigravityCloudCodeRelay.RelayUrl
            : null;
        var relayScriptPath = antigravityRelayUrl is null
            ? null
            : AntigravityCloudCodeRelay.WriteSharedRelayScript();
        var antigravityIdeLanguageServerPath = AntigravityCloudCodeRelay.IsAntigravityIdeExecutable(fullExecutablePath)
            ? AntigravityLanguageServerShim.InstallForAntigravityIde(fullExecutablePath)
            : null;
        KnownEditorProxySettings.WriteForExecutable(
            fullExecutablePath,
            profile.HttpProxyUri,
            antigravityRelayUrl,
            antigravityIdeLanguageServerPath);
        if (isAntigravity)
        {
            var shimPath = AntigravityLanguageServerShim.GetCurrentExecutablePath();
            if (!string.IsNullOrWhiteSpace(shimPath))
            {
                AntigravityLanguageServerShim.InstallForAntigravity(fullExecutablePath, shimPath);
            }
        }

        var isChatGpt = IsChatGptExecutable(fullExecutablePath, displayName);
        var isClaude = IsClaudeExecutable(fullExecutablePath, displayName);
        if (isChatGpt)
        {
            // Store activation does not inherit this launcher's environment.
            // ChatGPT desktop and its app-server load durable values from CODEX_HOME/.env.
            KnownEditorProxySettings.WriteCodexDotEnv(profile);
        }

        if (isChatGpt || isClaude)
        {
            WritePackagedAppLaunchHelper(directory);
        }

        var scripts = new List<string>();
        var proxyScriptPath = WriteLauncherVariant(
            directory,
            GetLauncherScriptName(fullExecutablePath, displayName),
            fullExecutablePath,
            profile,
            relayScriptPath,
            isAntigravity,
            isChatGpt,
            isClaude);
        scripts.Add(proxyScriptPath);
        if (createShortcuts)
        {
            PointShortcutsToScript(
                fullExecutablePath,
                displayName,
                Path.ChangeExtension(proxyScriptPath, ".vbs"));
        }

        RemoveLegacyCodexLauncher(directory, createShortcuts);
        return scripts;
    }

    private static string WriteLauncherVariant(
        string directory,
        string scriptName,
        string executablePath,
        ProxyEnvironmentProfile profile,
        string? antigravityRelayScriptPath,
        bool includeAntigravityLanguageServerShim,
        bool resolveChatGptAtLaunch,
        bool resolveClaudeAtLaunch)
    {
        var scriptPath = Path.Combine(directory, scriptName);
        var hiddenLauncherPath = Path.ChangeExtension(scriptPath, ".vbs");
        File.WriteAllText(
            scriptPath,
            BuildScript(
                executablePath,
                profile,
                antigravityRelayScriptPath,
                includeAntigravityLanguageServerShim,
                resolveChatGptAtLaunch,
                resolveClaudeAtLaunch),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            hiddenLauncherPath,
            BuildHiddenLauncherScript(scriptPath),
            Encoding.ASCII);
        return scriptPath;
    }

    private static string GetDefaultOutputDirectory(string executablePath, string displayName)
    {
        if ((IsChatGptExecutable(executablePath, displayName) || IsClaudeExecutable(executablePath, displayName))
            && TryGetWindowsAppPackageLocalCache(executablePath, out var appDirectory))
        {
            return Path.Combine(appDirectory, "EasyProxy");
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

        if (IsChatGptExecutable(executablePath, displayName))
        {
            return "ChatGPTProxy.cmd";
        }

        if (IsClaudeExecutable(executablePath, displayName))
        {
            return "ClaudeProxy.cmd";
        }

        return MakeSafeFileName($"{displayName}Proxy.cmd");
    }

    public static IReadOnlyList<string> CreateScriptsForInstalledApps(
        string proxyUri,
        string? outputDirectory = null)
    {
        var scripts = new List<string>();
        foreach (var app in TargetApplicationCatalog.FindInstalled())
        {
            scripts.AddRange(CreateScripts(
                app.ExecutablePath,
                app.Name,
                proxyUri,
                outputDirectory));
        }

        return scripts;
    }

    internal static string BuildScript(
        string executablePath,
        ProxyEnvironmentProfile profile,
        string? antigravityRelayScriptPath,
        bool includeAntigravityLanguageServerShim,
        bool resolveChatGptAtLaunch,
        bool resolveClaudeAtLaunch)
    {
        var chromiumProxyUri = resolveClaudeAtLaunch
            ? profile.HttpProxyUri
            : profile.ChromiumProxyUri;
        var arguments = ConfigLoader.BuildDefaultChromiumProxyArguments(chromiumProxyUri)
            .Select(EscapeCommandArgument);
        var joinedArguments = string.Join(" ", arguments);
        var proxyEnvironmentVariables = resolveClaudeAtLaunch
            ? profile.GetHttpProxyEnvironmentVariables()
            : profile.GetEnvironmentVariables();
        if (Path.GetFileName(executablePath).Equals("Cursor.exe", StringComparison.OrdinalIgnoreCase))
        {
            proxyEnvironmentVariables = proxyEnvironmentVariables.Concat(
                new[] { new KeyValuePair<string, string>("NODE_USE_ENV_PROXY", "1") });
        }
        var environmentLines = string.Join(
            Environment.NewLine,
            proxyEnvironmentVariables
                .Select(pair => "set \"" + pair.Key + "=" + EscapeSetValue(pair.Value) + "\""));
        var preLaunchLines = string.IsNullOrWhiteSpace(antigravityRelayScriptPath)
            ? string.Empty
            : Environment.NewLine + BuildAntigravityRelayLaunchLine(
                executablePath,
                antigravityRelayScriptPath,
                profile.HttpProxyUri,
                includeAntigravityLanguageServerShim);
        var targetExecutable = EscapePath(executablePath);
        if (resolveChatGptAtLaunch)
        {
            preLaunchLines = BuildChatGptDbHubLaunchLine() + BuildChatGptAppxResolutionLines(executablePath) + preLaunchLines;
            targetExecutable = "%EASYPROXY_TARGET_EXE%";
        }
        else if (resolveClaudeAtLaunch)
        {
            preLaunchLines = BuildClaudeAppxResolutionLines(executablePath) + BuildClaudeVmServiceStartLine() + preLaunchLines;
            targetExecutable = "%EASYPROXY_TARGET_EXE%";
        }

        // Newer ChatGPT MSIX packages deny CreateProcess on the exe inside
        // WindowsApps, so store-packaged targets go through the PowerShell helper
        // that retries via package activation when the direct launch is denied.
        var launchLine = resolveChatGptAtLaunch || resolveClaudeAtLaunch
            ? $"powershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0{PackagedAppLaunchHelperFileName}\" \"{targetExecutable}\" {joinedArguments}"
            : $"start \"\" \"{targetExecutable}\" {joinedArguments}";

        return $"""
            @echo off
            setlocal
            {environmentLines}
            {preLaunchLines}
            {launchLine}
            endlocal
            """;
    }

    internal const string PackagedAppLaunchHelperFileName = "Start-PackagedApp.ps1";

    internal static string WritePackagedAppLaunchHelper(string directory)
    {
        var path = Path.Combine(directory, PackagedAppLaunchHelperFileName);
        File.WriteAllText(path, BuildPackagedAppLaunchHelperScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    internal static string BuildPackagedAppLaunchHelperScript()
    {
        return """
            param(
                [Parameter(Mandatory = $true, Position = 0)]
                [string]$ExecutablePath,
                [Parameter(ValueFromRemainingArguments = $true)]
                [string[]]$Arguments
            )

            $argumentList = @($Arguments | Where-Object { $_ })

            $package = $null
            $nameMatch = [regex]::Match($ExecutablePath, '\\WindowsApps\\(?<name>[^_\\]+)_')
            if ($nameMatch.Success) {
                $package = Get-AppxPackage -Name $nameMatch.Groups['name'].Value -ErrorAction SilentlyContinue |
                    Sort-Object Version -Descending | Select-Object -First 1
            }
            if (-not $package) {
                $package = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
                    $_.InstallLocation -and $ExecutablePath.StartsWith($_.InstallLocation, [System.StringComparison]::OrdinalIgnoreCase)
                } | Select-Object -First 1
            }

            if ($package) {
                $appId = 'App'
                try {
                    $manifestApp = (Get-AppxPackageManifest -Package $package.PackageFullName).Package.Applications.Application | Select-Object -First 1
                    if ($manifestApp -and $manifestApp.Id) { $appId = $manifestApp.Id }
                } catch {}

                Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            namespace EasyProxy {
                [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
                public interface IApplicationActivationManager {
                    IntPtr ActivateApplication([In] string appUserModelId, [In] string arguments, [In] int options, [Out] out uint processId);
                }
                [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
                public class ApplicationActivationManager {}
                public static class PackagedAppLauncher {
                    public static uint Launch(string appUserModelId, string arguments) {
                        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                        uint processId;
                        manager.ActivateApplication(appUserModelId, arguments, 0, out processId);
                        return processId;
                    }
                }
            }
            '@

                try {
                    $appUserModelId = "$($package.PackageFamilyName)!$appId"
                    [EasyProxy.PackagedAppLauncher]::Launch($appUserModelId, ($argumentList -join ' ')) | Out-Null
                    exit 0
                } catch {
                    # Fall back to direct launch if package activation fails
                }
            }

            try {
                if ($argumentList.Count -gt 0) {
                    Start-Process -FilePath $ExecutablePath -ArgumentList $argumentList -ErrorAction Stop | Out-Null
                } else {
                    Start-Process -FilePath $ExecutablePath -ErrorAction Stop | Out-Null
                }
                exit 0
            } catch {
                Write-Error "Failed to launch $($ExecutablePath): $_"
                exit 1
            }
            """;
    }

    private static string BuildClaudeAppxResolutionLines(string fallbackExecutablePath)
    {
        var escapedFallback = EscapeSetValue(fallbackExecutablePath);
        return "set \"EASYPROXY_TARGET_EXE=" + escapedFallback + "\"" + Environment.NewLine
            + "for /f \"usebackq delims=\" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command \"$pkg = Get-AppxPackage -Name 'Claude' | Sort-Object Version -Descending | Select-Object -First 1; if ($pkg) { Join-Path $pkg.InstallLocation 'app\\claude.exe' }\"`) do set \"EASYPROXY_TARGET_EXE=%%I\"" + Environment.NewLine
            + "if not exist \"%EASYPROXY_TARGET_EXE%\" (" + Environment.NewLine
            + "  echo claude.exe was not found: %EASYPROXY_TARGET_EXE%" + Environment.NewLine
            + "  pause" + Environment.NewLine
            + "  exit /b 1" + Environment.NewLine
            + ")" + Environment.NewLine;
    }

    private static string BuildClaudeVmServiceStartLine()
    {
        return "powershell -NoProfile -ExecutionPolicy Bypass -Command \""
            + "$svc = Get-Service -Name 'CoworkVMService' -ErrorAction SilentlyContinue; "
            + "if ($svc -and $svc.Status -ne 'Running') { "
            + "Start-Service -Name 'CoworkVMService' -ErrorAction SilentlyContinue; "
            + "try { $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(10)) } catch {} "
            + "}\" >nul 2>nul"
            + Environment.NewLine;
    }

    private static string BuildChatGptAppxResolutionLines(string fallbackExecutablePath)
    {
        var escapedFallback = EscapeSetValue(fallbackExecutablePath);
        return "set \"EASYPROXY_TARGET_EXE=" + escapedFallback + "\"" + Environment.NewLine
            + "for /f \"usebackq delims=\" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command \""
            + "$pkg = Get-AppxPackage -Name 'OpenAI.Codex' | Sort-Object Version -Descending | Select-Object -First 1; "
            + "if ($pkg) { "
            + "$chatgpt = Join-Path $pkg.InstallLocation 'app\\ChatGPT.exe'; "
            + "$legacyExecutable = Join-Path $pkg.InstallLocation 'app\\Codex.exe'; "
            + "if (Test-Path $chatgpt) { $chatgpt } elseif (Test-Path $legacyExecutable) { $legacyExecutable } "
            + "}\"`) do set \"EASYPROXY_TARGET_EXE=%%I\"" + Environment.NewLine
            + "if not exist \"%EASYPROXY_TARGET_EXE%\" (" + Environment.NewLine
            + "  echo ChatGPT.exe was not found: %EASYPROXY_TARGET_EXE%" + Environment.NewLine
            + "  pause" + Environment.NewLine
            + "  exit /b 1" + Environment.NewLine
            + ")" + Environment.NewLine;
    }

    private static string BuildChatGptDbHubLaunchLine()
    {
        var scriptPath = @"D:\CodeSpace\GamerUnit_Main\GamerUnitManagement\start-dbhub.bat";
        return "if exist \"" + EscapePath(scriptPath) + "\" call \"" + EscapePath(scriptPath) + "\" >nul 2>nul"
            + Environment.NewLine;
    }

    private static string BuildAntigravityRelayLaunchLine(
        string executablePath,
        string relayScriptPath,
        string httpProxyUri,
        bool includeAntigravityLanguageServerShim)
    {
        var escapedRelayPath = relayScriptPath.Replace("'", "''", StringComparison.Ordinal);
        var escapedRelayFileName = Path.GetFileName(relayScriptPath).Replace("'", "''", StringComparison.Ordinal);
        var escapedWorkingDirectory = Path.GetDirectoryName(relayScriptPath)?.Replace("'", "''", StringComparison.Ordinal) ?? ".";
        var escapedProxyUri = httpProxyUri.Replace("'", "''", StringComparison.Ordinal);
        var escapedHealthUrl = AntigravityCloudCodeRelay.HealthUrl.Replace("'", "''", StringComparison.Ordinal);
        var shimPath = includeAntigravityLanguageServerShim
            ? AntigravityLanguageServerShim.GetCurrentExecutablePath()
            : null;
        var realLanguageServerPath = includeAntigravityLanguageServerShim
            ? AntigravityLanguageServerShim.GetRealLanguageServerPath(executablePath)
            : null;
        var shimLines = string.IsNullOrWhiteSpace(shimPath) || string.IsNullOrWhiteSpace(realLanguageServerPath)
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
            + "$usesCurrentRelay = $cmd -like ('*' + $relayFull + '*'); "
            + "$procInfo = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue; "
            + "$scriptInfo = Get-Item -LiteralPath $relayFull -ErrorAction SilentlyContinue; "
            + "$isOld = $procInfo -and $scriptInfo -and $procInfo.StartTime -lt $scriptInfo.LastWriteTime; "
            + "try { $response = Invoke-WebRequest -UseBasicParsing -Uri $healthUrl -TimeoutSec 3; $healthy = ($response.StatusCode -eq 200 -and $response.Content -match 'easyproxy-antigravity-relay') } catch { $healthy = $false }; "
            + "if ($isRelay -and (-not $healthy -or $isOld -or -not $usesCurrentRelay)) { Stop-Process -Id $listener.OwningProcess -Force; Start-Sleep -Milliseconds 300 } "
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

    private static void PointShortcutsToScript(
        string executablePath,
        string displayName,
        string scriptPath)
    {
        var shortcutName = GetShortcutName(executablePath, displayName);
        if (string.IsNullOrWhiteSpace(shortcutName))
        {
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var interactiveLocalAppData = FindInteractiveLocalAppData(desktop);
        var shortcutTargetPath = RebaseLocalAppDataPath(scriptPath, interactiveLocalAppData);
        var workingDirectory = Path.GetDirectoryName(shortcutTargetPath) ?? "";
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

            TryWriteShortcut(shortcutPath, shortcutTargetPath, workingDirectory, executablePath + ",0");
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

        if (IsChatGptExecutable(executablePath, displayName))
        {
            return "ChatGPT-Proxy.lnk";
        }

        if (IsClaudeExecutable(executablePath, displayName))
        {
            return "Claude-Proxy.lnk";
        }

        return string.IsNullOrWhiteSpace(displayName)
            ? null
            : MakeSafeFileName(displayName + "-Proxy") + ".lnk";
    }

    internal static string RebaseLocalAppDataPath(string path, string? interactiveLocalAppData)
    {
        if (string.IsNullOrWhiteSpace(interactiveLocalAppData))
        {
            return path;
        }

        var marker = Path.DirectorySeparatorChar + Path.Combine("AppData", "Local") + Path.DirectorySeparatorChar;
        var markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return path;
        }

        var suffix = path[(markerIndex + marker.Length)..];
        return Path.Combine(interactiveLocalAppData, suffix);
    }

    private static string? FindInteractiveLocalAppData(string desktopDirectory)
    {
        if (string.IsNullOrWhiteSpace(desktopDirectory))
        {
            return null;
        }

        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(desktopDirectory);
        }
        catch
        {
            return null;
        }

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "AppData", "Local");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void RemoveLegacyCodexLauncher(string directory, bool removeShortcuts)
    {
        TryDeleteGeneratedFile(Path.Combine(directory, "CodexProxy.cmd"));
        TryDeleteGeneratedFile(Path.Combine(directory, "CodexProxy.vbs"));
        TryDeleteGeneratedFile(Path.Combine(directory, "Start-CodexProxySkin.ps1"));
        if (removeShortcuts)
        {
            RemoveShortcuts("Codex-Proxy.lnk");
        }
    }

    private static void RemoveShortcuts(string shortcutName)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        foreach (var directory in new[] { desktop, programs })
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                TryDeleteGeneratedFile(Path.Combine(directory, shortcutName));
            }
        }
    }

    private static void TryDeleteGeneratedFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is best-effort; generating the current launchers still succeeds.
        }
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

    private static bool IsChatGptExecutable(string executablePath, string displayName)
    {
        var fileName = Path.GetFileNameWithoutExtension(executablePath);
        return fileName.Equals("Codex", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("Codex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClaudeExecutable(string executablePath, string displayName)
    {
        return Path.GetFileNameWithoutExtension(executablePath).Equals("claude", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains("Claude", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("Claude", StringComparison.OrdinalIgnoreCase);
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
