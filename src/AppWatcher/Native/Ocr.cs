using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AppWatcher.Native;

/// <summary>
/// Reads the text on a picture with the OCR engine that ships with Windows (Windows.Media.Ocr). It runs on
/// this PC, reads nothing but the picture already taken, and needs no install. Used for apps that draw their
/// own screens (Qt, Java, old Delphi), where the control tree is empty but the window still shows text.
/// The text, with each line's box, is written next to the picture as a layer an AI or the twin kit can read.
/// </summary>
internal static class Ocr
{
    public sealed class Line { public string Text { get; set; } = ""; public int X { get; set; } public int Y { get; set; } public int W { get; set; } public int H { get; set; } }
    public sealed class Result { public string Lang { get; set; } = ""; public int Width { get; set; } public int Height { get; set; } public List<Line> Lines { get; set; } = new(); public string Text => string.Join("\n", Lines.Select(l => l.Text)); }

    private static OcrEngine? _engine;
    private static bool _tried;

    public static bool Available
    {
        get
        {
            if (!_tried)
            {
                _tried = true;
                try { _engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Language("en-US")); }
                catch (Exception ex) { Core.Log.Warn("OCR engine not available: " + ex.Message); _engine = null; }
            }
            return _engine is not null;
        }
    }

    /// <summary>Reads the text of a PNG. Returns null when OCR is not available or the picture could not be read.</summary>
    public static Result? Read(byte[] png)
    {
        if (!Available) return null;
        try
        {
            return ReadAsync(png).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Core.Log.Warn("OCR failed: " + ex.Message); return null; }
    }

    private static async Task<Result?> ReadAsync(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bmp = await decoder.GetSoftwareBitmapAsync();
        var r = await _engine!.RecognizeAsync(bmp);
        var result = new Result { Lang = _engine.RecognizerLanguage.LanguageTag, Width = (int)decoder.PixelWidth, Height = (int)decoder.PixelHeight };
        foreach (var line in r.Lines)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
            foreach (var w in line.Words)
            {
                var b = w.BoundingRect;
                minX = Math.Min(minX, b.X); minY = Math.Min(minY, b.Y);
                maxX = Math.Max(maxX, b.X + b.Width); maxY = Math.Max(maxY, b.Y + b.Height);
            }
            if (line.Words.Count == 0) continue;
            result.Lines.Add(new Line { Text = line.Text, X = (int)minX, Y = (int)minY, W = (int)(maxX - minX), H = (int)(maxY - minY) });
        }
        result.Lines.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        return result;
    }
}
