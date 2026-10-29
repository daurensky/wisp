using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class VoicePreferencesTests
{
    [Fact]
    public void IntentPersistsIndependentlyAndCorruptFilesRecover()
    {
        var root = Path.Combine(Path.GetTempPath(), "wisp-voice-intent-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(new VoicePreferences(), VoicePreferences.Load(root));
            foreach (var preference in new[] { new VoicePreferences(true, false), new VoicePreferences(false, true), new VoicePreferences(true, true) })
            {
                preference.Save(root);
                Assert.Equal(preference, VoicePreferences.Load(root));
            }
            File.WriteAllText(Path.Combine(root, "voice-preferences.json"), "broken");
            Assert.Equal(new VoicePreferences(), VoicePreferences.Load(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ViewerEventsAreSessionScopedDeduplicatedAndRecoveryIsSilent()
    {
        var presence = new StreamViewerPresence();
        presence.Start("a");
        Assert.Null(presence.Observe("old", "viewer", true));
        Assert.Equal(SoundEvent.VoiceJoined, presence.Observe("a", "viewer", true));
        Assert.Null(presence.Observe("a", "viewer", true));
        Assert.Equal(SoundEvent.VoiceLeft, presence.Observe("a", "viewer", false));
        Assert.Null(presence.Observe("a", "viewer", false));
        Assert.Null(presence.Observe("a", "viewer", true, restoring: true));
        presence.Retain(Array.Empty<string>());
        Assert.Null(presence.Observe("a", "viewer", false));
        presence.Stop();
        Assert.Null(presence.Observe("a", "viewer", true));
    }
}
