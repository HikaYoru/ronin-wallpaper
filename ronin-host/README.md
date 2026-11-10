# Ronin Host

A small Windows 11 app that shows the Ronin wallpaper behind the desktop icons, without Wallpaper Engine.

It has two renderers (Settings → Display → Renderer):

- **Native (light)**, the default: the wallpaper re-drawn with Direct2D in one small process. Same look, about a third of the memory and a quarter of the CPU.
- **Web (original page)**: `../ronin-wallpaper/index.html`, unchanged, in WebView2.

## Run

```
cd ronin-host
dotnet run
```

Needs the .NET 8 SDK. The settings window (and the web renderer) also need the Microsoft Edge WebView2 Runtime. Both are installed on this PC.
It lives in the tray (gold diamond icon):

- **Left-click** or **Settings…**: the settings window
- **Arrange layout…**: drag the clock, quote and to-do list into place (see below)
- **Pause / Resume**
- **Monitors**: tick every screen the wallpaper should be on (or **All monitors**)
- **Start with Windows**
- **Quit**

Running `RoninHost.exe` again while it's running opens its settings; `RoninHost.exe --quit` closes it.

## Measured on this PC (2026-11-09; main monitor 1920×1080 at 280 Hz, RTX 4060)

| | Private bytes | Task Manager "Memory" | CPU (12 threads) | Processes |
|---|---|---|---|---|
| **Native renderer**, drawing at 60 fps | **~139 MB** | **~47 MB** | **~1.4 %** | **1** |
| Native renderer, paused (app maximized) | ~135 MB | ~46 MB | 0 % | 1 |
| Web renderer, drawing (60 fps cap) | ~385–424 MB | ~143–156 MB | ~5–5.6 % | 7 |
| Web renderer, paused | ~312 MB | ~83 MB | 0 % | 7 |
| Wallpaper Engine, Ronin's web processes | ~355–429 MB | – | – | 5 |
| Wallpaper Engine total (incl. a scene wallpaper on another monitor) | ~1.27 GB | – | – | 6 |

Most of the native renderer's private bytes are the graphics driver's own allocations (Direct3D device); the
app's own memory is small. With the native renderer, WebView2 only runs while the settings window is open.

## What it does

| Feature | How |
|---|---|
| Behind the icons | Sends `0x052C` to Progman. On Windows 11 24H2+ (this PC) the window becomes a child of Progman placed directly under `SHELLDLL_DefView`; older layouts use the WorkerW behind the icons. Re-attaches if Explorer restarts or displays change. |
| Settings | Built from `LivelyProperties.json`, applied live, saved to `%APPDATA%\RoninHost\settings.json` (only values you changed). |
| Music ink line | WASAPI loopback of the default output device (follows device changes), 2048-point FFT, 64 log-spaced bands per channel (30 Hz–16 kHz), automatic gain so it moves at any volume, ~30×/s. |
| Now playing | Windows media sessions (`GlobalSystemMediaTransportControlsSessionManager`), prefers whatever is actually playing. |
| Clickable to-dos | A low-level mouse hook spots left clicks on empty desktop (not on an icon, checked with `LVM_HITTEST`, and not on any app window) and passes them to the wallpaper. Ticks are saved (native: `settings.json`; web: the page's own storage). The hook never blocks clicks. |
| Pause | When another app is maximized or fullscreen on the wallpaper's monitor, drawing stops and audio capture stops: 0 % CPU. |
| Several monitors | One wallpaper window per chosen monitor (Settings → Display, or the tray's Monitors menu). Audio, now playing and to-do ticks are shared; each monitor pauses on its own when an app covers it. Native: one render thread and Direct3D device per monitor, on the GPU that drives it. |
| Arrange mode | Tray → Arrange layout… (or Settings → Display → Arrange…). A lightly dimmed topmost layer (per-pixel alpha) on each monitor boxes the clock, quote and to-do list; drag a box and the wallpaper under it moves live. Snaps to edges and centre (hold Shift to stop it), arrow keys nudge, right-click to hide an element, detach / re-attach the quote, or reset. Esc / Enter / Done finishes. With one monitor it edits the normal settings; with several, positions are saved per monitor (`monitorProperties` in `settings.json`), and moving a slider in Settings applies it to every monitor again. |
| Task boards | Tray → **Tasks…**: boards (one per project), each with section headers and tasks; a task has a status (not started / in progress / done), notes and sub-tasks. Each board picks the monitors it shows on (one board per monitor). On the desktop: click a task's diamond to cycle its status, click its name to open the task panel next to it (title, status, board, section, notes, sub-tasks; closes on Esc or a click elsewhere, saves as you go), click a header to fold its section; hover a row for ↑ ↓ ✕ (and + on headers). "Finished tasks": dimmed, moved to the bottom of their section, or hidden a day later. Saved to `%APPDATA%\RoninHost\tasks.json` (`TaskStore.cs`); the first start makes a board from the old ";" list and its ticks. Windows: `TaskWindow.cs` + `tasks.html`; desktop: `Render\Tasks.cs` (native), `web-widgets.js` (web). |
| One device per GPU | Every monitor a GPU drives shares one render thread and one Direct3D / Direct2D device (`Render\GpuWorker.cs`, `GfxDevice`); each monitor keeps its own swap chain and frame rate (presented without vsync, paced by the thread; "match the display" = that monitor's refresh rate). Device loss rebuilds everything on that GPU. |
| Video backgrounds | `.mp4 .m4v .mov .mkv .wmv .webm` in the background list play in a loop through Media Foundation's source reader (`Render\VideoBackground.cs`), decoded on the render thread only while that monitor draws; no sound. Native renderer only. |
| Layout schedule | Settings → Display (once a layout is saved): switch to a layout at a time of day, on chosen days. Switching by hand lasts until the next scheduled time. |
| Per-monitor values | Settings → "Editing: Monitor n" shows that monitor's values; changing one gives that monitor its own value (● marks it, ↺ goes back to the shared one). On "All monitors", ≠ marks settings that differ somewhere. |
| Web renderer | Ticks are shared between monitors (`storage` events), and the widgets work too: `web-widgets.js` is injected next to the bridge and fed by the host once a second. Panorama, shared weather, GIF / video backgrounds remain native-only. |
| Updates | `tools\publish.ps1` builds a release into `releases\` (zip + `version.json`); `tools\install.ps1` installs one (Start menu shortcut, keeps `app.previous`). The app checks its update source once a day (Settings → Display → Updates, or `update-source.txt` next to the exe) and offers "Install update" in the tray: the new version, started with `--apply-update`, waits for the old one to exit, swaps the app folder and starts again. |
| Arrange extras | Drag a box's corner to resize it (writes its size setting; it grows around the point it is pinned at). Gold guides appear when an edge or centre lines up with another element or the screen's thirds. Drop an element on another monitor to move it there. Undo (button or Ctrl+Z, a drag is one step), Cancel (everything back as it was when Arrange opened), Done. |
| Layouts | Tray → Layouts (or Settings → Display): save the current arrangement under a name and switch between them. A layout keeps positions, sizes, what each element shows, per-monitor settings and the background / panorama choice. |
| Frame rates | Settings → Display: a lower cap for the monitors that aren't the main one, and while a fullscreen app (a game) has focus, the other monitors drop to 15 fps (`CoverCheck.FullscreenForeground`). |
| Animated GIFs | A `.gif` background with several frames plays (`Render\GifBackground.cs`): GDI+ composes each frame, which is copied into the one GPU bitmap when its delay is up. Frames with no delay count as 100 ms; GDI+ skips GIFs whose frames all have a zero delay. |
| Task HUD | A sticky note of a board's tasks over everything, toggled by a global shortcut (Settings → To-do, default **Ctrl+Alt+T**; also tray → Task HUD). Click a diamond to cycle, a title to open the task, a header to fold; + Add task; drag it anywhere (remembered); Esc hides, ← → switch boards, wheel scrolls. Per-pixel-alpha window with its own opacity (`TaskHud.cs`, `RegisterHotKey`). `RONIN_DEBUG_HUD=1` opens it at start. |
| Task background | Settings → To-do: off, one panel behind the whole list, or a box behind each line, with colour, opacity and rounding. Styles live in `TaskStyle.cs` (shared by the HUD; add a style there and its name in `extras.json`). Native: `Render\TaskBackdrop.cs`; web: `web-widgets.js`. |
| To-do from the desktop | "+ Add task" under the list opens a small box; "Clear ticked" removes ticked items. "Untick every task at midnight" for daily routines. |
| Stable monitor ids | Monitors are saved by their device interface path (model + port, `MonitorIds.cs`), not `\\.\DISPLAYn`, which can change after a driver update; old settings move over on first start. |
| Import / export | Settings → Display: all settings and layouts as one `.json` file (the wallpaper folder is kept as it is on this PC). |
| Monitor roles | Settings → Monitors (with two or more wallpaper monitors): a card per monitor with a **Role** (Shared settings / Main: clock + tasks / Side: agenda, world clocks, album, system, quote / Ambient: just the scene), its own **Background**, and a toggle per element. Roles are presets written as that monitor's own values. |
| Panorama | Settings → Background → panorama: one image covers all wallpaper monitors together (using their real positions), each screen shows its part; breathing zoom, parallax, glow, vignette and lightning are laid over the whole desktop so the seams line up. Best with a wide image. |
| Weather across monitors | Settings → Weather → "drift across monitors" (on by default): one particle simulation for the whole desktop (`Render\Sky.cs`), stepped by whichever render thread gets there first; each monitor draws its slice, so snow, rain, mist and embers cross the bezels and lightning flashes everywhere at once. |
| Widgets | Native renderer only, all draggable in Arrange. **Calendar**: an .ics link (Google / Outlook / iCloud), next N days, fetched every 15 min, repeat rules, time zones, moved and cancelled occurrences. **System ink**: CPU, GPU (Windows' GPU Engine counters) and memory as gold sparklines, sampled every 2 s only while shown. **Focus timer**: click the ring on the desktop to start / pause / resume; focus then break; Reset when paused; shared by all monitors. **Album**: the track's cover from the media session. **World clocks**: `Name=Time zone; …` (IANA or Windows names). |
| Frame-rate cap | Settings → Display → Frame rate limit (60 fps default, 30, or match display). |

### Native renderer (`Render\`)

A port of `index.html` to C#: same settings keys and defaults, same particle maths (mist, embers, snow, rain,
lightning), the ink line, time-of-day tint and glow, vignette, film grain, live weather from Open-Meteo, the
clock / date / quote / now-playing / to-do layout rules, text shadows, colon pulse and quote fade. It draws with
Direct2D into a DirectComposition swap chain on its own render thread, picking the GPU that drives the chosen
monitor. The fonts are in `fonts\` (Cinzel, Cormorant Garamond, Shippori Mincho; SIL Open Font License); Shippori
Mincho (8.6 MB) only loads if you choose that font. Animated GIF backgrounds show their first frame.

### Web renderer

The page is never edited. A small script injected by the host only calls the page's existing Lively hooks, and holds
back the page's localStorage writes until the saved settings have arrived (otherwise the to-do list would forget
ticked items on every start).

## Files

- `TrayApp.cs`: tray menu, one wallpaper per monitor, per-monitor settings, pause and frame-rate logic, to-do requests, crash recovery, renderer choice;
  `TrayApp.Arrange.cs`: Arrange mode (drag, resize, move between monitors, undo); `TrayApp.Layouts.cs`: saved layouts, import / export
- `InputBox.cs`: the small text prompt (layout names, new tasks); `MonitorIds.cs`: stable monitor names
- `Render\GpuWorker.cs`: one render thread per GPU; `Render\VideoBackground.cs`, `Render\GifBackground.cs`: moving backgrounds
- `TrayApp.WebWidgets.cs` + `web-widgets.js`: widgets in the web renderer; `TrayApp.Updates.cs` + `Updater.cs`: updates
- `..\tools\publish.ps1`, `..\tools\install.ps1`: releases and installing
- `Render\Sky.cs`: particle simulation (per monitor or shared); `Render\Widgets.cs` + `Render\WidgetData.cs`: the widgets and their data (timer, system sampler, calendar / .ics parser)
- `extras.json`: the host's own settings (widgets, panorama, shared weather), shown after the wallpaper's `LivelyProperties.json`, which stays untouched
- `ArrangeOverlay.cs`: the Arrange layer drawn over each monitor; `TrayChrome.cs`: tray menu colours, icon, Explorer-restart watcher
- `IWallpaperView.cs`: what both renderers provide
- `Render\NativeWallpaper.cs`: native window + render thread; `Render\Scene.cs` (background, effects, particles, ink line),
  `Render\Hud.cs` (text and to-dos), `Render\Weather.cs`, `Render\Text.cs` (fonts), `Render\Gfx.cs` (Direct2D setup)
- `WallpaperForm.cs`: the WebView2 window and the injected bridge script
- `DesktopEmbed.cs`: getting behind the icons
- `AudioFeed.cs`, `MediaFeed.cs`: music data
- `DesktopInput.cs`: desktop click forwarding, "is an app covering the monitor?"
- `SettingsForm.cs` + `settings.html`: settings window
- `AppSettings.cs`: settings file, Start with Windows, log (`%APPDATA%\RoninHost\log.txt`)

Advanced (in `settings.json` only): `embedMode` = `"progman"` (default) or `"workerw"`, `wallpaperFolder`.
Debug: `RONIN_DEBUG=1` logs the wallpaper's state every 5 seconds; with the native renderer,
`RONIN_DEBUG_ARRANGE=1` opens Arrange mode at start; `RONIN_DEBUG_HOVER=n` shows task n's edit controls;
`RONIN_DEBUG_TASKS=1` opens the Tasks window at start; `RONIN_DEBUG_CLICK` takes several points (`x,y;x,y`, one per 5 s);
`RONIN_DEBUG_UPDATE=install` checks for an update a few seconds after start and installs it without asking; `RONIN_DEBUG_CLICK=x,y` replays one desktop click at those
desktop pixels; `RONIN_DEBUG_SHOT=<folder>` makes every monitor save its next frame as `<folder>\shot_DISPLAYn.png`
whenever `<folder>\shot.req` is created or touched.

Notes from building it: `--in-process-gpu` makes WebView2 fall back to software compositing (19 fps, a full CPU core),
so it is not used; and with Vortice, font files had to be loaded from memory (loading by path only worked for the first file).
