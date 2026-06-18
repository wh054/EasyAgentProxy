# CLI Context Menu Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add GUI-managed Explorer context menu entries that open Claude Code CLI or Codex CLI in the selected folder with EasyProxy proxy environment variables.

**Architecture:** Keep registry and script behavior outside the main form. Add small core classes for CLI tool definitions, launcher script generation, and Explorer context menu management, then add a focused WinForms dialog launched from the existing home tab.

**Tech Stack:** C#/.NET 10 Windows, WinForms, `Microsoft.Win32.Registry`, PowerShell/Windows Terminal launcher scripts, lightweight console-based tests.

---

## File Structure

- Create `AppProxyHelper/CliToolCatalog.cs`: supported CLI tool metadata.
- Create `AppProxyHelper/CliProxyLauncherScriptGenerator.cs`: writes `.cmd` scripts and exposes script content generation for tests.
- Create `AppProxyHelper/ExplorerContextMenuManager.cs`: builds registry commands, installs/removes HKCU context menu entries, reports status.
- Create `AppProxyHelper/CliContextMenuDialog.cs`: modal WinForms dialog for install/update/uninstall/status.
- Modify `AppProxyHelper/AppProxyGui.cs`: add a "CLI 右键菜单..." button that opens the dialog.
- Create `AppProxyHelper/Properties/AssemblyInfo.cs`: expose internals to the test assembly.
- Create `AppProxyHelper.Tests/AppProxyHelper.Tests.csproj`: console test runner project.
- Create `AppProxyHelper.Tests/Program.cs`: behavior tests for metadata, script generation, and registry command generation.

## Task 1: Add Failing Tests

**Files:**
- Create: `AppProxyHelper.Tests/AppProxyHelper.Tests.csproj`
- Create: `AppProxyHelper.Tests/Program.cs`
- Create: `AppProxyHelper/Properties/AssemblyInfo.cs`

- [ ] **Step 1: Add test project and internals visibility**

Create `AppProxyHelper.Tests/AppProxyHelper.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AppProxyHelper\AppProxyHelper.csproj" />
  </ItemGroup>
</Project>
```

Create `AppProxyHelper/Properties/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AppProxyHelper.Tests")]
```

- [ ] **Step 2: Write failing tests**

Create `AppProxyHelper.Tests/Program.cs`:

```csharp
using AppProxyHelper;

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

static void CatalogExposesClaudeAndCodex()
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

static void LauncherScriptInjectsProxyEnvironment()
{
    var tool = CliToolCatalog.All.Single(tool => tool.Id == "claude-code");
    var profile = ProxyEnvironmentProfile.Create("socks5://127.0.0.1:7890");
    var script = CliProxyLauncherScriptGenerator.BuildScript(tool, profile);

    AssertContains(script, "set \"HTTP_PROXY=http://127.0.0.1:7890\"");
    AssertContains(script, "set \"HTTPS_PROXY=http://127.0.0.1:7890\"");
    AssertContains(script, "set \"ALL_PROXY=socks5://127.0.0.1:7890\"");
    AssertContains(script, "if not exist \"%EASYPROXY_WORKDIR%\" set \"EASYPROXY_WORKDIR=%USERPROFILE%\"");
    AssertContains(script, "where wt.exe >nul 2>nul");
    AssertContains(script, "& claude");
}

static void ContextMenuCommandsPassExplorerDirectoryTokens()
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

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'");
    }
}

static void AssertContains(string text, string expected)
{
    if (!text.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"expected to find '{expected}'");
    }
}
```

- [ ] **Step 3: Run tests and verify RED**

Run:

```powershell
.\.dotnet\dotnet.exe run --project AppProxyHelper.Tests\AppProxyHelper.Tests.csproj
```

Expected: build fails because `CliToolCatalog`, `CliProxyLauncherScriptGenerator`, `ExplorerContextMenuManager`, and `ExplorerContextMenuTarget` do not exist.

## Task 2: Implement Core CLI Context Menu Logic

**Files:**
- Create: `AppProxyHelper/CliToolCatalog.cs`
- Create: `AppProxyHelper/CliProxyLauncherScriptGenerator.cs`
- Create: `AppProxyHelper/ExplorerContextMenuManager.cs`

- [ ] **Step 1: Add CLI tool catalog**

Create `AppProxyHelper/CliToolCatalog.cs`:

```csharp
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
```

- [ ] **Step 2: Add launcher script generator**

Create `AppProxyHelper/CliProxyLauncherScriptGenerator.cs` with `GetDefaultOutputDirectory`, `WriteScript`, `DeleteScript`, and internal `BuildScript`. The script must set proxy variables from `ProxyEnvironmentProfile`, normalize the working directory argument, prefer `wt.exe`, and fall back to PowerShell.

- [ ] **Step 3: Add Explorer context menu manager**

Create `AppProxyHelper/ExplorerContextMenuManager.cs` with:

- `ExplorerContextMenuTarget` enum.
- `CliContextMenuToolStatus` record.
- `Install(IEnumerable<CliToolDefinition> tools, string proxyUri)`.
- `Uninstall(IEnumerable<CliToolDefinition> tools)`.
- `GetStatuses(IEnumerable<CliToolDefinition> tools)`.
- Internal `BuildCommand` and `GetRegistrySubKeyPath` helpers for tests.

- [ ] **Step 4: Run tests and verify GREEN**

Run:

```powershell
.\.dotnet\dotnet.exe run --project AppProxyHelper.Tests\AppProxyHelper.Tests.csproj
```

Expected: all three tests pass.

## Task 3: Add GUI Dialog

**Files:**
- Create: `AppProxyHelper/CliContextMenuDialog.cs`
- Modify: `AppProxyHelper/AppProxyGui.cs`

- [ ] **Step 1: Add dialog**

Create `CliContextMenuDialog` as a fixed dialog with:

- Proxy URI textbox.
- Claude and Codex checkboxes, both checked by default.
- Read-only multiline status textbox.
- Buttons for install/update, uninstall, refresh, and close.

Install/update calls `ExplorerContextMenuManager.Install(SelectedTools, proxyUri)`. Uninstall calls `ExplorerContextMenuManager.Uninstall(SelectedTools)`. Refresh reads `ExplorerContextMenuManager.GetStatuses(CliToolCatalog.All)`.

- [ ] **Step 2: Add main window entry point**

Modify `AppProxyGui.cs`:

- Add a `_cliContextMenuButton` field.
- Increase `TargetSourceHeight` enough for the second button.
- Add the button under the existing "一键生成代理启动脚本" button in `BuildAiAppProxyGroup`.
- Add `OpenCliContextMenuDialog()` that passes `_proxyUriText.Text.Trim()` to the dialog.

- [ ] **Step 3: Build project**

Run:

```powershell
.\.dotnet\dotnet.exe build AppProxyHelper\AppProxyHelper.csproj
```

Expected: build succeeds.

## Task 4: Final Verification

**Files:**
- Documentation may be updated if command names or behavior changed from the design.

- [ ] **Step 1: Run tests**

Run:

```powershell
.\.dotnet\dotnet.exe run --project AppProxyHelper.Tests\AppProxyHelper.Tests.csproj
```

Expected: all tests pass.

- [ ] **Step 2: Build application**

Run:

```powershell
.\.dotnet\dotnet.exe build AppProxyHelper\AppProxyHelper.csproj
```

Expected: build succeeds without errors.

- [ ] **Step 3: Inspect git diff**

Run:

```powershell
git diff --stat
git diff -- AppProxyHelper AppProxyHelper.Tests docs
```

Expected: changes are limited to the CLI context menu feature and its tests/docs.
