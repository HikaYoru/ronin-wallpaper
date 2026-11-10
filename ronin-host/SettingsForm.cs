using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RoninHost;

/// <summary>
/// Settings window. The form itself is a WebView2 page (settings.html, embedded in the exe) that
/// builds its controls from LivelyProperties.json. It shares the wallpaper's WebView2 browser
/// process and is fully released when closed.
///
/// The wallpaper folder is served to the page as https://ronin-preview.local/ so it can show background
/// thumbnails and run the wallpaper page itself, scaled down, as a live preview (an iframe).
/// </summary>
internal sealed class SettingsForm : Form
{
    public const string PreviewHost = "ronin-preview.local";

    readonly WebView2 web;
    readonly CoreWebView2Environment env;
    readonly Func<JsonObject> initData;
    readonly string wallpaperFolder;
    bool ready;

    /// <summary>
    /// A message from the page: {"t":"set","k":..,"v":..} | {"t":"host",..} | {"t":"reset"}.
    /// Files dropped on the page arrive as a "paths" array added to the message.
    /// </summary>
    public event Action<JsonObject>? Message;

    // Runs in the preview iframe (the wallpaper page): settings, audio and the track come from the settings page
    // through postMessage and go to the page's own Lively hooks; video backgrounds, which the web page can't show,
    // play in a <video> over its background.
    const string PreviewBridge = """
        (() => {
          if (location.hostname !== 'ronin-preview.local' || window === window.top) return;
          const VIDEO = /\.(mp4|m4v|mov|mkv|wmv|webm)$/i;
          let video = null;
          function setVideo(name) {
            const bg = document.getElementById('bg');
            if (!bg) return;
            if (name && VIDEO.test(name)) {
              const src = (name.includes('/') ? '' : 'images/') + encodeURI(name);
              if (!video) {
                video = document.createElement('video');
                Object.assign(video, { muted: true, loop: true, autoplay: true, playsInline: true });
                video.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;object-fit:cover';
                bg.appendChild(video);
              }
              if (video.getAttribute('src') !== src) { video.src = src; video.play().catch(() => {}); }
            } else if (video) { video.remove(); video = null; }
          }
          window.addEventListener('message', e => {
            const m = e.data || {};
            try {
              if (m.t === 'p') {
                for (const k in m.v) if (typeof livelyPropertyListener === 'function') livelyPropertyListener(k, m.v[k]);
                if ('background' in m.v) setVideo(m.v.background);
              } else if (m.t === 'a') { if (typeof livelyAudioListener === 'function') livelyAudioListener(m.v); }
              else if (m.t === 'm') { if (typeof livelyCurrentTrack === 'function') livelyCurrentTrack(m.v); }
              else if (m.t === 'g') { const l = window.wallpaperPropertyListener; if (l && l.applyGeneralProperties) l.applyGeneralProperties({ fps: m.v }); }
            } catch (err) { }
          });
          window.addEventListener('load', () => parent.postMessage({ t: 'previewReady' }, '*'));
        })();
        """;

    public SettingsForm(CoreWebView2Environment env, Func<JsonObject> initData, string wallpaperFolder)
    {
        this.env = env;
        this.initData = initData;
        this.wallpaperFolder = wallpaperFolder;
        Text = "Ronin — Settings";
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0x14, 0x16, 0x18);
        MinimumSize = new Size(560, 480);
        ClientSize = LogicalToDeviceUnits(new Size(640, 860));
        Icon = TrayIcons.App;
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor, AllowExternalDrop = true };
        Controls.Add(web);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("RONIN_DEBUG") == "1";
            if (Directory.Exists(wallpaperFolder))
                core.SetVirtualHostNameToFolderMapping(PreviewHost, wallpaperFolder, CoreWebView2HostResourceAccessKind.Allow);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PreviewBridge);
            // a file dropped outside the drop zones would otherwise replace the page
            core.NewWindowRequested += (_, a) => a.Handled = true;
            core.NavigationStarting += (_, a) => { if (ready && !a.Uri.StartsWith("data:") && !a.Uri.StartsWith("about:")) a.Cancel = true; };
            core.WebMessageReceived += (_, a) =>
            {
                try
                {
                    if (JsonNode.Parse(a.WebMessageAsJson) is not JsonObject o) return;
                    if (a.AdditionalObjects is { Count: > 0 } extra)
                    {
                        var paths = new JsonArray();
                        foreach (var x in extra) if (x is CoreWebView2File f && f.Path.Length > 0) paths.Add(f.Path);
                        o["paths"] = paths;
                    }
                    Message?.Invoke(o);
                }
                catch (Exception ex) { Log.Write("settings message failed: " + ex.Message); }
            };
            core.NavigationCompleted += (_, _) => { ready = true; SendInit(); };
            using var s = typeof(SettingsForm).Assembly.GetManifestResourceStream("settings.html")!;
            using var r = new StreamReader(s);
            core.NavigateToString(r.ReadToEnd());
        }
        catch (Exception ex)
        {
            Log.Write("settings window failed: " + ex);
            MessageBox.Show(this, "The settings page could not load:\n" + ex.Message, "Ronin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void SendInit()
    {
        if (web.CoreWebView2 == null) return;
        var o = initData();
        o["t"] = "init";
        web.CoreWebView2.PostWebMessageAsJson(o.ToJsonString());
    }

    /// <summary>Any message for the page (e.g. {"t":"notice"}).</summary>
    public void Post(JsonObject o)
    {
        if (ready && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(o.ToJsonString());
    }

    /// <summary>Music for the live preview's ink line (only while the window is on screen).</summary>
    public void SendAudio(float[] bins)
    {
        if (!ready || web.CoreWebView2 == null || WindowState == FormWindowState.Minimized || !Visible) return;
        web.CoreWebView2.PostWebMessageAsJson(AudioFeed.Format(bins));
    }

    public void SendTrack(string? title, string? artist)
    {
        if (!ready || web.CoreWebView2 == null) return;
        var track = title == null ? "null" : new JsonObject { ["Title"] = title, ["Artist"] = artist ?? "" }.ToJsonString();
        web.CoreWebView2.PostWebMessageAsJson(new JsonObject { ["t"] = "m", ["v"] = track }.ToJsonString());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) web.Dispose();
        base.Dispose(disposing);
    }
}
