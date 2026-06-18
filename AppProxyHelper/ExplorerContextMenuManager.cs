using Microsoft.Win32;

namespace AppProxyHelper;

internal enum ExplorerContextMenuTarget
{
    DirectoryBackground,
    DirectoryItem
}

internal sealed record CliContextMenuToolStatus(
    CliToolDefinition Tool,
    bool BackgroundInstalled,
    bool DirectoryInstalled,
    bool CommandAvailable,
    string ScriptPath)
{
    public bool IsInstalled => BackgroundInstalled && DirectoryInstalled;
}

internal static class ExplorerContextMenuManager
{
    private const string DirectoryBackgroundPrefix = @"Software\Classes\Directory\Background\shell";
    private const string DirectoryItemPrefix = @"Software\Classes\Directory\shell";

    public static IReadOnlyList<string> Install(IEnumerable<CliToolDefinition> tools, string proxyUri)
    {
        var profile = ProxyEnvironmentProfile.Create(proxyUri);
        var scriptPaths = new List<string>();

        foreach (var tool in tools)
        {
            var scriptPath = CliProxyLauncherScriptGenerator.WriteScript(tool, profile);
            scriptPaths.Add(scriptPath);

            WriteMenuKey(tool, scriptPath, ExplorerContextMenuTarget.DirectoryBackground);
            WriteMenuKey(tool, scriptPath, ExplorerContextMenuTarget.DirectoryItem);
        }

        return scriptPaths;
    }

    public static void Uninstall(IEnumerable<CliToolDefinition> tools)
    {
        foreach (var tool in tools)
        {
            DeleteMenuKey(tool, ExplorerContextMenuTarget.DirectoryBackground);
            DeleteMenuKey(tool, ExplorerContextMenuTarget.DirectoryItem);
            CliProxyLauncherScriptGenerator.DeleteScript(tool);
        }
    }

    public static IReadOnlyList<CliContextMenuToolStatus> GetStatuses(IEnumerable<CliToolDefinition> tools)
    {
        return tools
            .Select(tool => new CliContextMenuToolStatus(
                tool,
                MenuKeyExists(tool, ExplorerContextMenuTarget.DirectoryBackground),
                MenuKeyExists(tool, ExplorerContextMenuTarget.DirectoryItem),
                CommandExistsOnPath(tool.CommandName),
                CliProxyLauncherScriptGenerator.GetScriptPath(tool)))
            .ToList();
    }

    internal static string BuildCommand(string scriptPath, ExplorerContextMenuTarget target)
    {
        var explorerToken = target == ExplorerContextMenuTarget.DirectoryBackground ? "%V" : "%1";
        return "cmd.exe /d /c \"\"" + scriptPath + "\" \"" + explorerToken + "\"\"";
    }

    internal static string GetRegistrySubKeyPath(
        CliToolDefinition tool,
        ExplorerContextMenuTarget target,
        bool includeCommandKey = false)
    {
        var prefix = target == ExplorerContextMenuTarget.DirectoryBackground
            ? DirectoryBackgroundPrefix
            : DirectoryItemPrefix;
        var path = prefix + "\\" + tool.RegistryName;
        return includeCommandKey ? path + "\\command" : path;
    }

    private static void WriteMenuKey(
        CliToolDefinition tool,
        string scriptPath,
        ExplorerContextMenuTarget target)
    {
        using var key = Registry.CurrentUser.CreateSubKey(GetRegistrySubKeyPath(tool, target));
        if (key is null)
        {
            throw new InvalidOperationException($"无法创建右键菜单注册表项: {tool.MenuTitle}");
        }

        key.SetValue("", tool.MenuTitle, RegistryValueKind.String);
        key.SetValue("Icon", Application.ExecutablePath, RegistryValueKind.String);

        using var commandKey = Registry.CurrentUser.CreateSubKey(
            GetRegistrySubKeyPath(tool, target, includeCommandKey: true));
        if (commandKey is null)
        {
            throw new InvalidOperationException($"无法创建右键菜单命令注册表项: {tool.MenuTitle}");
        }

        commandKey.SetValue("", BuildCommand(scriptPath, target), RegistryValueKind.String);
    }

    private static void DeleteMenuKey(CliToolDefinition tool, ExplorerContextMenuTarget target)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(GetRegistrySubKeyPath(tool, target), throwOnMissingSubKey: false);
        }
        catch
        {
            // Keep uninstall best-effort so one damaged key does not block the others.
        }
    }

    private static bool MenuKeyExists(CliToolDefinition tool, ExplorerContextMenuTarget target)
    {
        using var key = Registry.CurrentUser.OpenSubKey(GetRegistrySubKeyPath(tool, target));
        return key is not null;
    }

    private static bool CommandExistsOnPath(string commandName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var candidateFileName in GetCommandCandidateFileNames(commandName))
        {
            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(Path.Combine(directory.Trim(), candidateFileName)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> GetCommandCandidateFileNames(string commandName)
    {
        if (Path.HasExtension(commandName))
        {
            yield return commandName;
            yield break;
        }

        yield return commandName;
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathExt)
            ? new[] { ".exe", ".cmd", ".bat", ".ps1" }
            : pathExt.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var extension in extensions)
        {
            yield return commandName + extension;
        }
    }
}
