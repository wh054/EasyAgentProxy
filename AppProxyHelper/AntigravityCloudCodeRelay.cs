using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace AppProxyHelper;

internal static class AntigravityCloudCodeRelay
{
    public const int Port = 18990;
    public const string RelayUrl = "http://127.0.0.1:18990";
    public const string HealthUrl = "http://127.0.0.1:18990/__easyproxy/health";
    public const string DefaultUpstream = "https://daily-cloudcode-pa.googleapis.com";

    public static bool IsAntigravityExecutable(string executablePath)
    {
        return Path.GetFileName(executablePath).Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAntigravityIdeExecutable(string executablePath)
    {
        return Path.GetFileName(executablePath).Equals("Antigravity IDE.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool UsesCloudCodeRelay(string executablePath)
    {
        return IsAntigravityExecutable(executablePath)
            || IsAntigravityIdeExecutable(executablePath);
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
        if (await IsRelayHealthyAsync(cancellationToken))
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
        if (IsRelayListening())
        {
            throw new InvalidOperationException(
                $"Antigravity CloudCode relay port is already in use, but health check failed: {RelayUrl}");
        }

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

        if (!await WaitForRelayHealthyAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Antigravity CloudCode relay 未能监听 {RelayUrl}。pid={process.Id}");
        }
    }

    private static async Task<bool> WaitForRelayHealthyAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250, cancellationToken);
            if (await IsRelayHealthyAsync(cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> IsRelayHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
            using var response = await client.GetAsync(HealthUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Contains("easyproxy-antigravity-relay", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
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
        const crypto = require("crypto");
        const fs = require("fs");
        const net = require("net");
        const path = require("path");
        const tls = require("tls");

        const listenHost = process.env.RELAY_HOST || "127.0.0.1";
        const listenPort = Number(process.env.RELAY_PORT || "18990");
        const defaultUpstream = process.env.CLOUDCODE_UPSTREAM || "https://daily-cloudcode-pa.googleapis.com";
        const proxy = new URL(process.env.EASYPROXY_HTTP_PROXY || "http://127.0.0.1:7890");
        const logPath = process.env.RELAY_LOG || path.join(__dirname, "Antigravity CloudCode Relay.log");
        const onboardCache = new Map();
        let nextRequestId = 1;

        function log(message) {
          fs.appendFile(logPath, `${new Date().toISOString()} ${message}\n`, () => {});
        }

        function pickUpstream(clientUrl) {
          if (clientUrl.startsWith("/v1internal")) return new URL(defaultUpstream);
          return new URL("https://www.googleapis.com");
        }

        function pickFallbackUpstream(clientUrl, upstream) {
          if (!clientUrl.startsWith("/v1internal:onboardUser")) return null;
          const fallback = new URL("https://cloudcode-pa.googleapis.com");
          return fallback.hostname === upstream.hostname ? null : fallback;
        }

        function buildUpstreamHeaders(clientHeaders, upstream) {
          const headers = { ...clientHeaders, host: upstream.hostname };
          for (const name of [
            "connection",
            "expect",
            "keep-alive",
            "proxy-authenticate",
            "proxy-authorization",
            "proxy-connection",
            "te",
            "trailer",
            "transfer-encoding",
            "upgrade",
          ]) {
            delete headers[name];
          }
          return headers;
        }

        function getAuthScheme(headers) {
          const value = String(headers.authorization || "");
          const match = /^([A-Za-z]+)\s/.exec(value);
          return match ? match[1] : "";
        }

        function getAuthKey(headers) {
          return crypto.createHash("sha256").update(String(headers.authorization || "")).digest("hex");
        }

        function sanitizeClientUrl(clientUrl) {
          const raw = clientUrl || "/";
          const queryIndex = raw.indexOf("?");
          if (queryIndex < 0) return raw;
          return `${raw.slice(0, queryIndex)}?...`;
        }

        function writeRelayError(clientRes, message) {
          if (!clientRes.headersSent) {
            clientRes.writeHead(502, { "content-type": "text/plain; charset=utf-8" });
          }
          if (!clientRes.writableEnded) {
            clientRes.end(`CloudCode relay error: ${message}\n`);
          }
        }

        function sanitizeResponseHeaders(headers, bodyLength) {
          const sanitized = { ...headers, "content-length": String(bodyLength) };
          for (const name of [
            "connection",
            "keep-alive",
            "proxy-authenticate",
            "proxy-authorization",
            "te",
            "trailer",
            "transfer-encoding",
            "upgrade",
          ]) {
            delete sanitized[name];
          }
          return sanitized;
        }

        function sendCachedOnboardResponse(clientReq, clientRes, cached, requestId, startedAt) {
          clientReq.resume();
          log(`[${requestId}] <- cached ${cached.statusCode} ${Date.now() - startedAt}ms`);
          clientRes.writeHead(cached.statusCode, cached.headers);
          clientRes.end(cached.body);
        }

        function proxyBufferedWithFallback(clientReq, clientRes, upstream, headers, requestId, startedAt, authKey) {
          const chunks = [];
          let size = 0;
          let ended = false;
          clientReq.on("data", (chunk) => {
            size += chunk.length;
            if (size > 2 * 1024 * 1024) {
              clientReq.destroy(new Error("request body too large for retry buffer"));
              return;
            }
            chunks.push(chunk);
          });
          clientReq.on("end", () => {
            const body = Buffer.concat(chunks);
            const candidates = [upstream];
            const fallback = pickFallbackUpstream(clientReq.url || "/", upstream);
            if (fallback) candidates.push(fallback);
            let attemptIndex = 0;

            function attempt() {
              const attemptUpstream = candidates[attemptIndex];
              const attemptHeaders = { ...headers, host: attemptUpstream.hostname, "content-length": String(body.length) };
              const upstreamReq = https.request({
                protocol: "https:",
                hostname: attemptUpstream.hostname,
                port: 443,
                method: clientReq.method,
                path: clientReq.url,
                headers: attemptHeaders,
                createConnection: (_options, callback) => createProxiedTlsConnection(attemptUpstream, callback),
              }, (upstreamRes) => {
                if (ended) {
                  upstreamRes.resume();
                  return;
                }
                ended = true;
                clearTimeout(fallbackTimer);
                const statusCode = upstreamRes.statusCode || 502;
                const responseChunks = [];
                if (!clientRes.headersSent && !clientRes.destroyed) {
                  clientRes.writeHead(statusCode, upstreamRes.headers);
                }
                upstreamRes.on("data", (chunk) => {
                  responseChunks.push(chunk);
                  if (!clientRes.destroyed && !clientRes.writableEnded) {
                    clientRes.write(chunk);
                  }
                });
                upstreamRes.on("end", () => {
                  const responseBody = Buffer.concat(responseChunks);
                  const responseHeaders = sanitizeResponseHeaders(upstreamRes.headers, responseBody.length);
                  if (statusCode >= 200 && statusCode < 300) {
                    onboardCache.set(authKey, { statusCode, headers: responseHeaders, body: responseBody });
                  }
                  log(`[${requestId}] <- ${statusCode} ${Date.now() - startedAt}ms upstream=${attemptUpstream.hostname}`);
                  if (!clientRes.headersSent) {
                    clientRes.writeHead(statusCode, responseHeaders);
                  }
                  if (!clientRes.destroyed && !clientRes.writableEnded) {
                    clientRes.end();
                  }
                });
              });
              upstreamReq.setTimeout(25000, () => {
                upstreamReq.destroy(new Error("upstream request timed out"));
              });
              const fallbackTimer = setTimeout(() => {
                if (ended || attemptIndex + 1 >= candidates.length) return;
                log(`[${requestId}] .. fallback after ${Date.now() - startedAt}ms upstream=${candidates[attemptIndex + 1].hostname}`);
                attemptIndex++;
                attempt();
                upstreamReq.destroy(new Error("superseded by fallback"));
              }, 4500);
              upstreamReq.on("error", (error) => {
                clearTimeout(fallbackTimer);
                if (error.message === "superseded by fallback") return;
                if (!ended && attemptIndex + 1 < candidates.length) {
                  log(`[${requestId}] .. retry after error ${error.message}`);
                  attemptIndex++;
                  attempt();
                  return;
                }
                if (!ended) {
                  ended = true;
                  log(`[${requestId}] !! ${Date.now() - startedAt}ms ${error.message}`);
                  writeRelayError(clientRes, error.message);
                }
              });
              upstreamReq.end(body);
            }

            attempt();
          });
          clientReq.on("error", (error) => {
            if (!ended) {
              ended = true;
              log(`[${requestId}] !! ${Date.now() - startedAt}ms ${error.message}`);
              writeRelayError(clientRes, error.message);
            }
          });
          clientReq.on("aborted", () => {
            log(`[${requestId}] xx client aborted ${Date.now() - startedAt}ms`);
          });
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
          if ((clientReq.url || "") === "/__easyproxy/health") {
            clientRes.writeHead(200, { "content-type": "text/plain; charset=utf-8" });
            clientRes.end("easyproxy-antigravity-relay\n");
            return;
          }

          const upstream = pickUpstream(clientReq.url || "/");
          const headers = buildUpstreamHeaders(clientReq.headers, upstream);
          const requestId = nextRequestId++;
          const startedAt = Date.now();
          log(`[${requestId}] -> ${clientReq.method || "GET"} ${sanitizeClientUrl(clientReq.url)} upstream=${upstream.hostname} auth=${getAuthScheme(clientReq.headers)} len=${clientReq.headers["content-length"] || ""} expect=${clientReq.headers.expect ? "1" : "0"}`);
          if ((clientReq.url || "").startsWith("/v1internal:onboardUser")) {
            const authKey = getAuthKey(clientReq.headers);
            const cached = onboardCache.get(authKey);
            if (cached) {
              sendCachedOnboardResponse(clientReq, clientRes, cached, requestId, startedAt);
              return;
            }

            proxyBufferedWithFallback(clientReq, clientRes, upstream, headers, requestId, startedAt, authKey);
            return;
          }

          const upstreamReq = https.request({
            protocol: "https:",
            hostname: upstream.hostname,
            port: 443,
            method: clientReq.method,
            path: clientReq.url,
            headers,
            createConnection: (_options, callback) => createProxiedTlsConnection(upstream, callback),
          }, (upstreamRes) => {
            log(`[${requestId}] <- ${upstreamRes.statusCode || 502} ${Date.now() - startedAt}ms`);
            clientRes.writeHead(upstreamRes.statusCode || 502, upstreamRes.headers);
            upstreamRes.pipe(clientRes);
          });
          upstreamReq.setTimeout(25000, () => {
            upstreamReq.destroy(new Error("upstream request timed out"));
          });
          upstreamReq.on("error", (error) => {
            log(`[${requestId}] !! ${Date.now() - startedAt}ms ${error.message}`);
            writeRelayError(clientRes, error.message);
          });
          clientReq.on("aborted", () => {
            log(`[${requestId}] xx client aborted ${Date.now() - startedAt}ms`);
            upstreamReq.destroy(new Error("client aborted"));
          });
          clientRes.on("close", () => {
            if (!clientRes.writableEnded) {
              upstreamReq.destroy(new Error("client closed"));
            }
          });
          clientReq.pipe(upstreamReq);
        });

        server.listen(listenPort, listenHost, () => {
          log(`listening http://${listenHost}:${listenPort} -> ${defaultUpstream} via ${proxy.href}`);
        });
        """;
}
