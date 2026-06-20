using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
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
    /// it fixes the slot width so changing values never reflow the layout.
    /// <paramref name="FixedSlotPx"/> (&gt;0) overrides that with a fixed device-independent
    /// width whose value is left-aligned and ellipsis-clipped (for free-text like an event).
    /// <paramref name="Tight"/> hugs this segment to the previous one with a smaller gap.</summary>
    private readonly record struct Segment(
        string Label, string Value, string Template, Color Color, int FixedSlotPx = 0, bool Tight = false);

    private WidgetConfig _config;
    private MetricsSampler _sampler;
    private readonly System.Windows.Forms.Timer _sampleTimer = new();
    private readonly System.Windows.Forms.Timer _dockTimer = new();
    private readonly TrayIcon _tray;
    private Icon? _icon;

    // Stable identity for the tray icon so Windows 11 remembers the user's "show in
    // taskbar" choice across updates (a fresh .exe would otherwise revert to overflow).
    private static readonly Guid TrayGuid = new("9F2B7A14-3C6E-4D58-AE10-5B8C2D4F6071");

    private TaskbarInfo? _taskbar;
    private uint _lastDpi;
    private float _dpiScale = 1f;
    private bool _hiddenForFullscreen;

    // Cached left-clearance (physical px past the Widgets/Search content); -1 = unknown ->
    // fall back to the static offset. Refreshed on a throttle so UIA never runs per-frame.
    private int _leftClearancePx = -1;
    private int _clearanceTicks;

    // Current on-screen rect (physical pixels), recomputed when docking.
    private int _x, _y, _w = 220, _h = 48;

    // Click target for the agenda tile (window-client px); _calHitW == 0 means no tile.
    private int _calHitX, _calHitW;
    private string _calLaunchUrl = "";

    // Agenda cycling/crossfade state.
    private const int AgendaIntervalMs = 40;
    private readonly System.Windows.Forms.Timer _agendaTimer = new();
    private MetricsSnapshot? _lastSnapshot;
    private IReadOnlyList<CalEvent> _calEvents = Array.Empty<CalEvent>();
    private string _calEventsKey = "";
    private int _calIndex;
    private float _calAlpha = 1f;
    private int _calPhaseMs;
    private bool _calTransitioning;
    private bool _calSwitched;

    private List<Segment> _segments = new();
    private Bitmap? _measureBmp;
    private Graphics? _measure;
    private Bitmap? _surface;                                  // reused render target
    private readonly SolidBrush _brush = new(Color.White);     // reused text brush
    private readonly SolidBrush _hitBrush = new(Color.FromArgb(4, 0, 0, 0));
    private int _trimCounter;
    private Font _valueFont = null!;
    private Font _labelFont = null!;

    private Color _labelColor;
    private Color _accentColor;
    private Color _calendarColor;

    private static readonly StringFormat TightFormat = CreateTightFormat();

    public WidgetForm(WidgetConfig config)
    {
        _config = config;
        _sampler = new MetricsSampler(config);
        _calLaunchUrl = config.CalendarLaunchUrl;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; // we DPI-scale by hand for pixel control
        TopMost = true;                     // float above the taskbar's own layer
        Text = "SysWidge";

        _labelColor = ParseColor(_config.LabelColorHex, Color.Gray);
        _accentColor = ParseColor(_config.AccentColorHex, Color.DeepSkyBlue);
        _calendarColor = ParseColor(_config.CalendarColorHex, Color.Goldenrod);

        RebuildFonts(96);

        _sampleTimer.Interval = Math.Max(250, _config.RefreshMs);
        _sampleTimer.Tick += (_, _) => OnSample();

        _dockTimer.Interval = 1000;
        _dockTimer.Tick += (_, _) => EnsureDocked();

        _agendaTimer.Interval = AgendaIntervalMs;
        _agendaTimer.Tick += (_, _) => OnAgendaTick();

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
            // No WS_EX_TRANSPARENT: a per-pixel-alpha layered window hit-tests by alpha, so
            // transparent gaps stay click-through while content (and the agenda tile's faint
            // hit pad) receives clicks.
            cp.ExStyle |= (int)(NativeMethods.WS_EX_TOOLWINDOW
                              | NativeMethods.WS_EX_NOACTIVATE
                              | NativeMethods.WS_EX_LAYERED);
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
        OnSample();              // populates events; SyncAgenda starts the crossfade timer if needed
        _sampleTimer.Start();
        _dockTimer.Start();
        TrimWorkingSet();        // drop the startup allocation spike
    }

    // A layered window paints via UpdateLayeredWindow, not WM_PAINT.
    protected override void OnPaint(PaintEventArgs e) { /* intentionally empty */ }
    protected override void OnPaintBackground(PaintEventArgs e) { /* intentionally empty */ }

    private bool OverAgenda(int xClient) => _calHitW > 0 && xClient >= _calHitX && xClient < _calHitX + _calHitW;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = OverAgenda(e.X) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && OverAgenda(e.X))
            OpenUrl(_calLaunchUrl);
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // no browser / bad URL — ignore
        }
    }

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
                _clearanceTicks = 0; // re-measure the taskbar at the new DPI
            }

            if (IsFullscreenAppForeground())
            {
                if (!_hiddenForFullscreen) { _hiddenForFullscreen = true; Visible = false; }
                return;
            }

            if (_hiddenForFullscreen) { _hiddenForFullscreen = false; Visible = true; }

            UpdateLeftClearance(tb);
            Relayout();
        }
        catch
        {
            // Never let a docking hiccup take down the message loop.
        }
    }

    /// <summary>
    /// Refresh the cached left-clearance on a throttle (UIA is cross-process and must not run
    /// per-frame). <see cref="_clearanceTicks"/> is reset to 0 to force an immediate re-measure
    /// on re-dock / DPI change.
    /// </summary>
    private void UpdateLeftClearance(TaskbarInfo tb)
    {
        if (!_config.AutoClearTaskbarLeft) return;
        if (_clearanceTicks-- > 0) return;

        _leftClearancePx = TaskbarLeftProbe.Measure(tb.Handle, tb.Rect);
        _clearanceTicks = 5; // ~every 5s on the 1s dock timer
    }

    private void Relayout()
    {
        if (_taskbar is not { IsValid: true } tb) return;

        _h = tb.Rect.Height;
        _x = tb.Rect.Left + LeftInsetPx();
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
        _lastSnapshot = _sampler.Sample();
        SyncAgenda(_lastSnapshot.Events);
        RebuildAndRender();

        if (++_trimCounter >= 30) { _trimCounter = 0; TrimWorkingSet(); } // ~every 30s
    }

    private static void TrimWorkingSet()
    {
        try { NativeMethods.EmptyWorkingSet(NativeMethods.GetCurrentProcess()); }
        catch { /* best-effort */ }
    }

    private void RebuildAndRender()
    {
        if (_lastSnapshot is null) return;
        _segments = BuildSegments(_lastSnapshot);
        _w = RenderOrMeasure(null);
        if (!_hiddenForFullscreen) Relayout();
    }

    /// <summary>Adopt a new event list only when it actually changes, so cycling/index aren't reset every second.</summary>
    private void SyncAgenda(IReadOnlyList<CalEvent> events)
    {
        string key = string.Join("|", events.Select(e => $"{e.Start.Ticks}:{e.Title}"));
        if (key == _calEventsKey) return;

        _calEventsKey = key;
        _calEvents = events;
        _calIndex = 0;
        _calAlpha = 1f;
        _calPhaseMs = 0;
        _calTransitioning = false;
        _calSwitched = false;

        // Only run the 40ms crossfade timer when there's something to cycle.
        if (_calEvents.Count > 1) _agendaTimer.Start();
        else _agendaTimer.Stop();
    }

    /// <summary>Drives the gentle crossfade between the day's events.</summary>
    private void OnAgendaTick()
    {
        if (_calEvents.Count <= 1) return; // nothing to cycle

        int dwell = Math.Max(1500, _config.CalendarCycleSeconds * 1000);
        int fadeOut = Math.Max(60, _config.CalendarFadeOutMs);
        int fadeIn = Math.Max(60, _config.CalendarFadeInMs);
        _calPhaseMs += AgendaIntervalMs;

        if (!_calTransitioning)
        {
            if (_calPhaseMs < dwell) return;     // hold the current event, no redraw needed
            _calTransitioning = true;
            _calSwitched = false;
            _calPhaseMs = 0;
        }

        if (_calPhaseMs <= fadeOut)
        {
            _calAlpha = 1f - (float)_calPhaseMs / fadeOut;       // gentle fade out
        }
        else
        {
            if (!_calSwitched) { _calIndex = (_calIndex + 1) % _calEvents.Count; _calSwitched = true; }
            _calAlpha = Math.Min(1f, (float)(_calPhaseMs - fadeOut) / fadeIn); // quicker fade in
        }

        if (_calPhaseMs >= fadeOut + fadeIn)
        {
            _calTransitioning = false;
            _calPhaseMs = 0;
            _calAlpha = 1f;
            _calSwitched = false;
        }

        RebuildAndRender();
    }

    private List<Segment> BuildSegments(MetricsSnapshot s)
    {
        var list = new List<Segment>
        {
            new("CPU", $"{s.CpuPercent:0}%", "100%", LoadColor(s.CpuPercent)),
        };

        if (s.GpuLoadAvailable)
        {
            list.Add(new Segment("GPU", $"{s.GpuPercent:0}%", "100%", LoadColor(s.GpuPercent)));
            // GPU temp rides as a label-less segment that hugs the load reading (tight gap).
            if (s.GpuTempAvailable)
                list.Add(new Segment("", $"{s.GpuTempC:0}°", "199°", TempColor(s.GpuTempC), Tight: true));
        }

        list.Add(new Segment("MEM", $"{s.MemPercent:0}%", "100%", LoadColor(s.MemPercent)));

        if (s.GpuVramAvailable)
        {
            string total = $"{s.GpuVramTotalGb:0}";
            list.Add(new Segment("VRAM", $"{s.GpuVramUsedGb:0.0}/{total}G", $"{s.GpuVramTotalGb:0.0}/{total}G", _accentColor));
        }

        // Independent ↓ / ↑ slots so neither rate nudges the other.
        list.Add(new Segment("↓", HumanRate(s.NetDownBytesPerSec), "999.9M", _accentColor));
        list.Add(new Segment("↑", HumanRate(s.NetUpBytesPerSec), "999.9M", _accentColor));

        // One slot per ready drive; D:/E: appear and disappear as they're plugged/unplugged.
        foreach (var d in s.Disks)
            list.Add(new Segment($"{d.Letter}:", DiskFree(d.FreeGb), "99.9T", _accentColor));

        // Agenda tile (rightmost): the currently-cycled event in a fixed slot, ellipsis-clipped.
        // Alpha is driven by the crossfade so it gently dissolves between the day's events.
        if (_calEvents.Count > 0)
        {
            var ev = _calEvents[Math.Min(_calIndex, _calEvents.Count - 1)];
            Color baseColor = string.IsNullOrEmpty(ev.ColorHex) ? _calendarColor : ParseColor(ev.ColorHex, _calendarColor);
            int a = (int)Math.Round(Math.Clamp(_calAlpha, 0f, 1f) * 255);
            var color = Color.FromArgb(a, baseColor);
            list.Add(new Segment("", FormatEvent(ev), "", color, FixedSlotPx: _config.CalendarWidthPx));
        }

        return list;
    }

    private static string FormatEvent(CalEvent e)
    {
        string day = DayPrefix(e.Start.Date);
        if (e.AllDay)
            return day.Length == 0 ? e.Title : $"{day}  {e.Title}";

        string time = e.Start.ToString("h:mmt").ToLowerInvariant(); // 3:00p
        return day.Length == 0 ? $"{time}  {e.Title}" : $"{day} {time}  {e.Title}";
    }

    /// <summary>"" for today, "tmrw" for tomorrow, else the weekday abbreviation.</summary>
    private static string DayPrefix(DateTime date)
    {
        int d = (date.Date - DateTime.Now.Date).Days;
        if (d <= 0) return "";
        if (d == 1) return "tmrw";
        return date.ToString("ddd");
    }

    // ----------------------------------------------------------- rendering

    /// <summary>Render the current frame into an ARGB bitmap and push it via UpdateLayeredWindow.</summary>
    private void RenderLayered()
    {
        if (_w <= 0 || _h <= 0) return;

        // Reuse the render target; only reallocate when the size changes.
        if (_surface is null || _surface.Width != _w || _surface.Height != _h)
        {
            _surface?.Dispose();
            _surface = new Bitmap(_w, _h, PixelFormat.Format32bppArgb);
            _surface.SetResolution(96, 96);
        }

        using (var g = Graphics.FromImage(_surface))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias; // GDI+ alpha-blended glyph edges
            RenderOrMeasure(g);
        }

        PushLayered(_surface);
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
        _calHitW = 0; // recomputed below if an agenda tile is present

        for (int i = 0; i < _segments.Count; i++)
        {
            var seg = _segments[i];

            int labelW = 0;
            if (seg.Label.Length > 0)
            {
                labelW = (int)Math.Ceiling(Measure(ctx, seg.Label, _labelFont)) + labelGap;
                if (g is not null) DrawAt(g, seg.Label, _labelFont, _labelColor, x);
            }

            int slotW;
            if (seg.FixedSlotPx > 0)
            {
                // Free-text slot (e.g. an event title): fixed width, left-aligned, clipped.
                slotW = Scale(seg.FixedSlotPx);
                _calHitX = x + labelW;
                _calHitW = slotW;
                if (g is not null)
                {
                    // Faint hit pad (alpha>0) so the whole tile is clickable, not just glyph pixels.
                    g.FillRectangle(_hitBrush, _calHitX, 0, slotW, _h);
                    DrawClipped(g, seg.Value, _valueFont, seg.Color, _calHitX, slotW);
                }
            }
            else
            {
                slotW = (int)Math.Ceiling(Measure(ctx, seg.Template, _valueFont));
                if (g is not null)
                {
                    float valueW = Measure(ctx, seg.Value, _valueFont);
                    float vx = x + labelW + (slotW - valueW); // right-align within the slot
                    DrawAt(g, seg.Value, _valueFont, seg.Color, vx);
                }
            }

            x += labelW + slotW;
            if (i < _segments.Count - 1)
                x += _segments[i + 1].Tight ? Scale(7) : segGap; // temps hug their metric
        }

        return x + padX;
    }

    private static float Measure(Graphics ctx, string text, Font font)
        => ctx.MeasureString(text, font, int.MaxValue, TightFormat).Width;

    private void DrawAt(Graphics g, string text, Font font, Color color, float x)
    {
        float th = g.MeasureString(text, font, int.MaxValue, TightFormat).Height;
        float y = (_h - th) / 2f;
        _brush.Color = color;
        g.DrawString(text, font, _brush, new PointF(x, y), TightFormat);
    }

    /// <summary>Left-aligned text clipped to a fixed pixel width, with an ellipsis.</summary>
    private void DrawClipped(Graphics g, string text, Font font, Color color, float x, int widthPx)
    {
        float th = g.MeasureString("Ag", font, int.MaxValue, ClipFormat).Height;
        float y = (_h - th) / 2f;
        _brush.Color = color;
        g.DrawString(text, font, _brush, new RectangleF(x, y, widthPx, th + 2), ClipFormat);
    }

    // ----------------------------------------------------------- helpers

    private int Scale(int px) => (int)Math.Round(px * _dpiScale);

    /// <summary>Physical px to inset from the taskbar's left edge: past the auto-detected
    /// Widgets/Search content plus a small gap (tight when there's none), or the fixed
    /// offset when auto-clear is off or UIA couldn't measure.</summary>
    private int LeftInsetPx()
    {
        if (_config.AutoClearTaskbarLeft && _leftClearancePx >= 0)
            return _leftClearancePx + Scale(_config.LeftGapPx);
        return (int)(_config.LeftOffsetPx * _dpiScale);
    }

    private static string VersionString()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

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

    /// <summary>Thermal coloring: calm under 70°C, amber to 85°C, red above.</summary>
    private Color TempColor(double c)
    {
        if (c >= 85) return Color.FromArgb(0xFF, 0x6B, 0x6B);
        if (c >= 70) return Color.FromArgb(0xFF, 0xC1, 0x07);
        return Color.FromArgb(0xCF, 0xE8, 0xCF);
    }

    private static string HumanRate(double bytesPerSec)
    {
        double kb = bytesPerSec / 1024.0;
        if (kb < 1000) return $"{kb:0}K";
        double mb = kb / 1024.0;
        return mb < 100 ? $"{mb:0.0}M" : $"{mb:0}M";
    }

    /// <summary>Free space as "359G" or, past 1000 GiB, "1.8T".</summary>
    private static string DiskFree(double gb)
        => gb >= 1000 ? $"{gb / 1024.0:0.0}T" : $"{gb:0}G";

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

    // Single-line, ellipsis-trimmed, clipped to the layout rectangle (for free text).
    private static readonly StringFormat ClipFormat = CreateClipFormat();

    private static StringFormat CreateClipFormat()
    {
        var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
        sf.FormatFlags |= StringFormatFlags.NoWrap;
        sf.Trimming = StringTrimming.EllipsisCharacter;
        return sf;
    }

    // ----------------------------------------------------------- tray

    private TrayIcon BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Re-dock to taskbar", null, (_, _) => { _lastDpi = 0; EnsureDocked(); });

        var startup = new ToolStripMenuItem("Start with Windows")
        {
            Checked = AutoStartManager.IsEnabled(),
            CheckOnClick = true,
        };
        startup.CheckedChanged += (_, _) => AutoStartManager.SetEnabled(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add(BuildLookaheadMenu());
        menu.Items.Add("Edit config", null, (_, _) => OpenConfig());
        menu.Items.Add("Reload config", null, (_, _) => ReloadConfig());
        menu.Items.Add("About SysWidge", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _icon = AppIcon.CreateTrayIcon(_accentColor);
        return new TrayIcon(_icon, $"SysWidge v{VersionString()}", menu, TrayGuid);
    }

    private static void ShowAbout()
    {
        using var about = new AboutForm();
        about.ShowDialog();
    }

    private ToolStripMenuItem BuildLookaheadMenu()
    {
        var root = new ToolStripMenuItem("Agenda look-ahead");
        for (int d = 0; d <= 7; d++)
        {
            int days = d;
            var item = new ToolStripMenuItem(days == 0 ? "Today only" : $"+{days} day{(days == 1 ? "" : "s")}");
            item.Click += (_, _) => SetLookahead(days);
            root.DropDownItems.Add(item);
        }
        // Reflect the current value each time the submenu opens.
        root.DropDownOpening += (_, _) =>
        {
            for (int d = 0; d < root.DropDownItems.Count; d++)
                ((ToolStripMenuItem)root.DropDownItems[d]).Checked = _config.CalendarLookaheadDays == d;
        };
        return root;
    }

    private void SetLookahead(int days)
    {
        _config.CalendarLookaheadDays = Math.Clamp(days, 0, 7);
        _config.Save();
        ReloadConfig(); // recreate the calendar sampler with the new window and refresh
    }

    /// <summary>Re-read config.json and re-apply everything live (feeds, colors, fade, offset).</summary>
    private void ReloadConfig()
    {
        _config = WidgetConfig.Load();

        _labelColor = ParseColor(_config.LabelColorHex, Color.Gray);
        _accentColor = ParseColor(_config.AccentColorHex, Color.DeepSkyBlue);
        _calendarColor = ParseColor(_config.CalendarColorHex, Color.Goldenrod);
        _calLaunchUrl = _config.CalendarLaunchUrl;
        _sampleTimer.Interval = Math.Max(250, _config.RefreshMs);

        var old = _sampler;
        _sampler = new MetricsSampler(_config);
        old.Dispose();

        // Reset agenda cycling; events repopulate on the next sample.
        _calEventsKey = "";
        _calEvents = Array.Empty<CalEvent>();
        _calIndex = 0;
        _calAlpha = 1f;
        _calPhaseMs = 0;
        _calTransitioning = false;
        _calSwitched = false;

        _lastDpi = 0;       // force a font rebuild (font family/size may have changed)
        EnsureDocked();     // re-applies offset/DPI
        OnSample();         // immediate refresh
    }

    /// <summary>Best-effort open of config.json in the default handler. The config now lives
    /// at a known Documents path, so this is just a convenience — the user can always open
    /// it by hand.</summary>
    private static void OpenConfig()
    {
        try
        {
            string path = WidgetConfig.ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
                WidgetConfig.Load().Save();

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // best-effort; the path is documented for manual editing
        }
    }

    private void ExitApp()
    {
        _sampleTimer.Stop();
        _dockTimer.Stop();
        _agendaTimer.Stop();
        _tray.Hide();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sampleTimer.Dispose();
            _dockTimer.Dispose();
            _agendaTimer.Dispose();
            _sampler.Dispose();
            _tray.Dispose();
            _icon?.Dispose();
            _measure?.Dispose();
            _measureBmp?.Dispose();
            _surface?.Dispose();
            _brush.Dispose();
            _hitBrush.Dispose();
            _valueFont?.Dispose();
            _labelFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
