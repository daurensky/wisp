using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

public sealed partial class MainWindow
{
    private bool shutdownComplete;

    private async Task JoinVoice(Channel channel, bool automatic = false, CancellationToken cancellation = default)
    {
        if (closing || !automatic && voiceChannelId == channel.Id) return;
        if (!automatic) CancelReconnect();
        EnsureNoRecording();
        var joiningCommunity = automatic ? voiceCommunity : community;
        if (joiningCommunity is null) return;
        if (!automatic)
        {
            activeVoiceFailed = false;
            voiceWasConnected = false;
            soundEvents.ConfirmVoiceLeft();
        }
        StopVoice();
        voiceCommunity = joiningCommunity;
        long request = voiceIntent.Begin(channel.Id);
        voiceChannelId = channel.Id;
        cloudChannelId = channel.Id;
        RenderChannels();
        UpdateStatus();

        await Dispatcher.Yield(DispatcherPriority.Render);
        await voiceTransitions.WaitAsync(cancellation);
        bool refreshCloud = false;
        try
        {
            if (!voiceIntent.IsCurrent(request)) return;
            await voiceStopping;
            if (!voiceIntent.IsCurrent(request)) return;
            if (rtc is null || cloud is null) throw new InvalidOperationException("Нужно подключение к серверу Wisp.");
            await rtc.EnterAsync(joiningCommunity.Id, channel.Id, muted, deafened, cancellation);
            if (!voiceIntent.IsCurrent(request)) return;
            await rtc.SetMutedAsync(muted, deafened);
            if (!voiceIntent.IsCurrent(request)) return;
            voice.Muted = muted;
            voice.Deafened = deafened;
            voice.Start();
            voiceIntent.Complete(request);
            voiceWasConnected = true;
            activeVoiceFailed = false;
            voiceJoinedAt = cloud.ServerNow().ToUnixTimeMilliseconds();
            soundEvents.ConfirmVoiceConnected(VoiceRoomKey, rtc.Participants, automatic);
            RenderChannels();
            UpdateStatus();
            try
            {
                await cloud.PresenceAsync(joiningCommunity.Id, profile, channel.Id);
                refreshCloud = voiceIntent.IsCurrent(request);
            }
            catch (Exception error) { ReportVoicePresenceFailure(request, error); }
        }
        catch
        {
            if (voiceIntent.IsCurrent(request))
            {
                cloudChannelId = null;
                StopVoice();
                await voiceStopping;
                if (rtc is not null) await rtc.LeaveAsync();
                RenderChannels();
                UpdateStatus();
            }
            throw;
        }
        finally { voiceTransitions.Release(); }

        // Refresh can revoke membership and leave voice, which uses the same transition gate.
        if (refreshCloud && voiceIntent.IsCurrent(request))
        {
            try { await RefreshCloud(); }
            catch (Exception error) { ReportVoicePresenceFailure(request, error); }
        }
        if (voiceIntent.IsCurrent(request)) Notify("Подключён к серверу. Голос и демонстрация передаются через SFU.");
    }

    private void ReportVoicePresenceFailure(long request, Exception error)
    {
        DiagnosticLog.Write("voice-presence", error);
        if (voiceIntent.IsCurrent(request)) Notify("Голос подключён, но не удалось обновить список участников или чат: " + error.Message, true);
    }

    private Task LeaveVoice() => LeaveVoiceCore(publishPresence: true);

    private async Task LeaveVoiceCore(bool publishPresence)
    {
        if (closing) { await voiceStopping; return; }
        var notifications = remoteStreams.Where(pair => viewers.ContainsKey(pair.Key))
            .Select(pair => new WireMessage("screen-state", profile.Id, voiceChannelId,
                JsonSerializer.SerializeToUtf8Bytes(new ScreenCommand(pair.Key, pair.Value.SessionId, Viewing: false))))
            .ToArray();
        CancelReconnect();
        var leavingCommunity = voiceCommunity;
        activeVoiceFailed = false;
        voiceWasConnected = false;
        soundEvents.ConfirmVoiceLeft();
        cloudChannelId = null;
        StopVoice();
        var stopping = voiceStopping;
        voiceCommunity = null;
        RenderChannels();
        UpdateStatus();
        await voiceTransitions.WaitAsync();
        try
        {
            await stopping;
            foreach (var notification in notifications)
                await CleanupStepAsync("voice-viewer-leave", () => SendPacket(notification));
            if (rtc is not null) await rtc.LeaveAsync();
            if (publishPresence && cloud is not null && leavingCommunity is not null)
                await cloud.PresenceAsync(leavingCommunity.Id, profile, null);
        }
        finally { voiceTransitions.Release(); }
    }

    private void CancelReconnect() => Interlocked.Exchange(ref reconnect, null)?.Cancel();

    private void BeginReconnect(string communityId, Channel target)
    {
        CancelReconnect();
        var lifetime = new CancellationTokenSource();
        reconnect = lifetime;
        Run(async () =>
        {
            try
            {
                foreach (int delay in new[] { 1, 2, 4, 8, 15 })
                {
                    if (closing || voiceCommunity?.Id != communityId || lifetime.IsCancellationRequested) return;
                    voiceIntent.Begin(target.Id);
                    voiceChannelId = target.Id;
                    RenderChannels();
                    UpdateStatus();
                    await Task.Delay(TimeSpan.FromSeconds(delay), lifetime.Token);
                    try { await JoinVoice(target, true, lifetime.Token); return; }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                    catch (Exception error) { DiagnosticLog.Write("voice-reconnect", error); }
                }
                Notify("Не удалось восстановить голос после пяти попыток. Проверь сервер и подключись к каналу вручную.", true);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(reconnect, lifetime))
                {
                    reconnect = null;
                    if (rtc?.Joined != true) { cloudChannelId = null; StopVoice(); RenderChannels(); UpdateStatus(); }
                }
                lifetime.Dispose();
            }
        });
    }

    public void Quit() { exitRequested = true; Close(); }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        foreach (var viewer in viewers.Values) viewer.Show();
    }

    private async Task SignOut()
    {
        SessionVault.Clear(store.RootDirectory);
        try { await Disconnect(); if (cloud is not null) await cloud.LogoutAsync(); }
        finally { exitRequested = true; Close(); }
    }

    private void StopVoice()
    {
        if (recorder.IsRecording && recorder.UsesSharedSource) Run(RecordVoice);
        voiceIntent.Leave();
        voiceStopping = voice.StopAsync();
        CleanupStep("voice-soundboard-close", () => soundboardPopup?.Close());
        CleanupStep("voice-sharing-stop", StopSharing);
        CleanupStep("voice-share-playback-stop", sharePlayback.Stop);
        foreach (var id in remoteStreams.Keys.ToArray()) CleanupStep("voice-video-stop", () => StopVideoPeer(id));
        videoFrames.Clear();
        voiceChannelId = null;
        foreach (var viewer in viewers.Values.ToArray()) CleanupStep("voice-viewer-close", viewer.Close);
        viewers.Clear();
        remoteScreens.Clear();
        dismissedScreens.Clear();
        UpdateStatus();
    }

    private async Task Disconnect()
    {
        await LeaveVoice();
        cloudPresence = Array.Empty<PeerPresence>();
        Interlocked.Increment(ref sessionEpoch);
        latestFrames.Clear();
        presence = Array.Empty<PeerPresence>();
        RenderMembers();
        RenderChannels();
        UpdateStatus();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) { e.Cancel = !shutdownComplete; return; }
        if (!exitRequested)
        {
            e.Cancel = true;
            foreach (var viewer in viewers.Values) viewer.Hide();
            Hide();
            return;
        }
        e.Cancel = true;
        closing = true;
        try { await ShutdownAsync(); }
        catch (Exception error) { DiagnosticLog.Write("shutdown", error); }
        finally
        {
            shutdownComplete = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private async Task ShutdownAsync()
    {
        var presenceCommunities = new[] { community?.Id, voiceCommunity?.Id }.OfType<string>().Distinct().ToArray();
        CleanupStep("shutdown-reconnect", CancelReconnect);
        CleanupStep("shutdown-activity-timer", activityTimer.Stop);
        CleanupStep("shutdown-cloud-timer", cloudTimer.Stop);
        CleanupStep("shutdown-incoming-unsubscribe", () => { if (rtc is not null) rtc.MessageReceived -= EnqueueIncoming; });
        CleanupStep("shutdown-incoming-cancel", receiveLifetime.Cancel);
        incoming.Writer.TryComplete();
        latestFrames.Clear();
        CleanupStep("shutdown-voice-stop", StopVoice);

        await CleanupStepAsync("shutdown-recording-stop", async () =>
        {
            if (recorder.IsRecording) File.Delete(await recorder.StopAsync());
        });
        CleanupStep("shutdown-recording-file", () => { if (recordedPath is not null) File.Delete(recordedPath); recordedPath = null; });
        CleanupStep("shutdown-recorder", recorder.Dispose);
        CleanupStep("shutdown-recording-waveform", recordingWaveform.Dispose);
        await CleanupStepAsync("shutdown-voice-stop-completion", () => voiceStopping);
        await CleanupStepAsync("shutdown-incoming-completion", () => incomingTask);
        while (incoming.Reader.TryRead(out _)) { }
        Interlocked.Exchange(ref queuedBytes, 0);
        CleanupStep("shutdown-incoming-lifetime", receiveLifetime.Dispose);

        await CleanupStepAsync("shutdown-rtc", async () =>
        {
            await voiceTransitions.WaitAsync();
            try { if (rtc is not null) await rtc.DisposeAsync(); }
            finally { voiceTransitions.Release(); }
        });
        if (cloud is not null)
        {
            using var presenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            foreach (var id in presenceCommunities)
                await CleanupStepAsync("shutdown-cloud-presence", () => cloud.DeleteAsync($"communities/{id}/presence/{profile.Id}", presenceTimeout.Token));
        }

        CleanupStep("shutdown-tray-hide", () => tray.Visible = false);
        CleanupStep("shutdown-tray-indicator", () => trayIndicator?.Dispose());
        CleanupStep("shutdown-tray", tray.Dispose);
        CleanupStep("shutdown-message-playback", VoiceMessagePlayer.StopAll);
        await CleanupStepAsync("shutdown-voice", async () => await voice.DisposeAsync());
        await CleanupStepAsync("shutdown-screen", async () => await screen.DisposeAsync());
        await CleanupStepAsync("shutdown-video-receivers", DrainVideoReceiversAsync);
        CleanupStep("shutdown-sounds", sounds.Dispose);
        CleanupStep("shutdown-share-audio", shareAudio.Dispose);
        CleanupStep("shutdown-share-playback", sharePlayback.Dispose);
    }

    private static void CleanupStep(string source, Action action)
    {
        try { action(); }
        catch (Exception error) { DiagnosticLog.Write(source, error); }
    }

    private static async Task CleanupStepAsync(string source, Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { DiagnosticLog.Write(source, error); }
    }
}
