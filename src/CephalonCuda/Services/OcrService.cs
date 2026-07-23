using System.Runtime.InteropServices.WindowsRuntime;
using CephalonCuda.Interop;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace CephalonCuda.Services;

public sealed record OcrWord(string Text, double X, double Y, double W, double H)
{
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
}

/// <summary>
/// OCR over captured frames. Default backend is the built-in Windows OCR engine (no downloads,
/// per-word bounding boxes). If the user drops tesseract53.dll + tessdata into ./tesseract/,
/// it is used as a secondary plain-text backend.
/// </summary>
public sealed class OcrService
{
    private OcrEngine? _engine;
    private bool _engineInitTried;

    public bool TesseractAvailable => TesseractNative.Available;

    private OcrEngine? Engine
    {
        get
        {
            if (!_engineInitTried)
            {
                _engineInitTried = true;
                _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                          ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            }
            return _engine;
        }
    }

    /// <summary>Words with pixel-space bounding boxes (Windows OCR only — needed for card clustering).</summary>
    public async Task<List<OcrWord>> RecognizeWordsAsync(CapturedFrame frame)
    {
        var engine = Engine;
        if (engine is null) return [];

        var maxDim = (int)OcrEngine.MaxImageDimension;
        if (frame.Width > maxDim || frame.Height > maxDim) return [];

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            frame.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        var words = new List<OcrWord>();
        foreach (var line in result.Lines)
            foreach (var word in line.Words)
                words.Add(new OcrWord(word.Text,
                    word.BoundingRect.X, word.BoundingRect.Y,
                    word.BoundingRect.Width, word.BoundingRect.Height));
        return words;
    }

    /// <summary>Plain text: Tesseract when installed, otherwise Windows OCR lines joined.</summary>
    public async Task<string> RecognizeTextAsync(CapturedFrame frame)
    {
        if (TesseractAvailable)
        {
            var text = TesseractNative.Recognize(frame.Pixels, frame.Width, frame.Height, frame.Stride);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        var words = await RecognizeWordsAsync(frame);
        return string.Join("\n",
            words.GroupBy(w => (int)(w.CenterY / 24))
                 .OrderBy(g => g.Key)
                 .Select(g => string.Join(" ", g.OrderBy(w => w.X).Select(w => w.Text))));
    }
}
