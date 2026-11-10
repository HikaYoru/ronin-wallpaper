using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using static RoninHost.Native;

namespace RoninHost;

/// <summary>
/// A borderless window that shows the wallpaper's index.html in WebView2 and lives behind the desktop icons.
/// Talks to the page only through its existing Lively hooks, via a tiny bridge script.
/// </summary>
internal sealed class WallpaperForm : Form, IWallpaperView
{
    public const string HostName = "ronin.wallpaper";

    // Injected before the page's own scripts. It never changes the page; it only calls
    // the hooks the page already has (livelyPropertyListener, livelyAudioListener, livelyCurrentTrack)
    // and replays clicks / mouse moves that Windows delivers to the desktop instead of to us.
    const string Bridge = """
        (() => {
          const wv = window.chrome && window.chrome.webview;
          if (!wv) return;

          // The page first runs with its built-in defaults, before the host has sent the saved settings.
          // Anything it stores in that moment is based on defaults (Ronin's to-do list, for example,
          // "forgets" ticked items it can't see yet), so ignore storage writes until settings arrive.
          let settled = false;
          const sp = Storage.prototype, set = sp.setItem, rem = sp.removeItem, clr = sp.clear;
          sp.setItem = function (k, v) { if (settled) return set.call(this, k, v); };
          sp.removeItem = function (k) { if (settled) return rem.call(this, k); };
          sp.clear = function () { if (settled) return clr.call(this); };

          wv.addEventListener('message', e => {
            const m = e.data;
            try {
              if (m.t === 'a') { if (typeof livelyAudioListener === 'function') livelyAudioListener(m.v); }
              else if (m.t === 'p') {
                try { if (typeof livelyPropertyListener === 'function') for (const k in m.v) livelyPropertyListener(k, m.v[k]); }
                finally { settled = true; }
                if (window.__roninWidgets) window.__roninWidgets.props(m.v); // the host's widgets (web-widgets.js)
              }
              else if (m.t === 'wd') { if (window.__roninWidgets) window.__roninWidgets.data(m.v); }
              else if (m.t === 'tb') { if (window.__roninWidgets) window.__roninWidgets.board(m.v); }
              else if (m.t === 'm') { if (typeof livelyCurrentTrack === 'function') livelyCurrentTrack(m.v); }
              else if (m.t === 'g') { const l = window.wallpaperPropertyListener; if (l && l.applyGeneralProperties) l.applyGeneralProperties({ fps: m.v }); }
              else if (m.t === 'c' || m.t === 'mv') {
                const r = window.devicePixelRatio || 1, x = m.x / r, y = m.y / r;
                if (m.t === 'mv') { window.dispatchEvent(new MouseEvent('mousemove', { clientX: x, clientY: y })); return; }
                const el = document.elementFromPoint(x, y);
                if (!el) return;
                const o = { bubbles: true, cancelable: true, view: window, clientX: x, clientY: y, button: 0 };
                el.dispatchEvent(new MouseEvent('mousedown', o));
                el.dispatchEvent(new MouseEvent('mouseup', o));
                el.dispatchEvent(new MouseEvent('click', o));
              }
            } catch (err) { console.error(err); }
          });
        })();
        """;

    readonly WebView2 web;
    readonly CoreWebView2Environment env;
    readonly string folder;
    readonly string embedMode;
    public Screen Screen { get; }
    public bool IsReady { get; private set; }
    public bool IsSuspended { get; private set; }
    bool busy; // a pause/resume is in progress
    bool wantPaused;

    /// <summary>Raised once the page has loaded and can take messages.</summary>
    public event Action? Ready;
    public event Action? Gone;
    /// <summary>Raised when WebView2's browser, GPU or renderer process dies (value: which one).</summary>
    public event Action<string>? Crashed;
#pragma warning disable CS0067
    public event Action<string>? Request;
#pragma warning restore CS0067

    /// <summary>The kind of the last WebView2 process failure (the host recreates the environment for browser exits).</summary>
    public CoreWebView2ProcessFailedKind? LastFailure { get; private set; }

    public WallpaperForm(Screen screen, CoreWebView2Environment env, string folder, string embedMode)
    {
        Screen = screen;
        this.env = env;
        this.folder = folder;
        this.embedMode = embedMode;

        Text = "Ronin Wallpaper";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // start far off-screen; Attach() moves it under the icons
        Bounds = new Rectangle(-32000, -32000, screen.Bounds.Width, screen.Bounds.Height);
        BackColor = Color.FromArgb(0x0f, 0x10, 0x12);

        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(web);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            return cp;
        }
    }

    public async Task StartAsync()
    {
        Show(); // off-screen, not activated
        if (Environment.GetEnvironmentVariable("RONIN_NO_EMBED") == "1")
        {
            // diagnostics only: a plain window at the bottom of the z-order instead of behind the icons
            SetWindowPos(Handle, HWND_BOTTOM, Screen.Bounds.X, Screen.Bounds.Y, Screen.Bounds.Width, Screen.Bounds.Height, SWP_NOACTIVATE);
        }
        else if (!DesktopEmbed.Attach(Handle, Screen.Bounds, embedMode))
            throw new InvalidOperationException("Could not find the desktop window to draw behind the icons.");

        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = Environment.GetEnvironmentVariable("RONIN_DEBUG") == "1";
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;

        core.SetVirtualHostNameToFolderMapping(HostName, folder, CoreWebView2HostResourceAccessKind.Allow);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Bridge);
        using (var ws = typeof(WallpaperForm).Assembly.GetManifestResourceStream("web-widgets.js"))
        using (var wr = new StreamReader(ws!))
            await core.AddScriptToExecuteOnDocumentCreatedAsync(wr.ReadToEnd());
        // the widgets ask for things only the host can do (start / pause / reset the focus timer)
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                if (JsonNode.Parse(e.WebMessageAsJson) is JsonObject o && o["t"]?.GetValue<string>() == "req" && o["v"]?.GetValue<string>() is { } v)
                    Request?.Invoke(v);
            }
            catch (Exception ex) { Log.Write("web message failed: " + ex.Message); }
        };
        core.NavigationCompleted += (_, e) =>
        {
            Log.Write($"page loaded: success={e.IsSuccess} status={e.WebErrorStatus}");
            IsReady = e.IsSuccess;
            if (IsReady) Ready?.Invoke();
        };
        core.ProcessFailed += (_, e) =>
        {
            Log.Write($"webview process failed: {e.ProcessFailedKind} {e.Reason}");
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
                or CoreWebView2ProcessFailedKind.GpuProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                IsReady = false;
                LastFailure = e.ProcessFailedKind;
                BeginInvoke(() => Crashed?.Invoke(e.ProcessFailedKind.ToString()));
            }
        };
        core.Navigate(Environment.GetEnvironmentVariable("RONIN_DEBUG_URL") is { Length: > 0 } url ? url : $"https://{HostName}/index.html");
    }

    public void Post(string json)
    {
        if (!IsReady || IsSuspended || busy || web.CoreWebView2 == null) return;
        try { web.CoreWebView2.PostWebMessageAsJson(json); } catch (Exception e) { Log.Write("post failed: " + e.Message); }
    }

    public void SendProperties(JsonObject props) =>
        Post(new JsonObject { ["t"] = "p", ["v"] = props.DeepClone() }.ToJsonString());

    /// <summary>Frame cap through the page's existing Wallpaper Engine "fps" hook (0 = no cap).</summary>
    public void SendFpsLimit(int fps) => Post($"{{\"t\":\"g\",\"v\":{(fps > 0 ? fps : 1000)}}}");

    public void SendAudio(float[] bins) => Post(AudioFeed.Format(bins));

    public void CloseView() => Close();

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Gone?.Invoke();
    }

    /// <summary>Page state for the RONIN_DEBUG log (frame rate, audio, track, to-dos, settings).</summary>
    public async Task<string> DebugStateAsync()
    {
        if (Environment.GetEnvironmentVariable("RONIN_DEBUG_URL") is { Length: > 0 })
        {
            // diagnostics page (e.g. edge://gpu): its text, including shadow DOM
            var txt = await EvalAsync("(() => { const out = []; (function walk(n) { if (n.shadowRoot) walk(n.shadowRoot); " +
                "for (const c of n.childNodes) { if (c.nodeType === 3) { const t = c.textContent.trim(); if (t) out.push(t); } else walk(c); } })(document.body); return out.join('\\n'); })()");
            return System.Text.Json.JsonSerializer.Deserialize<string>(txt) ?? "";
        }
        var r = await EvalAsync(
            "(() => { let fps = 0; " +
            "if (!window.__ft) { window.__fc = 0; window.__ft = performance.now(); (function f() { window.__fc++; requestAnimationFrame(f); })(); } " +
            "else { fps = Math.round(window.__fc / ((performance.now() - window.__ft) / 1000)); window.__fc = 0; window.__ft = performance.now(); } " +
            "return JSON.stringify({renderer: 'web', fps, audioFrames, peak: +audioPeak.toFixed(3), bass: +bass.toFixed(3), " +
            "music: document.getElementById('music').textContent, " +
            "todo: [...document.querySelectorAll('#todoList li')].map(l => (l.classList.contains('done') ? '[x] ' : '[ ] ') + l.dataset.t), " +
            "todoRects: [...document.querySelectorAll('#todoList li .txt')].map(e => { const b = e.getBoundingClientRect(); return [Math.round(b.x + b.width / 2), Math.round(b.y + b.height / 2)]; }), S}); })()");
        return System.Text.Json.JsonSerializer.Deserialize<string>(r) ?? "null";
    }

    public void SendTrack(string? title, string? artist)
    {
        var track = title == null ? "null" : new JsonObject { ["Title"] = title, ["Artist"] = artist ?? "" }.ToJsonString();
        Post(new JsonObject { ["t"] = "m", ["v"] = track }.ToJsonString());
    }

    /// <summary>Screen point (physical pixels) -> page click.</summary>
    public void SendClick(Point screenPt) =>
        Post($"{{\"t\":\"c\",\"x\":{screenPt.X - Screen.Bounds.X},\"y\":{screenPt.Y - Screen.Bounds.Y}}}");

    public void SendMouseMove(Point screenPt) =>
        Post($"{{\"t\":\"mv\",\"x\":{screenPt.X - Screen.Bounds.X},\"y\":{screenPt.Y - Screen.Bounds.Y}}}");

    /// <summary>The task board for this monitor, drawn by web-widgets.js in place of the page's own simple list.</summary>
    public void SendBoard(JsonObject? board) => Post(new JsonObject { ["t"] = "tb", ["v"] = board?.DeepClone() }.ToJsonString());
    public void SendAlbumArt(byte[]? image) { } // album art goes with the widget data (SendWidgetData)

    /// <summary>Ready-made data for the injected widgets: {calendar, system, timer, album, world}.</summary>
    public void SendWidgetData(JsonObject data) => Post(new JsonObject { ["t"] = "wd", ["v"] = data.DeepClone() }.ToJsonString());

    public async Task<LayoutBox[]> GetLayoutAsync()
    {
        // give a property change that was just posted a moment to reach the page (getBoundingClientRect then forces layout)
        await Task.Delay(40);
        var r = await EvalAsync(
            "(() => { const d = window.devicePixelRatio || 1, out = []; " +
            "const add = (id, el) => { if (!el || el.classList.contains('off')) return; const b = el.getBoundingClientRect(); " +
            "if (b.width > 0 && b.height > 0) out.push({ id, x: b.x * d, y: b.y * d, w: b.width * d, h: b.height * d }); }; " +
            "add('clock', document.getElementById('hud')); const q = document.getElementById('quoteBox'); " +
            "if (!q.classList.contains('attached')) add('quote', q); add('todo', document.getElementById('todo')); " +
            "for (const id of ['calendar', 'system', 'timer', 'album', 'world']) add(id, document.getElementById('rw-' + id)); add('todo', document.getElementById('rw-tasks')); return JSON.stringify(out); })()");
        try
        {
            // ExecuteScriptAsync returns the value JSON-encoded (a string holding our JSON)
            var json = System.Text.Json.JsonSerializer.Deserialize<string>(r);
            if (json == null || JsonNode.Parse(json) is not JsonArray a) return Array.Empty<LayoutBox>();
            return a.OfType<JsonObject>().Select(o => new LayoutBox(o["id"]!.GetValue<string>(),
                new RectangleF((float)o["x"]!.GetValue<double>(), (float)o["y"]!.GetValue<double>(),
                    (float)o["w"]!.GetValue<double>(), (float)o["h"]!.GetValue<double>()))).ToArray();
        }
        catch (Exception e) { Log.Write("layout query failed: " + e.Message); return Array.Empty<LayoutBox>(); }
    }

    public Task<string> EvalAsync(string js) =>
        web.CoreWebView2 != null && !IsSuspended ? web.CoreWebView2.ExecuteScriptAsync(js) : Task.FromResult("null");

    /// <summary>
    /// Pause: keep a still of the last frame on the window, hide the WebView and suspend its renderer
    /// (stops JavaScript, timers and animation; memory is trimmed). Resume reverses it.
    /// </summary>
    public async Task SetPausedAsync(bool paused)
    {
        wantPaused = paused;
        if (busy || !IsReady || web.CoreWebView2 == null) return;
        busy = true;
        try
        {
            while (wantPaused != IsSuspended)
            {
                var core = web.CoreWebView2;
                if (wantPaused)
                {
                    try
                    {
                        using var ms = new MemoryStream();
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, ms);
                        ms.Position = 0;
                        var old = BackgroundImage;
                        BackgroundImage = Image.FromStream(ms);
                        BackgroundImageLayout = ImageLayout.Stretch;
                        old?.Dispose();
                    }
                    catch (Exception e) { Log.Write("snapshot failed: " + e.Message); }

                    web.Visible = false;
                    core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                    bool ok = await core.TrySuspendAsync();
                    IsSuspended = true;
                    Log.Write($"paused (suspended={ok})");
                }
                else
                {
                    if (core.IsSuspended) core.Resume();
                    core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                    web.Visible = true;
                    IsSuspended = false;
                    var old = BackgroundImage;
                    BackgroundImage = null;
                    old?.Dispose();
                    Log.Write("resumed");
                }
            }
        }
        finally { busy = false; }
    }

    /// <summary>True if the given window is this wallpaper or one of its (WebView) children.</summary>
    public bool Owns(IntPtr h)
    {
        for (; h != IntPtr.Zero; h = GetParent(h))
            if (h == Handle) return true;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            web.Dispose();
            BackgroundImage?.Dispose();
        }
        base.Dispose(disposing);
    }
}
