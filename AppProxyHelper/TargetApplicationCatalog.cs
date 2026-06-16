namespace AppProxyHelper;

internal sealed record TargetApplicationPreset(string Id, string Name, string ExecutablePath)
{
    public override string ToString() => Name;
}

internal static class TargetApplicationCatalog
{
    public static IReadOnlyList<TargetApplicationPreset> FindInstalled()
    {
        var presets = new List<TargetApplicationPreset>();
        AddTargetPreset(presets, "codex", "Codex 应用", GetCodexCandidates());
        AddTargetPreset(presets, "cursor", "Cursor 应用", GetCursorCandidates());
        AddTargetPreset(presets, "claude", "Claude 应用", GetClaudeCandidates());
        AddTargetPreset(presets, "antigravity", "Antigravity 应用", GetAntigravityCandidates());
        AddTargetPreset(presets, "antigravity-ide", "Antigravity IDE", GetAntigravityIdeCandidates());
        return presets;
    }

    public static TargetApplicationPreset? FindInstalledById(string id)
    {
        return FindInstalled()
            .FirstOrDefault(app => app.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public static string GuessNameFromPath(string executablePath)
    {
        var name = Path.GetFileNameWithoutExtension(executablePath);
        return string.IsNullOrWhiteSpace(name) ? "Electron App" : name;
    }

    private static void AddTargetPreset(
        ICollection<TargetApplicationPreset> presets,
        string id,
        string name,
        IEnumerable<string> candidates)
    {
        var executablePath = FindFirstExistingFile(candidates);
        if (executablePath is not null)
        {
            presets.Add(new TargetApplicationPreset(id, name, executablePath));
        }
    }

    private static string? FindFirstExistingFile(IEnumerable<string> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch
            {
                continue;
            }

            if (!seen.Add(fullPath))
            {
                continue;
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
                // Some packaged app directories can be permission-restricted.
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCodexCandidates()
    {
        foreach (var executable in FindExecutablesOnPath("Codex.exe"))
        {
            var directory = Path.GetDirectoryName(executable);
            var appDirectory = directory is null ? null : Path.GetDirectoryName(directory);
            if (directory is not null
                && !string.IsNullOrWhiteSpace(appDirectory)
                && directory.EndsWith(@"\resources", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(appDirectory, "Codex.exe");
            }

            yield return executable;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            foreach (var packageDirectory in GetAppxPackageInstallLocations("OpenAI.Codex"))
            {
                yield return Path.Combine(packageDirectory, "app", "Codex.exe");
            }

            var windowsApps = Path.Combine(programFiles, "WindowsApps");
            foreach (var packageDirectory in EnumerateWindowsAppPackageDirectories(
                windowsApps,
                "OpenAI.Codex",
                "2p2nqsd0c76g0"))
            {
                yield return Path.Combine(packageDirectory, "app", "Codex.exe");
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "Codex", "Codex.exe");
            yield return Path.Combine(localAppData, "Programs", "codex", "Codex.exe");
        }
    }

    private static IEnumerable<string> FindExecutablesOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim(), fileName);
            }
            catch
            {
                continue;
            }

            if (!seen.Add(candidate))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> GetCursorCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "cursor", "Cursor.exe");
            yield return Path.Combine(localAppData, "Programs", "Cursor", "Cursor.exe");
        }

        foreach (var programFiles in GetProgramFilesDirectories())
        {
            yield return Path.Combine(programFiles, "Cursor", "Cursor.exe");
        }
    }

    private static IEnumerable<string> GetClaudeCandidates()
    {
        foreach (var executable in FindExecutablesOnPath("claude.exe"))
        {
            var directory = Path.GetDirectoryName(executable);
            var appDirectory = directory is null ? null : Path.GetDirectoryName(directory);
            if (directory is not null
                && !string.IsNullOrWhiteSpace(appDirectory)
                && directory.EndsWith(@"\resources", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(appDirectory, "claude.exe");
            }

            yield return executable;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            foreach (var packageDirectory in GetAppxPackageInstallLocations("Claude"))
            {
                yield return Path.Combine(packageDirectory, "app", "claude.exe");
            }

            var windowsApps = Path.Combine(programFiles, "WindowsApps");
            foreach (var packageDirectory in EnumerateWindowsAppPackageDirectories(
                windowsApps,
                "Claude",
                "pzs8sxrjxfjjc"))
            {
                yield return Path.Combine(packageDirectory, "app", "claude.exe");
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "Claude", "claude.exe");
            yield return Path.Combine(localAppData, "Programs", "claude", "claude.exe");
        }
    }

    private static IEnumerable<string> GetAntigravityCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "Antigravity", "Antigravity.exe");
            yield return Path.Combine(localAppData, "Programs", "antigravity", "Antigravity.exe");
        }

        foreach (var programFiles in GetProgramFilesDirectories())
        {
            yield return Path.Combine(programFiles, "Antigravity", "Antigravity.exe");
        }
    }

    private static IEnumerable<string> GetAntigravityIdeCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "Antigravity IDE", "Antigravity IDE.exe");
            yield return Path.Combine(localAppData, "Programs", "antigravity ide", "Antigravity IDE.exe");
        }

        foreach (var programFiles in GetProgramFilesDirectories())
        {
            yield return Path.Combine(programFiles, "Antigravity IDE", "Antigravity IDE.exe");
        }
    }

    private static IEnumerable<string> GetProgramFilesDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        })
        {
            if (!string.IsNullOrWhiteSpace(folder) && seen.Add(folder))
            {
                yield return folder;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string root, string pattern)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        List<string> directories;
        try
        {
            directories = Directory
                .EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(GetSafeLastWriteTimeUtc)
                .ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            yield return directory;
        }
    }

    private static IEnumerable<string> GetAppxPackageInstallLocations(string packageName)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        string output;
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command "
                    + "\"Get-AppxPackage -Name '"
                    + packageName.Replace("'", "''", StringComparison.Ordinal)
                    + "' | Sort-Object Version -Descending | Select-Object -ExpandProperty InstallLocation\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (!process.Start())
            {
                yield break;
            }

            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort only; discovery will continue with the WindowsApps fallback.
                }

                yield break;
            }

            output = process.StandardOutput.ReadToEnd();
        }
        catch
        {
            yield break;
        }

        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var location = line.Trim();
            if (!string.IsNullOrWhiteSpace(location))
            {
                yield return location;
            }
        }
    }

    private static IEnumerable<string> EnumerateWindowsAppPackageDirectories(
        string root,
        string packageName,
        string publisherId)
    {
        var pattern = $"{packageName}_*__{publisherId}";
        foreach (var directory in EnumerateDirectories(root, pattern)
            .Select(directory => (
                Directory: directory,
                Version: TryGetWindowsAppPackageVersion(Path.GetFileName(directory), packageName, publisherId),
                LastWriteTimeUtc: GetSafeLastWriteTimeUtc(directory)))
            .OrderByDescending(item => item.Version ?? new Version(0, 0))
            .ThenByDescending(item => item.LastWriteTimeUtc)
            .Select(item => item.Directory))
        {
            yield return directory;
        }
    }

    private static Version? TryGetWindowsAppPackageVersion(
        string directoryName,
        string packageName,
        string publisherId)
    {
        var pattern = "^"
            + System.Text.RegularExpressions.Regex.Escape(packageName)
            + "_(?<version>[0-9]+(?:\\.[0-9]+){1,3})_[^_]+__"
            + System.Text.RegularExpressions.Regex.Escape(publisherId)
            + "$";
        var match = System.Text.RegularExpressions.Regex.Match(
            directoryName,
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version)
            ? version
            : null;
    }

    private static DateTime GetSafeLastWriteTimeUtc(string path)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
