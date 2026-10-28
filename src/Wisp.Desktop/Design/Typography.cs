using System.Windows;
using System.Windows.Media;

namespace Wisp.Desktop;

/// <summary>Loads only fonts shipped as application resources; no downloads or OS font installation.</summary>
public static class Typography
{
    public static bool HasOnest {get;private set;}
    public static string FamilyName=>HasOnest?"Onest":"Segoe UI";
    public static void Initialize()
    {
        FontFamily family=new("Segoe UI");HasOnest=false;
        try
        {
            var fonts=Fonts.GetFontFamilies(new Uri("pack://application:,,,/Wisp;component/Assets/Fonts/"));
            var bundled=fonts.FirstOrDefault(f=>f.FamilyNames.Values.Any(name=>name.Equals("Onest",StringComparison.OrdinalIgnoreCase)));
            if(bundled is not null){family=bundled;HasOnest=true;}
        }
        catch(Exception ex)when(ex is IOException or NotSupportedException or ArgumentException){}
        Application.Current.Resources["Font.App"]=family;
    }
}
