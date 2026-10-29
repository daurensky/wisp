using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

public enum ImageEditKind { UserAvatar, ServerIcon, ProfileBanner }

/// <summary>The caller owns an accepted draft until its enclosing profile/server form saves or cancels.</summary>
public sealed record EditedImage(string Path, int CoverFrame, int Width, int Height, int FrameCount) : IDisposable
{
    public void Dispose()
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
            var parent = System.IO.Path.GetDirectoryName(Path);
            if (parent is not null && System.IO.Path.GetFileName(parent).StartsWith("wisp-image-editor-", StringComparison.Ordinal) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { DiagnosticLog.Write("image-draft-cleanup", error); }
    }
}

/// <summary>One draft state drives avatar, server-icon and banner editing.</summary>
public sealed class ImageEditorViewModel : INotifyPropertyChanged
{
    private bool busy = true, playing;
    private string status = "Загрузка изображения…", error = "";
    private double zoom = 1;
    private int coverFrame, maximumFrame;
    private BitmapSource? source;
    public ImageEditKind Kind { get; }
    public bool Busy { get => busy; internal set { if (Set(ref busy, value)) { OnChanged(nameof(CanEdit)); OnChanged(nameof(CanSave)); } } }
    public bool CanEdit => !Busy && source is not null;
    public bool CanSave => CanEdit && Error.Length == 0;
    public bool Playing { get => playing; set => Set(ref playing, value); }
    public double Zoom { get => zoom; set => Set(ref zoom, Math.Clamp(value, 1, 8)); }
    public int CoverFrame { get => coverFrame; set => Set(ref coverFrame, Math.Clamp(value, 0, MaximumFrame)); }
    public int MaximumFrame { get => maximumFrame; internal set { if (Set(ref maximumFrame, value)) OnChanged(nameof(Animated)); } }
    public bool Animated => MaximumFrame > 0;
    public string Status { get => status; internal set => Set(ref status, value); }
    public string Error { get => error; internal set { if (Set(ref error, value)) OnChanged(nameof(CanSave)); } }
    public BitmapSource? Source { get => source; internal set { if (Set(ref source, value)) { OnChanged(nameof(CanEdit)); OnChanged(nameof(CanSave)); } } }
    internal ImageEditingDocument? Document { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ImageEditorViewModel(ImageEditKind kind) => Kind = kind;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string property = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnChanged(property); return true;
    }
    private void OnChanged(string property) => PropertyChanged?.Invoke(this, new(property));
}
