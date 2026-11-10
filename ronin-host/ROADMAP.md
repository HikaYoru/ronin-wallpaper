# Ronin — roadmap

Phase 3 shipped: **Arrange mode** (drag the clock, quote and to-do list, live) and **several monitors at once**, with positions saved per monitor.
Phase 4 shipped (4.1–4.4): **monitor roles**, **per-monitor backgrounds and panorama**, **weather across monitors**, and five **widgets**.
Phase 5 shipped in full: Arrange resize / guides / undo / cross-monitor moves / quote width, saved and scheduled layouts, one device per GPU and frame-rate savings, GIF and video backgrounds, task editing on the desktop, per-monitor values for every setting, stable monitor IDs, import / export, web-renderer widgets and shared ticks, installer and auto-update.
Effort: S = an evening, M = a few sessions, L = a phase of its own.

## Phase 4 — use the extra screens ✅ (except 4.5)

| # | Feature | Status |
|---|---|---|
| 4.1 | **Monitor roles**: Settings → Monitors, a card per screen with Main / Side / Ambient presets and a toggle per element | Done |
| 4.2 | **Per-monitor background** and **panorama** across all monitors (real positions, including the 53 px offset) | Done |
| 4.3 | **Weather across monitors**: one shared sky; snow, rain, mist and embers cross the bezels; lightning flashes on all at once | Done |
| 4.4 | **Widgets**: calendar (.ics), system ink, focus timer, album art, world clocks; all draggable in Arrange | Done (native renderer only) |
| 4.5 | **Wide ink line**: one music line under all monitors | Skipped for now |

### Phase 4 follow-ups
- **Lyrics** next to the album art (synced lyrics from LRCLIB by track title). *(M)*
- **Timer notification**: a toast and an optional chime when a focus block ends. *(S)*
- **Widgets in the web renderer**: right now only the native renderer draws them. *(M)*
- **Panorama image picker** that suggests your widest image and shows how it is cut across the monitors. *(S)*
- **Calendar colours** per calendar and several .ics links at once. *(S)*

## Phase 5 — improve what's already there ✅

### Done in Phase 5
- Arrange: **resize** from any corner, **alignment guides** (other elements, screen thirds), **Undo / Cancel**, **drop on another monitor**
- **Saved layouts** (tray and Settings)
- **Lower frame rate on side monitors**, and **15 fps on the other monitors while a fullscreen app has focus**
- **Animated GIF backgrounds**
- To-do: **add and clear tasks on the desktop**, **untick daily**
- **Stable monitor IDs** (model + port), **import / export**

### Finished in the full Phase 5
- **One device and render thread per GPU** (your two NVIDIA monitors share one; the AMD one has its own)
- **Video backgrounds** (Media Foundation, looping, no sound)
- **Layouts by time of day**, on chosen days
- **Edit, move and delete tasks** on the desktop (hover a task)
- **Quote wrap width** from side handles in Arrange
- **Per-monitor values for every setting** (Settings → Editing: Monitor n)
- **Installer, publish script and auto-update** (`tools\`, tray → Install update)
- Web renderer: **shared ticks** and **the widgets**

### Task boards (0.7.0)
- Boards per project, shown on chosen monitors; section headers that fold; tasks with status (click the diamond), notes and sub-tasks in a panel that opens next to the task; a Tasks window to manage everything (drag to reorder, drop on a board tab to move).
- Later: due dates and a "today" view, tags / colours per board, sync with Notion through its API (needs a Notion integration token).

### Ideas for later
- Lyrics next to the album art (LRCLIB), a chime when a focus block ends, several calendars with colours.
- Hardware video decoding (DXGI device manager) for 4K video backgrounds at lower CPU.
- Panorama and shared weather in the web renderer.
- Your right-hand monitor runs on the AMD integrated GPU. That works, but plugging it into the RTX card directly would save a cross-GPU copy.
## Known limits right now
- Arrange mode draws over your apps. If a monitor is covered (as in testing, with Discord, Substance Painter and Claude open), you move the boxes but can't see the wallpaper move until you press Win+D. A future option: hide the windows on that monitor while you arrange.
- If you choose monitors in Settings and then unplug one, it is remembered and comes back when you reconnect it. If none of the chosen monitors is connected, the wallpaper falls back to the main monitor.
