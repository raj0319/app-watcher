using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AppWatcher.Native;

/// <summary>
/// One picture of ONE window of the watched app (never the desktop, never other programs), taken the first
/// time a screen structure is seen, so every screen record has a matching image for comparison. PrintWindow
/// asks the window to paint itself into our bitmap, so overlapping windows of other programs are not in it.
/// </summary>
internal static class WindowCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    private const uint PW_RENDERFULLCONTENT = 2;

    /// <summary>Returns the PNG bytes of the window, or null when it cannot be drawn (minimized, gone, too small).</summary>
    public static byte[]? Capture(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd) || IsIconic(hwnd)) return null;
            if (!GetWindowRect(hwnd, out var r)) return null;
            var w = r.Right - r.Left; var h = r.Bottom - r.Top;
            if (w < 40 || h < 30 || w > 8000 || h > 8000) return null;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try
                {
                    if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)) PrintWindow(hwnd, hdc, 0);
                }
                finally { g.ReleaseHdc(hdc); }
            }
            if (IsBlank(bmp)) return null;
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex) { Core.Log.Warn("window capture failed: " + ex.Message); return null; }
    }

    /// <summary>A window that painted nothing (all one colour) is not worth keeping.</summary>
    private static bool IsBlank(Bitmap bmp)
    {
        var first = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        var steps = 0; var same = 0;
        for (var y = 0; y < bmp.Height; y += Math.Max(1, bmp.Height / 12))
            for (var x = 0; x < bmp.Width; x += Math.Max(1, bmp.Width / 12))
            { steps++; if (bmp.GetPixel(x, y) == first) same++; }
        return steps > 0 && same == steps;
    }
}
