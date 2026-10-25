using System.Security.Cryptography;
using System.Text;

namespace Wisp.Desktop;

public static class SessionVault
{
    public static void Save(string root, string value) => File.WriteAllBytes(Path.Combine(root, "session.bin"), ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public static string? Read(string root)
    {
        try { var path = Path.Combine(root, "session.bin"); return File.Exists(path) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)) : null; }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { return null; }
    }
    public static void Clear(string root) => File.Delete(Path.Combine(root, "session.bin"));
}
