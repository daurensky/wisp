using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

public sealed record ScreenShareChoice(string Label,int Value);
public sealed record ScreenShareSelection(CaptureSource Source,int Height,int FramesPerSecond,int Bitrate);

public sealed class ScreenShareViewModel : INotifyPropertyChanged
{
    private CaptureSource? source;
    public IReadOnlyList<ScreenShareChoice> Resolutions { get; }=[new("720p",720),new("1080p",1080)];
    public IReadOnlyList<ScreenShareChoice> FrameRates { get; }=[new("15 FPS",15),new("30 FPS",30),new("60 FPS",60)];
    public IReadOnlyList<ScreenShareChoice> Bitrates { get; }=[new("2 Мбит/с",2_000_000),new("4 Мбит/с",4_000_000),new("6 Мбит/с",6_000_000),new("8 Мбит/с",8_000_000)];
    public int ResolutionIndex { get; set; }=1;
    public int FrameRateIndex { get; set; }=1;
    public int BitrateIndex { get; set; }=1;
    public CaptureSource? Source { get=>source;set{source=value;PropertyChanged?.Invoke(this,new(nameof(CanLaunch)));} }
    public bool CanLaunch=>source is not null;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ScreenShareSelection? Selection()=>source is null?null:new(source,Resolutions[Math.Clamp(ResolutionIndex,0,Resolutions.Count-1)].Value,FrameRates[Math.Clamp(FrameRateIndex,0,FrameRates.Count-1)].Value,Bitrates[Math.Clamp(BitrateIndex,0,Bitrates.Count-1)].Value);
}

/// <summary>Source selection and real capture settings share one native modal surface.</summary>
public partial class ScreenShareDialog : UserControl,IDisposable
{
    private readonly ScreenSourcePicker picker;
    private ModalHandle? handle;
    public ScreenShareViewModel ViewModel { get; }=new();
    public ScreenShareSelection? Result { get;private set; }
    public ScreenShareDialog(Func<IReadOnlyList<CaptureSource>>? sources=null,Func<CaptureSource,BitmapSource>? previews=null)
    {
        InitializeComponent();DataContext=ViewModel;Buttons.Label(LaunchButton,"Запустить","screen-share");
        picker=new ScreenSourcePicker(sources,previews){Height=double.NaN};
        picker.SelectionChanged+=()=>ViewModel.Source=picker.SelectedSource;SourceContent.Content=picker;
        Unloaded+=(_,_)=>Dispose();
    }
    private void LaunchClicked(object sender,RoutedEventArgs args)
    {
        Result=ViewModel.Selection();if(Result is not null)handle?.Close();
    }
    public ScreenShareSelection? ShowDialog(Window owner)
    {
        Height=Math.Clamp(owner.ActualHeight-144,300,620);
        var frame=new DispatcherFrame();
        handle=ModalHost.Open(owner,"Демонстрация экрана",this,Ui.Metric("Width.ShareDialog"));
        handle.Closed+=()=>frame.Continue=false;
        Dispatcher.PushFrame(frame);return Result;
    }
    public static ScreenShareSelection? Show(Window owner)
    {
        using var dialog=new ScreenShareDialog();return dialog.ShowDialog(owner);
    }
    public void Dispose()=>picker.Dispose();
}
