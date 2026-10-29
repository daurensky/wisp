using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Wisp.Core;
using Wisp.Desktop;
using Xunit;
namespace Wisp.Desktop.Tests;

[CollectionDefinition("Tray resources", DisableParallelization = true)]
public sealed class TrayResourceCollection { }

[Collection("Tray resources")]
public sealed class TrayIndicatorTests
{
    [Fact]
    public void EveryStateContainsFourReadableDpiSizes()
    {
        foreach (var state in Enum.GetValues<VoiceTrayState>().Where(s => s != VoiceTrayState.Disconnected))
        {
            using var bytes = new MemoryStream(VoiceTrayIcons.Encode(state));
            using var reader = new BinaryReader(bytes);
            Assert.Equal(0, reader.ReadUInt16()); Assert.Equal(1, reader.ReadUInt16()); Assert.Equal(4, reader.ReadUInt16());
            foreach (int size in VoiceTrayIcons.Sizes)
            {
                Assert.Equal(size, reader.ReadByte()); Assert.Equal(size, reader.ReadByte());
                bytes.Position += 2; Assert.Equal(1, reader.ReadUInt16()); Assert.Equal(32, reader.ReadUInt16());
                int length = reader.ReadInt32(), offset = reader.ReadInt32(); long next = bytes.Position;
                Assert.InRange(offset + length, 1, (int)bytes.Length);
                bytes.Position = offset; Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, reader.ReadBytes(8)); bytes.Position = next;
                using var icon = VoiceTrayIcons.Create(state, size); Assert.Equal(size, icon.Width); Assert.Equal(size, icon.Height);
            }
        }
        using var silent = VoiceTrayIcons.Render(VoiceTrayState.Silent, 32);
        using var speaking = VoiceTrayIcons.Render(VoiceTrayState.Speaking, 32);
        using var error = VoiceTrayIcons.Render(VoiceTrayState.Error, 32);
        Assert.True(Lightness(speaking) > Lightness(silent) + 20);
        Assert.Equal(0, silent.GetPixel(0, 0).A);
        Assert.True(error.GetPixel(16, 16).R > error.GetPixel(16, 16).B + 50);
    }

    [Fact]
    public void RepeatedSpeechFramesDoNotUpdateNotifyIconAndDisposalReleasesImages()
    {
        using var tray = new System.Windows.Forms.NotifyIcon();
        var indicator = new TrayIndicator(tray);
        var standard = tray.Icon;
        Assert.False(indicator.Update(false, false, false, false, false));
        Assert.True(indicator.Update(true, false, false, false, true));
        var speaking = tray.Icon;
        for (int i = 0; i < 1000; i++) Assert.False(indicator.Update(true, false, false, false, true));
        Assert.Same(speaking, tray.Icon);
        Assert.True(indicator.Update(true, false, true, true, true)); Assert.Equal(VoiceTrayState.Deafened, indicator.State);
        Assert.True(indicator.Update(false, true, true, true, false)); Assert.Equal(VoiceTrayState.Error, indicator.State);
        Assert.True(indicator.Update(false, false, true, true, false)); Assert.Same(standard, tray.Icon);
        indicator.Dispose(); indicator.Dispose(); Assert.Null(tray.Icon);
        Assert.Throws<ObjectDisposedException>(() => indicator.Update(true, false, false, false, false));
    }

    [Fact]
    public void RepeatedTrayLifetimesDoNotAccumulateGdiHandles()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using (var warmTray = new System.Windows.Forms.NotifyIcon())
        using (var warmup = new TrayIndicator(warmTray)) { }
        int before = GetGuiResources(process.Handle, 0), beforeUser = GetGuiResources(process.Handle, 1);
        for (int i = 0; i < 25; i++)
        {
            using var tray = new System.Windows.Forms.NotifyIcon();
            using var indicator = new TrayIndicator(tray);
            indicator.Update(true, false, false, false, true);
        }
        Assert.InRange(GetGuiResources(process.Handle, 0), 0, before + 4);
        Assert.InRange(GetGuiResources(process.Handle, 1), 0, beforeUser + 4);
    }

    private static double Lightness(Bitmap bitmap)
    {
        double total = 0; int count = 0;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y); if (pixel.A < 200) continue;
            total += .2126 * pixel.R + .7152 * pixel.G + .0722 * pixel.B; count++;
        }
        return total / count;
    }
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
}
