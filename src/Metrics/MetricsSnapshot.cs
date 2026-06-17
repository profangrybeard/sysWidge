namespace SysWidge.Metrics;

/// <summary>
/// An immutable point-in-time reading of everything the widget shows.
/// New metrics get added here as fields; the renderer decides what to display.
/// </summary>
public sealed record MetricsSnapshot
{
    public double CpuPercent { get; init; }

    public bool GpuTempAvailable { get; init; }
    public double GpuTempC { get; init; }

    public double MemPercent { get; init; }
    public double MemUsedGb { get; init; }
    public double MemTotalGb { get; init; }

    public bool GpuLoadAvailable { get; init; }
    public double GpuPercent { get; init; }

    public bool GpuVramAvailable { get; init; }
    public double GpuVramUsedGb { get; init; }
    public double GpuVramTotalGb { get; init; }

    public string GpuName { get; init; } = "GPU";

    public double NetDownBytesPerSec { get; init; }
    public double NetUpBytesPerSec { get; init; }

    /// <summary>Ready fixed/removable drives, in letter order. Re-enumerated each sample
    /// so external/USB drives appear and disappear as they're plugged/unplugged.</summary>
    public IReadOnlyList<DriveReading> Disks { get; init; } = Array.Empty<DriveReading>();

    /// <summary>Today's upcoming events (or the next event if today is empty); empty if no ICS.</summary>
    public IReadOnlyList<CalEvent> Events { get; init; } = Array.Empty<CalEvent>();

    public DateTime Time { get; init; } = DateTime.Now;
}

/// <summary>Free/total space for one drive (e.g. "C").</summary>
public sealed record DriveReading(string Letter, double FreeGb, double TotalGb);

/// <summary>An upcoming calendar event. <paramref name="ColorHex"/> is its feed's color.</summary>
public sealed record CalEvent(DateTime Start, string Title, bool AllDay, string ColorHex = "");
