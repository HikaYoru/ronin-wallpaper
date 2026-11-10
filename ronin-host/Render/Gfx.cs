using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using D2DAlpha = Vortice.DCommon.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace RoninHost.Render;

/// <summary>
/// One GPU's Direct3D 11 device and Direct2D device context. Every monitor that GPU drives shares it
/// (one render thread per GPU, see <see cref="GpuWorker"/>), instead of one device per monitor.
/// </summary>
internal sealed class GfxDevice : IDisposable
{
    public readonly ID3D11Device D3D;
    public readonly IDXGIDevice Dxgi;
    public readonly IDXGIFactory2 DxgiFactory;
    public readonly ID2D1Factory1 Factory;
    public readonly ID2D1Device D2DDevice;
    public readonly ID2D1DeviceContext DC;
    public readonly string AdapterName;
    public readonly long Luid;

    public GfxDevice(long luid)
    {
        DxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        IDXGIAdapter1? adapter = null;
        AdapterName = "default";
        for (uint i = 0; DxgiFactory.EnumAdapters1(i, out var a).Success; i++)
        {
            if (LuidOf(a) == luid) { adapter = a; AdapterName = a.Description1.Description; break; }
            a.Dispose();
        }
        Luid = luid;
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        D3D11.D3D11CreateDevice(adapter, adapter == null ? DriverType.Hardware : DriverType.Unknown,
            DeviceCreationFlags.BgraSupport, levels, out var d3d).CheckError();
        D3D = d3d!;
        adapter?.Dispose();

        Dxgi = D3D.QueryInterface<IDXGIDevice>();
        // single-threaded: only this GPU's render thread ever uses it
        Factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
        D2DDevice = Factory.CreateDevice(Dxgi);
        DC = D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        DC.UnitMode = UnitMode.Pixels;
    }

    static long LuidOf(IDXGIAdapter1 a)
    {
        var l = a.Description1.Luid;
        return ((long)l.HighPart << 32) | l.LowPart;
    }

    /// <summary>The GPU whose output shows the given monitor (avoids cross-GPU copies on multi-GPU PCs). 0 = not found.</summary>
    public static long LuidFor(Rectangle monitor, out string name)
    {
        using var f = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        var c = new System.Drawing.Point(monitor.X + monitor.Width / 2, monitor.Y + monitor.Height / 2);
        for (uint i = 0; f.EnumAdapters1(i, out var adapter).Success; i++)
        {
            using (adapter)
                for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
                {
                    var r = output.Description.DesktopCoordinates;
                    output.Dispose();
                    if (c.X >= r.Left && c.X < r.Right && c.Y >= r.Top && c.Y < r.Bottom)
                    {
                        name = adapter.Description1.Description;
                        return LuidOf(adapter);
                    }
                }
        }
        name = "default";
        return 0;
    }

    public void Dispose()
    {
        DC.Target = null;
        DC.Dispose();
        D2DDevice.Dispose();
        Factory.Dispose();
        Dxgi.Dispose();
        D3D.Dispose();
        DxgiFactory.Dispose();
    }
}

/// <summary>
/// One wallpaper window's drawing surface: a DirectComposition swap chain on the window, made from its GPU's
/// shared <see cref="GfxDevice"/>. DirectComposition is used (instead of a plain window swap chain) because on
/// Windows 11 24H2 the window lives inside Explorer's Progman, which has no GDI redirection surface.
/// </summary>
internal sealed class Gfx : IDisposable
{
    public readonly GfxDevice Device;
    public ID2D1DeviceContext DC => Device.DC;
    public ID2D1Factory1 Factory => Device.Factory;
    public string AdapterName => Device.AdapterName;
    public readonly IDXGISwapChain1 Swap;
    public readonly ID2D1Bitmap1 BackBuffer;
    public readonly int Width, Height;

    readonly IDCompositionDevice comp;
    readonly IDCompositionTarget target;
    readonly IDCompositionVisual visual;

    public static readonly D2DPixelFormat Premul = new(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied);

    // DirectWrite / WIC are process-wide and thread-safe; Lazy makes sure every render thread gets the same
    // instance (one factory = one set of loaded fonts). Isolated: reads our own font files directly instead of
    // via the Windows font cache service, which can't open files inside the user's profile folders.
    static readonly Lazy<IDWriteFactory5> dw = new(() => DWrite.DWriteCreateFactory<IDWriteFactory5>(Vortice.DirectWrite.FactoryType.Isolated));
    static readonly Lazy<IWICImagingFactory> wic = new(() => new IWICImagingFactory());
    public static IDWriteFactory5 DW => dw.Value;
    public static IWICImagingFactory Wic => wic.Value;

    public Gfx(GfxDevice device, IntPtr hwnd, Rectangle monitor)
    {
        Device = device;
        Width = monitor.Width;
        Height = monitor.Height;

        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        };
        Swap = device.DxgiFactory.CreateSwapChainForComposition(device.D3D, desc, null);

        comp = DComp.DCompositionCreateDevice<IDCompositionDevice>(device.Dxgi);
        comp.CreateTargetForHwnd(hwnd, true, out target).CheckError();
        visual = comp.CreateVisual();
        visual.SetContent(Swap).CheckError();
        target.SetRoot(visual).CheckError();
        comp.Commit().CheckError();

        using var surface = Swap.GetBuffer<IDXGISurface>(0);
        BackBuffer = DC.CreateBitmapFromDxgiSurface(surface,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Ignore), 96, 96,
                BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    public ID2D1Bitmap1 CreateTargetBitmap(int w, int h) =>
        DC.CreateBitmap(new SizeI(Math.Max(1, w), Math.Max(1, h)), IntPtr.Zero, 0,
            new BitmapProperties1(Premul, 96, 96, BitmapOptions.Target));

    public ID2D1Bitmap1 CreateBitmap(int w, int h, IntPtr data, int pitch) =>
        DC.CreateBitmap(new SizeI(w, h), data, (uint)pitch, new BitmapProperties1(Premul, 96, 96, BitmapOptions.None));

    /// <summary>Decode an image file (jpg/png/webp/gif first frame) into a GPU bitmap.</summary>
    public ID2D1Bitmap1 LoadImage(string path)
    {
        using var decoder = Wic.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        using var conv = Wic.CreateFormatConverter();
        conv.Initialize(frame, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom).CheckError();
        return DC.CreateBitmapFromWicBitmap(conv, new BitmapProperties1(Premul, 96, 96, BitmapOptions.None));
    }

    /// <summary>Diagnostics: save the frame just drawn (before Present) as a PNG.</summary>
    public void SaveBackBuffer(string path)
    {
        using var cpu = DC.CreateBitmap(new SizeI(Width, Height), IntPtr.Zero, 0,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Ignore), 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        cpu.CopyFromBitmap(BackBuffer);
        var map = cpu.Map(MapOptions.Read);
        try
        {
            using var bmp = new System.Drawing.Bitmap(Width, Height, (int)map.Pitch, System.Drawing.Imaging.PixelFormat.Format32bppRgb, map.Bits);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally { cpu.Unmap(); }
    }

    /// <summary>Show the frame. Not synced to vblank: several monitors share one thread, which paces itself.</summary>
    public void Present() => Swap.Present(0, PresentFlags.None).CheckError();

    public void Dispose()
    {
        DC.Target = null;
        BackBuffer.Dispose();
        visual.Dispose();
        target.Dispose();
        comp.Dispose();
        Swap.Dispose();
    }
}
