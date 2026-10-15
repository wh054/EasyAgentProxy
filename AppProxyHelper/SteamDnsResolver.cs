using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace AppProxyHelper;

/// <summary>Origin resolution never uses the OS resolver, which contains our hosts mappings.</summary>
public sealed class SteamDnsResolver : IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(4)
    }) { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 65536 };
    private readonly string[] _endpoints;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IPAddress[] Addresses)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SteamDnsResolver(SteamProxySettings settings) => _endpoints = settings.DohEndpoints.ToArray();

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        if (IPAddress.TryParse(host, out var literal))
            return IsPublicAddress(literal) ? [literal] : throw new IOException("拒绝回源到本机或私有地址。");
        if (_cache.TryGetValue(host, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Addresses;
        foreach (var endpoint in _endpoints)
        {
            try
            {
                // A records are sufficient on IPv4 networks; no AAAA timeout on hosts-only mode.
                var separator = endpoint.Contains('?') ? '&' : '?';
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}{separator}name={Uri.EscapeDataString(host)}&type=A");
                request.Headers.Accept.ParseAdd("application/dns-json");
                using var response = await _client.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
                if (json.RootElement.GetProperty("Status").GetInt32() != 0 || !json.RootElement.TryGetProperty("Answer", out var answers)) continue;
                var addresses = new List<IPAddress>();
                var ttl = 300;
                foreach (var answer in answers.EnumerateArray())
                {
                    if (answer.GetProperty("type").GetInt32() != 1) continue;
                    if (IPAddress.TryParse(answer.GetProperty("data").GetString(), out var ip) && IsPublicAddress(ip))
                    {
                        addresses.Add(ip);
                        if (answer.TryGetProperty("TTL", out var time)) ttl = Math.Min(ttl, Math.Max(0, time.GetInt32()));
                    }
                }
                if (addresses.Count == 0) continue;
                var result = addresses.Distinct().ToArray();
                _cache[host] = (DateTimeOffset.UtcNow.AddSeconds(ttl), result);
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException($"无法通过独立 DoH 解析 Steam 回源域名 {host}；未回退系统 DNS，以免 Hosts 自环。");
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224
                && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20;
    }

    public void Dispose() => _client.Dispose();
}
