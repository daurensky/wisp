using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Wisp.Core;

public sealed record VoiceServerSettings(bool Enabled = false, string Url = "wss://89.126.195.20:8443/rtc", string CertificateSha256 = "")
{
    public static VoiceServerSettings Load(string root)
    {
        string path = Path.Combine(root, "voice-server.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<VoiceServerSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("voice-server.json") : new();
    }
    public Uri Endpoint()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != "wss" || uri.AbsolutePath != "/rtc" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new InvalidDataException("Нужен адрес wss://сервер:8443/rtc.");
        if (Enabled && (CertificateSha256.Length != 64 || !CertificateSha256.All(char.IsAsciiHexDigit)))
            throw new InvalidDataException("Нужен SHA-256 отпечаток сертификата: 64 шестнадцатеричных символа.");
        return uri;
    }
    public bool ValidateCertificate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || CertificateSha256.Length != 64 || !CertificateSha256.All(char.IsAsciiHexDigit)) return false;
        using var cert = new X509Certificate2(certificate);
        var now = DateTime.UtcNow;
        return now >= cert.NotBefore.ToUniversalTime() && now <= cert.NotAfter.ToUniversalTime() &&
            CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(CertificateSha256));
    }
    public void Save(string root)
    {
        _ = Endpoint();
        File.WriteAllText(Path.Combine(root, "voice-server.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
