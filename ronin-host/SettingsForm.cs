using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RoninHost;

/// <summary>
/// Settings window. The form itself is a WebView2 page (settings.html, embedded in the exe) that
/// builds its controls from LivelyProperties.json. It shares the wallpaper's WebView2 browser
/// process and is fully released when closed.
/// </summary>
internal sealed class SettingsForm : Form
{
    readonly WebView2 web;
    readonly CoreWebView2Environment env;
    readonly Func<JsonObject> initData;

    /// <summary>A message from the page: {"t":"set","k":..,"v":..} | {"t":"host",..} | {"t":"reset"}.</summary>
    public event Action<JsonObject>? Message;

    public SettingsForm(CoreWebView2Environment env, Func<JsonObject> initData)
    {
        this.env = env;
        this.initData = initData;
        Text = "Ronin — Settings";
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0x14, 0x16, 0x18);
        MinimumSize = new Size(520, 480);
        ClientSize = LogicalToDeviceUnits(new Size(600, 820));
        Icon = TrayIcons.App;
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
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
            core.WebMessageReceived += (_, a) =>
            {
                try { if (JsonNode.Parse(a.WebMessageAsJson) is JsonObject o) Message?.Invoke(o); }
                catch (Exception ex) { Log.Write("settings message failed: " + ex.Message); }
            };
            core.NavigationCompleted += (_, _) => SendInit();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) web.Dispose();
        base.Dispose(disposing);
    }
}
