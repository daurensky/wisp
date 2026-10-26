using System.Text.Json;
using System.Text.RegularExpressions;
using SIPSorcery.Net;

namespace Wisp.Core;

public interface IIceServerProvider
{
    Task<IReadOnlyList<RTCIceServer>> GetAsync(CancellationToken cancellation = default);
}

public sealed class StaticIceServerProvider : IIceServerProvider
{
    private readonly string[] urls;
    public StaticIceServerProvider(IEnumerable<string>? urls = null)
    {
        this.urls = FileIceServerProvider.Validate(urls ?? FileIceServerProvider.Defaults);
    }
    public Task<IReadOnlyList<RTCIceServer>> GetAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        IReadOnlyList<RTCIceServer> result = urls.Select(url => new RTCIceServer { urls = url }).ToArray();
        return Task.FromResult(result);
    }
}

public sealed record IceSettings(string[] StunUrls);

/// <summary>Explicit STUN configuration. TURN is outside the owner's requested scope.</summary>
public sealed class FileIceServerProvider(string rootDirectory) : IIceServerProvider
{
    private string PathName => Path.Combine(rootDirectory, "network.json");
    public static string[] Defaults => [RtcPeer.GoogleStun, "stun:stun1.l.google.com:19302"];

    public IceSettings Load()
    {
        if (!File.Exists(PathName))
            return new IceSettings(Defaults);
        var settings = JsonSerializer.Deserialize<IceSettings>(File.ReadAllText(PathName))
            ?? throw new InvalidDataException("Файл network.json не содержит сетевых настроек.");
        return new IceSettings(Validate(settings.StunUrls));
    }

    public void Save(IEnumerable<string> urls)
    {
        var settings = new IceSettings(Validate(urls));
        Directory.CreateDirectory(rootDirectory);
        var temporary = PathName + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, PathName, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task<IReadOnlyList<RTCIceServer>> GetAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        IReadOnlyList<RTCIceServer> servers = Load().StunUrls.Select(url => new RTCIceServer { urls = url }).ToArray();
        return Task.FromResult(servers);
    }

    public static string[] Validate(IEnumerable<string>? urls)
    {
        if (urls is null)
            throw new ArgumentException("Задай список STUN-серверов.");
        var result = urls.Select(url => url?.Trim() ?? "").Where(url => url.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length > 8)
            throw new ArgumentException("Допускается не более восьми STUN-серверов.");
        foreach (var url in result)
        {
            if (url.Length > 256 ||
                !Regex.IsMatch(url, @"^stun:(?:\[[0-9a-fA-F:]+\]|[a-zA-Z0-9.-]+)(?::(?<port>[0-9]{1,5}))?(?:\?transport=udp)?$", RegexOptions.IgnoreCase) ||
                !STUNUri.TryParse(url, out _))
                throw new ArgumentException("Нужен STUN URL по UDP, например stun:stun.l.google.com:19302. TURN и пароли здесь не используются.");
            var port = Regex.Match(url, @":(?<port>[0-9]+)(?:\?|$)").Groups["port"];
            if (port.Success && (!int.TryParse(port.Value, out int number) || number is < 1 or > 65535))
                throw new ArgumentException("Порт STUN должен быть от 1 до 65535.");
        }
        return result;
    }
}
