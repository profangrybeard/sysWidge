# CLAUDE.md — SysWidge

Context and working rules for AI/dev sessions on this project. Read this first.

## What SysWidge is

A low-key system widget that floats over the **vacant left region of the Windows 11
taskbar** (Start is center-aligned; tray on the right). It shows glanceable system
telemetry plus a cycling calendar agenda. Built by a game developer as their first
Windows desktop project — favor game-dev framing (immediate-mode draw loop, manual
layout) over enterprise patterns.

**Stack:** C# / .NET 9 / WinForms (`net9.0-windows`, x64), Win32 P/Invoke, Vortice.DXGI,
LibreHardwareMonitorLib, Ical.Net.

### Non-negotiable design ethos
- **Low-key, transparent, click-through.** The widget is a per-pixel-alpha layered window;
  empty gaps pass clicks through to the taskbar. Keep it visually minimal.
- **Never cover a fullscreen app.** It auto-hides when a fullscreen window is foreground
  (the user's core frustration was overlays hiding the Unreal editor). Preserve this.
- **No elevation.** Everything runs un-elevated. Do not add features that require admin.

## Build / run / publish

```powershell
dotnet build            # dev build
dotnet run              # run from source (debug)
```

**Install / daily-driver publish** — MUST target the RID, or LibreHardwareMonitorLib's
runtime-specific assembly is left out of the folder (GPU temp silently breaks on a clean
machine):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\SysWidge"
```

The widget is a `ShowInTaskbar=false` tool window — no taskbar button / not in alt-tab.
Control it from the **tray icon** (re-dock, look-ahead, edit/reload config, About, exit).
A single-instance mutex prevents duplicates.

## Conventions (rules)

1. **Bump `<Version>` in `SysWidge.csproj` on every single build** — even one-line fixes —
   and never present a build whose version number was already used. The user's first
   question is always "are we on the same revision?" Always **state the version** when
   asking them to verify, and keep it visible in the **tray tooltip** and **About** dialog.
2. **Zero build warnings.** Keep it clean.
3. **No CPU temperature.** It needs LibreHardwareMonitor's WinRing0 kernel driver, which
   the user's **Memory Integrity (HVCI)** blocks. GPU temp works (AMD ADL, userspace).
   Do not reintroduce a driver/elevated path unless the user lifts HVCI or opts into
   AIDA64/HWiNFO shared memory.
4. **Config lives in `Documents\SysWidge\config.json`** (often OneDrive-redirected). It's
   user-editable, auto-migrated from the old `%APPDATA%` path, and **normalized to the
   current schema on load** (new options appear, obsolete keys drop). Do **not** rely on
   the app spawning Explorer/Notepad to open it — that proved unreliable in this
   environment; the known path + "Reload config" is the contract.
5. **Secrets.** The user's ICS feed URLs grant calendar read access. They live only in the
   local config. Never commit them, and don't read or overwrite the user's `config.json`
   (it may contain them) — drive config changes through code or give edit instructions.
6. **Preserve invariants** when adding features: transparency, click-through on gaps,
   fullscreen auto-hide, fixed-width slots (the strip must never reflow as values change).

## Architecture

- **Floating overlay, not taskbar embedding.** Win11 has no API to put content in the
  taskbar; a true `SetParent` child of `Shell_TrayWnd` gets composited *behind* the
  taskbar's XAML layer. So SysWidge is a top-level, top-most, `WS_EX_LAYERED` (no
  `WS_EX_TRANSPARENT`) window pinned over the taskbar's left region, re-asserted each tick.
  Per-pixel alpha gives both blending and alpha-based hit-testing (gaps click through).
- **Rendering** (`Ui/WidgetForm.cs`): a single immediate-mode pass (`RenderOrMeasure`)
  used for both measuring and drawing an ARGB bitmap pushed via `UpdateLayeredWindow`.
  Each value occupies a **fixed-width slot** (sized from a `Template` string, or a fixed
  px slot for free text) so changing values never move neighbors. Right-aligned numbers;
  ellipsis-clipped free text.
- **Metrics** (`Metrics/`): `MetricsSampler` aggregates per-tick on the UI timer.
  `GpuSampler` = GPU load (PDH `GPU Engine`) + VRAM used (PDH `GPU Adapter Memory`) + VRAM
  total/name (DXGI). `TempSampler` = GPU temp (LibreHardwareMonitor, `IsCpuEnabled=false`).
  `CalendarSampler` = background ICS fetch/parse (Ical.Net), exposes the look-ahead
  window's events; merges multiple feeds, tags each event with its feed color.
- **Calendar UI**: the agenda is the rightmost tile, cycling through events with a
  crossfade (long fade-out, shorter fade-in) driven by a ~40ms timer. Clickable.

```
src/
  Interop/   NativeMethods.cs    — Win32 P/Invoke (windowing, layered, monitor, PDH, GDI)
  Metrics/   MetricsSnapshot.cs  — immutable reading + DriveReading + CalEvent
             MetricsSampler.cs   — CPU/MEM/NET/disk aggregator (+ GPU/Temp/Calendar)
             GpuSampler.cs        TempSampler.cs        CalendarSampler.cs
  Hosting/   TaskbarLocator.cs   — finds the real explorer Shell_TrayWnd
  Config/    WidgetConfig.cs     — JSON config (Documents), AutoStartManager.cs
  Ui/        WidgetForm.cs       — overlay docking, rendering, tray, agenda cycling
             AboutForm.cs         AppIcon.cs
  Program.cs                     — entry point, single-instance guard
tools/iconforge/                 — one-shot icon generator (not part of the app build)
textures/sysWidge.png            — source mascot art
assets/                          — generated app + tray .ico
```

## How to extend

- **New metric:** add a field to `MetricsSnapshot`, populate it in `MetricsSampler.Sample`
  (or a new `*Sampler`), and add a `Segment` in `WidgetForm.BuildSegments` with a
  `Template` for its widest value (keeps the slot fixed). No-elevation sources only.
- **New config option:** add a property to `WidgetConfig` (with a default). It auto-appears
  in users' config.json on next load (normalize-on-load). Read it where needed; support
  live changes via `ReloadConfig`.
- **New tray action:** add to `BuildTray`. Avoid relying on spawning external apps.

## Goals / roadmap

- ~~Trim idle memory~~ — done (v0.3.9): bitmap/brush reuse, temp throttle,
  workstation/non-concurrent GC, and `EmptyWorkingSet`; idle ~23 MB.
- Disk activity % (we show free space only).
- Optional weather tile; richer agenda (click an event → open that event).
- A small settings UI (so config isn't hand-edited JSON).
- A real installer / Start-menu entry; signed exe.
- Per-monitor / secondary-taskbar support.

See the user-facing `README.md` for the feature list and config reference.
