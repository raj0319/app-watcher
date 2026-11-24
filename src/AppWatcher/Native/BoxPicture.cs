using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AppWatcher.Native;

/// <summary>
/// The text a busy program's text box shows, read from the box's own picture. While the program builds a lookup list it
/// answers neither UI Automation nor WM_GETTEXT, but the characters the user typed are already drawn in the box. The picture
/// is copied from the box's own window (its device context, never the desktop), only that box, never saved, and read with
/// the OCR engine that ships with Windows. No message is sent to the program, so the copy is on time however busy it is.
/// </summary>
internal static class BoxPicture
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    private const uint SRCCOPY = 0x00CC0020, GA_ROOT = 2, GW_HWNDPREV = 3;

    /// <summary>
    /// True when a visible window above the box's own top window in the z-order overlaps the box. Only window positions are
    /// read (no message is sent to any program, so this is on time while the watched program is busy).
    /// </summary>
    private static bool Covered(IntPtr box, Rect b)
    {
        var root = GetAncestor(box, GA_ROOT);
        var n = 0;
        for (var w = GetWindow(root, GW_HWNDPREV); w != IntPtr.Zero && n < 500; w = GetWindow(w, GW_HWNDPREV), n++)
        {
            if (!IsWindowVisible(w) || !GetWindowRect(w, out var o)) continue;
            if (DwmGetWindowAttribute(w, 14 /* DWMWA_CLOAKED */, out var cloaked, 4) == 0 && cloaked != 0) continue;
            if ((GetWindowLong(w, -20) & 0x20) != 0) continue; // WS_EX_TRANSPARENT: a click-through overlay draws nothing over the box's text
            if (o.Left < b.Right && b.Left < o.Right && o.Top < b.Bottom && b.Top < o.Bottom) return true;
        }
        return n >= 500;
    }

    /// <summary>The one line of text in the box, or null when it cannot be read (no OCR, a box too big to be one field).</summary>
    public static string? Text(IntPtr box)
    {
        try
        {
            if (box == IntPtr.Zero || !Win32.IsWindow(box) || !GetWindowRect(box, out var r)) return null;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 8 || h < 8 || w > 1000 || h > 60) return null; // one line of one field, nothing bigger
            if (Covered(box, r)) return null; // the copy comes from the screen: a window over the box would be read instead
            using var shot = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot))
            {
                var dest = g.GetHdc();
                var src = GetWindowDC(box);
                try { if (src == IntPtr.Zero || !BitBlt(dest, 0, 0, w, h, src, 0, 0, SRCCOPY)) return null; }
                finally { if (src != IntPtr.Zero) ReleaseDC(box, src); g.ReleaseHdc(dest); }
            }
            // three times larger: the OCR engine reads small field text far better
            using var big = new Bitmap(w * 3, h * 3, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(big)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(shot, 0, 0, big.Width, big.Height); }
            using var ms = new MemoryStream();
            big.Save(ms, ImageFormat.Png);
            var result = Ocr.Read(ms.ToArray());
            return result is { Lines.Count: 1 } ? result.Lines[0].Text : null;
        }
        catch (Exception ex) { Core.Log.Warn("box picture read failed: " + ex.Message); return null; }
    }
}
