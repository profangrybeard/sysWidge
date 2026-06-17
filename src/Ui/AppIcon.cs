using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using SysWidge.Interop;

namespace SysWidge.Ui;

/// <summary>
/// Supplies the tray icon: the mascot's helmet+plume, generated from textures/sysWidge.png
/// by tools/iconforge and embedded as tray.ico. Falls back to a code-drawn three-bar glyph
/// if the embedded resource is ever missing.
/// </summary>
internal static class AppIcon
{
    private const string TrayResource = "SysWidge.tray.ico";

    public static Icon CreateTrayIcon(Color accentFallback)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(TrayResource);
            if (s is not null)
                return new Icon(s, SystemInformation.SmallIconSize);
        }
        catch
        {
            // fall through to the drawn glyph
        }
        return DrawBars(accentFallback);
    }

    private static Icon DrawBars(Color accent)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            int[] heights = { 13, 21, 29 };
            const int barW = 6, gap = 4, baseY = 30;
            int x = (size - (heights.Length * barW + (heights.Length - 1) * gap)) / 2;

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
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddLine(x + w, y + r, x + w, y + h);
        path.AddLine(x, y + h, x, y + r);
        path.CloseFigure();
        return path;
    }
}
