using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisp.Core;

namespace Wisp.Desktop;

public partial class AttachmentCard : UserControl
{
    private readonly MediaAsset asset;
    private readonly Action<string> error;
    private MediaElement? video;
    public static bool IsImage(string name) => new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" }.Contains(Path.GetExtension(name).ToLowerInvariant());
    public static bool IsVideo(string name) => new[] { ".mp4", ".m4v", ".mov", ".wmv", ".webm", ".avi", ".mkv" }.Contains(Path.GetExtension(name).ToLowerInvariant());
    public AttachmentCard(Window owner, MediaAsset asset, Action download, Action open, Action<string> error)
    {
        InitializeComponent(); this.asset = asset; this.error = error;
        Ui.Name(MediaDownload, "Скачать " + asset.Name); Ui.Name(FileDownload, "Скачать " + asset.Name); Ui.Name(OpenPreview, "Открыть " + asset.Name);
        void Save(object sender, RoutedEventArgs args) { args.Handled = true; try { download(); } catch (Exception ex) { error(ex.Message); } }
        MediaDownload.Click += Save; FileDownload.Click += Save;
        OpenPreview.Click += (_, _) => { try { open(); } catch (Exception ex) { error(ex.Message); } };
        FileName.Text = asset.Name; FileName.ToolTip = asset.Name; FileSize.Text = FormatSize(asset.Size);
        if (IsImage(asset.Name) || IsVideo(asset.Name))
        {
            MediaArea.Visibility = Visibility.Visible; FileArea.Visibility = Visibility.Collapsed;
            if (IsImage(asset.Name)) LoadPicture();
            else
            {
                video = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true, Volume = 0, ScrubbingEnabled = true, Stretch = Stretch.Uniform, MaxHeight = Ui.Metric("Height.Media"), MaxWidth = Ui.Metric("Width.Media") };
                video.MediaOpened += (_, _) => { video.Pause(); video.Position = TimeSpan.FromMilliseconds(100); PreviewState.Visibility = Visibility.Collapsed; };
                video.MediaFailed += (_, args) => { PreviewState.Text = "Превью недоступно · нажмите, чтобы открыть"; PreviewState.ToolTip = args.ErrorException.Message; };
                Preview.Children.Insert(0, video);
                Loaded += (_, _) => { video.Source = new Uri(Path.GetFullPath(asset.LocalPath)); video.Play(); };
                Unloaded += (_, _) => video.Close();
            }
        }
        else if (new[] { ".wav", ".mp3", ".ogg", ".flac" }.Contains(Path.GetExtension(asset.Name).ToLowerInvariant())) FileIcon.Kind = "volume-2";
        ToolTip = asset.Name + " · " + FormatSize(asset.Size);
    }
    private void LoadPicture()
    {
        try
        {
            var image = AvatarCatalog.Image(asset.LocalPath, Ui.Metric("Width.Media"), mode: GifPlaybackMode.Hover, hoverScope: OpenPreview);
            image.Width = image.Height = double.NaN; image.MaxWidth = Ui.Metric("Width.Media"); image.MaxHeight = Ui.Metric("Height.Media"); image.Stretch = Stretch.Uniform;
            Preview.Children.Insert(0, image); PreviewState.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { PreviewState.Text = "Изображение недоступно"; PreviewState.ToolTip = ex.Message; DiagnosticLog.Write("attachment-preview", ex); }
    }
    public static string FormatSize(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576d:0.#} МБ" : bytes >= 1024 ? $"{bytes / 1024d:0.#} КБ" : $"{bytes} Б";
    public static void Save(Window owner, MediaAsset asset)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = asset.Name, Title = "Сохранить вложение" };
        if (dialog.ShowDialog(owner) == true && !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(asset.LocalPath))) File.Copy(asset.LocalPath, dialog.FileName, true);
    }
}
