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
    public static byte[]? Capture(IntPtr hwnd) => Capture(hwnd, out _);

    /// <summary>Same, plus a coarse hash of what is on the window (small grey grid), used to tell one state from another.</summary>
    public static byte[]? Capture(IntPtr hwnd, out string hash)
    {
        hash = "";
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
            hash = Core.Screens.ShotStore.CoarseHash(GreyGrid(bmp, 96, 54));
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex) { Core.Log.Warn("window capture failed: " + ex.Message); return null; }
    }

    /// <summary>The picture reduced to a small grey grid (average of each cell), for the state hash.</summary>
    private static byte[] GreyGrid(Bitmap bmp, int cols, int rows)
    {
        var grid = new byte[cols * rows];
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bytes = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (var gy = 0; gy < rows; gy++)
                for (var gx = 0; gx < cols; gx++)
                {
                    int x0 = gx * bmp.Width / cols, x1 = Math.Max(x0 + 1, (gx + 1) * bmp.Width / cols);
                    int y0 = gy * bmp.Height / rows, y1 = Math.Max(y0 + 1, (gy + 1) * bmp.Height / rows);
                    long sum = 0; var n = 0;
                    for (var y = y0; y < y1; y += 2)
                        for (var x = x0; x < x1; x += 2)
                        {
                            var i = y * stride + x * 4;
                            sum += (bytes[i] + bytes[i + 1] + bytes[i + 2]) / 3; n++;
                        }
                    grid[gy * cols + gx] = (byte)(n == 0 ? 0 : sum / n);
                }
        }
        finally { bmp.UnlockBits(data); }
        return grid;
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
