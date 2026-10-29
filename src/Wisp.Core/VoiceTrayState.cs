namespace Wisp.Core;

public enum VoiceTrayState { Disconnected, Silent, Speaking, Muted, Deafened, Error }

/// <summary>Presentation only: never opens a device or changes a voice session.</summary>
public static class VoiceTrayStateResolver
{
    /// <param name="activeSessionFailed">A real failure of an intended voice session. Clear on successful reentry or voluntary leave.</param>
    public static VoiceTrayState Resolve(bool connected, bool activeSessionFailed, bool muted, bool deafened, bool speaking)
    {
        if (activeSessionFailed) return VoiceTrayState.Error;
        if (!connected) return VoiceTrayState.Disconnected;
        if (deafened) return VoiceTrayState.Deafened;
        if (muted) return VoiceTrayState.Muted;
        return speaking ? VoiceTrayState.Speaking : VoiceTrayState.Silent;
    }
}
