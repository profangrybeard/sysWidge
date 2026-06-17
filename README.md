# SysWidge

A low-key system widget that floats over the vacant **left region of the Windows 11
taskbar** (next to a center-aligned Start) and gives an at-a-glance readout of what
actually matters — replacing the unused stock Widgets button's spot.

Transparent, click-through, and **never covers a fullscreen app** (e.g. an Unreal play
session) — it hides itself whenever a fullscreen window is foreground, like the taskbar.

## What it shows

```
CPU 4%   GPU 31% 56°   MEM 67%   VRAM 7.4/24G   ↓16K ↑4K   C: 359G   D: 1.8T
```

| Metric | Source | Elevation |
|---|---|---|
| **CPU** load | `GetSystemTimes` deltas | none |
| **GPU** load | PDH `GPU Engine … Utilization` | none |
| **GPU** temp | LibreHardwareMonitor (AMD ADL) | none |
| **MEM** used % | `GlobalMemoryStatusEx` | none |
| **VRAM** used/total | PDH `GPU Adapter Memory` + DXGI | none |
| **NET** ↓/↑ throughput | `NetworkInterface` byte deltas | none |
| **Drives** free space | `DriveInfo` (all ready fixed/removable) | none |

Updates once per second. **No admin, no UAC.** Each value sits in a fixed-width slot, so
changing numbers never reflow the layout. Drives appear/disappear as they're plugged in.

**CPU temperature is intentionally omitted:** it needs LibreHardwareMonitor's WinRing0
kernel driver, which Windows blocks when Memory Integrity (HVCI) is enabled. GPU temp is
unaffected (AMD's ADL path is userspace).

## How it works

Windows 11 has no public API for putting content in the taskbar (deskbands and taskbar
toolbars were removed). True embedding via `SetParent` into `Shell_TrayWnd` is possible
but on Windows 11 the taskbar's XAML layer composites *over* the child, hiding it. So
instead SysWidge is a **top-level, top-most, per-pixel-alpha layered window** pinned over
the taskbar's left region:

1. `TaskbarLocator` finds the `Shell_TrayWnd` owned by `explorer.exe`.
2. `WidgetForm` positions a click-through (`WS_EX_TRANSPARENT`) layered window over its
   left region and re-asserts top-most each tick.
3. It hides while a fullscreen app is foreground, and re-docks on DPI / resolution
   changes and `explorer.exe` restarts.

Rendering is a single immediate-mode pass (`RenderOrMeasure`) used for both measuring and
painting an ARGB bitmap pushed via `UpdateLayeredWindow` — closer to a game draw loop than
XAML.

## Build & run (dev)

```powershell
dotnet build
dotnet run
```

Right-click the tray icon for **Re-dock**, **Start with Windows**, **Open config folder**,
and **Exit**.

## Install (daily driver)

Publish a Release build to a stable folder, then run it from there and enable **Start with
Windows** so autostart records the installed path:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\SysWidge"
```

> ⚠️ The `-r win-x64` matters: LibreHardwareMonitorLib ships its assembly as a
> **RID-specific** runtime asset. A portable (no-RID) publish leaves it out of the folder
> (the dev build only finds it via the NuGet cache), which breaks GPU temp on a clean
> machine.

## Config

First run writes `%APPDATA%\SysWidge\config.json`. Edit it (app closed) to change colors,
font size, refresh interval, and the left offset (default clears the Widgets button).

> Tip: turn off the stock **Widgets** button (Settings → Personalization → Taskbar) so
> SysWidge owns the left region cleanly.

## Roadmap

- ✅ Floating taskbar overlay, transparent + click-through, fullscreen-aware.
- ✅ No-driver metrics: CPU/GPU load, MEM, VRAM, network, multi-drive free space.
- ✅ GPU temperature; fixed-width slots; tray icon; autostart; install.
- ⏭️ Agenda tile (next calendar events), disk activity %, visual polish, a real app/exe icon.
- ⏸️ CPU temperature — blocked by HVCI; revisit only via AIDA64/HWiNFO shared memory.

## Layout

```
src/
  Interop/   NativeMethods.cs    — Win32 P/Invoke (windowing, layered, PDH, DXGI helpers)
  Metrics/   MetricsSnapshot.cs  — immutable reading + DriveReading
             MetricsSampler.cs   — CPU/MEM/NET/disk samplers
             GpuSampler.cs       — GPU load (PDH) + VRAM (PDH/DXGI)
             TempSampler.cs      — GPU temp (LibreHardwareMonitor)
  Hosting/   TaskbarLocator.cs   — finds the real explorer taskbar
  Config/    WidgetConfig.cs     — JSON settings in %APPDATA%
             AutoStartManager.cs — per-user Run-key autostart
  Ui/        WidgetForm.cs       — overlay docking + immediate-mode rendering + tray
             AppIcon.cs          — code-drawn tray icon
  Program.cs                     — entry point, single-instance guard
```
