using System.Media;
using NAudio.Wave;

namespace Wisp.Desktop.Media;

/// <summary>Small synthesized action cues plus at most one soundboard/file player.</summary>
public sealed class SoundService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _cues = new(StringComparer.OrdinalIgnoreCase);
    private SoundPlayer? _cue;
    private MemoryStream? _cueStream;
    private WaveOutEvent? _fileOutput;
    private AudioFileReader? _fileReader;

    public SoundService()
    {
        _cues["connect"] = Tone(523, 784);
        _cues["disconnect"] = Tone(523, 349);
        _cues["mute"] = Tone(440, 330);
        _cues["unmute"] = Tone(440, 660);
        _cues["deafen"] = Tone(392, 262);
        _cues["undeafen"] = Tone(392, 587);
        _cues["share"] = Tone(659, 880);
        _cues["message"] = Tone(880, 1047, 45);
        _cues["error"] = Tone(220, 196, 60, 1300);
    }

    public void Play(string cue)
    {
        lock (_gate)
        {
            if (!_cues.TryGetValue(cue, out var data)) return;
            _cue?.Stop(); _cue?.Dispose(); _cueStream?.Dispose();
            _cueStream = new MemoryStream(data, writable: false);
            _cue = new SoundPlayer(_cueStream);
            _cue.Play();
        }
    }

    public void PlayFile(string path)
    {
        lock (_gate)
        {
            StopFile();
            try
            {
                _fileReader = new AudioFileReader(path);
                _fileOutput = new WaveOutEvent { DesiredLatency = 100 };
                _fileOutput.Init(_fileReader);
                _fileOutput.Play();
            }
            catch { StopFile(); throw; }
        }
    }

    public void StopFile()
    {
        lock (_gate)
        {
            _fileOutput?.Stop(); _fileOutput?.Dispose(); _fileOutput = null;
            _fileReader?.Dispose(); _fileReader = null;
        }
    }

    private static byte[] Tone(double first, double second, int milliseconds = 85, int amplitude = 4500)
    {
        const int rate = 24000;
        var count = rate * milliseconds / 1000;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + count * 4); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
            writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(count * 4);
            foreach (var frequency in new[] { first, second })
                for (var i = 0; i < count; i++)
                {
                    var envelope = Math.Min(1d, i / 100d) * Math.Min(1d, (count - i) / 240d);
                    writer.Write((short)(Math.Sin(2 * Math.PI * frequency * i / rate) * envelope * amplitude));
                }
        }
        return stream.ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cue?.Stop(); _cue?.Dispose(); _cue = null;
            _cueStream?.Dispose(); _cueStream = null;
            StopFile();
        }
    }
}
