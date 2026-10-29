using System.Windows;
using System.Windows.Controls;
using Wisp.Core;

namespace Wisp.Desktop;

/// <summary>A continuous channel surface with independent navigation, chat and management hit areas.</summary>
public partial class ChannelRow : UserControl
{
    public static readonly DependencyProperty IsSelectedProperty=DependencyProperty.Register(nameof(IsSelected),typeof(bool),typeof(ChannelRow),new PropertyMetadata(false));
    public static readonly DependencyProperty IsVoiceProperty=DependencyProperty.Register(nameof(IsVoice),typeof(bool),typeof(ChannelRow),new PropertyMetadata(false));
    public static readonly DependencyProperty CanManageProperty=DependencyProperty.Register(nameof(CanManage),typeof(bool),typeof(ChannelRow),new PropertyMetadata(false));
    public bool IsSelected { get=>(bool)GetValue(IsSelectedProperty);set=>SetValue(IsSelectedProperty,value); }
    public bool IsVoice { get=>(bool)GetValue(IsVoiceProperty);private set=>SetValue(IsVoiceProperty,value); }
    public bool CanManage { get=>(bool)GetValue(CanManageProperty);private set=>SetValue(CanManageProperty,value); }
    public Button MainButton=>ChannelButton;
    public Button SettingsButton=>ChannelSettingsButton;
    public Button ChatButton=>VoiceChatButton;
    public string ChannelId { get;private set; }="";
    private Action activate=()=>{},edit=()=>{},openChat=()=>{};

    public ChannelRow(Channel channel,int participantCount,bool selected,bool canManage,Action activate,Action edit,Action openChat)
    {
        InitializeComponent();
        ChannelButton.Click+=(_,_)=>this.activate();
        ChannelSettingsButton.Click+=(_,_)=>this.edit();
        VoiceChatButton.Click+=(_,_)=>this.openChat();
        Update(channel,participantCount,selected,canManage,activate,edit,openChat);
    }

    public void Update(Channel channel,int participantCount,bool selected,bool canManage,Action activate,Action edit,Action openChat)
    {
        ChannelId=channel.Id;this.activate=activate;this.edit=edit;this.openChat=openChat;
        IsVoice=channel.IsVoice;IsSelected=selected;CanManage=canManage;
        ChannelName.Text=channel.Name;ChannelIcon.Kind=channel.IsVoice?"volume-2":"hash";
        ParticipantCount.Text=channel.IsVoice?Math.Max(0,participantCount).ToString():"";
        ParticipantCount.Visibility=channel.IsVoice?Visibility.Visible:Visibility.Collapsed;
        Ui.Name(ChannelButton,channel.Name);ChannelButton.ToolTip=channel.Name;
        Ui.Name(ChannelSettingsButton,"Настройки канала · "+channel.Name);ChannelSettingsButton.ToolTip="Настройки канала · "+channel.Name;
        Ui.Name(VoiceChatButton,"Открыть текстовый чат голосового канала");VoiceChatButton.ToolTip="Открыть текстовый чат голосового канала";
    }
}
