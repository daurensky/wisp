using System.IO;

namespace Wisp.Desktop.Tests;

internal static class UiCheckEvidence
{
    internal static void Write(string name, object value)
    {
        if(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")is not{Length:>0} workspace)return;
        var directory=Path.Combine(workspace,"artifacts","test-results");Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory,"ui-evidence.txt"),name+"="+value+Environment.NewLine);
    }
}
