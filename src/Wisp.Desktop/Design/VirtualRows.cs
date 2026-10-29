using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wisp.Desktop;

/// <summary>Data items contain factories, not prebuilt visual trees or decoded media.</summary>
public sealed record UiRow
{
    public string Key { get; }
    public Func<FrameworkElement> Create { get; private set; }
    public bool Animate { get; private set; }
    public Action<FrameworkElement>? Update { get; private set; }
    internal bool Presented;
    internal event Action? Changed;

    public UiRow(string Key, Func<FrameworkElement> Create, bool Animate = false, Action<FrameworkElement>? Update = null)
    {
        this.Key = Key; this.Create = Create; this.Animate = Animate; this.Update = Update;
    }
    internal void RefreshFrom(UiRow fresh)
    {
        Create = fresh.Create; Animate = fresh.Animate; Update = fresh.Update;
        Changed?.Invoke();
    }
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
    /// <summary>Retains the rows for unchanged keys. Updaters keep realized controls in place;
    /// immutable views without an updater rebuild only their content, never the entire list.</summary>
    public void Reconcile(IEnumerable<UiRow> desired)
    {
        var next = desired.ToArray();
        if (next.Any(row => string.IsNullOrWhiteSpace(row.Key)) || next.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count() != next.Length)
            throw new ArgumentException("Each virtual row needs a unique nonempty key.", nameof(desired));
        var previous = rows.ToDictionary(row => row.Key, StringComparer.Ordinal);
        var keep = next.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        for (int index = rows.Count - 1; index >= 0; index--)
            if (!keep.Contains(rows[index].Key)) rows.RemoveAt(index);
        for (int index = 0; index < next.Length; index++)
        {
            var fresh = next[index];
            if (!previous.TryGetValue(fresh.Key, out var existing)) { rows.Insert(index, fresh); continue; }
            int current = rows.IndexOf(existing);
            if (current != index) rows.Move(current, index);
            existing.RefreshFrom(fresh);
        }
    }
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
        private UiRow? observed;
        private bool subscribed;
        public RowPresenter()
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            DataContextChanged += (_, args) =>
            {
                Unsubscribe(); Motion.Stop(this);
                observed = args.NewValue as UiRow;
                Content = observed?.Create();
                if (IsLoaded) Subscribe();
            };
            Loaded += (_, _) =>
            {
                Subscribe();
                if (observed?.Update is { } update && Content is FrameworkElement child) update(child);
                if (observed is not { } row || row.Presented) return;
                row.Presented = true;
                if (row.Animate && !Scrolling(this)) Motion.Enter(this);
            };
            Unloaded += (_, _) => { Unsubscribe(); Motion.Stop(this); };
        }
        private void Subscribe()
        {
            if (subscribed || observed is null) return;
            observed.Changed += RefreshContent; subscribed = true;
        }
        private void Unsubscribe()
        {
            if (subscribed && observed is not null) observed.Changed -= RefreshContent;
            subscribed = false;
        }
        private void RefreshContent()
        {
            if (observed is null) return;
            if (observed.Update is { } update && Content is FrameworkElement child) update(child);
            else Content = observed.Create();
        }
        private static bool Scrolling(DependencyObject element)
        {
            for (var ancestor = VisualTreeHelper.GetParent(element); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is ScrollViewer scroll) return ScrollChrome.GetIsScrolling(scroll);
            return false;
        }
    }
}
