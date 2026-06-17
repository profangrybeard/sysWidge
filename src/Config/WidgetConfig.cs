using System.Text.Json;

namespace SysWidge.Config;

/// <summary>
/// User-tweakable settings, persisted to %APPDATA%\SysWidge\config.json.
/// Editable by hand while the widget is closed — saves a rebuild for quick tuning.
/// </summary>
public sealed class WidgetConfig
{
    /// <summary>Pixels from the left edge of the taskbar (device-independent; scaled by DPI at runtime).
    /// Defaults to clearing the Windows 11 Widgets button with room to spare.</summary>
    public int LeftOffsetPx { get; set; } = 150;

    /// <summary>How often to resample + repaint, in milliseconds.</summary>
    public int RefreshMs { get; set; } = 1000;

    public string LabelColorHex { get; set; } = "#7C828C";
    public string ValueColorHex { get; set; } = "#E8E8EA";
    public string AccentColorHex { get; set; } = "#4CC2FF";

    /// <summary>Background fill. Solid for now; Phase 1.1 will explore taskbar blending.</summary>
    public string BackColorHex { get; set; } = "#16161A";

    /// <summary>A single ICS feed URL (back-compat). Prefer <see cref="CalendarIcsUrls"/>.
    /// Treated as a secret (grants read access) — kept only in this local file.</summary>
    public string CalendarIcsUrl { get; set; } = "";

    /// <summary>One or more ICS feed URLs (e.g. personal + school). Merged into one agenda.
    /// These use the default <see cref="CalendarColorHex"/>; for per-feed colors use <see cref="CalendarFeeds"/>.</summary>
    public string[] CalendarIcsUrls { get; set; } = Array.Empty<string>();

    /// <summary>Color-coded feeds: each event is tinted by its feed's color.</summary>
    public CalendarFeed[] CalendarFeeds { get; set; } = Array.Empty<CalendarFeed>();

    /// <summary>How often to re-fetch the ICS feeds, in minutes.</summary>
    public int CalendarRefreshMinutes { get; set; } = 10;

    /// <summary>How many days past today to include in the agenda (0 = today only, max 7).</summary>
    public int CalendarLookaheadDays { get; set; } = 2;

    /// <summary>Seconds each event is shown before crossfading to the next.</summary>
    public int CalendarCycleSeconds { get; set; } = 6;

    /// <summary>Fade-out duration (ms) — the gentle dissolve of the current event.</summary>
    public int CalendarFadeOutMs { get; set; } = 900;

    /// <summary>Fade-in duration (ms) for the next event.</summary>
    public int CalendarFadeInMs { get; set; } = 450;

    /// <summary>All feeds as (url, colorHex) pairs, de-duplicated by URL. Color-coded
    /// <see cref="CalendarFeeds"/> win; plain URLs fall back to <see cref="CalendarColorHex"/>.</summary>
    public IReadOnlyList<(string Url, string ColorHex)> AllCalendarFeeds()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, string)>();

        void Add(string? url, string? color)
        {
            url = url?.Trim() ?? "";
            if (url.Length == 0 || !seen.Add(url)) return;
            result.Add((url, string.IsNullOrWhiteSpace(color) ? CalendarColorHex : color!.Trim()));
        }

        if (CalendarFeeds is not null)
            foreach (var f in CalendarFeeds) Add(f.Url, f.Color);
        if (CalendarIcsUrls is not null)
            foreach (var u in CalendarIcsUrls) Add(u, CalendarColorHex);
        Add(CalendarIcsUrl, CalendarColorHex);

        return result;
    }

    /// <summary>Fixed width (device-independent px) reserved for the agenda tile; titles clip with an ellipsis.</summary>
    public int CalendarWidthPx { get; set; } = 190;

    public string CalendarColorHex { get; set; } = "#E8C36A";

    /// <summary>Opened when the agenda tile is clicked.</summary>
    public string CalendarLaunchUrl { get; set; } = "https://calendar.google.com/";

    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>Base value-text size in device-independent pixels (scaled by DPI at runtime).</summary>
    public float FontSizePx { get; set; } = 12.5f;

    /// <summary>Config lives under Documents so it's trivial to find and hand-edit.</summary>
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "SysWidge",
        "config.json");

    /// <summary>Previous %APPDATA% location, migrated from once.</summary>
    private static string LegacyConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SysWidge",
        "config.json");

    public static WidgetConfig Load()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);

            // One-time migration from the old %APPDATA% location.
            if (!File.Exists(ConfigPath) && File.Exists(LegacyConfigPath))
                File.Copy(LegacyConfigPath, ConfigPath);

            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<WidgetConfig>(File.ReadAllText(ConfigPath)) ?? new WidgetConfig();
                cfg.Save(); // normalize on disk to the current schema (surfaces new options, drops obsolete)
                return cfg;
            }
        }
        catch
        {
            // Corrupt/unreadable config -> fall back to defaults rather than crash.
        }

        var fresh = new WidgetConfig();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // Non-fatal: a read-only profile just means no persistence this run.
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}

/// <summary>A color-coded calendar feed.</summary>
public sealed class CalendarFeed
{
    public string Url { get; set; } = "";
    public string Color { get; set; } = "";
}
