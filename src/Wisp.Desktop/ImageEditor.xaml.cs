using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.Desktop.Media;
using XamlAnimatedGif;

namespace Wisp.Desktop;

/// <summary>Visual crop gestures stay in the view; decoding/compositing/encoding run in the image service.</summary>
public partial class ImageEditor : UserControl, IDisposable
{
    private readonly string sourcePath;
    private readonly int initialCover;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? coverRequest;
    private MemoryStream? previewStream;
    private Point center = new(.5, .5), previousPointer;
    private Rect crop;
    private double displayScale;
    private bool loaded, disposed, dragging;
    public ImageEditorViewModel ViewModel { get; }
    public Rect SourceCrop => crop;
    public EditedImage? Result { get; private set; }
    public event Action<EditedImage>? Saved;
    public event Action? Cancelled;

    public ImageEditor(string sourcePath, ImageEditKind kind, int initialCoverFrame = 0)
    {
        this.sourcePath = sourcePath; initialCover = initialCoverFrame;
        ViewModel = new ImageEditorViewModel(kind); InitializeComponent(); DataContext = ViewModel;
        Ui.Name(this, "Редактор изображения");
        ViewModel.PropertyChanged += StateChanged;
        Loaded += Load; Unloaded += (_, _) => Dispose();
        Viewport.LostMouseCapture += (_, _) => dragging = false;
        OutputShape.Text = kind == ImageEditKind.ProfileBanner ? "Баннер 16:9" : "Аватар 1:1";
        if (kind != ImageEditKind.ProfileBanner) ResultBorder.Clip = new EllipseGeometry(new Point(72, 72), 72, 72);
        else ResultBorder.Height = 81;
    }

    public static EditedImage? Show(Window owner, string sourcePath, ImageEditKind kind, int initialCoverFrame = 0)
    {
        owner.Dispatcher.VerifyAccess();
        var pending = ShowAsync(owner, sourcePath, kind, initialCoverFrame);
        if (!pending.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = pending.ContinueWith(_ => owner.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        return pending.GetAwaiter().GetResult();
    }

    public static Task<EditedImage?> ShowAsync(Window owner, string sourcePath, ImageEditKind kind, int initialCoverFrame = 0)
    {
        owner.Dispatcher.VerifyAccess();
        var completed = new TaskCompletionSource<EditedImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new ImageEditor(sourcePath, kind, initialCoverFrame) { Height = Math.Clamp(owner.ActualHeight - 180, 330, 620) };
        var handle = ModalHost.Open(owner, "Редактирование изображения", editor, 920);
        void OwnerClosed(object? sender, EventArgs args) => handle.Close();
        editor.Saved += _ => handle.Close(); editor.Cancelled += () => handle.Close(); owner.Closed += OwnerClosed;
        handle.Closed += () => { owner.Closed -= OwnerClosed; editor.Dispose(); completed.TrySetResult(editor.Result); };
        return completed.Task;
    }

    private async void Load(object sender, RoutedEventArgs args)
    {
        if (loaded || disposed) return; loaded = true; var token = lifetime.Token;
        try
        {
            var document = await Task.Run(() => new ImageEditingDocument(sourcePath), token);
            var selected = Math.Clamp(initialCover, 0, document.FrameCount - 1);
            var preview = await Task.Run(() => document.GetFrame(selected, 1024, token), token);
            if (disposed) return;
            ViewModel.Document = document; ViewModel.MaximumFrame = document.FrameCount - 1;
            ViewModel.CoverFrame = selected; ViewModel.Source = preview; SourceImage.Source = preview;
            ViewModel.Status = document.Animated ? $"{document.Width}×{document.Height} · {document.FrameCount} кадров · анимация сохраняется" : $"{document.Width}×{document.Height}";
            ViewModel.Busy = false; UpdateCrop(); UpdateFrameLabel();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!disposed) { ViewModel.Error = error.Message; ViewModel.Busy = false; DiagnosticLog.Write("image-editor-load", error); } }
    }

    private void StateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (disposed) return;
        if (args.PropertyName == nameof(ImageEditorViewModel.Zoom)) { ViewModel.Error = ""; UpdateCrop(); }
        else if (args.PropertyName == nameof(ImageEditorViewModel.CoverFrame)) { ViewModel.Error = ""; UpdateFrameLabel(); _ = LoadCoverAsync(); }
    }
    private async Task LoadCoverAsync()
    {
        if (ViewModel.Document is not { } document || ViewModel.Busy) return;
        StopPlayback(); coverRequest?.Cancel(); coverRequest?.Dispose();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); coverRequest = request;
        var token = request.Token; int selected = ViewModel.CoverFrame;
        try
        {
            // Coalesce timeline dragging; do not decode each intermediate thumb.
            await Task.Delay(60, token);
            var frame = await Task.Run(() => document.GetFrame(selected, 1024, token), token);
            if (disposed || token.IsCancellationRequested || ViewModel.CoverFrame != selected) return;
            ViewModel.Source = frame; SourceImage.Source = frame;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!disposed) { ViewModel.Error = error.Message; DiagnosticLog.Write("image-editor-cover", error); } }
    }
    private void UpdateFrameLabel()
    {
        if (ViewModel.Document is not { } document) return;
        double selected = document.Delays.Take(ViewModel.CoverFrame).Sum() / 100d;
        double total = document.Delays.Sum() / 100d;
        FrameLabel.Text = $"{ViewModel.CoverFrame + 1}/{document.FrameCount} · {selected:0.00}/{total:0.00} с";
    }

    private void UpdateCrop()
    {
        if (ViewModel.Document is not { } document || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        double ratio = ViewModel.Kind == ImageEditKind.ProfileBanner ? 16d / 9 : 1;
        double width = Math.Max(1, Math.Min(Viewport.ActualWidth * .82, Viewport.ActualHeight * .82 * ratio)), height = width / ratio;
        var opening = new Rect((Viewport.ActualWidth - width) / 2, (Viewport.ActualHeight - height) / 2, width, height);
        displayScale = Math.Max(width / document.Width, height / document.Height) * ViewModel.Zoom;
        double cropWidth = width / displayScale, cropHeight = height / displayScale;
        center = new(Math.Clamp(center.X, cropWidth / document.Width / 2, 1 - cropWidth / document.Width / 2), Math.Clamp(center.Y, cropHeight / document.Height / 2, 1 - cropHeight / document.Height / 2));
        crop = new(center.X * document.Width - cropWidth / 2, center.Y * document.Height - cropHeight / 2, cropWidth, cropHeight);
        SourceImage.Width = document.Width; SourceImage.Height = document.Height;
        SourceImage.RenderTransform = new MatrixTransform(displayScale, 0, 0, displayScale, opening.X - crop.X * displayScale, opening.Y - crop.Y * displayScale);
        Geometry inside = ViewModel.Kind == ImageEditKind.ProfileBanner ? new RectangleGeometry(opening) : new EllipseGeometry(opening);
        CropOutline.Data = inside;
        OutsideShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight)), inside);
        ResultBrush.Viewbox = new(crop.X / document.Width, crop.Y / document.Height, crop.Width / document.Width, crop.Height / document.Height);
    }
    private void ViewportSizeChanged(object sender, SizeChangedEventArgs args) => UpdateCrop();
    private void StartDrag(object sender, MouseButtonEventArgs args)
    {
        if (!ViewModel.CanEdit) return; previousPointer = args.GetPosition(Viewport); dragging = Viewport.CaptureMouse(); args.Handled = true;
    }
    private void Drag(object sender, MouseEventArgs args)
    {
        if (!dragging || args.LeftButton != MouseButtonState.Pressed || ViewModel.Document is not { } document || displayScale <= 0) return;
        var pointer = args.GetPosition(Viewport); var delta = pointer - previousPointer; previousPointer = pointer;
        center = new(center.X - delta.X / displayScale / document.Width, center.Y - delta.Y / displayScale / document.Height); ViewModel.Error = ""; UpdateCrop(); args.Handled = true;
    }
    private void FinishDrag(object sender, MouseButtonEventArgs args) { dragging = false; Viewport.ReleaseMouseCapture(); args.Handled = true; }
    private void ZoomWheel(object sender, MouseWheelEventArgs args) { ViewModel.Zoom *= args.Delta > 0 ? 1.1 : 1 / 1.1; args.Handled = true; }
    private void Reset(object sender, RoutedEventArgs args) { center = new(.5, .5); ViewModel.Error = ""; ViewModel.Zoom = 1; UpdateCrop(); }

    private void TogglePlayback(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.CanEdit || ViewModel.Document is not { Animated: true } document) return;
        if (ViewModel.Playing) { StopPlayback(); _ = LoadCoverAsync(); return; }
        coverRequest?.Cancel(); ViewModel.Playing = true; PreviewPlayback.Content = "Пауза";
        AnimationBehavior.SetAutoStart(SourceImage, true); AnimationBehavior.SetRepeatBehavior(SourceImage, System.Windows.Media.Animation.RepeatBehavior.Forever);
        previewStream = new MemoryStream(document.Encoded, false); AnimationBehavior.SetSourceStream(SourceImage, previewStream);
    }
    private void StopPlayback()
    {
        ViewModel.Playing = false; PreviewPlayback.Content = "Воспроизвести";
        if (AnimationBehavior.GetSourceStream(SourceImage) is not null) AnimationBehavior.SetSourceStream(SourceImage, null!);
        previewStream?.Dispose(); previewStream = null;
        if (ViewModel.Source is not null) SourceImage.Source = ViewModel.Source;
    }

    private async void Save(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.CanSave || ViewModel.Document is not { } document) return;
        StopPlayback(); coverRequest?.Cancel(); ViewModel.Busy = true; ViewModel.Error = ""; SaveLabel.Text = "Сохранение…";
        var token = lifetime.Token; var selectedCrop = crop; int cover = ViewModel.CoverFrame;
        try
        {
            var result = await Task.Run(() => ImageEditingService.Save(document, selectedCrop, ViewModel.Kind, cover, token), token);
            if (disposed || token.IsCancellationRequested) { result.Dispose(); return; }
            Result = result; Saved?.Invoke(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!disposed) { ViewModel.Error = error.Message; DiagnosticLog.Write("image-editor-save", error); } }
        finally { if (!disposed) { ViewModel.Busy = false; SaveLabel.Text = "Сохранить"; } }
    }
    private void Cancel(object sender, RoutedEventArgs args) => Cancelled?.Invoke();
    public void Dispose()
    {
        if (disposed) return; disposed = true; dragging = false; Viewport.ReleaseMouseCapture();
        ViewModel.PropertyChanged -= StateChanged; lifetime.Cancel(); coverRequest?.Cancel(); StopPlayback();
        coverRequest?.Dispose(); lifetime.Dispose(); SourceImage.Source = null; ViewModel.Source = null; ViewModel.Document = null;
    }
}
