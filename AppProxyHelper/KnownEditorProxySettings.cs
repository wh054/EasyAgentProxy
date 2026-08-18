using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace AppProxyHelper;

internal static class KnownEditorProxySettings
{
    private static readonly HashSet<string> CodexProxyEnvironmentVariableNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "GRPC_PROXY",
        "NO_PROXY",
        "NO_GRPC_PROXY",
        "CLOUDSDK_PROXY_TYPE",
        "CLOUDSDK_PROXY_ADDRESS",
        "CLOUDSDK_PROXY_PORT"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static IReadOnlyList<string> WriteForExecutable(
        string executablePath,
        string httpProxyUri,
        string? antigravityCloudCodeUrl = null,
        string? antigravityIdeLanguageServerPath = null)
    {
        var writtenSettingsPaths = new List<string>();
        var settingsPaths = GetSettingsPaths(executablePath).ToArray();
        foreach (var settingsPath in settingsPaths)
        {
            WriteVsCodeStyleSettings(
                settingsPath,
                httpProxyUri,
                antigravityCloudCodeUrl,
                antigravityIdeLanguageServerPath);
            writtenSettingsPaths.Add(settingsPath);
        }

        var claudeCodeSettingsPath = GetClaudeCodeSettingsPath(executablePath);
        if (!string.IsNullOrWhiteSpace(claudeCodeSettingsPath))
        {
            WriteClaudeCodeSettings(claudeCodeSettingsPath, httpProxyUri);
            writtenSettingsPaths.Add(claudeCodeSettingsPath);
        }

        return writtenSettingsPaths;
    }

    public static string? WriteCodexDotEnv(ProxyEnvironmentProfile profile)
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                return null;
            }

            codexHome = Path.Combine(userProfile, ".codex");
        }

        var dotEnvPath = Path.Combine(Path.GetFullPath(codexHome), ".env");
        WriteCodexDotEnv(dotEnvPath, profile);
        return dotEnvPath;
    }

    internal static void WriteCodexDotEnv(string dotEnvPath, ProxyEnvironmentProfile profile)
    {
        var directory = Path.GetDirectoryName(dotEnvPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var retainedLines = File.Exists(dotEnvPath)
            ? File.ReadAllLines(dotEnvPath)
                .Where(static line => !IsCodexProxyEnvironmentLine(line))
                .ToList()
            : new List<string>();
        while (retainedLines.Count > 0 && string.IsNullOrWhiteSpace(retainedLines[^1]))
        {
            retainedLines.RemoveAt(retainedLines.Count - 1);
        }

        if (retainedLines.Count > 0)
        {
            retainedLines.Add(string.Empty);
        }

        retainedLines.Add("# Managed by EasyProxy for the ChatGPT desktop app.");
        retainedLines.AddRange(profile.GetEnvironmentVariables().Select(
            static pair => pair.Key + "=" + EscapeDotEnvValue(pair.Value)));
        File.WriteAllLines(
            dotEnvPath,
            retainedLines,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static bool IsCodexProxyEnvironmentLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Equals("# Managed by EasyProxy for the ChatGPT desktop app.", StringComparison.Ordinal)
            || trimmed.Equals("# Managed by EasyProxy for the Codex desktop app.", StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..].TrimStart();
        }

        var equalsIndex = trimmed.IndexOf('=');
        if (equalsIndex <= 0)
        {
            return false;
        }

        var name = trimmed[..equalsIndex].Trim();
        if (CodexProxyEnvironmentVariableNames.Contains(name))
        {
            return true;
        }

        // Clean up invalid proxy keys written by older/manual configurations,
        // such as "HTTP PROXY=...", before appending valid dotenv entries.
        return CodexProxyEnvironmentVariableNames.Contains(name.Replace(' ', '_'));
    }

    private static string EscapeDotEnvValue(string value)
    {
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }

    private static IEnumerable<string> GetSettingsPaths(string executablePath)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            yield break;
        }

        var fileName = Path.GetFileName(executablePath);
        if (fileName.Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(appData, "Antigravity", "User", "settings.json");
        }
        else if (fileName.Equals("Antigravity IDE.exe", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(appData, "Antigravity IDE", "User", "settings.json");
        }
        else if (fileName.Equals("Cursor.exe", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(appData, "Cursor", "User", "settings.json");
        }
    }

    private static string? GetClaudeCodeSettingsPath(string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);
        if (!fileName.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)
            && !executablePath.Contains("Claude", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? null
            : Path.Combine(userProfile, ".claude", "settings.json");
    }

    private static void WriteVsCodeStyleSettings(
        string settingsPath,
        string httpProxyUri,
        string? antigravityCloudCodeUrl,
        string? antigravityIdeLanguageServerPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var root = ReadSettingsObject(settingsPath);
        root["http.proxy"] = httpProxyUri;
        root["http.proxySupport"] = "override";
        root["http.systemCertificates"] = true;
        root["codeiumDev.languageServerEnv"] = new JsonObject
        {
            ["HTTP_PROXY"] = httpProxyUri,
            ["HTTPS_PROXY"] = httpProxyUri,
            ["http_proxy"] = httpProxyUri,
            ["https_proxy"] = httpProxyUri,
            ["GRPC_PROXY"] = httpProxyUri,
            ["grpc_proxy"] = httpProxyUri,
            ["NO_PROXY"] = "localhost,127.0.0.1,::1",
            ["no_proxy"] = "localhost,127.0.0.1,::1"
        };

        if (!string.IsNullOrWhiteSpace(antigravityCloudCodeUrl))
        {
            root["jetski.cloudCodeUrl"] = antigravityCloudCodeUrl;
        }

        if (!string.IsNullOrWhiteSpace(antigravityIdeLanguageServerPath))
        {
            root["codeiumDev.languageServerBinaryPath"] = antigravityIdeLanguageServerPath;
            root["codeiumDev.machineLanguageServerBinaryPath"] = antigravityIdeLanguageServerPath;
        }

        File.WriteAllText(settingsPath, root.ToJsonString(JsonOptions));
    }

    internal static void WriteClaudeCodeSettings(string settingsPath, string httpProxyUri)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var root = ReadSettingsObject(settingsPath);
        var env = root["env"] as JsonObject;
        if (env is null)
        {
            env = new JsonObject();
            root["env"] = env;
        }

        env["HTTP_PROXY"] = httpProxyUri;
        env["HTTPS_PROXY"] = httpProxyUri;
        env["http_proxy"] = httpProxyUri;
        env["https_proxy"] = httpProxyUri;
        env["GRPC_PROXY"] = httpProxyUri;
        env["grpc_proxy"] = httpProxyUri;
        env["NO_PROXY"] = "localhost,127.0.0.1,::1";
        env["no_proxy"] = "localhost,127.0.0.1,::1";
        env["NO_GRPC_PROXY"] = "localhost,127.0.0.1,::1";
        env["no_grpc_proxy"] = "localhost,127.0.0.1,::1";
        env.Remove("ALL_PROXY");
        env.Remove("all_proxy");
        env.Remove("CLOUDSDK_PROXY_TYPE");
        env.Remove("CLOUDSDK_PROXY_ADDRESS");
        env.Remove("CLOUDSDK_PROXY_PORT");

        File.WriteAllText(settingsPath, root.ToJsonString(JsonOptions));
    }

    private static JsonObject ReadSettingsObject(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return new JsonObject();
        }

        var json = File.ReadAllText(settingsPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(json, nodeOptions: null, documentOptions: DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException($"Settings file is not a JSON object: {settingsPath}");
    }
}
