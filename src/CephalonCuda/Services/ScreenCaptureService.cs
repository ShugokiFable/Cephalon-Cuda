using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CephalonCuda.Services;

/// <summary>Tightly-packed BGRA pixel buffer (stride = width * 4).</summary>
public sealed record CapturedFrame(byte[] Pixels, int Width, int Height)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Zero-overhead GPU screen capture via DXGI Desktop Duplication (Vortice), with a GDI BitBlt
/// fallback for setups where duplication is unavailable. Strictly visual — anti-cheat safe.
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    private readonly object _gate = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private Rectangle _outputBounds;

    public string LastBackend { get; private set; } = "none";

    /// <summary>Capture a screen-space region. Never throws; returns null when both backends fail.</summary>
    public CapturedFrame? CaptureRegion(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0) return null;
        lock (_gate)
        {
            try
            {
                var frame = CaptureViaDxgi(region);
                if (frame is not null) { LastBackend = "DXGI"; return frame; }
            }
            catch
            {
                ResetDuplication();
            }
            try
            {
                var frame = CaptureViaGdi(region);
                if (frame is not null) LastBackend = "GDI";
                return frame;
            }
            catch { return null; }
        }
    }

    // ---- DXGI desktop duplication ------------------------------------------

    private CapturedFrame? CaptureViaDxgi(Rectangle region)
    {
        EnsureDuplication(region);
        if (_duplication is null || _device is null || _context is null) return null;

        // First Acquire after idle can time out (no dirty rects); retry briefly — the game redraws constantly.
        IDXGIResource? desktopResource = null;
        for (int attempt = 0; attempt < 4 && desktopResource is null; attempt++)
        {
            var hr = _duplication.AcquireNextFrame(120, out _, out desktopResource);
            if (hr.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) continue;
            if (hr.Failure)
            {
                ResetDuplication();
                return null;
            }
        }
        if (desktopResource is null) return null;

        try
        {
            using var screenTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            var desc = screenTexture.Description;
            if (_staging is null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
            {
                _staging?.Dispose();
                _staging = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                });
            }
            _context.CopyResource(_staging, screenTexture);

            // Crop: translate the screen-space region into this output's local coordinates.
            var local = new Rectangle(region.X - _outputBounds.X, region.Y - _outputBounds.Y, region.Width, region.Height);
            local.Intersect(new Rectangle(0, 0, (int)desc.Width, (int)desc.Height));
            if (local.Width <= 0 || local.Height <= 0) return null;

            var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = new byte[local.Width * local.Height * 4];
                for (int y = 0; y < local.Height; y++)
                {
                    var src = mapped.DataPointer + (local.Y + y) * mapped.RowPitch + local.X * 4;
                    Marshal.Copy(src, pixels, y * local.Width * 4, local.Width * 4);
                }
                return new CapturedFrame(pixels, local.Width, local.Height);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }
        finally
        {
            desktopResource.Dispose();
            try { _duplication.ReleaseFrame(); } catch { }
        }
    }

    private void EnsureDuplication(Rectangle region)
    {
        if (_duplication is not null && _outputBounds.Contains(
                region.X + region.Width / 2, region.Y + region.Height / 2))
            return;

        ResetDuplication();

        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory);
        if (factory is null) return;
        using (factory)
        {
            var center = new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
            for (int a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
            {
                using (adapter)
                {
                    for (int o = 0; adapter!.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                    {
                        using (output)
                        {
                            var rc = output!.Description.DesktopCoordinates;
                            var bounds = new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
                            if (!bounds.Contains(center) && !(a == 0 && o == 0)) continue;

                            var hr = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                                adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                null, out ID3D11Device? device);
                            if (hr.Failure || device is null) continue;

                            try
                            {
                                using var output1 = output.QueryInterface<IDXGIOutput1>();
                                var dup = output1.DuplicateOutput(device);
                                // Dispose whatever candidate (if any) is currently held before replacing it —
                                // otherwise a non-matching primary-monitor fallback leaks when a later exact
                                // match overwrites it.
                                ResetDuplication();
                                _device = device;
                                _context = device.ImmediateContext;
                                _duplication = dup;
                                _outputBounds = bounds;
                                if (bounds.Contains(center)) return; // exact match; else keep primary as fallback
                            }
                            catch
                            {
                                device.Dispose();
                            }
                        }
                    }
                }
            }
        }
    }

    private void ResetDuplication()
    {
        _duplication?.Dispose(); _duplication = null;
        _staging?.Dispose(); _staging = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    // ---- GDI fallback --------------------------------------------------------

    private static CapturedFrame? CaptureViaGdi(Rectangle region)
    {
        using var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(region.Location, Point.Empty, region.Size);

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bmp.Width * 4, bmp.Width * 4);
            return new CapturedFrame(pixels, bmp.Width, bmp.Height);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // ---- encoding helpers ------------------------------------------------------

    /// <summary>PNG-encode a frame (optionally downscaled) for the vision model. Returns base64.</summary>
    public static string ToBase64Png(CapturedFrame frame, int maxWidth = 1600)
    {
        BitmapSource source = BitmapSource.Create(frame.Width, frame.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
        if (frame.Width > maxWidth)
        {
            double scale = (double)maxWidth / frame.Width;
            source = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    public void Dispose()
    {
        lock (_gate) ResetDuplication();
    }
}
