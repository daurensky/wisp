using System.Text.Json;

namespace Wisp.Core;

/// <summary>User intent survives room changes and process restarts; deafen also mutes capture.</summary>
public sealed record VoicePreferences(bool Muted = false, bool Deafened = false)
{
    public static VoicePreferences Load(string root)
    {
        try
        {
            var path = Path.Combine(root, "voice-preferences.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<VoicePreferences>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "voice-preferences.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, true);
    }
}
