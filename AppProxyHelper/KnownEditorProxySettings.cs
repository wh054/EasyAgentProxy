using System.Text.Json;
using System.Text.Json.Nodes;

namespace AppProxyHelper;

internal static class KnownEditorProxySettings
{
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
        var settingsPaths = GetSettingsPaths(executablePath).ToArray();
        foreach (var settingsPath in settingsPaths)
        {
            WriteVsCodeStyleSettings(
                settingsPath,
                httpProxyUri,
                antigravityCloudCodeUrl,
                antigravityIdeLanguageServerPath);
        }

        return settingsPaths;
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
