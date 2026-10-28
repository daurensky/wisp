using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Wisp.Desktop.Media;

/// <summary>Bounded 24kHz mono WAV recorder; an active call can lend its existing capture stream.</summary>
public sealed class VoiceRecorder : IDisposable
{
    private const int MaximumBytes = 24000 * 2 * 60;
    private readonly object gate = new();
    private IWaveIn? input;
    private VoiceService? source;
    private Channel<byte[]>? audio;
    private TaskCompletionSource<string>? completion;
    private Task? writerTask, captureCleanup;
    private string path = "";
    private int bytes;
    private bool active, stopping, disposed, sharedMode;
    public bool IsRecording { get { lock (gate) return active; } }
    public bool UsesSharedSource { get { lock (gate) return sharedMode && active; } }
    public TimeSpan Duration { get { lock (gate) return TimeSpan.FromSeconds(bytes / 48000d); } }
    public event Action<string>? Completed;
    public event Action? Started;
    public event Action<string>? Error;
    public event Action<float, TimeSpan>? LevelChanged;

    public void Start(string destination, VoiceService? sharedSource = null, string? inputDeviceId = null)
    {
        StartExternal(destination);
        try
        {
            if (sharedSource is not null)
            {
                lock (gate) { source = sharedSource; sharedMode = true; source.MicrophonePcmAvailable += AppendPcm; }
            }
            else
            {
                string endpoint;
                using (var devices = new MMDeviceEnumerator())
                using (var device = AudioDeviceSettings.Resolve(devices, inputDeviceId, DataFlow.Capture, _ => { })) endpoint = device.ID;
                var capture = new CommunicationsCapture(endpoint, _ => { });
                lock (gate) input = capture;
                capture.DataAvailable += NativeData;
                capture.RecordingStopped += NativeStopped;
                capture.StartRecording();
            }
        }
        catch (Exception ex) { RequestStop(ex); throw; }
    }

    /// <summary>Capture adapters feed borrowed 48kHz/16-bit/mono PCM through AppendPcm.</summary>
    public void StartExternal(string destination)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active || writerTask?.IsCompleted == false || captureCleanup?.IsCompleted == false)
                throw new InvalidOperationException("Предыдущая запись ещё завершается.");
            path = Path.GetFullPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new WaveFileWriter(path, new WaveFormat(24000, 16, 1));
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
            bytes = 0; stopping = false; active = true; sharedMode = false;
            var queue = audio; var result = completion; var output = path;
            writerTask = Task.Run(() => WriteAudio(file, queue, result, output));
        }
        Started?.Invoke();
    }
    public void AppendPcm(ReadOnlyMemory<byte> samples)
    {
        Channel<byte[]>? queue;
        lock (gate) { if (!active || stopping || disposed) return; queue = audio; }
        if (samples.Length == 0) return;
        if ((samples.Length & 1) != 0 || samples.Length > 192000)
        { RequestStop(new InvalidDataException("Некорректный блок микрофона.")); return; }
        if (queue?.Writer.TryWrite(samples.ToArray()) != true)
            RequestStop(new IOException("Запись остановлена: накопилась очередь аудио."));
    }
    private void NativeData(object? sender, WaveInEventArgs args) => AppendPcm(args.Buffer.AsMemory(0, args.BytesRecorded));
    private void NativeStopped(object? sender, StoppedEventArgs args) => RequestStop(args.Exception);

    private async Task WriteAudio(WaveFileWriter file, Channel<byte[]> queue, TaskCompletionSource<string> result, string output)
    {
        short? previous = null; Exception? failure = null;
        try
        {
            await foreach (var pcm in queue.Reader.ReadAllAsync())
            {
                var downsampled = new byte[pcm.Length / 2 + 2]; int written = 0; float peak = 0;
                for (int i = 0; i + 1 < pcm.Length; i += 2)
                {
                    short value = (short)(pcm[i] | pcm[i + 1] << 8);
                    if (previous is null) { previous = value; continue; }
                    short averaged = (short)((previous.Value + (int)value) / 2); previous = null;
                    downsampled[written++] = (byte)averaged; downsampled[written++] = (byte)(averaged >> 8);
                    peak = Math.Max(peak, Math.Abs(averaged / 32768f));
                }
                int count; TimeSpan elapsed;
                lock (gate) { count = Math.Min(written, MaximumBytes - bytes); bytes += count; elapsed = TimeSpan.FromSeconds(bytes / 48000d); }
                if (count > 0) { file.Write(downsampled, 0, count); LevelChanged?.Invoke(peak, elapsed); }
                if (elapsed.TotalSeconds >= 60) RequestStop();
            }
        }
        catch (Exception ex) { failure = ex; RequestStop(ex); }
        finally
        {
            try { file.Dispose(); } catch (Exception ex) { failure ??= ex; }
            lock (gate) { active = false; sharedMode = false; }
        }
        if (failure is not null) { result.TrySetException(failure); if (!disposed) Error?.Invoke("Запись голоса: " + failure.Message); }
        else { result.TrySetResult(output); if (!disposed) Completed?.Invoke(output); }
    }
    private void RequestStop(Exception? failure = null)
    {
        IWaveIn? capture;
        lock (gate)
        {
            if (!active || stopping) return;
            stopping = true;
            if (source is not null) { source.MicrophonePcmAvailable -= AppendPcm; source = null; }
            capture = input; input = null;
            if (capture is not null)
            {
                capture.DataAvailable -= NativeData; capture.RecordingStopped -= NativeStopped;
                captureCleanup = Task.Run(() => { try { capture.StopRecording(); } finally { capture.Dispose(); } });
            }
            audio?.Writer.TryComplete(failure);
        }
    }
    public async Task<string> StopAsync()
    {
        Task<string> result;
        lock (gate) result = completion?.Task ?? throw new InvalidOperationException("Запись не начата.");
        RequestStop();
        try { return await result; }
        finally
        {
            Task? writer, cleanup; lock (gate) { writer = writerTask; cleanup = captureCleanup; }
            if (writer is not null) await writer;
            if (cleanup is not null) await cleanup;
        }
    }
    public void Dispose() { lock (gate) disposed = true; RequestStop(); }
}
