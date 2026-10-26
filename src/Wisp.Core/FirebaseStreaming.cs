using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Wisp.Core;

public enum FirebaseFailure { PermissionDenied, Authentication, InvalidRequest, Unavailable, ClockUnavailable, StreamCancelled }

public sealed class FirebaseRequestException(HttpStatusCode status, FirebaseFailure failure, string message)
    : InvalidOperationException(message)
{
    public HttpStatusCode StatusCode { get; } = status;
    public FirebaseFailure Failure { get; } = failure;
}

public sealed record FirebaseVersion<T>(T? Value, string ETag);
public sealed record FirebaseStreamEvent(string Kind, string Path, JsonElement Data);

public sealed partial class FirebaseBackend
{
    private readonly TimeProvider time;
    private readonly object clockGate = new();
    private DateTimeOffset serverAnchor;
    private long anchorTimestamp;
    private bool hasServerTime;
    public TimeProvider Clock => time;
    public bool HasServerTime { get { lock (clockGate) return hasServerTime; } }

    public DateTimeOffset ServerNow()
    {
        lock (clockGate)
            return hasServerTime
                ? serverAnchor + time.GetElapsedTime(anchorTimestamp, time.GetTimestamp())
                : time.GetUtcNow();
    }

    private void ObserveServerDate(HttpResponseMessage response)
    {
        if (response.Headers.Date is not { } date) return;
        lock (clockGate)
        {
            // HTTP Date has one-second precision; use the middle of that second.
            serverAnchor = date.AddMilliseconds(500);
            anchorTimestamp = time.GetTimestamp();
            hasServerTime = true;
        }
    }

    public async Task EnsureServerTimeAsync()
    {
        if (HasServerTime) return;
        _ = await GetAsync<JsonElement>($"profiles/{Profile!.Id}/Info");
        if (!HasServerTime)
            throw new FirebaseRequestException(HttpStatusCode.ServiceUnavailable, FirebaseFailure.ClockUnavailable,
                "Firebase не передал серверное время. Проверь соединение и HTTP-прокси; место в канале пока не занято.");
    }

    public async Task<FirebaseVersion<T>> GetVersionedAsync<T>(string path, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, await Url(path));
        request.Headers.TryAddWithoutValidation("X-Firebase-ETag", "true");
        using var response = await http.SendAsync(request, cancellation);
        using var data = await Read(response);
        var etag = response.Headers.ETag?.Tag
            ?? throw new InvalidDataException("Firebase не вернул ETag для атомарной записи.");
        return new(data.RootElement.ValueKind == JsonValueKind.Null ? default : data.RootElement.Deserialize<T>(), etag);
    }

    public async Task<bool> TryPutVersionedAsync<T>(string path, T value, string etag, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, await Url(path))
        {
            Content = JsonContent.Create(value, options: new JsonSerializerOptions())
        };
        request.Headers.TryAddWithoutValidation("if-match", etag);
        using var response = await http.SendAsync(request, cancellation);
        ObserveServerDate(response);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) return false;
        using var result = await Read(response);
        return true;
    }

    /// <summary>One RTDB SSE connection, cancellation-aware and transparent on auth_revoked.</summary>
    public async IAsyncEnumerable<FirebaseStreamEvent> WatchAsync(
        string path, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, await Url(path));
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            ObserveServerDate(response);
            if (!response.IsSuccessStatusCode)
            {
                using var error = await Read(response);
                yield break;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string kind = "message";
            var data = new StringBuilder();
            bool renew = false;
            while (!cancellation.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellation);
                if (line is null) break;
                if (line.Length > 131072 || data.Length + line.Length > 131072)
                    throw new InvalidDataException("Слишком большое событие Firebase.");
                if (line.Length > 0)
                {
                    if (line.StartsWith("event:")) kind = line[6..].Trim();
                    else if (line.StartsWith("data:")) data.Append(line[5..].TrimStart());
                    continue;
                }
                if (kind == "auth_revoked")
                {
                    expires = DateTimeOffset.MinValue;
                    renew = true;
                    break;
                }
                if (kind == "cancel")
                    throw new FirebaseRequestException(HttpStatusCode.Forbidden, FirebaseFailure.StreamCancelled,
                        "Firebase остановил поток комнаты: доступ отозван или изменились правила.");
                if (kind is "put" or "patch" && data.Length > 0)
                {
                    using var json = JsonDocument.Parse(data.ToString());
                    var root = json.RootElement;
                    var eventPath = root.GetProperty("path").GetString() ?? "/";
                    if (eventPath.Length > 1024) throw new InvalidDataException("Некорректный путь события Firebase.");
                    yield return new FirebaseStreamEvent(kind, eventPath, root.GetProperty("data").Clone());
                }
                kind = "message";
                data.Clear();
            }
            if (!renew) yield break;
        }
    }
}
