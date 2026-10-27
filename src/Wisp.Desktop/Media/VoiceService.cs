using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wisp.Core;
namespace Wisp.Desktop.Media;

/// <summary>Independent WASAPI microphone, four Opus decoders and one mixed communications output.</summary>
public sealed class VoiceService : IDisposable
{
    private const int FrameSamples = 960;
    private readonly object gate = new();
    private readonly SemaphoreSlim deviceSwitch = new(1);
    private readonly Dictionary<string, PlaybackPeer> peers = new(StringComparer.Ordinal);
    private readonly short[] captureFrame = new short[FrameSamples];
    private IWaveIn? input;
    private WasapiOut? output;
    private MMDevice? inputDevice, outputDevice;
    private MixingSampleProvider? mixer;
    private IOpusEncoder? encoder;
    private AudioDeviceWatcher? watcher;
    private CancellationTokenSource? debounce;
    private int captureCount;
    private volatile bool muted, deafened, running;
    private volatile bool noiseSuppression = true, noiseSuppressionAvailable;
    private bool processingInitialized, processingWarningShown;
    private RnNoiseProcessor? denoiser;
    private MicProcessingChain? micProcessing;
    public bool NoiseSuppressionAvailable => noiseSuppressionAvailable;
    public event Action? NoiseSuppressionAvailabilityChanged;
    public bool NoiseSuppression
    {
        get => noiseSuppression;
        set
        {
            noiseSuppression = value;
            var processor = denoiser;
            if (processor is not null) processor.Enabled = value && noiseSuppressionAvailable;
        }
    }
    public AudioDeviceSettings Devices { get; private set; } = new();
    public event Action<byte[]>? PacketReady;
    public event Action<string>? Error;
    public bool Muted { get => muted; set => muted = value; }
    public bool Deafened
    {
        get => deafened;
        set { deafened = value; lock (gate) foreach (var peer in peers.Values) peer.Buffer.Clear(); }
    }
    public void InitializeMicrophoneProcessing(bool enabled = true)
    {
        lock (gate)
        {
            NoiseSuppression = enabled;
            if (processingInitialized) return;
            processingInitialized = true;
            try
            {
                denoiser = new RnNoiseProcessor { Enabled = enabled };
                micProcessing = new MicProcessingChain(denoiser);
                micProcessing.ProcessorFailed += (_, _) => MicrophoneProcessingFailed();
                noiseSuppressionAvailable = true;
            }
            catch (Exception)
            {
                denoiser?.Dispose(); denoiser = null;
                micProcessing = new MicProcessingChain();
                MicrophoneProcessingFailed();
            }
        }
    }
    private void MicrophoneProcessingFailed()
    {
        noiseSuppressionAvailable = false;
        if (denoiser is not null) denoiser.Enabled = false;
        NoiseSuppressionAvailabilityChanged?.Invoke();
        if (processingWarningShown) return;
        processingWarningShown = true;
        Error?.Invoke("Шумоподавление недоступно. Микрофон продолжает работать без обработки. Попробуй заново распаковать полную Windows x64 сборку Wisp.");
    }
    public void Start()
    {
        lock (gate)
        {
            if (running) return;
            if (!processingInitialized) InitializeMicrophoneProcessing(NoiseSuppression);
            denoiser?.RequestReset();
            encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = 32000; encoder.Complexity = 5; encoder.UseVBR = true;
            encoder.UseDTX = true; encoder.UseInbandFEC = true; encoder.PacketLossPercent = 5;
            mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)) { ReadFully = true };
            running = true;
        }
        try { watcher = new AudioDeviceWatcher(ScheduleDeviceRefresh); watcher.Start(); }
        catch (Exception ex) { Error?.Invoke("Наблюдение за аудиоустройствами: " + ex.Message); }
        _ = RefreshDevicesAsync();
    }
    public async Task ConfigureDevicesAsync(AudioDeviceSettings settings)
    {
        Devices = settings;
        if (running) await RefreshDevicesAsync().ConfigureAwait(false);
    }
    private void ScheduleDeviceRefresh()
    {
        if (!running) return;
        var next = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref debounce, next);
        try { old?.Cancel(); } catch (ObjectDisposedException) { }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(700, next.Token); if (running) await RefreshDevicesAsync(); }
            catch (OperationCanceledException) { }
            finally { Interlocked.CompareExchange(ref debounce, null, next); next.Dispose(); }
        });
    }
    private async Task RefreshDevicesAsync()
    {
        await deviceSwitch.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (!running) return;
                CloseDevices();
                using var catalog = new MMDeviceEnumerator();
                try
                {
                    outputDevice = AudioDeviceSettings.Resolve(catalog, Devices.OutputId, DataFlow.Render, text => Error?.Invoke(text));
                    output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 60);
                    output.PlaybackStopped += (_, args) => { if (running && args.Exception is not null) Error?.Invoke("Наушники отключены: " + args.Exception.Message); };
                    output.Init(new SampleToWaveProvider(new LimitedSampleProvider(mixer!)));
                    output.Play();
                }
                catch (Exception ex) { output?.Dispose(); output = null; Error?.Invoke("Вывод звука недоступен: " + ex.Message); }
                try
                {
                    inputDevice = AudioDeviceSettings.Resolve(catalog, Devices.InputId, DataFlow.Capture, text => Error?.Invoke(text));
                    var next = new CommunicationsCapture(inputDevice.ID, text => Error?.Invoke(text));
                    lock (gate) { input = next; captureCount = 0; }
                    next.DataAvailable += Capture;
                    next.RecordingStopped += (_, args) =>
                    {
                        if (running && ReferenceEquals(input, next) && args.Exception is not null)
                            Error?.Invoke("Микрофон недоступен: " + args.Exception.Message + ". Выбери другое устройство в настройках звука.");
                    };
                    next.StartRecording();
                }
                catch (Exception ex) { Error?.Invoke("Захват микрофона недоступен: " + ex.Message); }
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { if (running) Error?.Invoke("Аудиоустройства: " + ex.Message); }
        finally { deviceSwitch.Release(); }
    }
    private void Capture(object? sender, WaveInEventArgs args)
    {
        try
        {
            lock (gate)
            {
                if (!running || !ReferenceEquals(input, sender) || muted || deafened) { captureCount = 0; return; }
                for (int offset = 0; offset + 1 < args.BytesRecorded; offset += 2)
                {
                    captureFrame[captureCount++] = (short)(args.Buffer[offset] | args.Buffer[offset + 1] << 8);
                    if (captureCount != FrameSamples) continue;
                    captureCount = 0;
                    if (micProcessing?.Process(captureFrame, muted, deafened) == false) continue;
                    var packet = new byte[1275];
                    if (encoder is null) return;
                    int length = encoder.Encode(captureFrame.AsSpan(), FrameSamples, packet.AsSpan(), packet.Length);
                    if (length > 0) PacketReady?.Invoke(packet.AsSpan(0, length).ToArray());
                }
            }
        }
        catch (Exception ex) { Error?.Invoke("Микрофон: " + ex.Message); }
    }
    public void Receive(string peerId, byte[] packet, int missingFrames = 0)
    {
        if (deafened || packet.Length is 0 or > 1275) return;
        lock (gate)
        {
            if (mixer is null) return;
            if (!peers.TryGetValue(peerId, out var peer))
            {
                if (peers.Count >= 4) return;
                peer = new PlaybackPeer(); peers.Add(peerId, peer); mixer.AddMixerInput(peer);
            }
            if (!peer.Decoder.TryDecodeWithRecovery(packet, missingFrames, peer.Samples, out int samples)) return;
            peer.Buffer.ObserveArrival(missingFrames);
            peer.Buffer.Write(peer.Samples.AsSpan(0, samples));
        }
    }
    public void SynchronizePeers(IEnumerable<string> activePeerIds)
    {
        var active = activePeerIds.ToHashSet(StringComparer.Ordinal);
        lock (gate)
            foreach (var id in peers.Keys.Where(id => !active.Contains(id)).ToArray())
            {
                var peer = peers[id]; mixer?.RemoveMixerInput(peer); peer.Decoder.Dispose(); peers.Remove(id);
            }
    }
    private void CloseDevices()
    {
        IWaveIn? previous;
        lock (gate) { previous = input; input = null; }
        if (previous is not null) { previous.DataAvailable -= Capture; previous.StopRecording(); previous.Dispose(); }
        output?.Stop(); output?.Dispose(); output = null;
        inputDevice?.Dispose(); outputDevice?.Dispose(); inputDevice = outputDevice = null;
    }
    public void Stop()
    {
        running = false;
        var pending = Interlocked.Exchange(ref debounce, null);
        try { pending?.Cancel(); } catch (ObjectDisposedException) { }
        watcher?.Dispose(); watcher = null;
        deviceSwitch.Wait();
        try
        {
            CloseDevices();
            lock (gate)
            {
                (encoder as IDisposable)?.Dispose(); encoder = null; mixer = null;
                foreach (var peer in peers.Values) peer.Decoder.Dispose(); peers.Clear();
            }
        }
        finally { deviceSwitch.Release(); }
    }
    public void Dispose()
    {
        Stop();
        lock (gate) { micProcessing?.Dispose(); micProcessing = null; denoiser = null; }
    }
    private sealed class PlaybackPeer : ISampleProvider
    {
        public readonly OpusFrameDecoder Decoder = new();
        public readonly short[] Samples = new short[FrameSamples * 4];
        public readonly PcmJitterBuffer Buffer = new();
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(float[] buffer, int offset, int count) { Buffer.Read(buffer.AsSpan(offset, count)); return count; }
    }
    private sealed class LimitedSampleProvider(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            for (int i = offset; i < offset + read; i++) buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
            return read;
        }
    }
}
