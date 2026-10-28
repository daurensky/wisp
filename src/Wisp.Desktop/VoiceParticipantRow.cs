using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisp.Core;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

public sealed class VoiceParticipantRow : Grid
{
    private readonly VoiceService voice;
    private readonly string speakerId;
    private readonly bool muted, deafened;
    private readonly Border ring;
    public FrameworkElement Avatar { get; }
    public static readonly DependencyProperty IsSpeakingProperty = DependencyProperty.Register(nameof(IsSpeaking), typeof(bool), typeof(VoiceParticipantRow), new PropertyMetadata(false, Changed));
    public bool IsSpeaking { get => (bool)GetValue(IsSpeakingProperty); set => SetValue(IsSpeakingProperty, value); }
    public VoiceParticipantRow(PeerPresence presence, string? avatarPath, VoiceService voice, bool local, Action? openProfile = null, Action? saveVolume = null)
    {
        this.voice = voice; speakerId = local ? VoiceService.LocalSpeakerId : presence.Profile.Id;
        muted = presence.Muted; deafened = presence.Deafened;
        Height = Ui.RowHeight; Margin = Ui.Insets(Ui.M, 0, Ui.S, 0);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); ColumnDefinitions.Add(new ColumnDefinition()); ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Avatar = AvatarCatalog.Circle(avatarPath, Ui.Metric("Size.VoiceAvatar"), GifPlaybackMode.Hover);
        ring = new Border { Width = Ui.Metric("Size.Avatar"), Height = Ui.Metric("Size.Avatar"), Child = Avatar, CornerRadius = new CornerRadius(Ui.L), BorderThickness = new Thickness(2), Margin = Ui.Insets(0, 0, Ui.S, 0), BorderBrush = Brushes.Transparent };
        Children.Add(ring);GifPlayback.SetScope(Avatar,this);
        var name = Ui.Label(presence.Profile.Username, Ui.Caption, true); Grid.SetColumn(name, 1); Children.Add(name);
        var states = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void State(string icon, string label)
        {
            var glyph = new Icon { Kind = icon, Width = Ui.Metric("Size.InlineIcon"), Height = Ui.Metric("Size.InlineIcon"), Margin = Ui.Insets(Ui.Xs, 0, 0, 0), ToolTip = label };
            glyph.SetResourceReference(Icon.ForegroundProperty, "TextSecondary"); states.Children.Add(glyph);
        }
        if (muted || deafened) State("mic-off", "Микрофон выключен");
        if (deafened) State("headphone-off", "Звук выключен");
        Grid.SetColumn(states, 2); Children.Add(states);
        Ui.Name(this, presence.Profile.Username + (deafened ? " · звук и микрофон выключены" : muted ? " · микрофон выключен" : ""));
        if(!local)
        {
            Cursor=System.Windows.Input.Cursors.Hand;
            var menu=new ContextMenu();
            var title=new MenuItem{Header=presence.Profile.Username,IsEnabled=false};menu.Items.Add(title);
            var panel=new StackPanel{Width=180,Margin=Ui.Insets(Ui.S)};
            var label=Ui.Label("Громкость голоса · "+(voice.GetPeerVolume(presence.Profile.Id)*100).ToString("0")+"%",Ui.Caption);
            var volume=new Slider{Minimum=0,Maximum=100,Value=voice.GetPeerVolume(presence.Profile.Id)*100,TickFrequency=1,IsSnapToTickEnabled=false,Margin=Ui.Insets(0,Ui.S)};
            bool changed=false;
            volume.ValueChanged+=(_,args)=>{voice.SetPeerVolume(presence.Profile.Id,(float)(args.NewValue/100));label.Text="Громкость голоса · "+args.NewValue.ToString("0")+"%";changed=true;};
            panel.Children.Add(label);panel.Children.Add(volume);menu.Items.Add(new MenuItem{Header=panel,StaysOpenOnClick=true});
            if(openProfile is not null){var profileItem=new MenuItem{Header="Профиль"};profileItem.Click+=(_,_)=>openProfile();menu.Items.Add(profileItem);}
            menu.Closed+=(_,_)=>{if(changed){changed=false;saveVolume?.Invoke();}};
            ContextMenu=menu;
            MouseLeftButtonUp+=(_,args)=>{menu.PlacementTarget=this;menu.Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom;menu.IsOpen=true;args.Handled=true;};
        }
        Loaded += (_, _) => { voice.SpeakingChanged += OnSpeech; IsSpeaking = voice.IsSpeaking(speakerId); Refresh(); };
        Unloaded += (_, _) => { voice.SpeakingChanged -= OnSpeech; GifPlayback.SetSpeaking(Avatar, false); };
    }
    private void OnSpeech(string id, bool speaking)
    {
        if (id != speakerId || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => { if (IsLoaded) IsSpeaking = speaking; });
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs args) => ((VoiceParticipantRow)d).Refresh();
    private void Refresh()
    {
        bool speaking = IsSpeaking && !muted && !deafened;
        if (speaking) ring.SetResourceReference(Border.BorderBrushProperty, "Success"); else ring.BorderBrush = Brushes.Transparent;
        GifPlayback.SetSpeaking(Avatar, speaking);
    }
}
