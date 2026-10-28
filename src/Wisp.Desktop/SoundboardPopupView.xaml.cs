using System.Windows.Controls;

namespace Wisp.Desktop;

public partial class SoundboardPopupView : UserControl
{
    public SoundboardPopupView() => InitializeComponent();
    internal Panel SoundItems => Sounds;
    internal Panel FooterItems => Footer;
    internal TextBlock StatusLabel=>Status;
}
