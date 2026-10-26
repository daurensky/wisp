using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Wisp.Desktop.Media;

/// <summary>WGC with a two-frame free-threaded pool and one reusable CPU staging texture.</summary>
internal sealed class WindowsGraphicsScreenCapture : IScreenCapture
{
    private readonly CaptureSource source;
    private readonly Bitmap bitmap;
    private readonly Graphics graphics;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private IDirect3DDevice? projectedDevice;
    private GraphicsCaptureItem? item;
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? session;
    private ID3D11Texture2D? staging;
    private SizeInt32 size;
    private volatile bool closed;
    public string BackendName => "Windows Graphics Capture";

    public WindowsGraphicsScreenCapture(CaptureSource source, ScreenStreamInfo info)
    {
        this.source = source;
        bitmap = new Bitmap(info.Width, info.Height, PixelFormat.Format32bppRgb);
        graphics = Graphics.FromImage(bitmap);
        try
        {
            if (!GraphicsCaptureSession.IsSupported() || source.Id == "desktop")
                throw new NotSupportedException("Для всего рабочего стола используется GDI; WGC захватывает отдельный монитор или окно.");
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1], out device, out context).CheckError();
            using (var protection = device.QueryInterface<ID3D11Multithread>()) protection.SetMultithreadProtected(true);
            using (var dxgi = device.QueryInterface<IDXGIDevice>())
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var pointer));
                try { projectedDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pointer); }
                finally { Marshal.Release(pointer); }
            }
            item = CreateItem(source);
            size = item.Size;
            item.Closed += ItemClosed;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(projectedDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = true;
            // Keep Windows' capture border. No border suppression or extra consent API.
            session.StartCapture();
        }
        catch { Dispose(); throw; }
    }

    public CaptureState Read(byte[] pixels)
    {
        if (closed) throw new CaptureClosedException("Источник демонстрации закрыт или монитор отключён.");
        if (GdiScreenCapture.IsMinimized(source)) return CaptureState.Paused;
        var frame = pool!.TryGetNextFrame();
        if (frame is null) return CaptureState.Waiting;
        SizeInt32? resized = null;
        try
        {
            var content = frame.ContentSize;
            if (content.Width <= 0 || content.Height <= 0) return CaptureState.Waiting;
            if (content.Width != size.Width || content.Height != size.Height)
            {
                resized = content;
                return CaptureState.Waiting;
            }
            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var iid = typeof(ID3D11Texture2D).GUID;
            Marshal.ThrowExceptionForHR(access.GetInterface(ref iid, out var native));
            using var texture = new ID3D11Texture2D(native);
            var description = texture.Description;
            if (staging is null || staging.Description.Width != description.Width || staging.Description.Height != description.Height)
            {
                staging?.Dispose();
                description.Usage = ResourceUsage.Staging;
                description.BindFlags = BindFlags.None;
                description.CPUAccessFlags = CpuAccessFlags.Read;
                description.MiscFlags = ResourceOptionFlags.None;
                staging = device!.CreateTexture2D(description);
            }
            context!.CopyResource(staging, texture);
            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                using var image = new Bitmap(content.Width, content.Height, checked((int)mapped.RowPitch), PixelFormat.Format32bppRgb, mapped.DataPointer);
                double scale = Math.Min((double)bitmap.Width / content.Width, (double)bitmap.Height / content.Height);
                int width = Math.Max(1, (int)(content.Width * scale)), height = Math.Max(1, (int)(content.Height * scale));
                graphics.Clear(Color.Black);
                graphics.DrawImage(image, new Rectangle((bitmap.Width - width) / 2, (bitmap.Height - height) / 2, width, height), 0, 0, content.Width, content.Height, GraphicsUnit.Pixel);
            }
            finally { context.Unmap(staging, 0); }
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try { for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4); }
            finally { bitmap.UnlockBits(data); }
            return CaptureState.Active;
        }
        finally
        {
            frame.Dispose();
            if (resized is { } nextSize)
            {
                staging?.Dispose(); staging = null;
                size = nextSize;
                pool.Recreate(projectedDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            }
        }
    }

    private void ItemClosed(GraphicsCaptureItem sender, object args) => closed = true;
    public void Dispose()
    {
        if (item is not null) item.Closed -= ItemClosed;
        session?.Dispose(); pool?.Dispose(); staging?.Dispose();
        (projectedDevice as IDisposable)?.Dispose(); context?.Dispose(); device?.Dispose();
        graphics.Dispose(); bitmap.Dispose();
    }

    private static GraphicsCaptureItem CreateItem(CaptureSource source)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var text));
        var factoryIid = typeof(IGraphicsCaptureItemInterop).GUID;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, ref factoryIid, out var nativeFactory));
            try
            {
                var factory = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(nativeFactory);
                try
                {
                    var itemIid = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
                    IntPtr pointer;
                    if (GdiScreenCapture.TryWindow(source, out var window))
                        Marshal.ThrowExceptionForHR(factory.CreateForWindow(window, ref itemIid, out pointer));
                    else
                    {
                        var bounds = GdiScreenCapture.Resolve(source).Bounds;
                        var point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                        var monitor = MonitorFromPoint(point, 0);
                        if (monitor == IntPtr.Zero) throw new CaptureClosedException("Монитор отключён.");
                        Marshal.ThrowExceptionForHR(factory.CreateForMonitor(monitor, ref itemIid, out pointer));
                    }
                    try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(pointer); }
                    finally { Marshal.Release(pointer); }
                }
                finally { Marshal.ReleaseComObject(factory); }
            }
            finally { Marshal.Release(nativeFactory); }
        }
        finally { WindowsDeleteString(text); }
    }

    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr item);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr item);
    }
    [ComImport, Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess { [PreserveSig] int GetInterface(ref Guid iid, out IntPtr value); }
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out IntPtr text);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr text);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr className, ref Guid iid, out IntPtr factory);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
}
