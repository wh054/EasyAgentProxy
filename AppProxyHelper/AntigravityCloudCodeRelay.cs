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
    public const string GenerativeLanguagePathPrefix = "/__easyproxy/gemini";
    public const string GenerativeLanguageRelayUrl = RelayUrl + GenerativeLanguagePathPrefix;

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

    public static string GetRelayDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(AppContext.BaseDirectory, "AntigravityRelay")
            : Path.Combine(localAppData, "EasyProxy", "AntigravityRelay");
    }

    public static string WriteSharedRelayScript()
    {
        return WriteRelayScript(GetRelayDirectory());
    }

    public static async Task EnsureStartedAsync(string httpProxyUri, AppLogger logger, CancellationToken cancellationToken)
    {
        if (await IsRelayHealthyAsync(cancellationToken))
        {
            logger.Info($"Antigravity CloudCode relay 已在监听: {RelayUrl}");
            return;
        }

        var nodePath = await EnsureNodeAsync(logger, cancellationToken);
        var relayScriptPath = WriteSharedRelayScript();
        var relayDirectory = Path.GetDirectoryName(relayScriptPath) ?? GetRelayDirectory();
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
        const DEFAULT_UPSTREAM_TIMEOUT_MS = readTimeoutMs("EASYPROXY_UPSTREAM_TIMEOUT_MS", 25000);
        const STREAM_UPSTREAM_TIMEOUT_MS = readTimeoutMs("EASYPROXY_STREAM_TIMEOUT_MS", 0);
        const CONNECT_TIMEOUT_MS = readTimeoutMs("EASYPROXY_CONNECT_TIMEOUT_MS", 10000);
        const RETRY_ATTEMPTS = Math.max(1, readTimeoutMs("EASYPROXY_RETRY_ATTEMPTS", 3));
        const RETRY_BASE_DELAY_MS = readTimeoutMs("EASYPROXY_RETRY_BASE_DELAY_MS", 250);
        const MAX_BUFFERED_REQUEST_BYTES = readTimeoutMs("EASYPROXY_MAX_BUFFERED_REQUEST_BYTES", 8 * 1024 * 1024);
        const GENERATIVE_LANGUAGE_PREFIX = "/__easyproxy/gemini";

        function log(message) {
          fs.appendFile(logPath, `${new Date().toISOString()} ${message}\n`, () => {});
        }

        function readTimeoutMs(envName, fallback) {
          const raw = process.env[envName];
          if (raw === undefined || raw === "") return fallback;
          const parsed = Number(raw);
          return Number.isFinite(parsed) && parsed >= 0 ? parsed : fallback;
        }

        function isStreamingRequest(clientUrl) {
          const raw = clientUrl || "";
          return raw.includes(":streamGenerateContent")
            || raw.includes(":generateContent")
            || raw.includes("alt=sse")
            || raw.includes("loadCodebaseContext");
        }

        function getUpstreamTimeoutMs(clientUrl) {
          return isStreamingRequest(clientUrl)
            ? STREAM_UPSTREAM_TIMEOUT_MS
            : DEFAULT_UPSTREAM_TIMEOUT_MS;
        }

        function resolveRoute(clientUrl) {
          const raw = clientUrl || "/";
          if (raw === GENERATIVE_LANGUAGE_PREFIX
              || raw.startsWith(`${GENERATIVE_LANGUAGE_PREFIX}/`)
              || raw.startsWith(`${GENERATIVE_LANGUAGE_PREFIX}?`)) {
            const suffix = raw.slice(GENERATIVE_LANGUAGE_PREFIX.length);
            return {
              upstream: new URL("https://generativelanguage.googleapis.com"),
              path: suffix === "" ? "/" : (suffix.startsWith("?") ? `/${suffix}` : suffix),
            };
          }

          if (raw.startsWith("/v1internal")) {
            return { upstream: new URL(defaultUpstream), path: raw };
          }

          return { upstream: new URL("https://www.googleapis.com"), path: raw };
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
          if (clientRes.headersSent) {
            if (!clientRes.destroyed && !clientRes.writableEnded) {
              clientRes.destroy(new Error(message));
            }
            return;
          }

          clientRes.writeHead(502, { "content-type": "text/plain; charset=utf-8" });
          if (!clientRes.writableEnded) {
            clientRes.end(`CloudCode relay error: ${message}\n`);
          }
        }

        function sanitizeResponseHeaders(headers, bodyLength) {
          const sanitized = sanitizeStreamingResponseHeaders(headers);
          sanitized["content-length"] = String(bodyLength);
          return sanitized;
        }

        function sanitizeStreamingResponseHeaders(headers) {
          const sanitized = { ...headers };
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

        function isIdempotentOrGenerative(routePath) {
          // Only replay known read/generation RPCs before any response headers.
          // A lost generation response can still consume quota on the server.
          const pathname = String(routePath || "").split("?", 1)[0];
          return /^\/v1internal:(streamGenerateContent|generateContent|loadCodeAssist|loadCodebaseContext|fetchUserInfo|listExperiments|retrieveUserQuotaSummary|fetchAvailableModels|fetchAdminControls)$/.test(pathname)
            || /^\/v1(?:beta)?\/models\/[^/]+:(streamGenerateContent|generateContent)$/.test(pathname);
        }

        function isSafeToRetry(error, requestCommitted, method, routePath) {
          if (["GET", "HEAD", "OPTIONS"].includes(String(method || "GET").toUpperCase())) return true;
          const message = String(error && error.message || "").toLowerCase();
          const connectionSetupFailure = message.includes("before secure tls connection was established")
            || message.includes("proxy connect")
            || message.includes("tls handshake")
            || message.includes("connect timed out");
          if (connectionSetupFailure) return true;
          const isNetworkFailure = Boolean(error && ["ECONNRESET", "ETIMEDOUT", "ECONNREFUSED", "EPIPE"].includes(error.code))
            || message.includes("socket hang up")
            || message.includes("network socket disconnected");
          if (!isNetworkFailure) return false;
          if (!requestCommitted || isIdempotentOrGenerative(routePath)) return true;
          return false;
        }

        function retryDelay(attemptIndex) {
          return RETRY_BASE_DELAY_MS * Math.max(1, attemptIndex);
        }

        function proxyBufferedWithRetries(clientReq, clientRes, route, headers, requestId, startedAt, authKey) {
          const chunks = [];
          let size = 0;
          let ended = false;
          let activeRequest = null;
          let retryTimer = null;
          clientReq.on("data", (chunk) => {
            size += chunk.length;
            if (size > MAX_BUFFERED_REQUEST_BYTES) {
              clientReq.destroy(new Error("request body too large for retry buffer"));
              return;
            }
            chunks.push(chunk);
          });
          clientReq.on("end", () => {
            const body = Buffer.concat(chunks);
            const candidates = [route.upstream];
            const fallback = pickFallbackUpstream(route.path, route.upstream);
            if (fallback) candidates.push(fallback);
            let candidateIndex = 0;
            let attemptIndex = 0;

            function attempt() {
              if (ended || clientRes.destroyed) return;
              const attemptUpstream = candidates[candidateIndex];
              const attemptHeaders = { ...headers, host: attemptUpstream.hostname, "content-length": String(body.length) };
              let requestCommitted = false;
              let responseStarted = false;
              const upstreamReq = https.request({
                protocol: "https:",
                hostname: attemptUpstream.hostname,
                port: 443,
                method: clientReq.method,
                path: route.path,
                headers: attemptHeaders,
                createConnection: (_options, callback) => createProxiedTlsConnection(attemptUpstream, callback),
              }, (upstreamRes) => {
                responseStarted = true;
                ended = true;
                const statusCode = upstreamRes.statusCode || 502;
                const responseHeaders = sanitizeStreamingResponseHeaders(upstreamRes.headers);
                const responseChunks = authKey ? [] : null;
                log(`[${requestId}] <- ${statusCode} ${Date.now() - startedAt}ms upstream=${attemptUpstream.hostname} attempt=${attemptIndex + 1}`);
                if (!clientRes.headersSent && !clientRes.destroyed) {
                  clientRes.writeHead(statusCode, responseHeaders);
                }
                upstreamRes.on("data", (chunk) => {
                  if (responseChunks) responseChunks.push(chunk);
                  if (!clientRes.destroyed && !clientRes.writableEnded) clientRes.write(chunk);
                });
                upstreamRes.on("end", () => {
                  if (responseChunks && statusCode >= 200 && statusCode < 300) {
                    const responseBody = Buffer.concat(responseChunks);
                    onboardCache.set(authKey, {
                      statusCode,
                      headers: sanitizeResponseHeaders(upstreamRes.headers, responseBody.length),
                      body: responseBody,
                    });
                  }
                  if (!clientRes.destroyed && !clientRes.writableEnded) clientRes.end();
                });
                upstreamRes.on("error", (error) => {
                  log(`[${requestId}] !! response ${Date.now() - startedAt}ms ${error.message}`);
                  if (!clientRes.destroyed && !clientRes.writableEnded) clientRes.destroy(error);
                });
              });
              activeRequest = upstreamReq;
              upstreamReq.once("finish", () => { requestCommitted = true; });
              const upstreamTimeoutMs = getUpstreamTimeoutMs(route.path);
              if (upstreamTimeoutMs > 0) {
                upstreamReq.setTimeout(upstreamTimeoutMs, () => {
                  const error = new Error(`upstream request timed out after ${upstreamTimeoutMs}ms`);
                  error.code = "ETIMEDOUT";
                  upstreamReq.destroy(error);
                });
              }
              upstreamReq.on("error", (error) => {
                if (ended || responseStarted) return;
                const retryable = isSafeToRetry(error, requestCommitted, clientReq.method, route.path);
                if (retryable && attemptIndex + 1 < RETRY_ATTEMPTS) {
                  attemptIndex++;
                  const delay = retryDelay(attemptIndex);
                  log(`[${requestId}] .. retry ${attemptIndex + 1}/${RETRY_ATTEMPTS} after ${delay}ms elapsed=${Date.now() - startedAt}ms bodyBytes=${body.length} committed=${requestCommitted} upstream=${attemptUpstream.hostname} error=${error.message}`);
                  retryTimer = setTimeout(attempt, delay);
                  return;
                }
                if (retryable && candidateIndex + 1 < candidates.length) {
                  candidateIndex++;
                  attemptIndex = 0;
                  log(`[${requestId}] .. fallback upstream=${candidates[candidateIndex].hostname} error=${error.message}`);
                  retryTimer = setTimeout(attempt, RETRY_BASE_DELAY_MS);
                  return;
                }
                ended = true;
                log(`[${requestId}] !! ${Date.now() - startedAt}ms ${error.message}`);
                writeRelayError(clientRes, error.message);
              });
              upstreamReq.end(body);
            }

            attempt();
          });
          clientReq.on("error", (error) => {
            if (!ended) {
              ended = true;
              if (activeRequest) activeRequest.destroy(new Error("client request failed"));
              log(`[${requestId}] !! client ${Date.now() - startedAt}ms ${error.message}`);
              writeRelayError(clientRes, error.message);
            }
          });
          clientReq.on("aborted", () => {
            ended = true;
            if (activeRequest) activeRequest.destroy(new Error("client aborted"));
            log(`[${requestId}] xx client aborted ${Date.now() - startedAt}ms`);
          });
          clientRes.on("close", () => {
            clearTimeout(retryTimer);
            if (!clientRes.writableEnded) {
              ended = true;
              if (activeRequest) activeRequest.destroy(new Error("client closed"));
            }
          });
        }

        function createProxiedTlsConnection(upstream, callback) {
          const socket = net.connect(Number(proxy.port || 80), proxy.hostname);
          socket.setKeepAlive(true, 10000);
          socket.setNoDelay(true);
          const target = `${upstream.hostname}:443`;
          let completed = false;
          function finish(error, connectedSocket) {
            if (completed) {
              if (connectedSocket) connectedSocket.destroy();
              return;
            }
            completed = true;
            socket.off("error", onSocketError);
            socket.setTimeout(0);
            callback(error, connectedSocket);
          }
          function onSocketError(error) {
            finish(error);
          }
          socket.once("connect", () => {
            socket.write(`CONNECT ${target} HTTP/1.1\r\nHost: ${target}\r\nProxy-Connection: keep-alive\r\n\r\n`);
          });
          socket.setTimeout(CONNECT_TIMEOUT_MS, () => {
            const error = new Error(`Proxy CONNECT timed out after ${CONNECT_TIMEOUT_MS}ms: ${target}`);
            error.code = "ETIMEDOUT";
            socket.destroy(error);
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
            const tlsSocket = tls.connect({ socket, servername: upstream.hostname, ALPNProtocols: ["http/1.1"] });
            tlsSocket.setKeepAlive(true, 10000);
            tlsSocket.setNoDelay(true);
            tlsSocket.setTimeout(CONNECT_TIMEOUT_MS, () => {
              const error = new Error(`TLS handshake timed out after ${CONNECT_TIMEOUT_MS}ms: ${target}`);
              error.code = "ETIMEDOUT";
              tlsSocket.destroy(error);
            });
            tlsSocket.once("secureConnect", () => tlsSocket.setTimeout(0));
            finish(null, tlsSocket);
          });
          socket.once("error", onSocketError);
        }

        const server = http.createServer((clientReq, clientRes) => {
          if ((clientReq.url || "") === "/__easyproxy/health") {
            clientRes.writeHead(200, { "content-type": "text/plain; charset=utf-8" });
            clientRes.end("easyproxy-antigravity-relay\n");
            return;
          }

          const route = resolveRoute(clientReq.url || "/");
          const headers = buildUpstreamHeaders(clientReq.headers, route.upstream);
          const requestId = nextRequestId++;
          const startedAt = Date.now();
          log(`[${requestId}] -> ${clientReq.method || "GET"} ${sanitizeClientUrl(clientReq.url)} upstream=${route.upstream.hostname} auth=${getAuthScheme(clientReq.headers)} len=${clientReq.headers["content-length"] || ""} expect=${clientReq.headers.expect ? "1" : "0"}`);
          if ((clientReq.url || "").startsWith("/v1internal:onboardUser")) {
            const authKey = getAuthKey(clientReq.headers);
            const cached = onboardCache.get(authKey);
            if (cached) {
              sendCachedOnboardResponse(clientReq, clientRes, cached, requestId, startedAt);
              return;
            }

            proxyBufferedWithRetries(clientReq, clientRes, route, headers, requestId, startedAt, authKey);
            return;
          }
          proxyBufferedWithRetries(clientReq, clientRes, route, headers, requestId, startedAt, null);
        });

        server.listen(listenPort, listenHost, () => {
          log(`listening http://${listenHost}:${listenPort} -> ${defaultUpstream} via ${proxy.href}`);
        });
        """;
}
