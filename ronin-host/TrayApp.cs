using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace RoninHost;

/// <summary>The app: tray icon, one wallpaper window per chosen monitor, and everything that feeds them.</summary>
internal sealed partial class TrayApp : ApplicationContext, IArrangeHost
{
    readonly AppSettings settings = AppSettings.Load();
    readonly SynchronizationContext ui;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem pauseItem, arrangeItem, layoutsMenu, monitorMenu, startupItem, updateItem, hudItem;
    readonly System.Windows.Forms.Timer frameTimer = new() { Interval = 33 };   // audio + parallax, ~30/s
    readonly System.Windows.Forms.Timer coverTimer = new() { Interval = 1000 }; // fullscreen / maximized check
    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 600 };
    readonly System.Windows.Forms.Timer debugTimer = new() { Interval = 5000 };
    readonly ShellWatcher shellWatcher;

    CoreWebView2Environment? env; // only created when needed: the web renderer or the settings window
    PropertySchema? schema;
    SettingsForm? settingsForm;
    AudioFeed? audio;
    MediaFeed? media;
    DesktopClicks? clicks;

    /// <summary>One wallpaper on one monitor.</summary>
    sealed class Slot
    {
        public required IWallpaperView View;
        public bool CoverPaused, Paused;
        /// <summary>Running slower because a fullscreen app has focus on another monitor.</summary>
        public bool Slowed;
        public string Device => Id(View.Screen);
    }
    readonly List<Slot> slots = new();

    bool userPaused;
    Point lastCursor = new(int.MinValue, 0);
    bool exiting;

    readonly EventWaitHandle quitSignal = new(false, EventResetMode.AutoReset, Program.QuitEvent);
    readonly EventWaitHandle settingsSignal = new(false, EventResetMode.AutoReset, Program.ShowSettingsEvent);
    readonly RegisteredWaitHandle quitWait, settingsWait;
    readonly bool openSettingsAtStart;

    public TrayApp(bool openSettings = false)
    {
        openSettingsAtStart = openSettings;
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        ui = SynchronizationContext.Current!;

        pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        arrangeItem = new ToolStripMenuItem("Arrange layout…", null, (_, _) => { if (Arranging) EndArrange(); else StartArrange(); });
        layoutsMenu = new ToolStripMenuItem("Layouts");
        monitorMenu = new ToolStripMenuItem("Monitors");
        startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => SetStartup(!AppSettings.StartWithWindows));
        updateItem = new ToolStripMenuItem("Check for updates", null, (_, _) => { if (updateAvailable != null) InstallUpdate(); else _ = CheckForUpdatesAsync(manual: true); });
        var menu = new ContextMenuStrip { Renderer = new InkRenderer(), ShowImageMargin = false };
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Tasks…", null, (_, _) => OpenTaskBoard()) { ForeColor = InkRenderer.Text });
        hudItem = new ToolStripMenuItem("Task HUD", null, (_, _) => ToggleTaskHud());
        menu.Items.Add(hudItem);
        menu.Items.Add(arrangeItem);
        menu.Items.Add(layoutsMenu);
        menu.Items.Add(pauseItem);
        menu.Items.Add(monitorMenu);
        menu.Items.Add(startupItem);
        menu.Items.Add(updateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));
        menu.Opening += (_, _) => RefreshMenu();
        foreach (ToolStripItem i in menu.Items) i.ForeColor = InkRenderer.Text;

        tray = new NotifyIcon { Icon = TrayIcons.App, Text = "Ronin", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };

        frameTimer.Tick += (_, _) => Frame();
        coverTimer.Tick += (_, _) => CheckCovered();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); settings.Save(); };
        debugTimer.Tick += async (_, _) => await DebugDump();

        shellWatcher = new ShellWatcher(() =>
        {
            Log.Write("Explorer restarted; re-attaching");
            _ = RebuildWallpaperAsync(1500);
        });
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        // another "RoninHost" launch: --quit stops us, a plain launch opens settings
        quitWait = ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => ui.Post(_ => Quit(), null), null, -1, false);
        settingsWait = ThreadPool.RegisterWaitForSingleObject(settingsSignal, (_, _) => ui.Post(_ => OpenSettings(), null), null, -1, false);

        _ = StartAsync();
    }

    async Task StartAsync()
    {
        try
        {
            var folder = FindWallpaperFolder();
            if (folder == null) { ExitThread(); return; }
            schema = new PropertySchema(folder);
            Log.Write($"start: wallpaper={folder}, renderer={settings.Renderer}");

            audio = new AudioFeed();
            media = new MediaFeed(ui);
            media.Changed += () => { foreach (var s in slots) s.View.SendTrack(media.Title, media.Artist); };
            media.ArtChanged += () => { foreach (var s in slots) s.View.SendAlbumArt(media.Art); };
            _ = media.StartAsync();

            clicks = new DesktopClicks(ui) { IsOwnWindow = h => slots.Any(s => s.View.Owns(h)) };
            clicks.Click += p =>
            {
                var s = SlotAt(p);
                if (s == null || s.Paused || Arranging) return;
                Log.Write($"desktop click at {p} ({s.Device})");
                s.View.SendClick(p);
            };

            MigrateMonitorIds();
            StartTasks();
            StartTaskHud();
            await RebuildWallpaperAsync(0);
            coverTimer.Start();
            webWidgetTimer.Tick += (_, _) => { if (slots.Any(s => s.View is WallpaperForm)) PushWebWidgets(); };
            webWidgetTimer.Start();
            if (Environment.GetEnvironmentVariable("RONIN_DEBUG") == "1") debugTimer.Start();
            if (openSettingsAtStart) OpenSettings();
            if (Environment.GetEnvironmentVariable("RONIN_DEBUG_ARRANGE") == "1") StartArrange(); // diagnostics
            if (Environment.GetEnvironmentVariable("RONIN_DEBUG_UPDATE") == "install") DebugUpdate();
            if (Environment.GetEnvironmentVariable("RONIN_DEBUG_TASKS") == "1") OpenTaskBoard(); // diagnostics
            if (Environment.GetEnvironmentVariable("RONIN_DEBUG_HUD") == "1") ToggleTaskHud(); // diagnostics
        }
        catch (Exception e)
        {
            Log.Write("start failed: " + e);
            MessageBox.Show("Ronin could not start:\n\n" + e.Message +
                (e is WebView2RuntimeNotFoundException ? "\n\nInstall the Microsoft Edge WebView2 Runtime and try again." : ""),
                "Ronin", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Quit();
        }
    }

    Slot? SlotAt(Point p) => slots.FirstOrDefault(s => s.View.Screen.Bounds.Contains(p));
    Slot? SlotFor(Screen screen) => slots.FirstOrDefault(s => s.Device == Id(screen));
    static string Id(Screen s) => MonitorIds.For(s);

    Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoninHost", "WebView2");
        // ThrottleMainFrameTo60Hz: on a high-refresh screen (280 Hz here) Chromium otherwise runs the page
        // at the full refresh rate; measured 7.3% -> 5.6% CPU with it.
        var args = "--disable-background-networking --disable-component-update --disable-sync --no-first-run " +
                   "--enable-features=ThrottleMainFrameTo60Hz " +
                   "--disable-features=msSmartScreenProtection,Translate,msEdgeTranslate,AutofillServerCommunication,msWebOOUI,msPdfOOUI";
        // note: --in-process-gpu looks like a memory saver but makes Chromium fall back to software
        // compositing (measured: ~19 fps and a full CPU core), so it is deliberately not used.
        args += " " + Environment.GetEnvironmentVariable("RONIN_BROWSER_ARGS");
        Log.Write("browser args: " + args.Trim());
        return CoreWebView2Environment.CreateAsync(null, dataDir, new CoreWebView2EnvironmentOptions(args.Trim()) { AreBrowserExtensionsEnabled = false });
    }

    // ---------------- crash recovery ----------------

    readonly Queue<long> crashTimes = new();
    bool recovering;

    async Task<CoreWebView2Environment> EnsureEnvAsync() => env ??= await CreateEnvironmentAsync();

    async Task RecoverAsync(string kind)
    {
        if (recovering || exiting) return;
        long now = Environment.TickCount64;
        crashTimes.Enqueue(now);
        while (crashTimes.Count > 0 && now - crashTimes.Peek() > 120_000) crashTimes.Dequeue();
        if (crashTimes.Count > 4)
        {
            Log.Write("too many wallpaper crashes in 2 minutes; giving up until restarted");
            tray.ShowBalloonTip(5000, "Ronin", "The wallpaper keeps crashing, so it was stopped. Quit and reopen Ronin to try again.", ToolTipIcon.Warning);
            return;
        }
        recovering = true;
        try
        {
            Log.Write($"recovering from {kind}");
            if (kind == nameof(CoreWebView2ProcessFailedKind.BrowserProcessExited))
            {
                settingsForm?.Close();
                CloseAllWalls();
                await Task.Delay(1500);
                env = null; // a new browser process on next use
            }
            await RebuildWallpaperAsync(1000);
        }
        catch (Exception e) { Log.Write("recovery failed: " + e); }
        finally { recovering = false; }
    }

    // ---------------- wallpaper windows ----------------

    /// <summary>Settings saved under Windows' "\\.\DISPLAYn" names move to the stable monitor ids (once).</summary>
    void MigrateMonitorIds()
    {
        bool changed = false;
        foreach (var s in Screen.AllScreens)
        {
            string old = s.DeviceName, id = Id(s);
            if (old == id) continue;
            for (int i = 0; i < settings.Monitors.Count; i++)
                if (settings.Monitors[i] == old) { settings.Monitors[i] = id; changed = true; }
            if (settings.Monitor == old) { settings.Monitor = id; changed = true; }
            if (settings.MonitorProperties.Remove(old, out var ov) && !settings.MonitorProperties.ContainsKey(id))
            { settings.MonitorProperties[id] = ov; changed = true; }
        }
        if (changed) { Log.Write("monitor ids: moved settings to stable ids"); settings.Save(); }
        foreach (var s in Screen.AllScreens) Log.Write($"monitor {s.DeviceName} = {Id(s)}");
    }

    /// <summary>Device names the user chose (the old single-monitor setting if the list was never set).</summary>
    List<string> ChosenDevices() =>
        settings.Monitors.Count > 0 ? settings.Monitors.ToList() : new List<string> { settings.Monitor };

    /// <summary>The chosen monitors that are connected right now (the main one if none of them are).</summary>
    List<Screen> TargetScreens()
    {
        var chosen = ChosenDevices();
        var list = Screen.AllScreens.Where(s => chosen.Contains(Id(s))).ToList();
        if (list.Count == 0) list.Add(Screen.PrimaryScreen ?? Screen.AllScreens[0]);
        return list.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
    }

    /// <summary>Settings for one monitor: defaults, then shared settings, then that monitor's own overrides.</summary>
    JsonObject EffectiveFor(string device)
    {
        var o = schema!.Effective(settings.Properties);
        if (settings.MonitorProperties.TryGetValue(device, out var ov))
            foreach (var (k, v) in ov)
                if (o.ContainsKey(k) && v != null) o[k] = v.DeepClone();
        return o;
    }

    /// <summary>[x, y, w, h, count]: the rectangle around every wallpaper monitor (physical px) and how many there are.</summary>
    JsonArray SpanInfo()
    {
        var screens = TargetScreens();
        var r = screens.Select(s => s.Bounds).Aggregate(Rectangle.Union);
        return new JsonArray(r.X, r.Y, r.Width, r.Height, screens.Count);
    }

    void CloseAllWalls()
    {
        var old = slots.ToList();
        slots.Clear(); // first, so their Gone events don't trigger a rebuild
        foreach (var s in old) { s.View.CloseView(); s.View.Dispose(); }
    }

    bool rebuilding, rebuildAgain;
    async Task RebuildWallpaperAsync(int delayMs)
    {
        if (exiting || schema == null) return;
        if (rebuilding) { rebuildAgain = true; return; }
        rebuilding = true;
        try
        {
            do
            {
                rebuildAgain = false;
                if (delayMs > 0) await Task.Delay(delayMs);
                EndArrange();
                frameTimer.Stop();
                audio?.Stop();
                CloseAllWalls();

                foreach (var screen in TargetScreens())
                {
                    if (exiting) return;
                    try { await StartWallAsync(screen); }
                    catch (Exception e) { Log.Write($"wallpaper start failed on {Id(screen)}: " + e); }
                }
                await ApplyPauseAsync();
            } while (rebuildAgain && !exiting);
        }
        finally { rebuilding = false; }
    }

    async Task StartWallAsync(Screen screen)
    {
        var dev = Id(screen);
        IWallpaperView w = settings.Renderer == "web"
            ? new WallpaperForm(screen, await EnsureEnvAsync(), schema!.Folder, settings.EmbedMode)
            : new Render.NativeWallpaper(screen, schema!.Folder, settings.EmbedMode);
        var slot = new Slot { View = w };
        w.Ready += () =>
        {
            w.SendFpsLimit(FpsFor(slot));
            var props = EffectiveFor(dev);
            props["__span"] = SpanInfo(); // where the other monitors are (panorama, shared sky)
            w.SendProperties(props);
            if (media != null) { w.SendTrack(media.Title, media.Artist); w.SendAlbumArt(media.Art); }
            w.SendBoard(BoardJsonFor(dev));
            if (w is WallpaperForm wf) wf.SendWidgetData(WebWidgetData(dev));
        };
        w.Crashed += kind => { if (slots.Contains(slot)) _ = RecoverAsync(kind); };
        w.Request += r => OnWallpaperRequest(slot, r);
        w.Gone += () =>
        {
            // destroyed from outside (e.g. Explorer went away): rebuild
            if (slots.Contains(slot) && !exiting) { slots.Remove(slot); _ = RebuildWallpaperAsync(2000); }
        };
        slots.Add(slot);
        Log.Write($"wallpaper on {dev} {screen.Bounds}");
        await w.StartAsync();
    }

    void OnDisplayChanged(object? sender, EventArgs e) =>
        ui.Post(_ => { Log.Write("display settings changed"); MonitorIds.Reset(); _ = RebuildWallpaperAsync(1000); }, null);

    // ---------------- per-frame feed ----------------

    void Frame()
    {
        if (!slots.Any(s => !s.Paused)) return;

        var bins = audio?.NextBins();
        if (bins != null) foreach (var s in slots) if (!s.Paused) s.View.SendAudio(bins);

        // parallax: the page listens for mouse moves it can't receive under the icons
        if (Native.GetCursorPos(out var c))
        {
            var p = new Point(c.X, c.Y);
            if (p != lastCursor && SlotAt(p) is { Paused: false } s) { lastCursor = p; s.View.SendMouseMove(p); }
        }
    }

    // ---------------- pause ----------------

    bool ShouldPause(Slot s) => userPaused || (s.CoverPaused && !Arranging);

    void CheckCovered()
    {
        bool changed = false;
        foreach (var s in slots)
        {
            string? reason = settings.PauseWhenCovered ? CoverCheck.CoveringWindow(s.View.Screen) : null;
            bool covered = reason != null;
            if (covered == s.CoverPaused) continue;
            s.CoverPaused = covered;
            Log.Write(covered ? $"{s.Device} covered by {reason}" : $"{s.Device} uncovered");
            changed = true;
        }
        if (changed) _ = ApplyPauseAsync();

        // a fullscreen app (usually a game) has focus: the other monitors' wallpapers slow down to give it the GPU
        var game = settings.SlowWhenFullscreen ? CoverCheck.FullscreenForeground() : null;
        foreach (var s in slots)
        {
            bool slow = game != null && game.DeviceName != s.View.Screen.DeviceName;
            if (slow == s.Slowed) continue;
            s.Slowed = slow;
            s.View.SendFpsLimit(FpsFor(s));
            Log.Write(slow ? $"{s.Device} slowed: fullscreen app on {game!.DeviceName}" : $"{s.Device} back to full speed");
        }

        CheckTodoDay();
        CheckSchedule();
        MaybeCheckUpdates();
    }

    /// <summary>Frame-rate cap for one monitor: the main setting, the side-monitor setting, 15 fps while slowed.</summary>
    int FpsFor(Slot s)
    {
        int fps = settings.FpsLimit; // 0 = match the display
        if (!s.View.Screen.Primary && settings.SideFpsLimit > 0 && (fps == 0 || settings.SideFpsLimit < fps)) fps = settings.SideFpsLimit;
        if (s.Slowed) fps = fps == 0 ? 15 : Math.Min(fps, 15);
        return fps;
    }

    // ---------------- to-do: daily reset; everything else is in TrayApp.Tasks.cs ----------------

    void CheckTodoDay()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (settings.TodoDate == today || schema == null) return;
        bool daily = Bool(schema.Effective(settings.Properties)["todoDaily"]);
        if (daily && settings.TodoDate.Length > 0)
        {
            int n = tasks.ResetDone();
            if (n > 0) Log.Write("tasks: new day, " + n + " finished task(s) back to not started");
        }
        settings.TodoDate = today;
        QueueSave();
    }

    void OnWallpaperRequest(Slot slot, string request)
    {
        if (HandleTimerRequest(slot, request)) return;
        HandleTaskRequest(request);
    }

    async Task ApplyPauseAsync()
    {
        var list = slots.ToList();
        bool anyActive = list.Any(s => !ShouldPause(s));
        if (!anyActive) { frameTimer.Stop(); audio?.Stop(); }
        foreach (var s in list)
        {
            bool p = ShouldPause(s);
            bool resumed = s.Paused && !p;
            s.Paused = p;
            await s.View.SetPausedAsync(p);
            if (resumed && media != null && slots.Contains(s)) s.View.SendTrack(media.Title, media.Artist); // may have changed while suspended
        }
        if (anyActive && slots.Count > 0) { audio?.Start(); frameTimer.Start(); }
        tray.Text = userPaused ? "Ronin (paused)"
            : list.Count > 0 && list.All(s => s.Paused) ? "Ronin (sleeping: apps are fullscreen)"
            : list.Count > 1 ? $"Ronin ({list.Count} monitors)" : "Ronin";
    }

    void TogglePause()
    {
        userPaused = !userPaused;
        _ = ApplyPauseAsync();
    }

    // ---------------- settings ----------------

    JsonObject SettingsInit()
    {
        var monitors = new JsonArray();
        int n = 1;
        foreach (var s in Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y))
            monitors.Add(new JsonObject { ["id"] = Id(s), ["name"] = MonitorName(s, n++) });
        return new JsonObject
        {
            ["schema"] = schema!.Root.DeepClone(),
            ["values"] = schema.Effective(settings.Properties),
            ["folders"] = schema.FolderChoices(),
            ["host"] = new JsonObject
            {
                ["monitors"] = monitors,
                ["selected"] = new JsonArray(TargetScreens().Select(s => (JsonNode)Id(s)).ToArray()),
                ["perMonitor"] = settings.MonitorProperties.Any(kv => kv.Value.Count > 0),
                ["cards"] = MonitorCards(),
                ["boards"] = new JsonArray(tasks.Data.Boards.Select(b => (JsonNode)new JsonObject { ["id"] = b.Id, ["name"] = b.Name }).ToArray()),
                // each wallpaper monitor's values, and which keys it has its own value for (Settings' "Editing" selector)
                ["monValues"] = new JsonObject(TargetScreens().Select(s => KeyValuePair.Create(Id(s), (JsonNode?)EffectiveFor(Id(s))))),
                ["overrides"] = new JsonObject(TargetScreens().Select(s => KeyValuePair.Create(Id(s), (JsonNode?)new JsonArray(
                    (settings.MonitorProperties.TryGetValue(Id(s), out var ov) ? ov.Select(kv => kv.Key).Where(k => !k.StartsWith("__")) : Enumerable.Empty<string>())
                    .Select(k => (JsonNode)k).ToArray())))),
                ["pauseWhenCovered"] = settings.PauseWhenCovered,
                ["fps"] = settings.FpsLimit,
                ["sideFps"] = settings.SideFpsLimit,
                ["slowFullscreen"] = settings.SlowWhenFullscreen,
                ["layouts"] = new JsonArray(settings.Layouts.Keys.Select(k => (JsonNode)k).ToArray()),
                ["layout"] = settings.CurrentLayout,
                ["schedule"] = ScheduleJson(),
                ["renderer"] = settings.Renderer,
                ["startup"] = AppSettings.StartWithWindows,
                ["version"] = Updater.Current.ToString(3),
                ["updateSource"] = Updater.Source(settings),
                ["checkUpdates"] = settings.CheckUpdates,
                ["updateStatus"] = updateStatus,
                ["updateAvailable"] = updateAvailable?.Version.ToString(3),
            },
        };
    }

    static string MonitorName(Screen s, int n) =>
        $"Monitor {n} — {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (main)" : "")}";

    bool openingSettings;
    async void OpenSettings()
    {
        if (schema == null || exiting) return;
        if (settingsForm != null) { settingsForm.Activate(); return; }
        if (openingSettings) return;
        openingSettings = true;
        try
        {
            var e = await EnsureEnvAsync();
            settingsForm = new SettingsForm(e, SettingsInit);
            settingsForm.Message += OnSettingsMessage;
            settingsForm.FormClosed += (_, _) =>
            {
                settingsForm?.Dispose();
                settingsForm = null;
                // with the native renderer nothing else needs the browser: let its processes exit
                ReleaseBrowserIfIdle();
            };
            settingsForm.Show();
        }
        catch (Exception ex)
        {
            Log.Write("settings failed: " + ex);
            MessageBox.Show("The settings window needs the Microsoft Edge WebView2 Runtime:\n\n" + ex.Message, "Ronin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { openingSettings = false; }
    }

    void OnSettingsMessage(JsonObject m)
    {
        switch (m["t"]?.GetValue<string>())
        {
            case "set" when m["mon"] is JsonValue monId:
            {
                // one monitor's own value (Settings' "Editing: Monitor n")
                var dev = monId.GetValue<string>();
                var k1 = m["k"]!.GetValue<string>();
                if (!settings.MonitorProperties.TryGetValue(dev, out var ov1)) settings.MonitorProperties[dev] = ov1 = new JsonObject();
                ov1[k1] = m["v"]?.DeepClone();
                if (ShowKeys.Contains(k1)) ov1["__role"] = "custom";
                slots.FirstOrDefault(s => s.Device == dev)?.View.SendProperties(new JsonObject { [k1] = m["v"]?.DeepClone() });
                QueueSave();
                break;
            }
            case "unset":
            {
                var dev = m["mon"]!.GetValue<string>();
                var k1 = m["k"]!.GetValue<string>();
                var slot1 = slots.FirstOrDefault(s => s.Device == dev);
                var before1 = EffectiveFor(dev);
                if (settings.MonitorProperties.TryGetValue(dev, out var ov1)) ov1.Remove(k1);
                PruneOverrides();
                if (slot1 != null) SendDiff(slot1, before1);
                QueueSave();
                settingsForm?.SendInit();
                break;
            }
            case "set":
                var key = m["k"]!.GetValue<string>();
                var val = m["v"]?.DeepClone();
                settings.Properties[key] = val;
                // a setting changed here applies to every monitor, replacing any per-monitor value
                foreach (var ov in settings.MonitorProperties.Values) ov.Remove(key);
                PruneOverrides();
                foreach (var s in slots) s.View.SendProperties(new JsonObject { [key] = val?.DeepClone() });
                TaskHudSettingChanged(key);
                QueueSave();
                break;
            case "reset":
                settings.Properties = new JsonObject();
                settings.MonitorProperties.Clear();
                foreach (var s in slots) s.View.SendProperties(EffectiveFor(s.Device));
                QueueSave();
                settingsForm?.SendInit();
                RefreshOverlays();
                RegisterHudKey(announce: true);
                taskHud?.RefreshHud();
                break;
            case "mon" when m["k"]?.GetValue<string>() == "board":
                SetMonitorBoard(m["id"]!.GetValue<string>(), m["v"]?.GetValue<string>() ?? "");
                break;
            case "mon":
                SetMonitorValue(m["id"]!.GetValue<string>(), m["k"]!.GetValue<string>(), m["v"]?.DeepClone());
                break;
            case "host":
                var k = m["k"]!.GetValue<string>();
                if (k == "monitors") SetMonitors(m["v"]!.AsArray().Select(v => v!.GetValue<string>()).ToList());
                else if (k == "arrange") StartArrange();
                else if (k == "openTasks") OpenTaskBoard();
                else if (k == "export") ExportSettings();
                else if (k == "checkUpdates") { settings.CheckUpdates = m["v"]!.GetValue<bool>(); QueueSave(); }
                else if (k == "updateSource") { settings.UpdateSource = m["v"]?.GetValue<string>()?.Trim() ?? ""; settings.Save(); updateAvailable = null; _ = CheckForUpdatesAsync(manual: false); }
                else if (k == "checkNow") _ = CheckForUpdatesAsync(manual: true);
                else if (k == "installUpdate") InstallUpdate();
                else if (k == "import") ImportSettings();
                else if (k == "layout") ApplyLayout(m["v"]!.GetValue<string>());
                else if (k == "saveLayout") { SaveLayoutAs(); settingsForm?.SendInit(); }
                else if (k == "schedule") { SetSchedule(m["v"]!.AsArray()); settingsForm?.SendInit(); }
                else if (k == "slowFullscreen") { settings.SlowWhenFullscreen = m["v"]!.GetValue<bool>(); QueueSave(); CheckCovered(); }
                else if (k == "sideFps")
                {
                    settings.SideFpsLimit = (int)m["v"]!.GetValue<double>();
                    foreach (var s in slots) s.View.SendFpsLimit(FpsFor(s));
                    QueueSave();
                }
                else if (k == "pauseWhenCovered") { settings.PauseWhenCovered = m["v"]!.GetValue<bool>(); QueueSave(); CheckCovered(); }
                else if (k == "startup") SetStartup(m["v"]!.GetValue<bool>());
                else if (k == "renderer")
                {
                    var r = m["v"]!.GetValue<string>() == "web" ? "web" : "native";
                    if (r != settings.Renderer) { settings.Renderer = r; settings.Save(); _ = RebuildWallpaperAsync(0); }
                }
                else if (k == "fps")
                {
                    settings.FpsLimit = (int)m["v"]!.GetValue<double>();
                    foreach (var s in slots) s.View.SendFpsLimit(FpsFor(s));
                    QueueSave();
                }
                break;
        }
    }

    void QueueSave() { saveTimer.Stop(); saveTimer.Start(); }

    // ---------------- per-monitor roles, backgrounds and elements ----------------

    static readonly string[] ShowKeys = { "showClock", "showQuote", "showMusic", "showTodo", "showCalendar", "showSystem", "showTimer", "showAlbum", "showWorld" };

    /// <summary>
    /// Presets for what a monitor shows. They are written as that monitor's own values, so Arrange and the
    /// toggles in Settings can still change them afterwards (the monitor then counts as "custom").
    /// </summary>
    static JsonObject? RolePreset(string role)
    {
        var o = new JsonObject();
        switch (role)
        {
            case "main":
                foreach (var k in ShowKeys) o[k] = k is "showClock" or "showQuote" or "showMusic" or "showTodo";
                break;
            case "side":
                foreach (var k in ShowKeys) o[k] = k is "showQuote" or "showCalendar" or "showWorld" or "showAlbum" or "showSystem";
                o["quoteLayout"] = 1.0; o["quoteX"] = 50.0; o["quoteY"] = 50.0;
                o["calX"] = 0.0; o["calY"] = 0.0;
                o["worldX"] = 100.0; o["worldY"] = 0.0;
                o["albumX"] = 0.0; o["albumY"] = 100.0;
                o["sysX"] = 100.0; o["sysY"] = 100.0;
                break;
            case "ambient":
                foreach (var k in ShowKeys) o[k] = false;
                break;
            default: return null;
        }
        return o;
    }

    JsonArray MonitorCards()
    {
        var cards = new JsonArray();
        int n = 1;
        var all = Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
        var chosen = TargetScreens().Select(Id).ToHashSet();
        foreach (var s in all)
        {
            int num = n++;
            if (!chosen.Contains(Id(s))) continue;
            settings.MonitorProperties.TryGetValue(Id(s), out var ov);
            var eff = EffectiveFor(Id(s));
            var show = new JsonObject();
            foreach (var k in ShowKeys) show[k] = Bool(eff[k]);
            cards.Add(new JsonObject
            {
                ["id"] = Id(s),
                ["name"] = MonitorName(s, num),
                ["role"] = ov?["__role"]?.GetValue<string>() ?? (ov == null || ov.Count == 0 ? "shared" : "custom"),
                ["background"] = ov != null && ov.TryGetPropertyValue("background", out var bg) ? bg?.DeepClone() : null,
                ["show"] = show,
                ["board"] = tasks.BoardFor(Id(s))?.Id ?? "",
            });
        }
        return cards;
    }

    /// <summary>A change from a monitor's card in Settings: its role, its own background, or one element on / off.</summary>
    void SetMonitorValue(string device, string key, JsonNode? value)
    {
        var slot = slots.FirstOrDefault(s => s.Device == device);
        if (!settings.MonitorProperties.TryGetValue(device, out var ov))
            settings.MonitorProperties[device] = ov = new JsonObject();
        var before = EffectiveFor(device);

        if (key == "role")
        {
            var role = value?.GetValue<string>() ?? "shared";
            if (role == "shared")
            {
                // back to the shared settings for everything this card controls
                foreach (var k in ShowKeys) ov.Remove(k);
                foreach (var k in RolePreset("side")!.Select(kv => kv.Key)) ov.Remove(k);
                ov.Remove("__role");
            }
            else if (RolePreset(role) is { } preset)
            {
                foreach (var (k, v) in preset) ov[k] = v?.DeepClone();
                ov["__role"] = role;
            }
        }
        else if (key == "background")
        {
            var file = value?.GetValue<string>() ?? "";
            if (file.Length == 0) ov.Remove("background"); else ov["background"] = file;
        }
        else if (ShowKeys.Contains(key))
        {
            ov[key] = value is JsonValue jv && jv.TryGetValue(out bool b) && b;
            ov["__role"] = "custom";
        }
        else return;

        PruneOverrides();
        // send only what actually changed on that monitor
        var after = EffectiveFor(device);
        var diff = new JsonObject();
        foreach (var (k, v) in after)
            if (!JsonNode.DeepEquals(v, before[k])) diff[k] = v?.DeepClone();
        if (slot != null && diff.Count > 0) slot.View.SendProperties(diff);
        Log.Write($"monitor {device}: {key} = {value?.ToJsonString()} ({diff.Count} changed)");
        QueueSave();
        settingsForm?.SendInit();
        RefreshOverlays();
    }

    void PruneOverrides()
    {
        foreach (var dev in settings.MonitorProperties.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            settings.MonitorProperties.Remove(dev);
    }

    void SetMonitors(List<string> devices)
    {
        var connected = Screen.AllScreens.Select(Id).ToHashSet();
        devices = devices.Where(connected.Contains).Distinct().ToList();
        if (devices.Count == 0) return; // always keep at least one
        // keep chosen monitors that are unplugged right now, so they come back when reconnected
        var keep = ChosenDevices().Where(d => !connected.Contains(d) && d.Length > 0);
        var next = devices.Concat(keep).ToList();
        if (next.ToHashSet().SetEquals(ChosenDevices())) return;
        settings.Monitors = next;
        settings.Save();
        Log.Write("monitors: " + string.Join(", ", next));
        _ = RebuildWallpaperAsync(0);
        settingsForm?.SendInit();
    }

    void ToggleMonitor(string device)
    {
        var current = TargetScreens().Select(Id).ToList();
        if (current.Contains(device)) { if (current.Count > 1) current.Remove(device); }
        else current.Add(device);
        SetMonitors(current);
    }

    void SetStartup(bool on)
    {
        try { AppSettings.StartWithWindows = on; }
        catch (Exception e) { Log.Write("startup toggle failed: " + e.Message); }
    }

    void RefreshMenu()
    {
        pauseItem.Text = userPaused ? "Resume" : "Pause";
        arrangeItem.Text = Arranging ? "Finish arranging" : "Arrange layout…";
        FillLayoutsMenu(layoutsMenu);
        RefreshUpdateItem();
        startupItem.Checked = AppSettings.StartWithWindows;
        monitorMenu.DropDownItems.Clear();
        var current = TargetScreens().Select(Id).ToHashSet();
        int n = 1;
        var all = Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
        foreach (var s in all)
        {
            var dev = Id(s);
            var item = new ToolStripMenuItem(MonitorName(s, n++), null, (_, _) => ToggleMonitor(dev))
            { Checked = current.Contains(dev), ForeColor = InkRenderer.Text };
            monitorMenu.DropDownItems.Add(item);
        }
        if (all.Count > 1)
        {
            monitorMenu.DropDownItems.Add(new ToolStripSeparator());
            monitorMenu.DropDownItems.Add(new ToolStripMenuItem("All monitors", null, (_, _) => SetMonitors(all.Select(Id).ToList()))
            { Checked = current.Count == all.Count, ForeColor = InkRenderer.Text });
        }
        if (monitorMenu.DropDown is ToolStripDropDownMenu dd) dd.ShowImageMargin = false;
        monitorMenu.DropDown.Renderer = new InkRenderer();
    }

    // ---------------- wallpaper folder ----------------

    string? FindWallpaperFolder()
    {
        static bool Ok(string? d) => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "index.html"));
        if (Ok(settings.WallpaperFolder)) return settings.WallpaperFolder;

        // look for a ronin-wallpaper folder next to the app or in any parent folder
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "ronin-wallpaper");
            if (Ok(c)) return c;
        }

        using var dlg = new FolderBrowserDialog { Description = "Choose the Ronin wallpaper folder (the one with index.html)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == DialogResult.OK && Ok(dlg.SelectedPath))
        {
            settings.WallpaperFolder = dlg.SelectedPath;
            settings.Save();
            return dlg.SelectedPath;
        }
        return null;
    }

    // ---------------- debug ----------------

    int debugClickIndex;

    async Task DebugDump()
    {
        foreach (var s in slots.ToList())
        {
            if (!s.View.IsReady || s.Paused) continue;
            try
            {
                Log.Write($"page {s.Device}: " + await s.View.DebugStateAsync());
                Log.Write($"layout {s.Device}: " + string.Join("; ", (await s.View.GetLayoutAsync()).Select(b => $"{b.Id} {b.Bounds}")));
                // diagnostics: RONIN_DEBUG_CLICK="x,y" (desktop pixels) replays one click there
                // several points: "x,y;x,y" (one per debug tick)
                if (Environment.GetEnvironmentVariable("RONIN_DEBUG_CLICK") is { Length: > 0 } dcs && debugClickIndex < dcs.Split(';').Length)
                {
                    var dc = dcs.Split(';')[debugClickIndex++];
                    var xy = dc.Split(',').Select(int.Parse).ToArray();
                    var p = new Point(xy[0], xy[1]);
                    SlotAt(p)?.View.SendClick(p);
                    Log.Write($"debug click at {dc} ({SlotAt(p)?.Device})");
                    break;
                }
            }
            catch (Exception e) { Log.Write("debug eval failed: " + e.Message); }
        }
        if (audio != null) Log.Write("audio: " + audio.Stats);
    }

    // ---------------- quit ----------------

    void Quit()
    {
        if (exiting) return;
        exiting = true;
        Log.Write("quit");
        frameTimer.Stop(); coverTimer.Stop(); debugTimer.Stop(); webWidgetTimer.Stop();
        EndArrange();
        if (saveTimer.Enabled) { saveTimer.Stop(); settings.Save(); }
        tasks.Flush();
        StopTaskHud();
        panelWindow?.Close();
        boardWindow?.Close();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        tray.Visible = false;
        settingsForm?.Close();
        clicks?.Dispose();
        audio?.Dispose();
        media?.Dispose();
        CloseAllWalls();
        shellWatcher.Dispose();
        quitWait.Unregister(null);
        settingsWait.Unregister(null);
        DesktopEmbed.RefreshStaticWallpaper();
        tray.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !exiting) Quit();
        base.Dispose(disposing);
    }
}
