using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace AppProxyHelper;

internal static class AntigravityCloudCodeRelay
{
    public const int Port = 18990;
    public const string RelayUrl = "http://127.0.0.1:18990";
    public const string DefaultUpstream = "https://daily-cloudcode-pa.googleapis.com";

    public static bool IsAntigravityExecutable(string executablePath)
    {
        return Path.GetFileName(executablePath).Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static string WriteRelayScript(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var relayScriptPath = Path.Combine(outputDirectory, "Antigravity CloudCode Relay.js");
        File.WriteAllText(
            relayScriptPath,
            ScriptContent,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return relayScriptPath;
    }

    public static async Task EnsureStartedAsync(string httpProxyUri, AppLogger logger, CancellationToken cancellationToken)
    {
        if (IsRelayListening())
        {
            logger.Info($"Antigravity CloudCode relay 已在监听: {RelayUrl}");
            return;
        }

        var nodePath = await EnsureNodeAsync(logger, cancellationToken);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var relayDirectory = string.IsNullOrWhiteSpace(localAppData)
            ? AppContext.BaseDirectory
            : Path.Combine(localAppData, "EasyProxy");
        var relayScriptPath = WriteRelayScript(relayDirectory);

        var startInfo = new ProcessStartInfo(nodePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = relayDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(relayScriptPath);
        startInfo.Environment["EASYPROXY_HTTP_PROXY"] = httpProxyUri;
        startInfo.Environment["CLOUDCODE_UPSTREAM"] = DefaultUpstream;

        logger.Info($"启动 Antigravity CloudCode relay: {RelayUrl} -> {DefaultUpstream}");
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Antigravity CloudCode relay。");

        await Task.Delay(800, cancellationToken);
        if (!IsRelayListening())
        {
            throw new InvalidOperationException($"Antigravity CloudCode relay 未能监听 {RelayUrl}。pid={process.Id}");
        }
    }

    private static async Task<string> EnsureNodeAsync(AppLogger logger, CancellationToken cancellationToken)
    {
        var nodePath = FindExecutable("node.exe");
        if (!string.IsNullOrWhiteSpace(nodePath))
        {
            return nodePath;
        }

        logger.Warn("未找到 Node.js；将尝试通过 winget 安装 Node.js LTS，用于 Antigravity CloudCode relay。");
        var wingetPath = FindExecutable("winget.exe")
            ?? throw new InvalidOperationException("未找到 winget.exe。请安装 Node.js LTS 后重试。");

        var installInfo = new ProcessStartInfo(wingetPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        installInfo.ArgumentList.Add("install");
        installInfo.ArgumentList.Add("--id");
        installInfo.ArgumentList.Add("OpenJS.NodeJS.LTS");
        installInfo.ArgumentList.Add("-e");
        installInfo.ArgumentList.Add("--accept-source-agreements");
        installInfo.ArgumentList.Add("--accept-package-agreements");

        using var install = Process.Start(installInfo)
            ?? throw new InvalidOperationException("无法启动 winget 安装 Node.js LTS。");
        await install.WaitForExitAsync(cancellationToken);
        if (install.ExitCode != 0)
        {
            throw new InvalidOperationException($"winget 安装 Node.js LTS 失败，exitCode={install.ExitCode}。");
        }

        nodePath = FindExecutable("node.exe");
        return !string.IsNullOrWhiteSpace(nodePath)
            ? nodePath
            : throw new InvalidOperationException("Node.js 安装完成，但仍未找到 node.exe。请重新打开 EasyProxy 后再试。");
    }

    private static bool IsRelayListening()
    {
        return IPGlobalProperties
            .GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == Port
                && (IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(IPAddress.Any)));
    }

    private static string? FindExecutable(string fileName)
    {
        foreach (var candidate in GetExecutableCandidates(fileName))
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> GetExecutableCandidates(string fileName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, fileName);
            }
            catch
            {
                continue;
            }

            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", fileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", fileName)
        })
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    public const string ScriptContent = """
        const http = require("http");
        const https = require("https");
        const net = require("net");
        const tls = require("tls");

        const listenHost = process.env.RELAY_HOST || "127.0.0.1";
        const listenPort = Number(process.env.RELAY_PORT || "18990");
        const defaultUpstream = process.env.CLOUDCODE_UPSTREAM || "https://daily-cloudcode-pa.googleapis.com";
        const proxy = new URL(process.env.EASYPROXY_HTTP_PROXY || "http://127.0.0.1:7890");

        function pickUpstream(clientUrl) {
          if (clientUrl.startsWith("/v1internal")) return new URL(defaultUpstream);
          return new URL("https://www.googleapis.com");
        }

        function createProxiedTlsConnection(upstream, callback) {
          const socket = net.connect(Number(proxy.port || 80), proxy.hostname);
          const target = `${upstream.hostname}:443`;
          socket.once("connect", () => {
            socket.write(`CONNECT ${target} HTTP/1.1\r\nHost: ${target}\r\nProxy-Connection: keep-alive\r\n\r\n`);
          });
          let buffer = Buffer.alloc(0);
          socket.on("data", function onData(chunk) {
            buffer = Buffer.concat([buffer, chunk]);
            const headerEnd = buffer.indexOf("\r\n\r\n");
            if (headerEnd < 0) return;
            socket.off("data", onData);
            const statusLine = buffer.subarray(0, headerEnd).toString("ascii").split("\r\n", 1)[0] || "";
            if (!/^HTTP\/1\.[01] 2\d\d\b/.test(statusLine)) {
              socket.destroy(new Error(`Proxy CONNECT failed: ${statusLine}`));
              return;
            }
            const rest = buffer.subarray(headerEnd + 4);
            if (rest.length) socket.unshift(rest);
            callback(null, tls.connect({ socket, servername: upstream.hostname, ALPNProtocols: ["http/1.1"] }));
          });
          socket.once("error", callback);
        }

        const server = http.createServer((clientReq, clientRes) => {
          const upstream = pickUpstream(clientReq.url || "/");
          const headers = { ...clientReq.headers, host: upstream.hostname };
          delete headers["proxy-connection"];
          const upstreamReq = https.request({
            protocol: "https:",
            hostname: upstream.hostname,
            port: 443,
            method: clientReq.method,
            path: clientReq.url,
            headers,
            createConnection: (_options, callback) => createProxiedTlsConnection(upstream, callback),
          }, (upstreamRes) => {
            clientRes.writeHead(upstreamRes.statusCode || 502, upstreamRes.headers);
            upstreamRes.pipe(clientRes);
          });
          upstreamReq.on("error", (error) => {
            clientRes.writeHead(502, { "content-type": "text/plain; charset=utf-8" });
            clientRes.end(`CloudCode relay error: ${error.message}\n`);
          });
          clientReq.pipe(upstreamReq);
        });

        server.listen(listenPort, listenHost);
        """;
}
