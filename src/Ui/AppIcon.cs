using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SysWidge.Interop;

namespace SysWidge.Ui;

/// <summary>
/// Builds the tray icon in code (no .ico asset to ship): a minimal three-bar telemetry
/// glyph in the accent color, rounded, on a transparent background so it reads on both
/// light and dark trays.
/// </summary>
internal static class AppIcon
{
    public static Icon CreateTrayIcon(Color accent)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Three ascending bars, like a tiny activity graph.
            int[] heights = { 13, 21, 29 };
            const int barW = 6;
            const int gap = 4;
            const int baseY = 30;
            int totalW = heights.Length * barW + (heights.Length - 1) * gap;
            int x = (size - totalW) / 2;

            using var brush = new SolidBrush(accent);
            foreach (int h in heights)
            {
                using var path = RoundedBar(x, baseY - h, barW, h, 2);
                g.FillPath(brush, path);
                x += barW + gap;
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            // Clone into a managed icon we own; the raw HICON is then safe to destroy.
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath RoundedBar(int x, int y, int w, int h, int r)
    {
        int d = r * 2;
        var path = new GraphicsPath();
        // Round only the top corners; flat base.
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddLine(x + w, y + r, x + w, y + h);
        path.AddLine(x, y + h, x, y + r);
        path.CloseFigure();
        return path;
    }
}
