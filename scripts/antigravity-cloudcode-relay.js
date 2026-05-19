const http = require("http");
const https = require("https");
const net = require("net");
const tls = require("tls");

const listenHost = process.env.RELAY_HOST || "127.0.0.1";
const listenPort = Number(process.env.RELAY_PORT || "18990");
const upstream = new URL(process.env.CLOUDCODE_UPSTREAM || "https://daily-cloudcode-pa.googleapis.com");
const proxy = new URL(process.env.EASYPROXY_HTTP_PROXY || "http://127.0.0.1:7890");

function createProxiedTlsConnection(options, callback) {
  const proxyPort = Number(proxy.port || 80);
  const socket = net.connect(proxyPort, proxy.hostname);
  const target = `${upstream.hostname}:443`;

  socket.once("connect", () => {
    socket.write(
      `CONNECT ${target} HTTP/1.1\r\n` +
        `Host: ${target}\r\n` +
        "Proxy-Connection: keep-alive\r\n" +
        "\r\n",
    );
  });

  let buffer = Buffer.alloc(0);

  socket.on("data", function onData(chunk) {
    buffer = Buffer.concat([buffer, chunk]);
    const headerEnd = buffer.indexOf("\r\n\r\n");
    if (headerEnd < 0) {
      return;
    }

    socket.off("data", onData);
    const header = buffer.subarray(0, headerEnd).toString("ascii");
    const statusLine = header.split("\r\n", 1)[0] || "";
    if (!/^HTTP\/1\.[01] 2\d\d\b/.test(statusLine)) {
      socket.destroy(new Error(`Proxy CONNECT failed: ${statusLine}`));
      return;
    }

    const rest = buffer.subarray(headerEnd + 4);
    if (rest.length) {
      socket.unshift(rest);
    }

    const tlsSocket = tls.connect({
      socket,
      servername: upstream.hostname,
      ALPNProtocols: ["http/1.1"],
    });
    callback(null, tlsSocket);
  });

  socket.once("error", callback);
}

const server = http.createServer((clientReq, clientRes) => {
  const headers = { ...clientReq.headers, host: upstream.hostname };
  delete headers["proxy-connection"];

  const upstreamReq = https.request(
    {
      protocol: "https:",
      hostname: upstream.hostname,
      port: 443,
      method: clientReq.method,
      path: clientReq.url,
      headers,
      createConnection: createProxiedTlsConnection,
    },
    (upstreamRes) => {
      clientRes.writeHead(upstreamRes.statusCode || 502, upstreamRes.headers);
      upstreamRes.pipe(clientRes);
    },
  );

  upstreamReq.on("error", (error) => {
    clientRes.writeHead(502, { "content-type": "text/plain; charset=utf-8" });
    clientRes.end(`CloudCode relay error: ${error.message}\n`);
  });

  clientReq.pipe(upstreamReq);
});

server.listen(listenPort, listenHost, () => {
  console.log(
    `Antigravity CloudCode relay listening on http://${listenHost}:${listenPort}, forwarding to ${upstream.origin} via ${proxy.origin}`,
  );
});

