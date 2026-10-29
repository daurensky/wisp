using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

/// <summary>Real, bounded one-shot source previews. Enumeration is refreshed only while the picker is visible.</summary>
public sealed class ScreenSourcePicker : UserControl, IDisposable
{
    private sealed record SourceCard(CaptureSource Source, Button Button, Image Image, TextBlock State, Grid Preview);
    private readonly WrapPanel cards = new();
    private readonly TextBlock state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button applications, screens, refresh;
    private readonly Dictionary<string, SourceCard> current = new();
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim previews = new(2, 2);
    private readonly Func<IReadOnlyList<CaptureSource>> sourceProvider;
    private readonly Func<CaptureSource, BitmapSource> previewProvider;
    private CancellationTokenSource lifetime = new();
    private bool showScreens, refreshing, pendingRefresh, disposed;
    private int generation;
    public CaptureSource? SelectedSource { get; private set; }
    public event Action? SelectionChanged;

    public ScreenSourcePicker(Func<IReadOnlyList<CaptureSource>>? sourceProvider = null, Func<CaptureSource, BitmapSource>? previewProvider = null)
    {
        this.sourceProvider = sourceProvider ?? ScreenShareService.GetSources;
        this.previewProvider = previewProvider ?? CapturePreview;
        Height=Ui.Metric("Height.SourceList");MinHeight=180;
        var root=new DockPanel();
        var tabs=new Grid{Margin=Ui.Insets(0,0,0,Ui.S)};
        tabs.ColumnDefinitions.Add(new ColumnDefinition());tabs.ColumnDefinitions.Add(new ColumnDefinition());
        applications=new Button{Content="Приложение",Margin=Ui.Insets(0)};applications.Click+=(_,_)=>SelectTab(false);
        screens=new Button{Content="Экран",Margin=Ui.Insets(0)};screens.Click+=(_,_)=>SelectTab(true);
        applications.HorizontalContentAlignment=screens.HorizontalContentAlignment=HorizontalAlignment.Center;
        Ui.Name(applications,"Источники · Приложение");Ui.Name(screens,"Источники · Экран");
        tabs.Children.Add(applications);Grid.SetColumn(screens,1);tabs.Children.Add(screens);
        DockPanel.SetDock(tabs,Dock.Top);root.Children.Add(tabs);
        var statusRow=new DockPanel{Margin=Ui.Insets(0,Ui.Xs,0,0)};
        refresh=Buttons.Tool("refresh-cw","Обновить источники и превью",()=>_=RefreshAsync(true));DockPanel.SetDock(refresh,Dock.Right);statusRow.Children.Add(refresh);
        Ui.Secondary(state);state.Margin=Ui.Insets(Ui.S);statusRow.Children.Add(state);DockPanel.SetDock(statusRow,Dock.Bottom);root.Children.Add(statusRow);
        root.Children.Add(new ScrollViewer{Content=cards,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});Content=root;
        SizeChanged += (_, _) => ResizeCards();
        IsVisibleChanged += (_, _) => { if (IsVisible && !disposed) { refreshTimer.Start(); _ = RefreshAsync(); } else refreshTimer.Stop(); };
        refreshTimer.Tick += (_, _) => { if (IsVisible) _ = RefreshAsync(); };
        Loaded += (_, _) => { if (!disposed) { refreshTimer.Start(); _ = RefreshAsync(); } };
        Unloaded += (_, _) => Dispose();
        UpdateTabs();
    }

    private void SelectTab(bool monitors)
    {
        if (showScreens == monitors) return;
        showScreens = monitors; SelectedSource = null; SelectionChanged?.Invoke();
        UpdateTabs(); _ = RefreshAsync();
    }
    private void UpdateTabs()
    {
        foreach(var button in new[]{applications,screens}){Ui.Style(button,"Button.Ghost");button.BorderThickness=new Thickness(0,0,0,2);}
        applications.SetResourceReference(Control.BorderBrushProperty,showScreens?"Border":"TextPrimary");screens.SetResourceReference(Control.BorderBrushProperty,showScreens?"TextPrimary":"Border");
        applications.SetResourceReference(Control.BackgroundProperty,showScreens?"Surface":"Selected");screens.SetResourceReference(Control.BackgroundProperty,showScreens?"Selected":"Surface");
    }
    public async Task RefreshAsync(bool refreshPreviews = false)
    {
        if (disposed) return;
        if (refreshing) { pendingRefresh = true; return; }
        refreshing = true; refresh.IsEnabled = false;
        var token = lifetime.Token;
        int request = ++generation;
        try
        {
            state.Text = current.Count == 0 ? "Поиск источников…" : "Обновление источников…";
            var sources = await Task.Run(sourceProvider, token);
            if (disposed || token.IsCancellationRequested || request != generation) return;
            var filtered = sources.Where(source => source.Id.StartsWith("window:", StringComparison.Ordinal) != showScreens).ToArray();
            var ids = filtered.Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var old in current.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                cards.Children.Remove(current[old].Button); current.Remove(old);
            }
            if (SelectedSource is not null && !ids.Contains(SelectedSource.Id)) { SelectedSource = null; SelectionChanged?.Invoke(); }
            var pending = new List<Task>();
            foreach (var source in filtered)
            {
                if (current.TryGetValue(source.Id, out var existing) && existing.Source.Name != source.Name)
                {
                    cards.Children.Remove(existing.Button); current.Remove(source.Id);
                }
                if (!current.TryGetValue(source.Id, out var card))
                {
                    card = CreateCard(source); current.Add(source.Id, card); cards.Children.Add(card.Button);
                    pending.Add(LoadPreviewAsync(card, token));
                }
                else if (refreshPreviews) pending.Add(LoadPreviewAsync(card, token));
            }
            UpdateSelection(); ResizeCards();
            state.Text = filtered.Length == 0
                ? (showScreens ? "Нет доступных экранов. Проверьте подключение мониторов." : "Нет доступных окон. Откройте или разверните приложение и нажмите «Обновить».")
                : "";
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!disposed) { state.Text = "Не удалось получить источники: " + ex.Message; DiagnosticLog.Write("screen-source-picker", ex); }
        }
        finally
        {
            refreshing = false;
            if (!disposed)
            {
                refresh.IsEnabled = true;
                if (pendingRefresh) { pendingRefresh = false; _ = RefreshAsync(); }
            }
        }
    }

    private SourceCard CreateCard(CaptureSource source)
    {
        var image = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        var previewState = Ui.Label("Загрузка превью…", Ui.Caption, true); previewState.HorizontalAlignment = HorizontalAlignment.Center; previewState.TextWrapping = TextWrapping.Wrap;
        var preview = new Grid { Height = Ui.Metric("Height.MediaPreview") }; preview.SetResourceReference(Panel.BackgroundProperty, "VideoBackground"); preview.Children.Add(image); preview.Children.Add(previewState);
        var panel = new StackPanel(); panel.Children.Add(preview);
        var label = Ui.Label(source.Name.StartsWith("Приложение · ", StringComparison.Ordinal) ? source.Name[13..] : source.Name, Ui.Caption);
        label.Margin = Ui.Insets(0, Ui.S, 0, 0); label.ToolTip = source.Name; panel.Children.Add(label);
        var button = new Button { Content = panel, Padding = Ui.Insets(Ui.S), Margin = Ui.Insets(Ui.Xs), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = source.Name };
        Ui.Style(button, "Button.Secondary"); Ui.Name(button, "Источник · " + source.Name);
        button.Click += (_, _) => { SelectedSource = source; UpdateSelection(); SelectionChanged?.Invoke(); };
        return new SourceCard(source, button, image, previewState, preview);
    }
    private void UpdateSelection()
    {
        foreach (var card in current.Values)
        {
            bool selected = card.Source.Id == SelectedSource?.Id;
            card.Button.BorderThickness = new Thickness(selected ? Ui.Metric("Size.Hairline") * 2 : Ui.Metric("Size.Hairline"));
            card.Button.SetResourceReference(Control.BorderBrushProperty, selected ? "TextPrimary" : "Border");
            card.Button.ToolTip = card.Source.Name + (selected ? " · выбран" : "");
        }
    }
    private void ResizeCards()
    {
        double available=Math.Max(Ui.ControlHeight,ActualWidth-Ui.S);
        double cardMinimum=Ui.Metric("Width.SourceCardMinimum");
        int count=Math.Max(1,(int)Math.Floor(available/(cardMinimum+Ui.S)));
        double width=Math.Max(Ui.ControlHeight,available/count-Ui.S);
        foreach(var card in current.Values)
        {
            card.Button.Width=width;
            card.Preview.Height=Math.Max(1,width-Ui.S*2-2)*9/16;
        }
    }

    private async Task LoadPreviewAsync(SourceCard card, CancellationToken token)
    {
        bool acquired = false;
        try
        {
            await previews.WaitAsync(token); acquired = true;
            var image = await Task.Run(() => previewProvider(card.Source), token);
            if (disposed || token.IsCancellationRequested || !current.TryGetValue(card.Source.Id, out var shown) || shown != card) return;
            card.Image.Source = image; card.State.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!disposed) { card.State.Text = "Превью недоступно"; card.State.ToolTip = ex.Message; }
            // A protected or accelerated window can deny GDI preview while still
            // supporting Graphics Capture. Keep it selectable for that path.
        }
        finally { if (acquired) previews.Release(); }
    }
    private static BitmapSource CapturePreview(CaptureSource source)
    {
        var info=ScreenShareService.Describe(source,180,15,2_000_000);
        int width=info.Width,height=info.Height;
        using var capture = new GdiScreenCapture(source,info);
        var pixels = new byte[width * height * 4];
        if (capture.Read(pixels) != CaptureState.Active) throw new InvalidOperationException("Источник свёрнут или недоступен.");
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4); bitmap.Freeze(); return bitmap;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; refreshTimer.Stop(); lifetime.Cancel(); lifetime.Dispose();
        foreach (var card in current.Values) card.Image.Source = null;
        current.Clear(); cards.Children.Clear();
        // Pending workers release the semaphore themselves; it is deliberately
        // not disposed while native preview work can still be finishing.
    }
}
