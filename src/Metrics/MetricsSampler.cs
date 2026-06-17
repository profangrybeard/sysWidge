using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using SysWidge.Interop;

namespace SysWidge.Metrics;

/// <summary>
/// Samples system metrics that require no elevation and no kernel driver.
/// Rate-style metrics (CPU, network) are computed as deltas between calls, so
/// the first <see cref="Sample"/> returns a baseline of zero for those.
///
/// Phase 2 will add temperature/GPU sources behind this same shape.
/// </summary>
public sealed class MetricsSampler : IDisposable
{
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasCpuBaseline;

    private long _prevRx, _prevTx, _prevNetTicks;
    private bool _hasNetBaseline;

    private readonly string _systemDriveRoot;
    private readonly GpuSampler _gpu = new();

    public MetricsSampler()
    {
        _systemDriveRoot =
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\";
    }

    public MetricsSnapshot Sample()
    {
        var (memPct, memUsed, memTotal) = SampleMemory();
        var (down, up) = SampleNetwork();
        var (freeGb, totalGb) = SampleDisk();
        var (gpuLoad, vramUsed, vramTotal) = _gpu.Sample();

        return new MetricsSnapshot
        {
            CpuPercent = SampleCpu(),
            MemPercent = memPct,
            MemUsedGb = memUsed,
            MemTotalGb = memTotal,
            GpuLoadAvailable = _gpu.LoadAvailable,
            GpuPercent = gpuLoad,
            GpuVramAvailable = _gpu.VramAvailable,
            GpuVramUsedGb = vramUsed,
            GpuVramTotalGb = vramTotal,
            GpuName = _gpu.Name,
            NetDownBytesPerSec = down,
            NetUpBytesPerSec = up,
            DiskFreeGb = freeGb,
            DiskTotalGb = totalGb,
            Time = DateTime.Now,
        };
    }

    public void Dispose() => _gpu.Dispose();

    private double SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        ulong i = idle.Value, k = kernel.Value, u = user.Value;

        if (!_hasCpuBaseline)
        {
            (_prevIdle, _prevKernel, _prevUser) = (i, k, u);
            _hasCpuBaseline = true;
            return 0;
        }

        ulong idleDelta = i - _prevIdle;
        ulong kernelDelta = k - _prevKernel; // NB: kernel time already includes idle
        ulong userDelta = u - _prevUser;
        (_prevIdle, _prevKernel, _prevUser) = (i, k, u);

        ulong total = kernelDelta + userDelta;
        if (total == 0) return 0;

        double busy = (double)(total - idleDelta) / total;
        return Math.Clamp(busy * 100.0, 0, 100);
    }

    private static (double pct, double usedGb, double totalGb) SampleMemory()
    {
        var m = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
        };
        if (!NativeMethods.GlobalMemoryStatusEx(ref m))
            return (0, 0, 0);

        double totalGb = m.ullTotalPhys / BytesPerGb;
        double availGb = m.ullAvailPhys / BytesPerGb;
        return (m.dwMemoryLoad, totalGb - availGb, totalGb);
    }

    private (double downBytesPerSec, double upBytesPerSec) SampleNetwork()
    {
        long rx = 0, tx = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var s = ni.GetIPv4Statistics();
            rx += s.BytesReceived;
            tx += s.BytesSent;
        }

        long now = Environment.TickCount64;

        if (!_hasNetBaseline)
        {
            (_prevRx, _prevTx, _prevNetTicks) = (rx, tx, now);
            _hasNetBaseline = true;
            return (0, 0);
        }

        double seconds = Math.Max(1, now - _prevNetTicks) / 1000.0;
        double down = Math.Max(0, rx - _prevRx) / seconds;
        double up = Math.Max(0, tx - _prevTx) / seconds;
        (_prevRx, _prevTx, _prevNetTicks) = (rx, tx, now);
        return (down, up);
    }

    private (double freeGb, double totalGb) SampleDisk()
    {
        try
        {
            var di = new DriveInfo(_systemDriveRoot);
            if (!di.IsReady) return (0, 0);
            return (di.AvailableFreeSpace / BytesPerGb, di.TotalSize / BytesPerGb);
        }
        catch
        {
            return (0, 0);
        }
    }
}
