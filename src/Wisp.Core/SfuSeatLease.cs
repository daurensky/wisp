using System.Text.Json;
namespace Wisp.Core;

/// <summary>Retains the existing five-slot schema, CAS ownership and independent 40s guard.</summary>
internal sealed class SfuSeatLease(FirebaseBackend cloud, string community, string channel) : IAsyncDisposable
{
    private readonly string uid = cloud.Profile!.Id;
    private readonly SemaphoreSlim renewal = new(1);
    private readonly CancellationTokenSource stop = new();
    private Task? loop, guard;
    private string? slot;
    private volatile bool valid;
    private bool muted, deafened;
    private long renewed;
    public string Session { get; } = Guid.NewGuid().ToString("N");
    private string Path => $"communities/{community}/rtcRooms/{channel}";
    public IReadOnlyList<RtcSeat> Participants { get; private set; } = Array.Empty<RtcSeat>();
    public event Action? Changed;
    public event Action<string>? Lost;
    public async Task ClaimAsync(bool mute, bool deafen, CancellationToken token)
    {
        muted = mute; deafened = deafen;
        await cloud.EnsureServerTimeAsync();
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var snapshot = await cloud.GetAsync<JsonElement>(Path, token);
            var seats = RtcMesh.ActiveParticipants(snapshot, cloud.ServerNow());
            string? candidate = null;
            for (int i = 0; i < 5; i++)
            {
                string name = $"s{i}";
                RtcSeat? old = snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty(name, out var data) && data.ValueKind == JsonValueKind.Object ? data.Deserialize<RtcSeat>() : null;
                if (old?.UserId == uid) { candidate = name; break; }
                if (candidate is null && (old is null || cloud.ServerNow().ToUnixTimeMilliseconds() - old.SeenAt > 45000)) candidate = name;
            }
            if (candidate is null) throw new InvalidOperationException("Канал заполнен: максимум 5 человек.");
            var current = await cloud.GetVersionedAsync<RtcSeat>($"{Path}/{candidate}", token);
            if (current.Value is { } seat && seat.UserId != uid && cloud.ServerNow().ToUnixTimeMilliseconds() - seat.SeenAt <= 45000) continue;
            if (await cloud.TryPutVersionedAsync($"{Path}/{candidate}", Seat(), current.ETag, token))
            {
                slot = candidate; valid = true; renewed = cloud.Clock.GetTimestamp();
                Participants = seats.Where(s => s.UserId != uid).Append(Seat()).ToArray();
                loop = Task.Run(LoopAsync); guard = Task.Run(GuardAsync); return;
            }
            await Task.Delay(100, token);
        }
        throw new InvalidOperationException("Место занял другой участник. Войди ещё раз.");
    }
    private RtcSeat Seat() => new(uid, Session, cloud.ServerNow().ToUnixTimeMilliseconds(), muted, deafened);
    private void Expire() { if (!valid) return; valid = false; stop.Cancel(); Lost?.Invoke("Место в голосовом канале потеряно. Подключись снова."); }
    public async Task SetMutedAsync(bool mute, bool deafen) { muted = mute; deafened = deafen; if (valid) await RenewAsync(stop.Token); }
    private async Task RenewAsync(CancellationToken token)
    {
        await renewal.WaitAsync(token);
        try
        {
            if (!valid || slot is null) return;
            var current = await cloud.GetVersionedAsync<RtcSeat>($"{Path}/{slot}", token);
            if (current.Value?.UserId != uid || current.Value.SessionId != Session) { Expire(); return; }
            if (await cloud.TryPutVersionedAsync($"{Path}/{slot}", Seat(), current.ETag, token)) Interlocked.Exchange(ref renewed, cloud.Clock.GetTimestamp());
        }
        finally { renewal.Release(); }
    }
    private async Task LoopAsync()
    {
        try
        {
            int tick = 0;
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cloud.Clock, stop.Token);
                try
                {
                    if (++tick % 2 == 0) await RenewAsync(stop.Token);
                    var data = await cloud.GetAsync<JsonElement>(Path, stop.Token);
                    Participants = RtcMesh.ActiveParticipants(data, cloud.ServerNow());
                    if (!Participants.Any(s => s.UserId == uid && s.SessionId == Session)) { Expire(); return; }
                    Changed?.Invoke();
                }
                catch (FirebaseRequestException ex) when (ex.Failure is FirebaseFailure.PermissionDenied or FirebaseFailure.Authentication) { Expire(); return; }
                catch (Exception) when (!stop.IsCancellationRequested) { /* The independent guard bounds any outage. */ }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task GuardAsync()
    {
        try { while (!stop.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(1), cloud.Clock, stop.Token); if (cloud.Clock.GetElapsedTime(Interlocked.Read(ref renewed)) >= TimeSpan.FromSeconds(40)) { Expire(); return; } } }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        valid = false; stop.Cancel();
        try { await Task.WhenAll(new[] { loop, guard }.OfType<Task>()); } catch (OperationCanceledException) { }
        if (slot is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var current = await cloud.GetVersionedAsync<RtcSeat>($"{Path}/{slot}", timeout.Token);
                if (current.Value?.UserId == uid && current.Value.SessionId == Session) await cloud.TryPutVersionedAsync<object?>($"{Path}/{slot}", null, current.ETag, timeout.Token);
            }
            catch (Exception) { /* Existing slot expires after 45 seconds. */ }
        }
        stop.Dispose();
    }
}
