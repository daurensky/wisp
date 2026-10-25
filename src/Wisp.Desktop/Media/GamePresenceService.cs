using System.Diagnostics;

namespace Wisp.Desktop.Media;

/// <summary>Allowlisted process names only; call at most once every 15 seconds.</summary>
public static class GamePresenceService
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs2"] = "Counter-Strike 2", ["dota2"] = "Dota 2", ["valorant-win64-shipping"] = "VALORANT",
        ["minecraft.windows"] = "Minecraft", ["gta5"] = "Grand Theft Auto V", ["eldenring"] = "Elden Ring",
        ["cyberpunk2077"] = "Cyberpunk 2077", ["rocketleague"] = "Rocket League", ["overwatch"] = "Overwatch 2",
        ["fortniteclient-win64-shipping"] = "Fortnite", ["r5apex"] = "Apex Legends", ["terraria"] = "Terraria",
        ["stardew valley"] = "Stardew Valley", ["bg3"] = "Baldur's Gate 3", ["bg3_dx11"] = "Baldur's Gate 3"
    };

    public static string? DetectGame()
    {
        string? result = null;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (result is not null) continue;
                try { if (Known.TryGetValue(process.ProcessName, out var game)) result = game; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return result;
    }
}
