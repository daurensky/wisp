using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace Wisp.Core;

public enum ServerConnectionState { Disconnected, Connecting, Connected, Unreachable }
public enum ConnectionQuality { Unknown, Good, Fair, Poor }
public sealed record SfuConnectionInfo(string Endpoint, ServerConnectionState State, double? PingMilliseconds = null,
    DateTimeOffset? MeasuredAt = null, string? CandidateTypes = null)
{
    public ConnectionQuality Quality => State == ServerConnectionState.Unreachable ? ConnectionQuality.Fair : PingMilliseconds switch
    {
        >= 200 => ConnectionQuality.Poor,
        >= 100 => ConnectionQuality.Fair,
        >= 0 => ConnectionQuality.Good,
        _ => ConnectionQuality.Unknown
    };
}

/// <summary>Small authenticated-by-pin HTTPS health RTT, including server processing, not ICMP/media latency.</summary>
public sealed class SfuHealthProbe : IDisposable
{
    private readonly HttpClient http;
    private readonly Uri endpoint;
    public SfuHealthProbe(VoiceServerSettings settings, HttpMessageHandler? handler = null)
    {
        endpoint = new UriBuilder(settings.Endpoint()) { Scheme = "https", Path = "/healthz" }.Uri;
        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => settings.ValidateCertificate(certificate, errors)
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    }
    public async Task<double?> MeasureAsync(CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var token = timeout.Token;
        try
        {
            long start = Stopwatch.GetTimestamp();
            using var response = await http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var bytes = new byte[128]; int length = 0;
            while (length < bytes.Length)
            {
                int count = await stream.ReadAsync(bytes.AsMemory(length), token);
                if (count == 0) break;
                length += count;
            }
            if (length == bytes.Length || Encoding.UTF8.GetString(bytes, 0, length).Trim() != "wisp-sfu protocol=1") return null;
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        catch (IOException) { return null; }
    }
    public void Dispose() => http.Dispose();
}
