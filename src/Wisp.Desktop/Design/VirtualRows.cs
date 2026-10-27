using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wisp.Desktop;

/// <summary>Data items contain factories, not prebuilt visual trees or decoded media.</summary>
public sealed record UiRow(string Key, Func<FrameworkElement> Create, bool Animate = false)
{
    internal bool Presented;
}

public sealed class VirtualRows : ListBox
{
    private readonly ObservableCollection<UiRow> rows = new();
    private bool scrollQueued;
    public int Count => rows.Count;
    public IEnumerable<string> Keys => rows.Select(row => row.Key);
    public VirtualRows()
    {
        // WPF implicit styles do not apply to derived controls.
        Ui.Style(this, "List.Virtual");
        ItemsSource = rows;
        var presenter = new FrameworkElementFactory(typeof(RowPresenter));
        presenter.SetBinding(FrameworkElement.DataContextProperty, new Binding());
        ItemTemplate = new DataTemplate { VisualTree = presenter };
        SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(this, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }
    public void Add(string key, Func<FrameworkElement> create, bool animate = false) => rows.Add(new UiRow(key, create, animate));
    public void Clear() => rows.Clear();
    public void RemoveFirst() { if (rows.Count > 0) rows.RemoveAt(0); }
    public void ScrollToEnd()
    {
        if (scrollQueued) return;
        scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            scrollQueued = false;
            if (rows.Count > 0) ScrollIntoView(rows[^1]);
        }));
    }
    public sealed class RowPresenter : ContentControl
    {
        public RowPresenter()
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            DataContextChanged += (_, args) =>
            {
                Motion.Stop(this);
                Content = args.NewValue is UiRow row ? row.Create() : null;
            };
            Loaded += (_, _) =>
            {
                if (DataContext is not UiRow row || row.Presented) return;
                row.Presented = true;
                if (row.Animate && !Scrolling(this)) Motion.Enter(this);
            };
            Unloaded += (_, _) => Motion.Stop(this);
        }
        private static bool Scrolling(DependencyObject element)
        {
            for (var ancestor = VisualTreeHelper.GetParent(element); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is ScrollViewer scroll) return ScrollChrome.GetIsScrolling(scroll);
            return false;
        }
    }
}
