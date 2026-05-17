using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace AppProxyHelper;

public static class ProxyDiagnostics
{
    public static async Task<bool> CheckAsync(AppProxyConfig config, AppLogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var proxy = ProxyEndpoint.Parse(config.ProxyUri);
            logger.Info($"代理端点: {proxy.Scheme}://{proxy.Host}:{proxy.Port}");

            await LogDnsResolutionAsync(proxy.Host, logger, cancellationToken);
            await TestTcpConnectionAsync(proxy, config.Diagnostics.TcpConnectTimeoutMs, logger, cancellationToken);

            if (config.Diagnostics.TestProxyHandshake)
            {
                if (proxy.IsSocks)
                {
                    await TestSocks5Async(proxy, config, logger, cancellationToken);
                }
                else if (proxy.IsHttp)
                {
                    await TestHttpProxyAsync(proxy, config, logger, cancellationToken);
                }
            }

            logger.Info("代理诊断完成: ok");
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or InvalidOperationException)
        {
            logger.Error("代理诊断失败。", ex);
            return false;
        }
    }

    private static async Task LogDnsResolutionAsync(string host, AppLogger logger, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            logger.Info($"代理主机是 IP 地址: {address}");
            return;
        }

        logger.Info($"解析代理主机: {host}");
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        logger.Info($"代理主机解析结果: {string.Join(", ", addresses.Select(static item => item.ToString()))}");
    }

    private static async Task TestTcpConnectionAsync(
        ProxyEndpoint proxy,
        int timeoutMs,
        AppLogger logger,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        logger.Info($"测试 TCP 连接: {proxy.Host}:{proxy.Port}, timeout={timeoutMs}ms");
        await client.ConnectAsync(proxy.Host, proxy.Port).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
        logger.Info("TCP 连接测试通过。");
    }

    private static async Task TestSocks5Async(
        ProxyEndpoint proxy,
        AppProxyConfig config,
        AppLogger logger,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(proxy.Host, proxy.Port).WaitAsync(
            TimeSpan.FromMilliseconds(config.Diagnostics.TcpConnectTimeoutMs),
            cancellationToken);

        var stream = client.GetStream();
        logger.Info(string.IsNullOrEmpty(proxy.UserInfo)
            ? "测试 SOCKS5 无认证握手。"
            : "测试 SOCKS5 用户名密码认证握手。");

        var methods = string.IsNullOrEmpty(proxy.UserInfo)
            ? new byte[] { 0x05, 0x01, 0x00 }
            : new byte[] { 0x05, 0x02, 0x00, 0x02 };

        await stream.WriteAsync(methods, cancellationToken);
        var response = await ReadExactAsync(stream, 2, cancellationToken);

        if (response[0] != 0x05)
        {
            throw new InvalidOperationException($"SOCKS5 响应版本异常: 0x{response[0]:X2}");
        }

        if (response[1] == 0xFF)
        {
            throw new InvalidOperationException("SOCKS5 代理拒绝客户端提供的认证方式。");
        }

        if (response[1] == 0x02)
        {
            await AuthenticateSocks5Async(stream, proxy, cancellationToken);
        }
        else if (response[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 认证方法不支持: 0x{response[1]:X2}");
        }

        logger.Info($"SOCKS5 握手通过，认证方法=0x{response[1]:X2}");

        if (!config.Diagnostics.TestProxyConnect)
        {
            logger.Info("testProxyConnect=false，跳过通过代理连接外部目标的测试。");
            return;
        }

        logger.Info($"测试 SOCKS5 CONNECT: {config.Diagnostics.ConnectTestHost}:{config.Diagnostics.ConnectTestPort}");
        var request = BuildSocks5ConnectRequest(config.Diagnostics.ConnectTestHost, config.Diagnostics.ConnectTestPort);
        await stream.WriteAsync(request, cancellationToken);

        var header = await ReadExactAsync(stream, 4, cancellationToken);
        if (header[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 CONNECT 失败，reply=0x{header[1]:X2}");
        }

        await DrainSocksBindAddressAsync(stream, header[3], cancellationToken);
        logger.Info("SOCKS5 CONNECT 测试通过。");
    }

    private static async Task TestHttpProxyAsync(
        ProxyEndpoint proxy,
        AppProxyConfig config,
        AppLogger logger,
        CancellationToken cancellationToken)
    {
        if (!config.Diagnostics.TestProxyConnect)
        {
            logger.Info("HTTP/HTTPS 代理已完成 TCP 连通性测试；testProxyConnect=false，跳过 CONNECT 测试。");
            return;
        }

        using var client = new TcpClient();
        await client.ConnectAsync(proxy.Host, proxy.Port).WaitAsync(
            TimeSpan.FromMilliseconds(config.Diagnostics.TcpConnectTimeoutMs),
            cancellationToken);

        Stream stream = client.GetStream();
        if (proxy.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
            await sslStream.AuthenticateAsClientAsync(proxy.Host);
            stream = sslStream;
        }

        var target = $"{config.Diagnostics.ConnectTestHost}:{config.Diagnostics.ConnectTestPort}";
        logger.Info($"测试 HTTP CONNECT: {target}");
        var requestText =
            $"CONNECT {target} HTTP/1.1\r\n" +
            $"Host: {target}\r\n" +
            "Proxy-Connection: keep-alive\r\n" +
            "User-Agent: EasyProxy/1.0\r\n";

        if (!string.IsNullOrWhiteSpace(proxy.UserInfo))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(proxy.UserInfo)));
            requestText += $"Proxy-Authorization: Basic {auth}\r\n";
        }

        requestText += "\r\n";

        var request = Encoding.ASCII.GetBytes(requestText);
        await stream.WriteAsync(request, cancellationToken);
        var response = await ReadHttpHeaderAsync(stream, cancellationToken);
        var statusLine = response.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];

        if (!statusLine.Contains(" 200 ", StringComparison.Ordinal)
            && !statusLine.Contains(" 2", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"HTTP CONNECT 响应异常: {statusLine}");
        }

        logger.Info($"HTTP CONNECT 测试通过: {statusLine}");
    }

    private static byte[] BuildSocks5ConnectRequest(string host, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("connectTestPort 必须在 1-65535 范围内。");
        }

        var hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length > 255)
        {
            throw new InvalidOperationException("connectTestHost 太长，SOCKS5 域名字段最多 255 字节。");
        }

        var request = new byte[7 + hostBytes.Length];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x03;
        request[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)(port & 0xFF);
        return request;
    }

    private static async Task AuthenticateSocks5Async(
        Stream stream,
        ProxyEndpoint proxy,
        CancellationToken cancellationToken)
    {
        var separatorIndex = proxy.UserInfo.IndexOf(':');
        var username = separatorIndex >= 0 ? proxy.UserInfo[..separatorIndex] : proxy.UserInfo;
        var password = separatorIndex >= 0 ? proxy.UserInfo[(separatorIndex + 1)..] : "";

        var usernameBytes = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(username));
        var passwordBytes = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(password));
        if (usernameBytes.Length > 255 || passwordBytes.Length > 255)
        {
            throw new InvalidOperationException("SOCKS5 用户名和密码最多 255 字节。");
        }

        var request = new byte[3 + usernameBytes.Length + passwordBytes.Length];
        request[0] = 0x01;
        request[1] = (byte)usernameBytes.Length;
        Buffer.BlockCopy(usernameBytes, 0, request, 2, usernameBytes.Length);
        request[2 + usernameBytes.Length] = (byte)passwordBytes.Length;
        Buffer.BlockCopy(passwordBytes, 0, request, 3 + usernameBytes.Length, passwordBytes.Length);

        await stream.WriteAsync(request, cancellationToken);
        var response = await ReadExactAsync(stream, 2, cancellationToken);
        if (response[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 用户名密码认证失败: 0x{response[1]:X2}");
        }
    }

    private static async Task DrainSocksBindAddressAsync(Stream stream, byte addressType, CancellationToken cancellationToken)
    {
        var addressLength = addressType switch
        {
            0x01 => 4,
            0x03 => (await ReadExactAsync(stream, 1, cancellationToken))[0],
            0x04 => 16,
            _ => throw new InvalidOperationException($"SOCKS5 地址类型异常: 0x{addressType:X2}")
        };

        await ReadExactAsync(stream, addressLength + 2, cancellationToken);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("连接被对端关闭。");
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var used = 0;

        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used, buffer.Length - used), cancellationToken);
            if (read == 0)
            {
                break;
            }

            used += read;
            var text = Encoding.ASCII.GetString(buffer, 0, used);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return text;
            }
        }

        throw new IOException("读取 HTTP 代理响应头失败。");
    }
}
