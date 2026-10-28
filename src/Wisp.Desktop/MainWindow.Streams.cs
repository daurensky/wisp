using System.Text.Json;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

public sealed partial class MainWindow
{
    private void WatchStream(string peerId)
    {
        if (peerId == profile.Id)
        {
            if (!sharing || screen.CurrentInfo is null) return;
            var local = CreateViewer(peerId);
            if (local.IsMini) local.ToggleMini();
            local.Show(); screen.PreviewEnabled = true;
            return;
        }
        dismissedScreens.Remove(peerId);
        if (viewers.TryGetValue(peerId, out var existing))
        {
            if (existing.IsMini) existing.ToggleMini();
            existing.Show();
        }
        else if (remoteStreams.ContainsKey(peerId)) OpenVideoPeer(peerId);
        else if (remoteScreens.TryGetValue(peerId, out var jpeg)) ShowFrame(peerId, jpeg);
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
}
