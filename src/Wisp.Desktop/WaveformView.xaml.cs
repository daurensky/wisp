using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp.Desktop;

public partial class WaveformView : UserControl
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(nameof(Peaks), typeof(IReadOnlyList<float>), typeof(WaveformView));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(WaveformView), new PropertyMetadata(0d));
    public IReadOnlyList<float>? Peaks { get => (IReadOnlyList<float>?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public event Action<double>? SeekRequested;
    public WaveformView()
    {
        InitializeComponent(); Ui.Name(this, "Звуковая волна · нажмите для перемотки");
        MouseLeftButtonDown += (_, e) => { Focus(); CaptureMouse(); Seek(e.GetPosition(this).X); e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Seek(e.GetPosition(this).X); };
        MouseLeftButtonUp += (_, e) => { if (IsMouseCaptured) { ReleaseMouseCapture(); e.Handled = true; } };
        KeyDown += (_, e) =>
        {
            double? next = e.Key switch { Key.Left => Progress - .02, Key.Right => Progress + .02, Key.Home => 0, Key.End => 1, _ => null };
            if (next is null) return; SeekRequested?.Invoke(Math.Clamp(next.Value, 0, 1)); e.Handled = true;
        };
    }
    private void Seek(double x) { if (ActualWidth > 0) SeekRequested?.Invoke(Math.Clamp(x / ActualWidth, 0, 1)); }
}

public sealed class WaveformBars : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(nameof(Peaks), typeof(IReadOnlyList<float>), typeof(WaveformBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(WaveformBars), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(WaveformBars), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(nameof(ProgressBrush), typeof(Brush), typeof(WaveformBars), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<float>? Peaks { get => (IReadOnlyList<float>?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush ProgressBrush { get => (Brush)GetValue(ProgressBrushProperty); set => SetValue(ProgressBrushProperty, value); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var values = Peaks; double center = ActualHeight / 2;
        if (values is null || values.Count == 0) { drawing.DrawLine(new Pen(Foreground, 1), new Point(0, center), new Point(ActualWidth, center)); return; }
        int bars = Math.Min(values.Count, Math.Max(1, (int)(ActualWidth / 4)));
        for (int bar = 0; bar < bars; bar++)
        {
            int start = bar * values.Count / bars, end = Math.Max(start + 1, (bar + 1) * values.Count / bars);
            float peak = 0; for (int i = start; i < end; i++) peak = Math.Max(peak, values[i]);
            double height = Math.Max(1, Math.Clamp(peak, 0, 1) * (ActualHeight - 2));
            double x = (bar + .5) * ActualWidth / bars;
            var brush = (bar + .5) / bars <= Progress ? ProgressBrush : Foreground;
            drawing.DrawRoundedRectangle(brush, null, new Rect(x - 1, center - height / 2, 2, height), 1, 1);
        }
    }
}
