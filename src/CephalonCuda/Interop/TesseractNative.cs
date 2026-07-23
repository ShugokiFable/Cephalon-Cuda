using System.IO;
using System.Runtime.InteropServices;

namespace CephalonCuda.Interop;

/// <summary>
/// Optional Tesseract OCR backend via direct P/Invoke to a user-supplied tesseract53.dll.
/// Drop tesseract53.dll (+ leptonica deps) into ./tesseract/ next to the exe along with
/// ./tesseract/tessdata/eng.traineddata. When absent, the app uses the built-in Windows OCR engine.
/// </summary>
internal static partial class TesseractNative
{
    private const string Dll = "tesseract53";
    private static IntPtr _lib = IntPtr.Zero;

    public static string BaseDir => Path.Combine(AppContext.BaseDirectory, "tesseract");
    public static string TessDataDir => Path.Combine(BaseDir, "tessdata");

    public static bool Available
    {
        get
        {
            EnsureResolver();
            return _lib != IntPtr.Zero && Directory.Exists(TessDataDir);
        }
    }

    private static bool _resolverSet;

    private static void EnsureResolver()
    {
        if (_resolverSet) return;
        _resolverSet = true;
        var candidate = Path.Combine(BaseDir, "tesseract53.dll");
        if (File.Exists(candidate))
            NativeLibrary.TryLoad(candidate, out _lib);

        NativeLibrary.SetDllImportResolver(typeof(TesseractNative).Assembly, (name, _, _) =>
            name == Dll && _lib != IntPtr.Zero ? _lib : IntPtr.Zero);
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr TessBaseAPICreate();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int TessBaseAPIInit3(IntPtr handle, string datapath, string language);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void TessBaseAPISetImage(IntPtr handle, IntPtr imagedata, int width, int height, int bytesPerPixel, int bytesPerLine);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr TessBaseAPIGetUTF8Text(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void TessBaseAPIEnd(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void TessBaseAPIDelete(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void TessDeleteText(IntPtr text);

    /// <summary>Run OCR over a BGRA buffer. Returns null when the native dll is unavailable or init fails.</summary>
    public static string? Recognize(byte[] bgra, int width, int height, int stride)
    {
        if (!Available) return null;

        var handle = TessBaseAPICreate();
        if (handle == IntPtr.Zero) return null;
        var pinned = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            if (TessBaseAPIInit3(handle, TessDataDir, "eng") != 0) return null;
            TessBaseAPISetImage(handle, pinned.AddrOfPinnedObject(), width, height, 4, stride);
            var textPtr = TessBaseAPIGetUTF8Text(handle);
            if (textPtr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(textPtr); }
            finally { TessDeleteText(textPtr); }
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        finally
        {
            pinned.Free();
            TessBaseAPIEnd(handle);
            TessBaseAPIDelete(handle);
        }
    }
}
