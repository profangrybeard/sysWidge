using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using SysWidge.Config;
using SysWidge.Hosting;
using SysWidge.Interop;
using SysWidge.Metrics;

namespace SysWidge.Ui;

/// <summary>
/// The widget: a top-level, click-through, per-pixel-alpha layered window that floats
/// over the left region of the taskbar.
///
/// Why layered (UpdateLayeredWindow) instead of normal WM_PAINT: it lets the text sit
/// directly on the taskbar with a fully transparent background and clean anti-aliased
/// edges — no opaque rectangle. We render each tick into an ARGB bitmap and push it.
///
/// Layout is a single immediate-mode pass (<see cref="RenderOrMeasure"/>) used for both
/// measuring the width and drawing — closer to a game draw loop than retained UI.
/// </summary>
public sealed class WidgetForm : Form
{
    /// <summary><paramref name="Template"/> is the widest value this slot can ever show;
    /// it fixes the slot width so changing values never reflow the layout.</summary>
    private readonly record struct Segment(string Label, string Value, string Template, Color Color);

    private readonly WidgetConfig _config;
    private readonly MetricsSampler _sampler = new();
    private readonly System.Windows.Forms.Timer _sampleTimer = new();
    private readonly System.Windows.Forms.Timer _dockTimer = new();
    private readonly NotifyIcon _tray;

    private TaskbarInfo? _taskbar;
    private uint _lastDpi;
    private float _dpiScale = 1f;
    private bool _hiddenForFullscreen;

    // Current on-screen rect (physical pixels), recomputed when docking.
    private int _x, _y, _w = 220, _h = 48;

    private List<Segment> _segments = new();
    private Bitmap? _measureBmp;
    private Graphics? _measure;
    private Font _valueFont = null!;
    private Font _labelFont = null!;

    private Color _labelColor;
    private Color _accentColor;

    private static readonly StringFormat TightFormat = CreateTightFormat();

    public WidgetForm(WidgetConfig config)
    {
        _config = config;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; // we DPI-scale by hand for pixel control
        TopMost = true;                     // float above the taskbar's own layer
        Text = "SysWidge";

        _labelColor = ParseColor(_config.LabelColorHex, Color.Gray);
        _accentColor = ParseColor(_config.AccentColorHex, Color.DeepSkyBlue);

        RebuildFonts(96);

        _sampleTimer.Interval = Math.Max(250, _config.RefreshMs);
        _sampleTimer.Tick += (_, _) => OnSample();

        _dockTimer.Interval = 1000;
        _dockTimer.Tick += (_, _) => EnsureDocked();

        _tray = BuildTray();
    }

    /// <summary>
    /// Layered + click-through + tool window (no taskbar button / not in alt-tab) that
    /// never steals focus. WS_EX_TRANSPARENT makes the whole widget click-through, so the
    /// taskbar (and Widgets button) underneath stays fully interactive.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(NativeMethods.WS_EX_TOOLWINDOW
                              | NativeMethods.WS_EX_NOACTIVATE
                              | NativeMethods.WS_EX_LAYERED
                              | NativeMethods.WS_EX_TRANSPARENT);
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // A throwaway 96-DPI surface for width measurement, so measuring matches the
        // 96-DPI bitmap we draw into.
        _measureBmp = new Bitmap(1, 1);
        _measure = Graphics.FromImage(_measureBmp);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        EnsureDocked();
        OnSample();
        _sampleTimer.Start();
        _dockTimer.Start();
    }

    // A layered window paints via UpdateLayeredWindow, not WM_PAINT.
    protected override void OnPaint(PaintEventArgs e) { /* intentionally empty */ }
    protected override void OnPaintBackground(PaintEventArgs e) { /* intentionally empty */ }

    // ----------------------------------------------------------- docking

    /// <summary>
    /// Find the taskbar and float over its left region. On Windows 11 a true taskbar
    /// child gets composited *behind* the taskbar's XAML layer, so instead we keep a
    /// top-level top-most window pinned over that region and re-assert it each tick.
    /// Hides itself while a fullscreen app is foreground (like the taskbar does).
    /// </summary>
    private void EnsureDocked()
    {
        try
        {
            var tb = TaskbarLocator.FindPrimaryTaskbar();
            if (tb is null)
                return; // taskbar momentarily gone (e.g. explorer restarting) — retry next tick

            _taskbar = tb;

            if (tb.Dpi != _lastDpi)
            {
                _lastDpi = tb.Dpi;
                RebuildFonts(tb.Dpi);
            }

            if (IsFullscreenAppForeground())
            {
                if (!_hiddenForFullscreen) { _hiddenForFullscreen = true; Visible = false; }
                return;
            }

            if (_hiddenForFullscreen) { _hiddenForFullscreen = false; Visible = true; }

            Relayout();
        }
        catch
        {
            // Never let a docking hiccup take down the message loop.
        }
    }

    private void Relayout()
    {
        if (_taskbar is not { IsValid: true } tb) return;

        _h = tb.Rect.Height;
        _x = tb.Rect.Left + (int)(_config.LeftOffsetPx * _dpiScale);
        _y = tb.Rect.Top;
        // _w is set by the most recent measure in OnSample.

        // Keep ourselves above the taskbar's own layer; position/size come from the
        // layered-window push below.
        NativeMethods.SetWindowPos(
            Handle, NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        RenderLayered();
    }

    /// <summary>
    /// True when the foreground window covers its entire monitor and isn't the shell
    /// (desktop / taskbar). Mirrors how Explorer hides the taskbar for fullscreen apps.
    /// </summary>
    private static bool IsFullscreenAppForeground()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        var sb = new System.Text.StringBuilder(64);
        NativeMethods.GetClassName(fg, sb, sb.Capacity);
        string cls = sb.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        if (!NativeMethods.GetWindowRect(fg, out var wr)) return false;

        IntPtr mon = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return false;

        var m = mi.rcMonitor;
        return wr.Left <= m.Left && wr.Top <= m.Top && wr.Right >= m.Right && wr.Bottom >= m.Bottom;
    }

    // ----------------------------------------------------------- sampling

    private void OnSample()
    {
        var snapshot = _sampler.Sample();
        _segments = BuildSegments(snapshot);
        _w = RenderOrMeasure(null);
        if (!_hiddenForFullscreen) Relayout();
    }

    private List<Segment> BuildSegments(MetricsSnapshot s)
    {
        var list = new List<Segment>
        {
            new("CPU", $"{s.CpuPercent:0}%", "100%", LoadColor(s.CpuPercent)),
        };

        if (s.GpuLoadAvailable)
            list.Add(new Segment("GPU", $"{s.GpuPercent:0}%", "100%", LoadColor(s.GpuPercent)));

        list.Add(new Segment("MEM", $"{s.MemPercent:0}%", "100%", LoadColor(s.MemPercent)));

        if (s.GpuVramAvailable)
        {
            string total = $"{s.GpuVramTotalGb:0}";
            list.Add(new Segment("VRAM", $"{s.GpuVramUsedGb:0.0}/{total}G", $"{s.GpuVramTotalGb:0.0}/{total}G", _accentColor));
        }

        // Independent ↓ / ↑ slots so neither rate nudges the other.
        list.Add(new Segment("↓", HumanRate(s.NetDownBytesPerSec), "999.9M", _accentColor));
        list.Add(new Segment("↑", HumanRate(s.NetUpBytesPerSec), "999.9M", _accentColor));
        list.Add(new Segment("C:", $"{s.DiskFreeGb:0}G free", "9999G free", _accentColor));
        return list;
    }

    // ----------------------------------------------------------- rendering

    /// <summary>Render the current frame into an ARGB bitmap and push it via UpdateLayeredWindow.</summary>
    private void RenderLayered()
    {
        if (_w <= 0 || _h <= 0) return;

        using var bmp = new Bitmap(_w, _h, PixelFormat.Format32bppArgb);
        bmp.SetResolution(96, 96);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias; // GDI+ alpha-blended glyph edges
            RenderOrMeasure(g);
        }

        PushLayered(bmp);
    }

    private void PushLayered(Bitmap bmp)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

            var size = new NativeMethods.SIZE { cx = _w, cy = _h };
            var srcPoint = new NativeMethods.POINT { x = 0, y = 0 };
            var dstPoint = new NativeMethods.POINT { x = _x, y = _y };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };

            NativeMethods.UpdateLayeredWindow(
                Handle, screenDc, ref dstPoint, ref size,
                memDc, ref srcPoint, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            if (hBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memDc, oldBitmap);
                NativeMethods.DeleteObject(hBitmap);
            }
            NativeMethods.DeleteDC(memDc);
        }
    }

    /// <summary>
    /// Single layout pass for measuring (g == null) and drawing. Every value lives in a
    /// FIXED-WIDTH slot sized from its widest-possible template, so changing values never
    /// move their neighbors and the total width is constant. Values are right-aligned
    /// within their slot, so digits grow leftward into reserved space and suffixes hold.
    /// </summary>
    private int RenderOrMeasure(Graphics? g)
    {
        var ctx = g ?? _measure!;
        int padX = Scale(12);
        int segGap = Scale(14);
        int labelGap = Scale(4);
        int x = padX;

        for (int i = 0; i < _segments.Count; i++)
        {
            var seg = _segments[i];

            int labelW = 0;
            if (seg.Label.Length > 0)
            {
                labelW = (int)Math.Ceiling(Measure(ctx, seg.Label, _labelFont)) + labelGap;
                if (g is not null) DrawAt(g, seg.Label, _labelFont, _labelColor, x);
            }

            int slotW = (int)Math.Ceiling(Measure(ctx, seg.Template, _valueFont));
            if (g is not null)
            {
                float valueW = Measure(ctx, seg.Value, _valueFont);
                float vx = x + labelW + (slotW - valueW); // right-align within the slot
                DrawAt(g, seg.Value, _valueFont, seg.Color, vx);
            }

            x += labelW + slotW;
            if (i < _segments.Count - 1) x += segGap;
        }

        return x + padX;
    }

    private static float Measure(Graphics ctx, string text, Font font)
        => ctx.MeasureString(text, font, int.MaxValue, TightFormat).Width;

    private void DrawAt(Graphics g, string text, Font font, Color color, float x)
    {
        float th = g.MeasureString(text, font, int.MaxValue, TightFormat).Height;
        float y = (_h - th) / 2f;
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, new PointF(x, y), TightFormat);
    }

    // ----------------------------------------------------------- helpers

    private int Scale(int px) => (int)Math.Round(px * _dpiScale);

    private void RebuildFonts(uint dpi)
    {
        _dpiScale = dpi / 96f;
        _valueFont?.Dispose();
        _labelFont?.Dispose();
        _valueFont = new Font(_config.FontFamily, _config.FontSizePx * _dpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
        _labelFont = new Font(_config.FontFamily, (_config.FontSizePx - 2f) * _dpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    /// <summary>Subtle load coloring: calm under 60%, amber to ~85%, red above.</summary>
    private Color LoadColor(double pct)
    {
        if (pct >= 85) return Color.FromArgb(0xFF, 0x6B, 0x6B);
        if (pct >= 60) return Color.FromArgb(0xFF, 0xC1, 0x07);
        return Color.FromArgb(0xCF, 0xE8, 0xCF);
    }

    private static string HumanRate(double bytesPerSec)
    {
        double kb = bytesPerSec / 1024.0;
        if (kb < 1000) return $"{kb:0}K";
        double mb = kb / 1024.0;
        return mb < 100 ? $"{mb:0.0}M" : $"{mb:0}M";
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch { return fallback; }
    }

    private static StringFormat CreateTightFormat()
    {
        var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
        sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        sf.Trimming = StringTrimming.None;
        return sf;
    }

    // ----------------------------------------------------------- tray

    private NotifyIcon BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Re-dock to taskbar", null, (_, _) => { _lastDpi = 0; EnsureDocked(); });
        menu.Items.Add("Open config folder", null, (_, _) => OpenConfigFolder());

        var startup = new ToolStripMenuItem("Start with Windows")
        {
            Checked = AutoStartManager.IsEnabled(),
            CheckOnClick = true,
        };
        startup.CheckedChanged += (_, _) => AutoStartManager.SetEnabled(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        return new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "SysWidge",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    private static void OpenConfigFolder()
    {
        try
        {
            string dir = Path.GetDirectoryName(WidgetConfig.ConfigPath)!;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch
        {
            // best-effort
        }
    }

    private void ExitApp()
    {
        _sampleTimer.Stop();
        _dockTimer.Stop();
        _tray.Visible = false;
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sampleTimer.Dispose();
            _dockTimer.Dispose();
            _sampler.Dispose();
            _tray.Dispose();
            _measure?.Dispose();
            _measureBmp?.Dispose();
            _valueFont?.Dispose();
            _labelFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
