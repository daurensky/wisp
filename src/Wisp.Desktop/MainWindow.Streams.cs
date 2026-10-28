using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

public sealed partial class MainWindow
{
    // Audience snapshots use the existing authenticated event channel. Small pages keep
    // the original 512-byte command limit and are committed together at the receiver.
    private sealed record StreamStateMessage(string TargetId, string SessionId, bool Paused = false,
        bool? Viewing = null, bool Restoring = false, string[]? Viewers = null,
        long Revision = 0, int Page = 0, int Pages = 1);
    private sealed class AudienceSnapshot
    {
        public string SessionId = "";
        public long Revision;
        public int Pages;
        public readonly Dictionary<int, string[]> Pending = new();
        public readonly HashSet<string> Visible = new(StringComparer.Ordinal);
    }
    private readonly Dictionary<string, AudienceSnapshot> remoteAudiences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> previousStreamVolumes = new(StringComparer.Ordinal);
    private long audienceRevision;
    private bool streamPresentationHooks;
    private string audienceRosterKey = "";

    private void WatchStream(string peerId)
    {
        if (peerId == profile.Id)
        {
            if (!sharing || screen.CurrentInfo is null) return;
            CloseOtherCentralViewers(peerId);
            var local = CreateViewer(peerId);
            if (local.IsMini) local.ToggleMini();
            local.Show(); UpdateLocalPreview();
            return;
        }
        dismissedScreens.Remove(peerId);
        CloseOtherCentralViewers(peerId);
        if (viewers.TryGetValue(peerId, out var existing))
        {
            if (existing.IsMini) existing.ToggleMini();
            existing.Show();
        }
        else if (remoteStreams.ContainsKey(peerId)) OpenVideoPeer(peerId);
        else if (remoteScreens.TryGetValue(peerId, out var jpeg)) ShowFrame(peerId, jpeg);
    }

    private void CloseOtherCentralViewers(string peerId)
    {
        foreach (var other in viewers.Where(pair => pair.Key != peerId && !pair.Value.IsMini).ToArray())
        {
            if (other.Key == profile.Id && sharing) other.Value.ToggleMini();
            else other.Value.Close();
        }
    }

    private void UpdateLocalPreview()
    {
        screen.PreviewEnabled = sharing && IsVisible && WindowState != WindowState.Minimized &&
            viewers.TryGetValue(profile.Id, out var own) && own.IsVisible && own.Target.IsVisible;
    }

    private void ReconcileStreamViewers()
    {
        var connected = rtc?.ConnectedPeerIds ?? Array.Empty<string>();
        streamViewers.Retain(connected);
        foreach (var snapshot in remoteAudiences.Values)
            snapshot.Visible.RemoveWhere(id => id != profile.Id && !connected.Contains(id));
        string rosterKey = (sharing ? screen.CurrentInfo?.SessionId : "") + ":" + string.Join("|", connected.OrderBy(id => id, StringComparer.Ordinal));
        if (sharing && rosterKey != audienceRosterKey) BroadcastViewerSnapshot();
        audienceRosterKey = rosterKey;
        RefreshViewerAvatars();
    }

    private void RefreshViewerAvatars()
    {
        var people = VoicePresence;
        foreach (var pair in viewers)
        {
            var ids = pair.Key == profile.Id ? streamViewers.Viewers :
                remoteAudiences.TryGetValue(pair.Key, out var snapshot) ? snapshot.Visible.ToArray() : Array.Empty<string>();
            // A receiver knows its own explicit view intent even when a legacy host
            // cannot publish the new optional snapshot fields.
            var visibleIds = ids.ToHashSet(StringComparer.Ordinal);
            if (pair.Key != profile.Id && videoReceivers.ContainsKey(pair.Key)) visibleIds.Add(profile.Id);
            pair.Value.UpdateViewers(people.Where(person => visibleIds.Contains(person.Profile.Id)).ToArray(),
                person => avatars.Resolve(person, store), voice, profile.Id);
        }
    }

    private void BroadcastViewerSnapshot()
    {
        if (!sharing || screen.CurrentInfo is not { } info) return;
        var ids = streamViewers.Viewers;
        long revision = ++audienceRevision;
        // Authentication creates compact UUID ids; two ids per page also leaves room
        // for future UUID representations without changing the transport bound.
        const int pageSize = 2;
        int pages = Math.Max(1, (ids.Count + pageSize - 1) / pageSize);
        for (int page = 0; page < pages; page++)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new StreamStateMessage(profile.Id, info.SessionId,
                Viewers: ids.Skip(page * pageSize).Take(pageSize).ToArray(), Revision: revision, Page: page, Pages: pages));
            if (payload.Length <= 512) SendRealtime("screen-state", payload);
        }
    }

    private void HandleStreamState(WireMessage message)
    {
        if (voiceChannelId != message.ChannelId || message.Payload.Length > 512) return;
        var state = JsonSerializer.Deserialize<StreamStateMessage>(message.Payload);
        if (state is null) return;
        if (state.Viewing is { } viewing && state.TargetId == profile.Id && sharing &&
            state.SessionId == screen.CurrentInfo?.SessionId && rtc?.ConnectedPeerIds.Contains(message.SenderId) == true)
        {
            var cue = streamViewers.Observe(state.SessionId, message.SenderId, viewing, state.Restoring);
            if (cue is { } effect && !deafened) sounds.Play(effect);
            BroadcastViewerSnapshot(); RefreshViewerAvatars();
            return;
        }
        // Only the source may author its audience; another participant cannot submit
        // a list on behalf of a host or reuse a previous capture session.
        if (state.TargetId != message.SenderId || state.Viewing is not null ||
            rtc?.ConnectedPeerIds.Contains(message.SenderId) != true ||
            !remoteStreams.TryGetValue(message.SenderId, out var info) || state.SessionId != info.SessionId) return;
        if (state.Viewers is null && viewers.TryGetValue(message.SenderId, out var viewer)) viewer.SetPaused(state.Paused);
        if (state.Viewers is null) return;
        int maximumPages = (rtc?.ConnectedPeerIds.Count ?? 0) + 1;
        if (state.Revision < 1 || state.Pages < 1 || state.Pages > maximumPages || state.Page < 0 ||
            state.Page >= state.Pages || state.Viewers.Length > 2 ||
            state.Viewers.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128)) return;
        if (!remoteAudiences.TryGetValue(message.SenderId, out var audience) || audience.SessionId != state.SessionId)
            remoteAudiences[message.SenderId] = audience = new AudienceSnapshot { SessionId = state.SessionId };
        if (state.Revision < audience.Revision) return;
        if (state.Revision != audience.Revision)
        {
            audience.Revision = state.Revision; audience.Pages = state.Pages; audience.Pending.Clear();
        }
        if (audience.Pages != state.Pages) return;
        audience.Pending[state.Page] = state.Viewers;
        if (audience.Pending.Count != audience.Pages) return;
        audience.Visible.Clear();
        foreach (var id in audience.Pending.Values.SelectMany(ids => ids))
            if (id == profile.Id || rtc?.ConnectedPeerIds.Contains(id) == true) audience.Visible.Add(id);
        audience.Pending.Clear(); RefreshViewerAvatars();
    }

    // Optional fields on an existing event preserve compatibility with v2 clients:
    // older peers ignore states whose TargetId differs from the sender.
    private void AnnounceView(string peerId, bool viewing, bool restoring = false)
    {
        if (peerId == profile.Id || !remoteStreams.TryGetValue(peerId, out var info)) return;
        SendRealtime("screen-state", JsonSerializer.SerializeToUtf8Bytes(new ScreenCommand(peerId, info.SessionId, Viewing: viewing, Restoring: restoring)));
    }

    private void QueueVideoFrame(string peerId, ScreenStreamInfo info, byte[] pixels)
    {
        if (closing) return;
        videoFrames[peerId] = (info, pixels);
        if (Interlocked.Exchange(ref videoScheduled, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref videoScheduled, 0);
            foreach (var id in videoFrames.Keys)
                if (videoFrames.TryRemove(id, out var frame) && viewers.TryGetValue(id, out var view))
                    view.Present(frame.Pixels, frame.Info.Width, frame.Info.Height);
        }));
    }
    private async Task ShareScreen()
    {
        if(rtc?.Joined!=true||voiceIntent.Connecting)throw new InvalidOperationException("Для демонстрации сначала дождись подключения к голосовому каналу.");
        if (sharing) { StopSharing(); return; } if (voiceChannelId is null) throw new InvalidOperationException("Сначала подключись к голосовому каналу.");
        long shareRequest = Interlocked.Increment(ref shareGeneration);
        if (ScreenShareDialog.Show(this) is not { } selection) return;
        var selected = selection.Source;
        string? sharingChannel = voiceChannelId;
        bool withoutBorder = true;
        bool borderlessGranted = withoutBorder && await BorderlessCaptureAccess.RequestAsync();
        if (closing || sharingChannel is null || voiceChannelId != sharingChannel || shareRequest != Volatile.Read(ref shareGeneration)) return;
        var info = ScreenShareService.Describe(selected, selection.Height, selection.FramesPerSecond, selection.Bitrate) with { SessionId = Guid.NewGuid().ToString("N"), StartedAtUnixMs=(cloud?.ServerNow()??DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() };
        await SendPacket(new WireMessage("screen-start", profile.Id, voiceChannelId, JsonSerializer.SerializeToUtf8Bytes(info)));
        if (closing || voiceChannelId != sharingChannel || shareRequest != Volatile.Read(ref shareGeneration)) return;
        streamViewers.Start(info.SessionId);sharing=true;
        try{screen.Start(selected,info,withoutBorder,borderlessGranted);}catch{StopSharing();throw;}
        soundEvents.ObserveStreamStarted(VoiceRoomKey,profile.Id,info.SessionId);sharedPeers="";
        ((ToggleIconButton)shareButton).IsChecked = true;
        var preview = CreateViewer(profile.Id);
        preview.SetVideoAspect(info.Width, info.Height);
        if (!preview.IsMini) preview.ToggleMini();
        preview.Show(); UpdateLocalPreview(); BroadcastViewerSnapshot();
        if (ShareAudioCapture.Supported) { try { await shareAudio.StartAsync(selected); } catch (Exception ex) { Notify(ex.Message, true); } }
        if (closing || !sharing || voiceChannelId != sharingChannel || shareRequest != Volatile.Read(ref shareGeneration)) return;
        Notify($"Демонстрация начата: {selected.Name} · H.264 · {info.FramesPerSecond} FPS");UpdateStatus();
    }
    private void StopSharing()
    {
        Interlocked.Increment(ref shareGeneration);
        notice.Text="";notice.Visibility=Visibility.Collapsed;streamViewers.Stop();shareAudio.Stop();screen.Stop();sharedPeers="";if(viewers.Remove(profile.Id,out var localView))localView.Close();
        if (!sharing) return; sharing = false; ((ToggleIconButton)shareButton).IsChecked = false; SendRealtime("screen-stop", Array.Empty<byte>());  Notify("Демонстрация остановлена.");RenderChannels();
    }
    private ScreenViewer CreateViewer(string peerId)
    {
        if (viewers.TryGetValue(peerId, out var existing)) return existing;
        if (!streamPresentationHooks)
        {
            streamPresentationHooks = true;
            StateChanged += (_, _) => UpdateLocalPreview();
            IsVisibleChanged += (_, _) => UpdateLocalPreview();
        }
        foreach (var other in viewers.Values.Where(v => v.IsFullscreen).ToList()) other.ToggleFullscreen();
        var name = VoicePresence.FirstOrDefault(x => x.Profile.Id == peerId)?.Profile.Username ?? "Участник";
        if (peerId != profile.Id)
            foreach (var other in viewers.Where(pair => pair.Key != profile.Id && pair.Key != peerId).ToArray()) other.Value.Close();
        if (peerId != profile.Id) CloseOtherCentralViewers(peerId);
        var viewer = new ScreenViewer(this, $"{name} · Демонстрация", streamWorkspace, ownStream: peerId == profile.Id);
        viewers[peerId] = viewer; sharePlayback.SetMuted(peerId, false);
        viewer.SetVolume(previousStreamVolumes.GetValueOrDefault(peerId, 1));
        viewer.SetVolume(sharePlayback.GetVolume(peerId));
        viewer.VolumeChanged += value =>
        {
            if (value > 0) previousStreamVolumes[peerId] = value;
            sharePlayback.SetVolume(peerId, value);
        };
        if (peerId == profile.Id)
        {
            viewer.StopRequested += StopSharing;
            viewer.Target.IsVisibleChanged += (_, _) => UpdateLocalPreview();
        }
        viewer.PresentationChanged += () =>
        {
            if (!viewer.IsMini && !viewer.IsClosed) CloseOtherCentralViewers(peerId);
            if (peerId != profile.Id && viewer.IsMini && viewers.TryGetValue(profile.Id, out var own) && own.IsMini)
                viewer.DockMini(MiniDockPosition.TopRight, false);
            UpdateLocalPreview();
        };
        if (peerId == profile.Id && screen.CurrentInfo is { } localInfo) viewer.SetVideoAspect(localInfo.Width, localInfo.Height);
        else if (remoteStreams.TryGetValue(peerId, out var streamInfo)) viewer.SetVideoAspect(streamInfo.Width, streamInfo.Height);
        viewer.Closed += () =>
        {
            if(peerId==profile.Id)screen.PreviewEnabled=false;
            AnnounceView(peerId,false);viewers.Remove(peerId); dismissedScreens.Add(peerId);
            if (videoReceivers.TryRemove(peerId, out var receiver)) receiver.Dispose();
            videoFrames.TryRemove(peerId, out _); sharePlayback.SetMuted(peerId, true);
        };
        RefreshViewerAvatars();
        return viewer;
    }
    private void OpenVideoPeer(string peerId)
    {
        if (dismissedScreens.Contains(peerId) || !remoteStreams.TryGetValue(peerId, out var info) || videoReceivers.ContainsKey(peerId)) return;
        CreateViewer(peerId);
        var receiver = new ScreenVideoReceiver(info, pixels =>
        {
            if (closing || !videoReceivers.TryGetValue(peerId, out var current) || current.Info != info) return;
            QueueVideoFrame(peerId,info,pixels);
        }, error => Dispatcher.BeginInvoke(() => { if (!closing) Notify(error, true); }),
            () => SendRealtime("screen-keyframe", JsonSerializer.SerializeToUtf8Bytes(new ScreenCommand(peerId, info.SessionId))));
        if (!videoReceivers.TryAdd(peerId, receiver)) receiver.Dispose();else { AnnounceView(peerId,true); RefreshViewerAvatars(); }
    }
    private void StopVideoPeer(string peerId,bool ended=false)
    {
        if (videoReceivers.TryRemove(peerId, out var receiver)) receiver.Dispose(); videoFrames.TryRemove(peerId, out _);
        if(viewers.Remove(peerId,out var viewer)) viewer.Close();
        sharePlayback.Remove(peerId); remoteStreams.Remove(peerId); remoteAudiences.Remove(peerId); dismissedScreens.Remove(peerId);
    }
    private void ShowFrame(string peerId, byte[] jpeg)
    {
        if (jpeg.Length > 2 * 1024 * 1024) return; remoteScreens[peerId] = jpeg; if (dismissedScreens.Contains(peerId)) return;
        try { using var stream = new MemoryStream(jpeg); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 1920; image.StreamSource = stream; image.EndInit(); image.Freeze(); CreateViewer(peerId).PresentImage(image); } catch { }
    }
    private void WatchScreens()
    {
        var d = new FormDialog(this, "Демонстрации участников"); var ids = remoteStreams.Keys.Concat(remoteScreens.Keys).Concat(sharing?new[]{profile.Id}:Array.Empty<string>()).Distinct().ToList();
        if (ids.Count == 0) d.Note("Сейчас никто не демонстрирует экран.");
        foreach(var id in ids)d.Action(id==profile.Id?"Моя демонстрация":VoicePresence.FirstOrDefault(p=>p.Profile.Id==id)?.Profile.Username??"Участник",()=>{d.Close();WatchStream(id);});
        d.Accept("Закрыть"); d.ShowDialog();
    }
}
