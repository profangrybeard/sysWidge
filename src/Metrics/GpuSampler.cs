using System.Runtime.InteropServices;
using SysWidge.Interop;
using Vortice.DXGI;

namespace SysWidge.Metrics;

/// <summary>
/// GPU telemetry that needs no elevation:
///   • Load — summed 3D-engine "Utilization Percentage" (PDH).
///   • VRAM used — adapter-wide "Dedicated Usage" (PDH). This is the system-wide figure
///     Task Manager shows; note DXGI's QueryVideoMemoryInfo reports only the *calling
///     process's* usage, which is ~0 for us, so we deliberately don't use it for "used".
///   • VRAM total — dedicated video memory from DXGI (IDXGIAdapter1.Description1).
///
/// Picks the discrete adapter (largest dedicated VRAM) so it tracks the RX 7900 XTX
/// rather than the integrated Radeon. Degrades to "unavailable" rather than throwing.
/// </summary>
public sealed class GpuSampler : IDisposable
{
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    private IntPtr _pdhQuery;
    private IntPtr _loadCounter;
    private IntPtr _vramCounter;

    private ulong _vramTotalBytes;

    public string Name { get; private set; } = "GPU";
    public bool LoadAvailable => _loadCounter != IntPtr.Zero;
    public bool VramAvailable => _vramTotalBytes > 0 && _vramCounter != IntPtr.Zero;

    public GpuSampler()
    {
        TryInitDxgi();   // total VRAM + adapter name
        TryInitPdh();    // load + system-wide VRAM usage
    }

    public (double loadPct, double vramUsedGb, double vramTotalGb) Sample()
    {
        double load = 0;
        double vramUsedBytes = 0;

        if (_pdhQuery != IntPtr.Zero && NativeMethods.PdhCollectQueryData(_pdhQuery) == 0)
        {
            if (_loadCounter != IntPtr.Zero) load = Math.Clamp(SumCounter(_loadCounter), 0, 100);
            if (_vramCounter != IntPtr.Zero) vramUsedBytes = SumCounter(_vramCounter);
        }

        return (load, vramUsedBytes / BytesPerGb, _vramTotalBytes / BytesPerGb);
    }

    // --------------------------------------------------------------- PDH

    private void TryInitPdh()
    {
        try
        {
            if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _pdhQuery) != 0)
            {
                _pdhQuery = IntPtr.Zero;
                return;
            }

            // English counter paths so they work regardless of UI language. The "*"
            // wildcard expands to one instance per process/adapter; we sum them.
            if (NativeMethods.PdhAddEnglishCounter(
                    _pdhQuery, @"\GPU Engine(*engtype_3D)\Utilization Percentage",
                    IntPtr.Zero, out _loadCounter) != 0)
                _loadCounter = IntPtr.Zero;

            if (NativeMethods.PdhAddEnglishCounter(
                    _pdhQuery, @"\GPU Adapter Memory(*)\Dedicated Usage",
                    IntPtr.Zero, out _vramCounter) != 0)
                _vramCounter = IntPtr.Zero;

            // Prime the query — first collection establishes the baseline.
            NativeMethods.PdhCollectQueryData(_pdhQuery);
        }
        catch
        {
            _loadCounter = IntPtr.Zero;
            _vramCounter = IntPtr.Zero;
        }
    }

    /// <summary>Sum a wildcard counter's instances (valid items only) as a double.</summary>
    private static double SumCounter(IntPtr counter)
    {
        uint bufferSize = 0;
        uint res = NativeMethods.PdhGetFormattedCounterArray(
            counter, NativeMethods.PDH_FMT_DOUBLE, ref bufferSize, out _, IntPtr.Zero);
        if (res != NativeMethods.PDH_MORE_DATA || bufferSize == 0) return 0;

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            res = NativeMethods.PdhGetFormattedCounterArray(
                counter, NativeMethods.PDH_FMT_DOUBLE, ref bufferSize, out uint itemCount, buffer);
            if (res != 0) return 0;

            double sum = 0;
            int stride = Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
            for (int i = 0; i < itemCount; i++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(buffer + i * stride);
                if (item.FmtValue.CStatus == NativeMethods.PDH_CSTATUS_VALID_DATA)
                    sum += item.FmtValue.doubleValue;
            }
            return sum;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // --------------------------------------------------------------- DXGI (total + name)

    private void TryInitDxgi()
    {
        try
        {
            if (DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).Failure || factory is null)
                return;

            using (factory)
            {
                for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success && adapter is not null; i++)
                {
                    using (adapter)
                    {
                        var desc = adapter.Description1;
                        bool isSoftware = (desc.Flags & AdapterFlags.Software) != 0;
                        ulong mem = (ulong)desc.DedicatedVideoMemory;

                        if (!isSoftware && mem > _vramTotalBytes)
                        {
                            _vramTotalBytes = mem;
                            Name = string.IsNullOrWhiteSpace(desc.Description) ? "GPU" : desc.Description.Trim();
                        }
                    }
                }
            }
        }
        catch
        {
            // leave _vramTotalBytes at 0 -> VRAM segment hidden
        }
    }

    public void Dispose()
    {
        try { if (_pdhQuery != IntPtr.Zero) NativeMethods.PdhCloseQuery(_pdhQuery); } catch { }
        _pdhQuery = IntPtr.Zero;
    }
}
