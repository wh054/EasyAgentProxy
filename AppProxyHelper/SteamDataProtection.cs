using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace AppProxyHelper;

/// <summary>Per-user protection for dedicated EasyProxy Steam data directories.</summary>
internal static class SteamDataProtection
{
    private static readonly byte[] Entropy = SHA256.HashData("EasyProxy.Steam.ProtectedData.v1"u8);

    internal static byte[] Protect(byte[] plainBytes)
    {
        ArgumentNullException.ThrowIfNull(plainBytes);
        EnsureWindows();
        return ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
    }

    internal static byte[] Unprotect(byte[] protectedBytes)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        EnsureWindows();
        return ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
    }

    /// <summary>Use only on a dedicated application data directory, never a shared/user root.</summary>
    internal static void EnsurePrivateDirectory(string directory)
    {
        EnsureWindows();
        var info = Directory.CreateDirectory(Path.GetFullPath(directory));
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Steam 私有数据目录不能是符号链接或目录联接。");
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("无法确定当前 Windows 用户身份。");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Steam 数据保护依赖 Windows CurrentUser DPAPI。");
    }
}
