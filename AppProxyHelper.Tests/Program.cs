using AppProxyHelper;

internal static class Program
{
    public static int Main()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("catalog exposes claude and codex CLI tools", CatalogExposesClaudeAndCodex),
            ("launcher script injects proxy environment and starts terminal", LauncherScriptInjectsProxyEnvironment),
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
        AssertContains(script, "set \"ALL_PROXY=socks5://127.0.0.1:7890\"");
        AssertContains(script, "if not exist \"%EASYPROXY_WORKDIR%\" set \"EASYPROXY_WORKDIR=%USERPROFILE%\"");
        AssertContains(script, "where wt.exe >nul 2>nul");
        AssertContains(script, "wt.exe -d \"%EASYPROXY_WORKDIR%\" powershell.exe -NoExit -NoProfile -ExecutionPolicy Bypass -Command \"claude\"");
        AssertContains(script, "start \"\" /D \"%EASYPROXY_WORKDIR%\" powershell.exe -NoExit -NoProfile -ExecutionPolicy Bypass -Command \"claude\"");
        AssertDoesNotContain(script, "Set-Location");
        AssertDoesNotContain(script, "; &");
        AssertDoesNotContain(script, "& claude");
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
