using AppProxyHelper;

internal static class SteamLauncherTests
{
    public static IEnumerable<(string Name, Action Body)> Cases =>
    [
        ("Steam launcher renders the dedicated session command", LauncherRendersDedicatedSessionCommand),
        ("Steam launcher renders quoted literal CMD metacharacters", LauncherRendersLiteralMetacharacters),
        ("Steam launcher renders the dotnet assembly helper fallback", LauncherRendersAssemblyHelper),
        ("Steam hidden launcher runs a fixed relative command without expanding its directory", HiddenLauncherUsesFixedRelativeCommand),
        ("Steam catalog candidate construction preserves priority and removes duplicates", CatalogCandidateConstructionPreservesPriority),
        ("Steam process start info strips generated Chromium flags and preserves Steam arguments", ProcessStartInfoPreservesSteamArguments)
    ];

    private static void LauncherRendersDedicatedSessionCommand()
    {
        const string executable = @"C:\EasyProxy Examples\Steam\steam.exe";
        var profile = ProxyEnvironmentProfile.Create("socks5://127.0.0.1:7890");
        var script = ProxyLauncherScriptGenerator.BuildSteamScript(executable, profile, @"C:\EasyProxy Examples\EasyProxy.exe");

        Contains(script, "steam launch --target \"%EASYPROXY_STEAM_TARGET%\" --proxy \"%EASYPROXY_STEAM_PROXY%\"");
        Contains(script, "set \"EASYPROXY_STEAM_TARGET=" + executable + "\"");
        Contains(script, "set \"EASYPROXY_STEAM_PROXY=" + profile.Endpoint.ToUriString() + "\"");
        Contains(script, "cd /d \"%EASYPROXY_STEAM_DIRECTORY%\"");
        Contains(script, "exit /b %ERRORLEVEL%");
        DoesNotContain(script, "--proxy-server=");
        Equal("Steam-Proxy.lnk", ProxyLauncherScriptGenerator.GetShortcutName(executable, "Steam"), "shared shortcut naming convention");
    }

    private static void LauncherRendersLiteralMetacharacters()
    {
        const string executable = @"C:\EasyProxy Examples\Steam 空格 & ! %PATH% ^ (游戏)\steam.exe";
        var profile = ProxyEnvironmentProfile.Create("socks5://user:p%25ss!%40x@127.0.0.1:7890");
        var script = ProxyLauncherScriptGenerator.BuildSteamScript(executable, profile, @"C:\Easy Proxy & Tools\EasyProxy.exe");

        Contains(script, "setlocal EnableExtensions DisableDelayedExpansion");
        Contains(script, "chcp 65001");
        Contains(script, "set \"EASYPROXY_STEAM_TARGET=" + executable.Replace("%", "%%", StringComparison.Ordinal) + "\"");
        Contains(script, "set \"EASYPROXY_STEAM_PROXY=" + profile.Endpoint.ToUriString().Replace("%", "%%", StringComparison.Ordinal) + "\"");
        Contains(script, "\"%EASYPROXY_STEAM_HELPER%\" steam launch");
        DoesNotContain(script, "call ");
        DoesNotContain(script, "start ");
    }

    private static void LauncherRendersAssemblyHelper()
    {
        var profile = ProxyEnvironmentProfile.Create("http://127.0.0.1:7890");
        var script = ProxyLauncherScriptGenerator.BuildSteamScript(
            @"C:\EasyProxy Examples\Steam\steam.exe", profile,
            @"C:\Program Files\dotnet\dotnet.exe", @"C:\Easy Proxy Examples\EasyProxy.dll");

        Contains(script, "set \"EASYPROXY_STEAM_HELPER=C:\\Program Files\\dotnet\\dotnet.exe\"");
        Contains(script, "set \"EASYPROXY_STEAM_ASSEMBLY=C:\\Easy Proxy Examples\\EasyProxy.dll\"");
        Contains(script, "\"%EASYPROXY_STEAM_HELPER%\" \"%EASYPROXY_STEAM_ASSEMBLY%\" steam launch");
    }

    private static void HiddenLauncherUsesFixedRelativeCommand()
    {
        var script = ProxyLauncherScriptGenerator.BuildSteamHiddenLauncherScript();

        Contains(script, "Set fileSystem = CreateObject(\"Scripting.FileSystemObject\")");
        Contains(script, "shell.CurrentDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)");
        Contains(script, "exitCode = shell.Run(\".\\SteamProxy.cmd\", 0, True)");
        Contains(script, "If Err.Number = 0 Then");
        Contains(script, "MsgBox");
        Contains(script, "WScript.Quit exitCode");
        DoesNotContain(script, "scriptPath");
        DoesNotContain(script, "shell.Run(Chr(34)");
        DoesNotContain(script, "socks5://");
    }

    private static void CatalogCandidateConstructionPreservesPriority()
    {
        const string executable = @"C:\EasyProxy Examples\registry-exe\steam.exe";
        const string steamDirectory = @"C:\EasyProxy Examples\registry-directory";
        const string programFilesDirectory = @"C:\EasyProxy Examples\Program Files (x86)";
        var candidates = TargetApplicationCatalog.BuildSteamCandidates(
            [null, "", "relative\\steam.exe", "\0", " \"" + executable.Replace('\\', '/') + "\" ", executable.ToUpperInvariant()],
            [null, steamDirectory, steamDirectory],
            [programFilesDirectory, programFilesDirectory]).ToArray();

        EqualArguments(
            [executable, Path.Combine(steamDirectory, "steam.exe"), Path.Combine(programFilesDirectory, "Steam", "steam.exe")],
            candidates);
        Equal(true, TargetApplicationCatalog.IsSteamExecutable(executable.ToUpperInvariant()), "Steam recognition is case insensitive");
        Equal(false, TargetApplicationCatalog.IsSteamExecutable(Path.Combine(steamDirectory, "steamwebhelper.exe")), "browser subprocess is not the Steam launcher");
    }

    private static void ProcessStartInfoPreservesSteamArguments()
    {
        const string executable = @"C:\EasyProxy Examples\Steam 空格 & !\steam.exe";
        var steamArguments = new[] { "-silent", "-applaunch", "570", "+connect", "127.0.0.1:27015", "steam://open/friends", "参数 空格 & ! %PATH%" };
        var chromiumArguments = ConfigLoader.BuildDefaultChromiumProxyArguments("socks5://127.0.0.1:7890");
        var info = SteamApplicationLauncher.CreateStartInfo(executable, chromiumArguments.Concat(steamArguments));

        Equal(executable, info.FileName, "Steam executable remains a file name");
        Equal(Path.GetDirectoryName(executable), info.WorkingDirectory, "working directory defaults to the installation directory");
        Equal(false, info.UseShellExecute, "shell argument reinterpretation is disabled");
        EqualArguments(steamArguments, info.ArgumentList);
        Equal("", info.Arguments, "individual Steam arguments remain in ArgumentList");

        const string workingDirectory = @"C:\EasyProxy Examples\Custom work directory";
        var custom = SteamApplicationLauncher.CreateStartInfo(executable, steamArguments, workingDirectory);
        Equal(workingDirectory, custom.WorkingDirectory, "explicit working directory preserved");
        EqualArguments(steamArguments, custom.ArgumentList);
    }

    private static void EqualArguments(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidOperationException("The constructed Steam argument list changed order or values.");
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'");
    }

    private static void Contains(string text, string expected)
    {
        if (!text.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected to find '{expected}'.");
    }

    private static void DoesNotContain(string text, string unexpected)
    {
        if (text.Contains(unexpected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected '{unexpected}'.");
    }
}
