using NAudio.Wave;

namespace Wisp.Desktop.Media;

/// <summary>Locally recorded WAV, mono 24kHz, at most 60 seconds (about 2.9 MB).</summary>
public sealed class VoiceRecorder : IDisposable
{
    private const int MaximumBytes = 24000 * 2 * 60;
    private readonly object _gate = new();
    private WaveInEvent? _input;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<string>? _completion;
    private string _path = "";
    private int _bytes;
    public bool IsRecording { get { lock (_gate) return _input is not null; } }
    public event Action<string>? Completed;

    public void Start(string path)
    {
        lock (_gate)
        {
            if (_input is not null) throw new InvalidOperationException("A voice recording is already active.");
            _path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _bytes = 0;
            try
            {
                _input = new WaveInEvent { WaveFormat = new WaveFormat(24000, 16, 1), BufferMilliseconds = 40 };
                _writer = new WaveFileWriter(_path, _input.WaveFormat);
                _input.DataAvailable += DataAvailable;
                _input.RecordingStopped += RecordingStopped;
                _input.StartRecording();
            }
            catch
            {
                _input?.Dispose(); _input = null;
                _writer?.Dispose(); _writer = null;
                throw;
            }
        }
    }

    private void DataAvailable(object? sender, WaveInEventArgs e)
    {
        WaveInEvent? stop = null;
        lock (_gate)
        {
            if (_writer is null) return;
            var count = Math.Min(e.BytesRecorded, MaximumBytes - _bytes);
            if (count > 0) _writer.Write(e.Buffer, 0, count);
            _bytes += count;
            if (_bytes >= MaximumBytes) stop = _input;
        }
        stop?.StopRecording();
    }

    private void RecordingStopped(object? sender, StoppedEventArgs e)
    {
        string path;
        TaskCompletionSource<string>? completion;
        lock (_gate)
        {
            _writer?.Dispose(); _writer = null;
            _input?.Dispose(); _input = null;
            path = _path;
            completion = _completion;
        }
        if (e.Exception is not null) completion?.TrySetException(e.Exception);
        else { completion?.TrySetResult(path); Completed?.Invoke(path); }
    }

    public Task<string> StopAsync()
    {
        lock (_gate)
        {
            if (_completion is null) throw new InvalidOperationException("No voice recording has been started.");
            _input?.StopRecording();
            return _completion.Task;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_input is not null)
            {
                _input.DataAvailable -= DataAvailable;
                _input.RecordingStopped -= RecordingStopped;
                _input.StopRecording();
                _input.Dispose(); _input = null;
            }
            _writer?.Dispose(); _writer = null;
            _completion?.TrySetCanceled();
        }
    }
}
