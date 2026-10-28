using System.Text.RegularExpressions;
namespace Wisp.Desktop;

internal static class DiagnosticLog
{
    private static readonly object gate=new();
    public static void WriteStartup()
    {
        Append("startup",BrandAscii.Art);
        try{Console.WriteLine(BrandAscii.Art);}catch(IOException){}
    }
    public static void Write(string source,Exception exception)=>Append(source,exception.ToString());
    private static void Append(string source,string text)
    {
        try
        {
            lock(gate)
            {
                var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Wisp","logs");Directory.CreateDirectory(root);
                var path=Path.Combine(root,"diagnostics.log");
                if(File.Exists(path)&&new FileInfo(path).Length>1024*1024)File.Move(path,path+".previous",true);
                // Account/invitation tokens never belong in diagnostics.
                var detail=Regex.Replace(text,@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])","[token]");
                File.AppendAllText(path,DateTimeOffset.UtcNow.ToString("O")+" "+source+Environment.NewLine+detail+Environment.NewLine);
            }
        }
        catch { /* A logging failure must never prevent shutdown/recovery. */ }
    }
}
