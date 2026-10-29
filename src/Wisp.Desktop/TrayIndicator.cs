using System.Drawing;
using System.Windows.Forms;
using Wisp.Core;
using DrawingIcon = System.Drawing.Icon;

namespace Wisp.Desktop;

/// <summary>UI-thread owner of six cached icons. No timer, rendering loop, audio device or sound playback.</summary>
public sealed class TrayIndicator : IDisposable
{
    private readonly NotifyIcon tray;
    private readonly Dictionary<VoiceTrayState, DrawingIcon> icons = new();
    private bool disposed;
    public VoiceTrayState State { get; private set; } = VoiceTrayState.Disconnected;

    public TrayIndicator(NotifyIcon tray)
    {
        this.tray = tray;
        try
        {
            foreach (var state in Enum.GetValues<VoiceTrayState>()) icons.Add(state, VoiceTrayIcons.Create(state));
            tray.Icon = icons[State];
        }
        catch { foreach (var icon in icons.Values) icon.Dispose(); throw; }
    }

    /// <returns>True only when NotifyIcon actually changes its image.</returns>
    public bool Update(bool connected, bool activeSessionFailed, bool muted, bool deafened, bool speaking)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var next = VoiceTrayStateResolver.Resolve(connected, activeSessionFailed, muted, deafened, speaking);
        if (next == State) return false;
        tray.Icon = icons[next]; State = next; return true;
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        // Detach the borrowed image before releasing all owned icon handles.
        try { if (tray.Icon is { } current && icons.Values.Contains(current)) tray.Icon = null; }
        catch (ObjectDisposedException) { }
        finally
        {
            foreach (var icon in icons.Values) icon.Dispose();
            icons.Clear();
        }
    }
}
