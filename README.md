# SysWidge

A low-key system widget that lives **inside the Windows 11 taskbar** — the vacant
left region next to a center-aligned Start. It replaces the unused stock Widgets
button with an at-a-glance readout of what actually matters.

## Status: Phase 1 (vertical slice)

Proves the hard part first — genuinely embedding a custom window into the taskbar —
while showing the metrics that need **no elevation and no kernel driver**:

- **CPU** load (`GetSystemTimes` deltas)
- **MEM** used % (`GlobalMemoryStatusEx`)
- **NET** down/up throughput (`NetworkInterface` byte-count deltas)
- **C:** free space (`DriveInfo`)
- **Clock** (HH:mm)

Updates once per second. No admin rights, no UAC prompt.

## How it works

Windows 11 has no public API for putting content in the taskbar (deskbands and
taskbar toolbars were removed). The proven technique — used by tools like
TrafficMonitor — is to re-parent your own window into the taskbar window:

1. `TaskbarLocator` enumerates `Shell_TrayWnd` windows and picks the one owned by
   `explorer.exe` (others can exist from shell utilities).
2. `WidgetForm` flips itself to `WS_CHILD` and calls `SetParent` into that window.
3. A 1-second timer re-finds the taskbar and repositions, so it survives DPI
   changes, resolution changes, and `explorer.exe` restarts.

Because it's a true child of the taskbar, it only appears when the taskbar does —
it will **never** float over a fullscreen app (e.g. an Unreal play session).

Rendering is a single immediate-mode layout pass (`RenderOrMeasure`) used for both
measuring the width and painting — closer to a game draw loop than XAML.

## Build & run

```powershell
dotnet build
dotnet run
```

Exit via the tray icon (right-click → Exit). A right-click menu there also offers
**Re-dock to taskbar** and **Open config folder**.

## Config

First run writes `%APPDATA%\SysWidge\config.json`. Edit it (while the app is closed)
to change colors, font size, refresh interval, and the left offset.

> Tip: turn off the stock **Widgets** button (Settings → Personalization → Taskbar)
> so SysWidge owns the left region cleanly.

## Roadmap

- **Phase 1** *(here)* — taskbar embedding + no-driver metrics.
- **Phase 1.1** — visual polish: taskbar-color blending, per-tile icons, click-to-expand detail popup.
- **Phase 2** — CPU/GPU temps, GPU load/VRAM/power (LibreHardwareMonitor via a small
  elevated helper so the UI stays un-elevated and embeds cleanly).
- **Phase 3** — calendar/agenda tile (upcoming events), optional weather.

## Layout

```
src/
  Interop/      NativeMethods.cs   — thin Win32 P/Invoke surface
  Metrics/      MetricsSnapshot.cs — immutable reading
                MetricsSampler.cs  — no-elevation samplers (CPU/MEM/NET/disk)
  Hosting/      TaskbarLocator.cs  — finds the real explorer taskbar
  Config/       WidgetConfig.cs    — JSON settings in %APPDATA%
  Ui/           WidgetForm.cs      — embedding + immediate-mode rendering + tray
  Program.cs                       — entry point, single-instance guard
```
