# SysWidge

A low-key system widget that floats over the vacant **left region of the Windows 11
taskbar** (next to a center-aligned Start) and gives an at-a-glance readout of what
actually matters — replacing the unused stock Widgets button's spot.

Transparent, click-through, and **never covers a fullscreen app** (e.g. an Unreal play
session) — it hides itself whenever a fullscreen window is foreground, like the taskbar.
Runs un-elevated; no admin, no UAC.

## What it shows

```
CPU 4%   GPU 31% 56°   MEM 67%   VRAM 7.4/24G   ↓16K ↑4K   C: 359G   D: 1.8T   tmrw 9a Standup
```

| Metric | Source |
|---|---|
| **CPU** load | `GetSystemTimes` deltas |
| **GPU** load + temp | PDH `GPU Engine` / LibreHardwareMonitor (AMD ADL) |
| **MEM** used % | `GlobalMemoryStatusEx` |
| **VRAM** used/total | PDH `GPU Adapter Memory` + DXGI |
| **NET** ↓/↑ throughput | `NetworkInterface` byte deltas |
| **Drives** free space | `DriveInfo` (every ready fixed/removable drive) |
| **Agenda** upcoming events | ICS feed(s) via Ical.Net |

Updates once per second. Each value sits in a **fixed-width slot**, so changing numbers
never reflow the layout. Drives appear/disappear as they're plugged in.

> **CPU temperature is intentionally omitted** — it needs LibreHardwareMonitor's WinRing0
> kernel driver, which Windows blocks when Memory Integrity (HVCI) is on. GPU temp is
> unaffected (AMD's ADL path is userspace).

### Agenda

The rightmost tile cycles through upcoming events with a gentle crossfade. It pulls from
one or more **ICS calendar feeds**, merged, within a **look-ahead window** you set live
from the tray (today only … +7 days). Each feed can have its own **color**. Click the
tile to open your calendar in the browser.

## How it works

Windows 11 has no public API for putting content in the taskbar (deskbands and taskbar
toolbars were removed). True embedding via `SetParent` into `Shell_TrayWnd` is possible,
but on Windows 11 the taskbar's XAML layer composites *over* the child and hides it. So
SysWidge is a **top-level, top-most, per-pixel-alpha layered window** pinned over the
taskbar's left region:

1. `TaskbarLocator` finds the `Shell_TrayWnd` owned by `explorer.exe`.
2. `WidgetForm` positions a layered window over its left region and re-asserts top-most
   each tick. There's no `WS_EX_TRANSPARENT` — the per-pixel alpha drives hit-testing, so
   transparent gaps click through to the taskbar while the agenda tile is clickable.
3. It hides while a fullscreen app is foreground, and re-docks on DPI / resolution changes
   and `explorer.exe` restarts.

Rendering is a single immediate-mode pass (`RenderOrMeasure`) used for both measuring and
painting an ARGB bitmap pushed via `UpdateLayeredWindow` — closer to a game draw loop than
XAML.

## Build & run (dev)

```powershell
dotnet build
dotnet run
```

Tray icon menu: **Re-dock**, **Agenda look-ahead**, **Edit config**, **Reload config**,
**Start with Windows**, **About**, **Exit**. (The tray tooltip and About both show the
running version.)

## Install (daily driver)

Publish a Release build to a stable folder, run it from there, then enable **Start with
Windows** so autostart records the installed path:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\SysWidge"
```

> ⚠️ The `-r win-x64` matters: LibreHardwareMonitorLib ships its assembly as a
> **RID-specific** runtime asset. A portable (no-RID) publish leaves it out of the folder
> (the dev build only finds it via the NuGet cache), which breaks GPU temp on a clean
> machine.

> **Prerequisite:** this build is *framework-dependent* — the target machine needs the
> **.NET 9 Desktop Runtime** (`Microsoft.WindowsDesktop.App` 9.x). A dev box already has it;
> a clean Windows install does not, and SysWidge won't start without it. Install it with:
> ```powershell
> winget install Microsoft.DotNet.DesktopRuntime.9
> ```

### Copy to a machine without .NET (self-contained)

To hand SysWidge to a fresh machine with **no prerequisites**, publish self-contained — it
bundles the .NET runtime into the folder, so it just runs (cost: the folder grows to
~110 MB instead of ~7 MB):

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:DebugType=none -p:DebugSymbols=false -o ".\publish\SysWidge-selfcontained"
```

Copy the **whole folder** (not just `SysWidge.exe`) to the target, then run the exe.
The exe is unsigned, so the first launch shows a SmartScreen prompt — *More info →
Run anyway*.

## Config

Config lives at **`Documents\SysWidge\config.json`** (may be OneDrive-redirected). It's
created on first run, auto-migrated from the old `%APPDATA%` location, and normalized to
the current schema on load (new options appear automatically). Edit it with any editor,
then tray → **Reload config** to apply changes live (no restart).

Calendar keys:

```jsonc
// Color-coded feeds (preferred): each event tinted by its feed's color
"CalendarFeeds": [
  { "Url": "https://…personal….ics", "Color": "#4CC2FF" },
  { "Url": "https://…school….ics",   "Color": "#FF8A65" }
],
"CalendarIcsUrls": [],          // plain feeds (use the default color); merged with the above
"CalendarLookaheadDays": 2,     // 0 = today only … 7 (also settable from the tray)
"CalendarCycleSeconds": 6,      // dwell per event
"CalendarFadeOutMs": 900,       // gentle dissolve out
"CalendarFadeInMs": 450,        // settle in
"CalendarWidthPx": 190,         // agenda slot width
"CalendarColorHex": "#E8C36A",  // default agenda color
"CalendarLaunchUrl": "https://calendar.google.com/"
```

Get a feed URL from Google Calendar → **Settings and sharing** → **Integrate calendar** →
*Secret address in iCal format* (or *Public address* for a public calendar), or use any
ICS link a service gives you. URLs are secrets — they stay in this local file only.

> Tip: turn off the stock **Widgets** button (Settings → Personalization → Taskbar) so
> SysWidge owns the left region cleanly. A `calendar.log` in the same folder shows the last
> fetch/parse status per feed.

## Roadmap

- ✅ Floating taskbar overlay — transparent, click-through, fullscreen-aware.
- ✅ No-driver metrics: CPU/GPU load, GPU temp, MEM, VRAM, network, multi-drive free space.
- ✅ Fixed-width slots, tray icon (mascot), autostart, install.
- ✅ Calendar agenda: multi-feed, color-coded, cycling crossfade, live look-ahead, clickable.
- ✅ Idle memory trimmed (~165 MB → ~23 MB).
- ⏭️ Disk activity %; weather; a settings UI; signed installer.
- ⏸️ CPU temperature — blocked by HVCI; revisit only via AIDA64/HWiNFO shared memory.

## Layout

```
src/
  Interop/   NativeMethods.cs    — Win32 P/Invoke (windowing, layered, monitor, PDH, GDI)
  Metrics/   MetricsSnapshot.cs  — immutable reading + DriveReading + CalEvent
             MetricsSampler.cs   — per-tick aggregator
             GpuSampler.cs       — GPU load (PDH) + VRAM (PDH/DXGI)
             TempSampler.cs      — GPU temp (LibreHardwareMonitor)
             CalendarSampler.cs  — background ICS fetch/parse (Ical.Net)
  Hosting/   TaskbarLocator.cs   — finds the real explorer taskbar
  Config/    WidgetConfig.cs     — JSON config (Documents\SysWidge)
             AutoStartManager.cs — per-user Run-key autostart
  Ui/        WidgetForm.cs       — overlay docking, rendering, tray, agenda cycling
             AboutForm.cs        — About dialog (version + build date)
             AppIcon.cs          — tray icon (mascot helmet)
  Program.cs                     — entry point, single-instance guard
tools/iconforge/                 — one-shot icon generator (not built with the app)
```

See `CLAUDE.md` for architecture details and contributor/agent working rules.
