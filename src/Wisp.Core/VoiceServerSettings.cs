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

/// <summary>Loads the owner's explicit choice on each join; never falls back to P2P silently.</summary>
public sealed class ConfiguredVoiceRoom(FirebaseBackend cloud, string root, IIceServerProvider ice) : IVoiceRoom
{
    private IVoiceRoom? current;
    public bool Joined => current?.Joined == true;
    public string? ChannelId => current?.ChannelId;
    public string TransportName => current?.TransportName ?? "WebRTC";
    public SfuConnectionInfo? ConnectionInfo => current?.ConnectionInfo;
    public IReadOnlyList<RtcSeat> Participants => current?.Participants ?? Array.Empty<RtcSeat>();
    public int ConnectedCount => current?.ConnectedCount ?? 0;
    public IReadOnlyList<string> ConnectedPeerIds => current?.ConnectedPeerIds ?? Array.Empty<string>();
    public IReadOnlyDictionary<string, string> ConnectionPaths => current?.ConnectionPaths ?? new Dictionary<string, string>();
    public event Action<WireMessage>? MessageReceived;
    public event Action<string>? Error;
    public event Action? Changed;
    public async Task EnterAsync(string communityId, string channelId, bool mute, bool deafen)
    {
        await LeaveAsync();
        var settings = VoiceServerSettings.Load(root);
        if (settings.Enabled) _ = settings.Endpoint();
        current = settings.Enabled ? new SfuRoom(cloud, settings, ice) : new RtcMesh(cloud, ice);
        current.MessageReceived += OnMessage; current.Error += OnError; current.Changed += OnChanged;
        await current.EnterAsync(communityId, channelId, mute, deafen);
    }
    private void OnMessage(WireMessage m) => MessageReceived?.Invoke(m);
    private void OnError(string text) => Error?.Invoke(text);
    private void OnChanged() => Changed?.Invoke();
    public Task SetMutedAsync(bool mute, bool deafen) => current?.SetMutedAsync(mute, deafen) ?? Task.CompletedTask;
    public Task SendAsync(WireMessage m) => current?.SendAsync(m) ?? Task.CompletedTask;
    public async Task LeaveAsync()
    {
        var old = current;
        current = null;
        if (old is null) return;
        old.MessageReceived -= OnMessage; old.Error -= OnError; old.Changed -= OnChanged;
        await old.DisposeAsync();
        Changed?.Invoke();
    }
    public async ValueTask DisposeAsync() => await LeaveAsync();
}
