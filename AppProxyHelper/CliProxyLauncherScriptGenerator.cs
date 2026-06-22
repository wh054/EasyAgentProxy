using System.Text;

namespace AppProxyHelper;

internal static class CliProxyLauncherScriptGenerator
{
    public static string GetDefaultOutputDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(Environment.CurrentDirectory, "CliContextMenu")
            : Path.Combine(localAppData, "EasyProxy", "CliContextMenu");
    }

    public static string GetScriptPath(CliToolDefinition tool, string? outputDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? GetDefaultOutputDirectory()
            : Path.GetFullPath(outputDirectory);
        return Path.Combine(directory, tool.ScriptFileName);
    }

    public static string WriteScript(
        CliToolDefinition tool,
        ProxyEnvironmentProfile profile,
        string? outputDirectory = null)
    {
        var scriptPath = GetScriptPath(tool, outputDirectory);
        var directory = Path.GetDirectoryName(scriptPath)
            ?? throw new InvalidOperationException("无法确定 CLI 右键菜单脚本目录。");

        Directory.CreateDirectory(directory);
        File.WriteAllText(
            scriptPath,
            BuildScript(tool, profile),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return scriptPath;
    }

    public static void DeleteScript(CliToolDefinition tool, string? outputDirectory = null)
    {
        var scriptPath = GetScriptPath(tool, outputDirectory);
        try
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
        catch
        {
            // Best effort only. The registry uninstall is the authoritative menu removal.
        }
    }

    internal static string BuildScript(CliToolDefinition tool, ProxyEnvironmentProfile profile)
    {
        var proxyEnvironmentVariables = tool.Id.Equals("claude-code", StringComparison.OrdinalIgnoreCase)
            ? profile.GetHttpProxyEnvironmentVariables()
            : profile.GetEnvironmentVariables();
        var environmentLines = string.Join(
            Environment.NewLine,
            proxyEnvironmentVariables
                .Select(pair => "set \"" + pair.Key + "=" + EscapeSetValue(pair.Value) + "\""));
        var command = GetPowerShellCommand(tool);

        return $"""
            @echo off
            setlocal
            set "EASYPROXY_WORKDIR=%~1"
            if "%EASYPROXY_WORKDIR%"=="" set "EASYPROXY_WORKDIR=%USERPROFILE%"
            if not exist "%EASYPROXY_WORKDIR%" set "EASYPROXY_WORKDIR=%USERPROFILE%"
            {environmentLines}
            where wt.exe >nul 2>nul
            if not errorlevel 1 (
              start "" wt.exe -d "%EASYPROXY_WORKDIR%" powershell.exe -NoExit -NoProfile -ExecutionPolicy Bypass -Command "{command}"
            ) else (
              start "" /D "%EASYPROXY_WORKDIR%" powershell.exe -NoExit -NoProfile -ExecutionPolicy Bypass -Command "{command}"
            )
            endlocal
            """;
    }

    private static string GetPowerShellCommand(CliToolDefinition tool)
    {
        var command = tool.CommandName.Trim();
        if (string.IsNullOrWhiteSpace(command)
            || command.Any(char.IsWhiteSpace)
            || command.Contains("\"", StringComparison.Ordinal)
            || command.Contains("&", StringComparison.Ordinal)
            || command.Contains(";", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Invalid CLI command name: {tool.CommandName}");
        }

        return command;
    }

    private static string EscapeSetValue(string value)
    {
        return value.Replace("%", "%%", StringComparison.Ordinal);
    }
}
