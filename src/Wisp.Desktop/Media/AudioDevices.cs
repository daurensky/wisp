using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
namespace Wisp.Desktop.Media;

public sealed record AudioDeviceChoice(string? Id, string Name);
public sealed record AudioDeviceSettings(string? InputId = null, string? OutputId = null)
{
    public static AudioDeviceSettings Load(string root)
    {
        var path = Path.Combine(root, "audio.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AudioDeviceSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }
    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "audio.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, true);
    }
    public static IReadOnlyList<AudioDeviceChoice> List(bool input)
    {
        using var catalog = new MMDeviceEnumerator();
        var list = new List<AudioDeviceChoice> { new(null, "По умолчанию · устройство связи Windows") };
        foreach (var device in catalog.EnumerateAudioEndPoints(input ? DataFlow.Capture : DataFlow.Render, DeviceState.Active))
            using (device) list.Add(new(device.ID, device.FriendlyName));
        return list;
    }
    internal static MMDevice Resolve(MMDeviceEnumerator catalog, string? selected, DataFlow flow, Action<string> warning)
    {
        if (selected is not null)
        {
            try
            {
                var device = catalog.GetDevice(selected);
                if (device.State == DeviceState.Active) return device;
                device.Dispose();
            }
            catch (System.Runtime.InteropServices.COMException) { }
            warning("Выбранное аудиоустройство отключено; временно используется устройство связи по умолчанию.");
        }
        return catalog.GetDefaultAudioEndpoint(flow, Role.Communications);
    }
}

internal sealed class AudioDeviceWatcher(Action changed) : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator catalog = new();
    public void Start() => System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(catalog.RegisterEndpointNotificationCallback(this));
    public void OnDeviceStateChanged(string id, DeviceState state) => changed();
    public void OnDeviceAdded(string id) => changed();
    public void OnDeviceRemoved(string id) => changed();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) { if (role == Role.Communications) changed(); }
    public void OnPropertyValueChanged(string id, PropertyKey key) { }
    public void Dispose() { catalog.UnregisterEndpointNotificationCallback(this); catalog.Dispose(); }
}
