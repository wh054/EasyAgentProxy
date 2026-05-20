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
            var windowsApps = Path.Combine(programFiles, "WindowsApps");
            foreach (var packageDirectory in EnumerateDirectories(windowsApps, "OpenAI.Codex_*_x64__2p2nqsd0c76g0"))
            {
                yield return Path.Combine(packageDirectory, "app", "Codex.exe");
            }

            yield return Path.Combine(
                programFiles,
                @"WindowsApps\OpenAI.Codex_26.506.3741.0_x64__2p2nqsd0c76g0\app\Codex.exe");
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
