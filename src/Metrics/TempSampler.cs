using LibreHardwareMonitor.Hardware;

namespace SysWidge.Metrics;

/// <summary>
/// GPU temperature via LibreHardwareMonitor (AMD ADL path — userspace, no kernel driver,
/// no elevation).
///
/// CPU die temp is intentionally NOT read: it needs LHM's WinRing0 kernel driver, which
/// Windows blocks when Memory Integrity (HVCI) is enabled. So we leave <c>IsCpuEnabled</c>
/// off to avoid the blocked-driver churn entirely. Missing/zero sensors just read null.
/// </summary>
public sealed class TempSampler : IDisposable
{
    private Computer? _computer;
    private IHardware? _gpu;

    public TempSampler()
    {
        try
        {
            _computer = new Computer { IsCpuEnabled = false, IsGpuEnabled = true };
            _computer.Open();

            foreach (var hw in _computer.Hardware)
            {
                switch (hw.HardwareType)
                {
                    case HardwareType.GpuAmd:
                        _gpu = hw; // prefer the discrete AMD card (the 7900 XTX)
                        break;
                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuIntel:
                        _gpu ??= hw;
                        break;
                }
            }
        }
        catch
        {
            _computer = null;
        }
    }

    /// <summary>Latest GPU temperature in °C, or null if unavailable.</summary>
    public double? Sample()
    {
        if (_computer is null || _gpu is null) return null;

        try
        {
            _gpu.Update();
            return ReadTemp(_gpu, "GPU Core", "GPU Hot Spot");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>First matching preferred sensor name, else the first temperature sensor found.</summary>
    private static double? ReadTemp(IHardware hw, params string[] preferredNames)
    {
        double? fallback = null;
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Temperature || s.Value is not float v) continue;
            fallback ??= v;
            foreach (var name in preferredNames)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    return v;
        }
        return fallback;
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { }
        _computer = null;
    }
}
