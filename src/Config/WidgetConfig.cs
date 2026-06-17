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

    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>Base value-text size in device-independent pixels (scaled by DPI at runtime).</summary>
    public float FontSizePx { get; set; } = 12.5f;

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SysWidge",
        "config.json");

    public static WidgetConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<WidgetConfig>(File.ReadAllText(ConfigPath)) ?? new WidgetConfig();
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
