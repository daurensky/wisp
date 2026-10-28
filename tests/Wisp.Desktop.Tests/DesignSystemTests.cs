using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisp.Desktop;
using Xunit;
namespace Wisp.Desktop.Tests;

public sealed class DesignSystemTests
{
    [Fact]
    public void PaletteResourcesHaveReadableContrastAndRectangularCaptionTemplates()
    {
        // Resource dictionaries can be inspected without creating another WPF
        // Application, which would conflict with the existing UI integration test.
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                foreach(var name in new[]{"Dark","Light"})
                {
                    var palette=new ResourceDictionary{Source=new Uri("pack://application:,,,/Wisp;component/Design/Palette."+name+".xaml")};
                    Color Get(string key)=>Assert.IsType<SolidColorBrush>(palette[key]).Color;
                    Assert.True(Contrast(Get("TextPrimary"),Get("ChatSurface"))>=7,"Main chat text must retain high contrast.");
                    Assert.True(Contrast(Get("TextSecondary"),Get("Surface"))>=4.5,"Secondary text must remain readable in both themes.");
                    if(name=="Dark")
                    {
                        Assert.Equal(Color.FromRgb(16,19,25),Get("Background"));
                        Assert.Equal(Color.FromRgb(139,92,246),Get("Accent"));
                        Assert.Equal(Color.FromRgb(33,133,255),Get("AccentBlue"));
                    }
                }
                var theme=new ResourceDictionary{Source=new Uri("pack://application:,,,/Wisp;component/Theme.xaml")};
                var style=Assert.IsType<Style>(theme["Button.Caption"]);
                var template=Assert.IsType<ControlTemplate>(style.Setters.OfType<Setter>().Single(s=>s.Property==Control.TemplateProperty).Value);
                var border=Assert.IsType<Border>(template.LoadContent());
                Assert.Equal(new CornerRadius(0),border.CornerRadius);
                Assert.Equal(72d,(double)theme["Width.Rail"]);Assert.Equal(252d,(double)theme["Width.Channels"]);Assert.Equal(220d,(double)theme["Width.Members"]);
                Assert.Equal(1050d,(double)theme["Width.CompactBreakpoint"]);Assert.Equal(1200d,(double)theme["Width.CallBreakpoint"]);
            }
            catch(Exception ex){failure=ex;}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static double Contrast(Color a,Color b)
    {
        static double Component(byte c){double v=c/255d;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}
        static double L(Color c)=>.2126*Component(c.R)+.7152*Component(c.G)+.0722*Component(c.B);
        var x=L(a);var y=L(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);
    }
}
