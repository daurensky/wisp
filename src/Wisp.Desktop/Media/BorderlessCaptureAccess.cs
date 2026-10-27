using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Wisp.Desktop.Media;

public static class BorderlessCaptureAccess
{
    // Requested only after the user chooses to start a share. An unpackaged
    // portable build or system policy may deny it; the border-free fallback is GDI.
    public static async Task<bool> RequestAsync()
    {
        if (!ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess") ||
            !ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired")) return false;
        try { return await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless) == AppCapabilityAccessStatus.Allowed; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotSupportedException or UnauthorizedAccessException) { return false; }
    }
}
