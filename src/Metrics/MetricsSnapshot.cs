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

    public double DiskFreeGb { get; init; }
    public double DiskTotalGb { get; init; }

    public DateTime Time { get; init; } = DateTime.Now;
}
