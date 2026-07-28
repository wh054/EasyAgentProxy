using AppProxyHelper;
using System.Text.Json.Nodes;

internal static class Program
{
    public static int Main()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("catalog exposes claude and codex CLI tools", CatalogExposesClaudeAndCodex),
            ("launcher script injects proxy environment and starts terminal", LauncherScriptInjectsProxyEnvironment),
            ("codex launcher script keeps all proxy environment", CodexLauncherScriptKeepsAllProxyEnvironment),
            ("codex dotenv preserves unrelated values and replaces proxy values", CodexDotEnvPreservesUnrelatedValues),
            ("config persists Codex QQ skin preference", ConfigPersistsCodexQqSkinPreference),
            ("Codex QQ skin preference survives temporary uninstall", CodexQqSkinPreferenceSurvivesTemporaryUninstall),
            ("codex desktop launcher combines proxy and QQ skin", CodexDesktopLauncherCombinesProxyAndQqSkin),
            ("claude desktop launcher uses http proxy argument", ClaudeDesktopLauncherUsesHttpProxyArgument),
            ("claude code settings receive http proxy env", ClaudeCodeSettingsReceiveHttpProxyEnv),
            ("context menu commands pass explorer directory tokens", ContextMenuCommandsPassExplorerDirectoryTokens)
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static void CatalogExposesClaudeAndCodex()
    {
        var tools = CliToolCatalog.All;
        AssertEqual(2, tools.Count, "tool count");

        var claude = tools.Single(tool => tool.Id == "claude-code");
        AssertEqual("Claude Code CLI", claude.DisplayName, "claude display name");
        AssertEqual("claude", claude.CommandName, "claude command");
        AssertEqual("在此处打开 Claude", claude.MenuTitle, "claude menu title");

        var codex = tools.Single(tool => tool.Id == "codex-cli");
        AssertEqual("Codex CLI", codex.DisplayName, "codex display name");
        AssertEqual("codex", codex.CommandName, "codex command");
        AssertEqual("在此处打开 Codex", codex.MenuTitle, "codex menu title");
    }

    private static void LauncherScriptInjectsProxyEnvironment()
    {
        var tool = CliToolCatalog.All.Single(tool => tool.Id == "claude-code");
        var profile = ProxyEnvironmentProfile.Create("socks5://127.0.0.1:7890");
        var script = CliProxyLauncherScriptGenerator.BuildScript(tool, profile);

        AssertContains(script, "set \"HTTP_PROXY=http://127.0.0.1:7890\"");
        AssertContains(script, "set \"HTTPS_PROXY=http://127.0.0.1:7890\"");
        AssertDoesNotContain(script, "set \"ALL_PROXY=socks5://127.0.0.1:7890\"");
        AssertDoesNotContain(script, "set \"all_proxy=socks5://127.0.0.1:7890\"");
        AssertContains(script, "if not exist \"%EASYPROXY_WORKDIR%\" set \"EASYPROXY_WORKDIR=%USERPROFILE%\"");
        AssertContains(script, "where wt.exe >nul 2>nul");
        AssertContains(script, "wt.exe -d \"%EASYPROXY_WORKDIR%\" powershell.exe -NoExit -NoProfile -ExecutionPolicy Bypass -Command \"claude\"");
        AssertContains(script, "start \"\" /D \"%EASYPROXY_WORKDIR%\" powershell.exe -NoExit -NoProfile -ExecutionPolicy Bypass -Command \"claude\"");
        AssertDoesNotContain(script, "Set-Location");
        AssertDoesNotContain(script, "; &");
        AssertDoesNotContain(script, "& claude");
    }

    private static void CodexLauncherScriptKeepsAllProxyEnvironment()
    {
        var tool = CliToolCatalog.All.Single(tool => tool.Id == "codex-cli");
        var profile = ProxyEnvironmentProfile.Create("socks5://127.0.0.1:7890");
        var script = CliProxyLauncherScriptGenerator.BuildScript(tool, profile);

        AssertContains(script, "set \"HTTP_PROXY=http://127.0.0.1:7890\"");
        AssertContains(script, "set \"HTTPS_PROXY=http://127.0.0.1:7890\"");
        AssertContains(script, "set \"ALL_PROXY=socks5://127.0.0.1:7890\"");
        AssertContains(script, "set \"all_proxy=socks5://127.0.0.1:7890\"");
        AssertContains(script, "wt.exe -d \"%EASYPROXY_WORKDIR%\" powershell.exe -NoExit -NoProfile -ExecutionPolicy Bypass -Command \"codex\"");
    }

    private static void CodexDotEnvPreservesUnrelatedValues()
    {
        WithTempDirectory(tempDirectory =>
        {
            var dotEnvPath = Path.Combine(tempDirectory, ".env");
            File.WriteAllText(dotEnvPath, "KEEP_ME=1\nexport HTTP_PROXY=\"http://old-proxy:8080\"\nall_proxy=socks5://old-proxy:1080\nHTTPS PROXY=http://invalid-proxy:8080\n");

            KnownEditorProxySettings.WriteCodexDotEnv(
                dotEnvPath,
                ProxyEnvironmentProfile.Create("socks5://127.0.0.1:7890"));

            var dotEnv = File.ReadAllText(dotEnvPath);
            AssertContains(dotEnv, "KEEP_ME=1");
            AssertContains(dotEnv, "HTTP_PROXY=\"http://127.0.0.1:7890\"");
            AssertContains(dotEnv, "HTTPS_PROXY=\"http://127.0.0.1:7890\"");
            AssertContains(dotEnv, "ALL_PROXY=\"socks5://127.0.0.1:7890\"");
            AssertContains(dotEnv, "all_proxy=\"socks5://127.0.0.1:7890\"");
            AssertDoesNotContain(dotEnv, "old-proxy");
            AssertDoesNotContain(dotEnv, "invalid-proxy");
        });
    }

    private static void CodexDesktopLauncherCombinesProxyAndQqSkin()
    {
        var profile = ProxyEnvironmentProfile.Create("socks5://127.0.0.1:7890");
        var skinStartScript = @"C:\Users\me\AppData\Local\CodexQQSkin\engine\scripts\windows\start-qq-skin-windows.ps1";
        var launcher = ProxyLauncherScriptGenerator.BuildScript(
            @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0.0.0_x64__test\app\ChatGPT.exe",
            profile,
            antigravityRelayScriptPath: null,
            includeAntigravityLanguageServerShim: false,
            resolveCodexAtLaunch: true,
            resolveClaudeAtLaunch: false,
            codexQqSkinStartScript: skinStartScript);
        var helper = ProxyLauncherScriptGenerator.BuildCodexProxySkinHelperScript();
        var proxyOnlyLauncher = ProxyLauncherScriptGenerator.BuildScript(
            @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0.0.0_x64__test\app\ChatGPT.exe",
            profile,
            antigravityRelayScriptPath: null,
            includeAntigravityLanguageServerShim: false,
            resolveCodexAtLaunch: true,
            resolveClaudeAtLaunch: false,
            codexQqSkinStartScript: null);

        AssertContains(launcher, ProxyLauncherScriptGenerator.CodexProxySkinHelperFileName);
        AssertContains(launcher, skinStartScript);
        AssertDoesNotContain(proxyOnlyLauncher, ProxyLauncherScriptGenerator.CodexProxySkinHelperFileName);
        AssertContains(helper, "--proxy-server=$ProxyServer");
        AssertContains(helper, "--remote-debugging-address=127.0.0.1");
        AssertContains(helper, "--remote-debugging-port=$Port");
        AssertContains(helper, "Wait-CodexCdpEndpoint -Port $Port");
        AssertContains(helper, "& $SkinStartScript -Port $Port -SkinMode 'qq'");
        AssertContains(helper, "Codex QQ Skin is unavailable; starting proxy-only Codex.");
        AssertContains(helper, "& $packagedAppLauncher $ExecutablePath @proxyOnlyArguments");
    }

    private static void ConfigPersistsCodexQqSkinPreference()
    {
        WithTempDirectory(tempDirectory =>
        {
            var configPath = Path.Combine(tempDirectory, "app-proxy.json");
            ConfigLoader.Save(configPath, new AppProxyConfig
            {
                ProxyUri = "socks5://127.0.0.1:7890",
                EnableCodexQqSkin = true
            });

            var loaded = ConfigLoader.Load(configPath).Value;
            AssertEqual(true, loaded.EnableCodexQqSkin, "Codex QQ Skin preference");
            AssertContains(File.ReadAllText(configPath), "\"enableCodexQqSkin\": true");
        });
    }

    private static void CodexQqSkinPreferenceSurvivesTemporaryUninstall()
    {
        var state = AppProxyGuiForm.ResolveCodexQqSkinOptionState(
            configuredPreference: true,
            skinInstalled: false,
            codexAvailable: true,
            codexSelected: true);

        AssertEqual(true, state.Checked, "stored preference remains checked");
        AssertEqual(true, state.Enabled, "option remains editable before reinstall");
        AssertContains(state.Text, "未安装");
        AssertContains(state.Text, "仅代理");
    }

    private static void ClaudeDesktopLauncherUsesHttpProxyArgument()
    {
        var profile = ProxyEnvironmentProfile.Create("socks5://127.0.0.1:7890");
        var script = ProxyLauncherScriptGenerator.BuildScript(
            @"C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__pzs8sxrjxfjjc\app\claude.exe",
            profile,
            antigravityRelayScriptPath: null,
            includeAntigravityLanguageServerShim: false,
            resolveCodexAtLaunch: false,
            resolveClaudeAtLaunch: true);

        AssertContains(script, "\"--proxy-server=http://127.0.0.1:7890\"");
        AssertContains(script, "Get-Service -Name 'CoworkVMService'");
        AssertDoesNotContain(script, "\"--proxy-server=socks5://127.0.0.1:7890\"");
        AssertDoesNotContain(script, "set \"ALL_PROXY=socks5://127.0.0.1:7890\"");
    }

    private static void ClaudeCodeSettingsReceiveHttpProxyEnv()
    {
        WithTempDirectory(tempDirectory =>
        {
            var settingsPath = Path.Combine(tempDirectory, "settings.json");
            File.WriteAllText(settingsPath, """
                {
                  "env": {
                    "KEEP_ME": "1",
                    "ALL_PROXY": "socks5://127.0.0.1:7890",
                    "all_proxy": "socks5://127.0.0.1:7890",
                    "CLOUDSDK_PROXY_TYPE": "http"
                  },
                  "model": "opus"
                }
                """);

            KnownEditorProxySettings.WriteClaudeCodeSettings(settingsPath, "http://127.0.0.1:7890");

            var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            var env = root["env"]!.AsObject();
            AssertEqual("1", env["KEEP_ME"]!.GetValue<string>(), "existing env preserved");
            AssertEqual("opus", root["model"]!.GetValue<string>(), "existing setting preserved");
            AssertEqual("http://127.0.0.1:7890", env["HTTP_PROXY"]!.GetValue<string>(), "HTTP_PROXY");
            AssertEqual("http://127.0.0.1:7890", env["HTTPS_PROXY"]!.GetValue<string>(), "HTTPS_PROXY");
            AssertEqual("localhost,127.0.0.1,::1", env["NO_PROXY"]!.GetValue<string>(), "NO_PROXY");
            AssertEqual(false, env.ContainsKey("ALL_PROXY"), "ALL_PROXY removed");
            AssertEqual(false, env.ContainsKey("all_proxy"), "all_proxy removed");
            AssertEqual(false, env.ContainsKey("CLOUDSDK_PROXY_TYPE"), "CLOUDSDK_PROXY_TYPE removed");
        });
    }

    private static void ContextMenuCommandsPassExplorerDirectoryTokens()
    {
        var commandForBackground = ExplorerContextMenuManager.BuildCommand(
            @"C:\Users\me\AppData\Local\EasyProxy\CliContextMenu\OpenClaudeHere.cmd",
            ExplorerContextMenuTarget.DirectoryBackground);
        var commandForDirectory = ExplorerContextMenuManager.BuildCommand(
            @"C:\Users\me\AppData\Local\EasyProxy\CliContextMenu\OpenClaudeHere.cmd",
            ExplorerContextMenuTarget.DirectoryItem);

        AssertContains(commandForBackground, "\"%V\"");
        AssertContains(commandForDirectory, "\"%1\"");
        AssertContains(commandForBackground, "cmd.exe /d /c");
    }

    private static void WithTempDirectory(Action<string> body)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "EasyProxyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            body(tempDirectory);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'");
        }
    }

    private static void AssertContains(string text, string expected)
    {
        if (!text.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"expected to find '{expected}'");
        }
    }

    private static void AssertDoesNotContain(string text, string unexpected)
    {
        if (text.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"did not expect to find '{unexpected}'");
        }
    }
}
