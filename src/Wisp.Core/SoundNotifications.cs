using System.Text.Json;

namespace Wisp.Core;

public enum SoundEvent
{
    VoiceJoined,
    VoiceLeft,
    ScreenShareStarted,
    MessageReceived,
    MicMuted,
    MicUnmuted,
    Deafened,
    Undeafened
}

public sealed record SoundNotificationPreferences(bool Enabled = true, float Volume = .8f)
{
    public static SoundNotificationPreferences Load(string root)
    {
        try
        {
            var path = Path.Combine(root, "notification-sounds.json");
            var value = File.Exists(path) ? JsonSerializer.Deserialize<SoundNotificationPreferences>(File.ReadAllText(path)) : null;
            return value is null ? new() : value with { Volume = float.IsFinite(value.Volume) ? Math.Clamp(value.Volume, 0, 1) : .8f };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "notification-sounds.json");
        var value = this with { Volume = float.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1) : .8f };
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value)); File.Move(path + ".tmp", path, true);
    }
}

/// <summary>
/// Semantic notification state, independent of views and render callbacks.
/// UI/network adapters confirm events here; only this policy emits notification cues.
/// </summary>
public sealed class SoundNotificationPolicy(string localUserId)
{
    private string? voiceRoom;
    private bool connected, muted, deafened;
    private HashSet<string> roster = new(StringComparer.Ordinal);
    private readonly HashSet<string> streams = new(StringComparer.Ordinal);
    private readonly Queue<string> streamOrder = new();
    private readonly Dictionary<string, MessageWindow> messageWindows = new(StringComparer.Ordinal);
    private readonly Queue<string> channelOrder = new();
    public event Action<SoundEvent>? Emitted;

    private void Emit(SoundEvent value, bool local = false)
    {
        if (local || !deafened) Emitted?.Invoke(value);
    }

    public void ConfirmVoiceConnected(string roomKey, IEnumerable<RtcSeat> participants, bool recovered = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomKey);
        if (connected && voiceRoom == roomKey) { ObserveVoiceRoster(roomKey, participants); return; }
        if (connected && voiceRoom != roomKey) ConfirmVoiceLeft();
        voiceRoom = roomKey; connected = true;
        roster = participants.Select(p => p.UserId).ToHashSet(StringComparer.Ordinal);
        // Existing peers are a baseline, not newly joined events. A recovered WSS
        // establishes another silent baseline instead of replaying join sounds.
        if (!recovered) Emit(SoundEvent.VoiceJoined, local: true);
    }

    public void ObserveVoiceRoster(string roomKey, IEnumerable<RtcSeat> participants)
    {
        if (!connected || roomKey != voiceRoom) return;
        var next = participants.Select(p => p.UserId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in next.Except(roster).Where(id => id != localUserId)) Emit(SoundEvent.VoiceJoined);
        foreach (var id in roster.Except(next).Where(id => id != localUserId)) Emit(SoundEvent.VoiceLeft);
        roster = next;
    }

    public void ConnectionLost() { connected = false; roster.Clear(); }

    public void ConfirmVoiceLeft()
    {
        bool hadRoom = voiceRoom is not null;
        voiceRoom = null; connected = false; roster.Clear(); streams.Clear(); streamOrder.Clear();
        if (hadRoom) Emit(SoundEvent.VoiceLeft, local: true);
    }

    public void ObserveLocalAudio(bool mute, bool deafen, bool notify = true)
    {
        var cue = deafen != deafened ? deafen ? SoundEvent.Deafened : SoundEvent.Undeafened
            : mute != muted ? mute ? SoundEvent.MicMuted : SoundEvent.MicUnmuted : (SoundEvent?)null;
        muted = mute; deafened = deafen;
        if (notify && cue is { } value) Emit(value, local: true);
    }

    public void ObserveStreamStarted(string roomKey, string senderId, string sessionId, bool restoring = false)
    {
        if (!connected || voiceRoom != roomKey || string.IsNullOrWhiteSpace(sessionId)) return;
        string key = roomKey + "\0" + senderId + "\0" + sessionId;
        if (!streams.Add(key)) return;
        streamOrder.Enqueue(key);
        while (streamOrder.Count > 256) streams.Remove(streamOrder.Dequeue());
        if (!restoring) Emit(SoundEvent.ScreenShareStarted, local: senderId == localUserId);
    }

    public void ObserveMessages(string channelKey, IEnumerable<ChatMessage> messages, bool history = false)
    {
        if (!messageWindows.TryGetValue(channelKey, out var window))
        {
            messageWindows[channelKey] = window = new(); channelOrder.Enqueue(channelKey); history = true;
            while (channelOrder.Count > 64) messageWindows.Remove(channelOrder.Dequeue());
        }
        foreach (var message in messages)
        {
            if (!window.Seen.Add(message.Id)) continue;
            window.Order.Enqueue(message.Id);
            while (window.Order.Count > 512) window.Seen.Remove(window.Order.Dequeue());
            if (!history && message.AuthorId != localUserId) Emit(SoundEvent.MessageReceived);
        }
    }

    private sealed class MessageWindow
    {
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        public readonly Queue<string> Order = new();
    }
}
