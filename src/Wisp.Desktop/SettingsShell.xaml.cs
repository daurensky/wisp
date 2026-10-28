using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Wisp.Desktop;

public sealed record SettingsNavigationItem(string Key, string Label, string Icon);

/// <summary>Shared native settings layout. Pages supply their own commands and content.</summary>
public partial class SettingsShell : UserControl
{
    private bool selecting;
    public ObservableCollection<SettingsNavigationItem> Sections { get; } = new();
    public event Action<string>? SectionSelected;
    public object? PageContent { get => PageHost.Content; set { PageHost.Content = value; PageScroll.ScrollToTop(); } }
    public object? Footer { get => FooterHost.Content; set => FooterHost.Content = value; }
    public string? SelectedKey => (Navigation.SelectedItem as SettingsNavigationItem)?.Key;

    public SettingsShell()
    {
        InitializeComponent(); Navigation.ItemsSource = Sections;
        AutomationProperties.SetName(Navigation, "Разделы настроек");
    }
    public void Select(string key)
    {
        selecting = true;
        try { Navigation.SelectedItem = Sections.FirstOrDefault(section => section.Key == key); }
        finally { selecting = false; }
    }
    private void NavigationChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!selecting && Navigation.SelectedItem is SettingsNavigationItem section) SectionSelected?.Invoke(section.Key);
    }
}
