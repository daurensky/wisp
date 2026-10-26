using Concentus.Enums;
using Wisp.Core;
using Concentus;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Wisp.Desktop.Media;

/// <summary>One 20ms Opus stream per participant; one mixed output device.</summary>
public sealed class VoiceService : IDisposable
{
    private const int SampleRate = 48000;
    private const int FrameSamples = 960;
    private readonly object _gate = new();
    private readonly Dictionary<string, PlaybackPeer> _peers = new(StringComparer.Ordinal);
    private readonly short[] _captureFrame = new short[FrameSamples];
    private WaveInEvent? _input;
    private WaveOutEvent? _output;
    private MixingSampleProvider? _mixer;
    private IOpusEncoder? _encoder;
    private int _captureCount;
    private volatile bool _muted;
    private volatile bool _deafened;

    public event Action<byte[]>? PacketReady;
    public event Action<string>? Error;
    public bool Muted { get => _muted; set => _muted = value; }
    public bool Deafened
    {
        get => _deafened;
        set
        {
            _deafened = value;
            lock (_gate)
                foreach (var peer in _peers.Values) peer.Buffer.ClearBuffer();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_input is not null) return;
            try
            {
                _encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
                _encoder.Bitrate = 32000;
                _encoder.Complexity = 5;
                _encoder.UseVBR = true;
                _encoder.UseDTX = true;
                _encoder.UseInbandFEC = true;
                _encoder.PacketLossPercent = 5;
                _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)) { ReadFully = true };
                _output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 3 };
                _output.Init(new LimitedSampleProvider(_mixer));
                _output.Play();
                _input = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(SampleRate, 16, 1),
                    BufferMilliseconds = 20,
                    NumberOfBuffers = 3
                };
                _input.DataAvailable += Capture;
                _input.RecordingStopped += (_, e) => { if (e.Exception is not null) Error?.Invoke(e.Exception.Message); };
                _captureCount = 0;
                _input.StartRecording();
            }
            catch
            {
                Stop();
                throw;
            }
        }
    }

    private void Capture(object? sender, WaveInEventArgs e)
    {
        try
        {
            lock (_gate)
            {
            if (_muted || _deafened) { _captureCount = 0; return; }
            // WaveIn normally supplies exactly 20ms, but aggregate partial device buffers too.
            for (var offset = 0; offset + 1 < e.BytesRecorded; offset += 2)
            {
                _captureFrame[_captureCount++] = (short)(e.Buffer[offset] | e.Buffer[offset + 1] << 8);
                if (_captureCount != FrameSamples) continue;
                _captureCount = 0;
                var packet = new byte[1275];
                var encoder = _encoder;
                if (encoder is null) return;
                var length = encoder.Encode(_captureFrame.AsSpan(), FrameSamples, packet.AsSpan(), packet.Length);
                if (length > 0) PacketReady?.Invoke(packet.AsSpan(0, length).ToArray());
            }
            }
        }
        catch (Exception ex) { Error?.Invoke("Microphone: " + ex.Message); }
    }

    public void Receive(string peerId, byte[] packet)
    {
        if (_deafened || packet.Length is 0 or > 1275) return;
        lock (_gate)
        {
            if (_mixer is null) return;
            if (!_peers.TryGetValue(peerId, out var peer))
            {
                if (_peers.Count >= 4) return;
                peer = new PlaybackPeer();
                _peers.Add(peerId, peer);
                _mixer.AddMixerInput(peer.SampleProvider);
            }
            try
            {
                if (!peer.Decoder.TryDecode(packet, peer.Samples, out var samples)) return;
                // Discard stale audio rather than allowing latency to grow indefinitely.
                if (peer.Buffer.BufferedDuration.TotalMilliseconds > 120) peer.Buffer.ClearBuffer();
                Buffer.BlockCopy(peer.Samples, 0, peer.Bytes, 0, samples * 2);
                peer.Buffer.AddSamples(peer.Bytes, 0, samples * 2);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { peer.Buffer.ClearBuffer(); }
        }
    }

    /// <summary>Release decoder and mixer inputs for participants who left this channel.</summary>
    public void SynchronizePeers(IEnumerable<string> activePeerIds)
    {
        var active = activePeerIds.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var id in _peers.Keys.Where(id => !active.Contains(id)).ToArray())
            {
                var peer = _peers[id];
                _mixer?.RemoveMixerInput(peer.SampleProvider);
                peer.Buffer.ClearBuffer();
                peer.Decoder.Dispose();
                _peers.Remove(id);
            }
        }
    }

    public void Stop()
    {
        WaveInEvent? input;
        WaveOutEvent? output;
        lock (_gate)
        {
            input = _input;
            output = _output;
            _input = null;
            _output = null;
            _mixer = null;
            (_encoder as IDisposable)?.Dispose(); _encoder = null;
            foreach (var peer in _peers.Values) peer.Decoder.Dispose();
            _peers.Clear();
        }
        if (input is not null)
        {
            input.DataAvailable -= Capture;
            input.StopRecording();
            input.Dispose();
        }
        output?.Stop();
        output?.Dispose();
    }

    public void Dispose() => Stop();

    private sealed class PlaybackPeer
    {
        public readonly OpusFrameDecoder Decoder = new();
        public readonly short[] Samples = new short[FrameSamples];
        public readonly byte[] Bytes = new byte[FrameSamples * 2];
        public readonly BufferedWaveProvider Buffer = new(new WaveFormat(SampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(240), DiscardOnBufferOverflow = true, ReadFully = true
        };
        public ISampleProvider SampleProvider { get; }
        public PlaybackPeer() => SampleProvider = Buffer.ToSampleProvider();
    }

    private sealed class LimitedSampleProvider(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            for (var i = offset; i < offset + read; i++) buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
            return read;
        }
    }
}
