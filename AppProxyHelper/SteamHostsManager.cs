using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AppProxyHelper;

/// <summary>Owns only the EasyProxy Steam block; never replaces user or Steam++ mappings.</summary>
public sealed class SteamHostsManager
{
    public const string StartMarker = "# EasyProxy Steam Start";
    public const string EndMarker = "# EasyProxy Steam End";
    private const string MissingFinalNewlineMarker = "# EasyProxy Steam Original-Final-Newline: absent";
    private static readonly Regex OtherAcceleratorMarker = new(
        @"^\s*#\s*(?:Steam\+\+|SteamTools|Watt\s+Toolkit)\s+(?:Backup\s+)?(?:Start|End)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public SteamHostsManager(string? hostsPath = null)
    {
        HostsPath = Path.GetFullPath(hostsPath ?? Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts"));
    }

    public string HostsPath { get; }
    public string BackupPath => HostsPath + ".easyproxy-steam.bak";

    public bool HasManagedEntries
    {
        get
        {
            var document = HostsDocument.Read(ReadSnapshot());
            return FindManagedBlock(document.Text) is not null;
        }
    }

    /// <summary>Read-only preflight. Apply always repeats this check under its write lock.</summary>
    public void Validate(IEnumerable<string> domains)
    {
        var normalized = NormalizeDomains(domains);
        var document = HostsDocument.Read(ReadSnapshot());
        ValidateUnmanagedContent(RemoveBlock(document.Text, FindManagedBlock(document.Text)), normalized);
    }

    public void Apply(IEnumerable<string> domains, bool createBackup = true)
    {
        var normalized = NormalizeDomains(domains);
        using var operationLock = AcquireOperationLock();
        // Deny in-place writers while preparing the atomic replacement. FileShare.Delete is
        // required by File.Replace; a second read also detects external whole-file replacements.
        using var source = new FileStream(HostsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var original = ReadAll(source);
        var document = HostsDocument.Read(original);
        var oldBlock = FindManagedBlock(document.Text);
        var unmanaged = RemoveBlock(document.Text, oldBlock);
        ValidateUnmanagedContent(unmanaged, normalized);

        var newline = DetectNewline(unmanaged);
        var needsSeparator = unmanaged.Length > 0 && unmanaged[^1] != '\n' && unmanaged[^1] != '\r';
        var block = new StringBuilder();
        if (needsSeparator)
            block.Append(newline);
        block.Append(StartMarker).Append(newline);
        if (needsSeparator)
            block.Append(MissingFinalNewlineMarker).Append(newline);
        foreach (var host in normalized)
            block.Append("127.0.0.1 ").Append(host).Append(newline);
        block.Append(EndMarker).Append(newline);
        var replacement = document.Encode(unmanaged + block);
        if (original.AsSpan().SequenceEqual(replacement))
            return;

        // This durable original is recovery evidence only. Remove() operates on the latest
        // hosts document so that another program's subsequent additions are never rolled back.
        if (oldBlock is null && createBackup)
            WriteAtomicFile(BackupPath, original, replaceExisting: true);
        ReplaceHosts(original, replacement);
    }

    private static void ValidateUnmanagedContent(string unmanaged, IEnumerable<string> normalized)
    {
        var targetSet = new HashSet<string>(normalized, StringComparer.OrdinalIgnoreCase);
        foreach (var line in SplitLines(unmanaged))
        {
            if (OtherAcceleratorMarker.IsMatch(line.Content))
                throw new InvalidOperationException("hosts 中存在 Watt Toolkit / Steam++ 管理块。请先在该工具中停止加速，再启动 EasyProxy Steam 代理。");

            var content = line.Content.Split('#', 2)[0];
            var parts = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out _))
                continue;

            foreach (var host in parts.Skip(1))
            {
                if (targetSet.Contains(host.TrimEnd('.')))
                    throw new InvalidOperationException($"hosts 已有非 EasyProxy 托管的域名映射：{host}。为保留原配置，未修改 hosts。");
            }
        }

    }

    public void Remove()
    {
        using var operationLock = AcquireOperationLock();
        using var source = new FileStream(HostsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var original = ReadAll(source);
        var document = HostsDocument.Read(original);
        var block = FindManagedBlock(document.Text);
        if (block is null)
            return;
        ReplaceHosts(original, document.Encode(RemoveBlock(document.Text, block)));
    }

    private FileStream AcquireOperationLock()
    {
        try
        {
            return new FileStream(HostsPath + ".easyproxy-steam.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.DeleteOnClose);
        }
        catch (IOException exception)
        {
            throw new IOException("无法独占 Steam hosts 操作锁；可能已有另一个 EasyProxy 正在修改 hosts。", exception);
        }
    }

    private byte[] ReadSnapshot()
    {
        using var stream = new FileStream(HostsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadAll(stream);
    }

    private void ReplaceHosts(byte[] expected, byte[] replacement)
    {
        var tempPath = HostsPath + ".easyproxy-steam." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDurably(tempPath, replacement);
            if (!File.ReadAllBytes(HostsPath).AsSpan().SequenceEqual(expected))
                throw new IOException("hosts 在操作期间被其他程序替换，已取消写入以保留最新内容。请重试。");
            // Atomic on the same volume, retaining the existing hosts file's security attributes.
            File.Replace(tempPath, HostsPath, null);
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
    }

    private static void WriteAtomicFile(string path, byte[] bytes, bool replaceExisting)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDurably(temp, bytes);
            File.Move(temp, path, replaceExisting);
        }
        finally
        {
            TryDeleteTemporaryFile(temp);
        }
    }

    private static void WriteDurably(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string NormalizeDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("Steam 域名不能为空。", nameof(domain));
        var host = new IdnMapping().GetAscii(domain.Trim().TrimEnd('.')).ToLowerInvariant();
        if (host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns || host.Contains('*') || host.Contains('_') ||
            host.Split('.').Any(label => label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'))
            throw new ArgumentException($"无效的 Steam 域名：{domain}", nameof(domain));
        return host;
    }

    private static string[] NormalizeDomains(IEnumerable<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        var normalized = domains.Select(NormalizeDomain).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Length == 0)
            throw new ArgumentException("Steam 代理域名列表不能为空。", nameof(domains));
        if (normalized.Any(host => !SteamDomainCatalog.IsSupported(host)))
            throw new ArgumentException("Hosts 只允许接管 Steam 网页域名，下载与通用 CDN 保留原网络链路。", nameof(domains));
        return normalized;
    }

    private static ManagedBlock? FindManagedBlock(string text)
    {
        var starts = new List<TextLine>();
        var ends = new List<TextLine>();
        foreach (var line in SplitLines(text))
        {
            if (line.Content.Trim().Equals(StartMarker, StringComparison.Ordinal)) starts.Add(line);
            if (line.Content.Trim().Equals(EndMarker, StringComparison.Ordinal)) ends.Add(line);
        }
        if (starts.Count == 0 && ends.Count == 0)
            return null;
        if (starts.Count != 1 || ends.Count != 1 || starts[0].Offset >= ends[0].Offset)
            throw new InvalidDataException("EasyProxy Steam hosts 标记缺失、重复或顺序错误；为避免丢失其他内容，未修改 hosts。请参考 .easyproxy-steam.bak 备份检查标记。");
        return new ManagedBlock(starts[0].Offset, ends[0].Offset + ends[0].Length);
    }

    private static string RemoveBlock(string text, ManagedBlock? block)
    {
        if (block is null)
            return text;
        var start = block.Start;
        // Remove the one separator that we inserted for a file without a final newline,
        // but keep it when another program has appended mappings after our block.
        if (block.End == text.Length &&
            SplitLines(text[block.Start..block.End]).Any(line => line.Content == MissingFinalNewlineMarker))
        {
            if (start > 1 && text.AsSpan(start - 2, 2).SequenceEqual("\r\n")) start -= 2;
            else if (start > 0 && text[start - 1] is '\r' or '\n') start--;
        }
        return text[..start] + text[block.End..];
    }

    private static string DetectNewline(string text)
    {
        foreach (var line in SplitLines(text))
        {
            var ending = text.AsSpan(line.Offset + line.Content.Length, line.Length - line.Content.Length);
            if (ending.Length > 0)
                return ending.ToString();
        }
        return "\r\n";
    }

    private static IEnumerable<TextLine> SplitLines(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = start;
            while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
            var content = text[start..end];
            if (end < text.Length && text[end++] == '\r' && end < text.Length && text[end] == '\n') end++;
            yield return new TextLine(start, end - start, content);
            start = end;
        }
    }

    private sealed record TextLine(int Offset, int Length, string Content);
    private sealed record ManagedBlock(int Start, int End);

    private sealed record HostsDocument(Encoding Encoding, byte[] Preamble, string Text)
    {
        public static HostsDocument Read(byte[] bytes)
        {
            Encoding encoding;
            var preambleLength = 0;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { encoding = new UTF32Encoding(false, true, true); preambleLength = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, true, true); preambleLength = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, true, true); preambleLength = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, true, true); preambleLength = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = new UTF8Encoding(true, true); preambleLength = 3; }
            // No-BOM hosts files may contain ANSI comments. Latin1 is a lossless byte
            // representation here: all inserted and inspected names/markers are ASCII.
            else encoding = Encoding.Latin1;
            return new HostsDocument(encoding, bytes[..preambleLength], encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength));
        }

        public byte[] Encode(string text)
        {
            var content = Encoding.GetBytes(text);
            var result = new byte[Preamble.Length + content.Length];
            Preamble.CopyTo(result, 0);
            content.CopyTo(result, Preamble.Length);
            return result;
        }
    }
}
