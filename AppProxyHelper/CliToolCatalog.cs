namespace AppProxyHelper;

internal sealed record CliToolDefinition(
    string Id,
    string DisplayName,
    string CommandName,
    string MenuTitle,
    string ScriptFileName,
    string RegistryName);

internal static class CliToolCatalog
{
    public static IReadOnlyList<CliToolDefinition> All { get; } =
    [
        new(
            "claude-code",
            "Claude Code CLI",
            "claude",
            "在此处打开 Claude",
            "OpenClaudeHere.cmd",
            "EasyProxy.OpenClaudeHere"),
        new(
            "codex-cli",
            "Codex CLI",
            "codex",
            "在此处打开 Codex",
            "OpenCodexHere.cmd",
            "EasyProxy.OpenCodexHere")
    ];
}
