using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wisp.Core;
namespace Wisp.Desktop.Media;

public sealed class ShareAudioPlayback : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Peer> peers = new();
    private readonly HashSet<string> muted = new();
    private SharedAudioOutput? output;
    private string? outputId;
    private MixingSampleProvider? mixer;
    private bool deafened, failed;
    public event Action<string>? Error;
    public bool Deafened { get => deafened; set { lock (gate) { deafened = value; foreach (var p in peers.Values) p.Buffer.ClearBuffer(); } } }
    public void SetMuted(string id, bool value) { lock (gate) { if (value) muted.Add(id); else muted.Remove(id); if (peers.TryGetValue(id, out var peer)) peer.Buffer.ClearBuffer(); } }
    public void Receive(string id, byte[] packet)
    {
        lock (gate)
        {
            if (deafened || muted.Contains(id) || failed) return;
            try
            {
                if (mixer is null) { mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ReadFully = true }; output = new SharedAudioOutput(outputId,100); output.Init(new Limiter(mixer)); output.Play(); }
                if (!peers.TryGetValue(id, out var peer)) {  peer = new Peer(); peers.Add(id, peer); mixer.AddMixerInput(peer.Samples); }
                if (!peer.Decoder.TryDecode(packet, peer.Pcm, out int samples)) return;
                if (peer.Buffer.BufferedDuration.TotalMilliseconds > 150) peer.Buffer.ClearBuffer();
                Buffer.BlockCopy(peer.Pcm, 0, peer.Bytes, 0, samples * 4); peer.Buffer.AddSamples(peer.Bytes, 0, samples * 4);
            }
            catch (Exception ex) { output?.Dispose();output=null;failed = true; Error?.Invoke("Не удалось воспроизвести звук демонстрации: " + ex.Message); }
        }
    }
    public void ConfigureOutput(string? id)
    {
        lock(gate)
        {
            if(outputId==id)return;outputId=id;
            output?.Dispose();output=null;failed=false;
            if(mixer is null)return;
            try{output=new SharedAudioOutput(outputId,100);output.Init(new Limiter(mixer));output.Play();}
            catch(Exception ex){output?.Dispose();output=null;failed=true;Error?.Invoke("Вывод звука демонстрации недоступен: "+ex.Message);}
        }
    }
    public void Remove(string id) { lock (gate) { if (peers.Remove(id, out var p)) { mixer?.RemoveMixerInput(p.Samples); p.Decoder.Dispose(); } muted.Remove(id); } }
    public void Stop() { lock (gate) { output?.Stop(); output?.Dispose(); output = null; mixer = null; foreach (var p in peers.Values) p.Decoder.Dispose(); peers.Clear(); muted.Clear(); failed = false; } }
    public void Dispose() => Stop();
    private sealed class Peer
    {
        public readonly OpusFrameDecoder Decoder = new(2); public readonly short[] Pcm = new short[1920]; public readonly byte[] Bytes = new byte[3840];
        public readonly BufferedWaveProvider Buffer = new(new WaveFormat(48000, 16, 2)) { BufferDuration = TimeSpan.FromMilliseconds(250), DiscardOnBufferOverflow = true };
        public ISampleProvider Samples { get; } public Peer() => Samples = Buffer.ToSampleProvider();
    }
    private sealed class Limiter(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] samples, int offset, int count) { int read = source.Read(samples, offset, count); for (int i = offset; i < offset + read; i++) samples[i] = Math.Clamp(samples[i], -1f, 1f); return read; }
    }
}
