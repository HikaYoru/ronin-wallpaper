using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RoninHost;

/// <summary>
/// tasks.html in a window. Two kinds:
///  * "board": the Tasks window (tray → Tasks…), every board with its headers, tasks, notes and sub-tasks;
///  * "panel": one task, opened next to the mouse when you click a task's name on the desktop; it closes when you
///    click anywhere else or press Esc. Changes are saved as you go.
/// </summary>
internal sealed class TaskWindow : Form
{
    public string Mode { get; }
    public string? Board { get; private set; }
    public string? Item { get; private set; }
    readonly WebView2 web;
    readonly CoreWebView2Environment env;

    /// <summary>A message from the page: {"t":"ready"} | {"t":"op", …} | {"t":"close"}.</summary>
    public event Action<TaskWindow, JsonObject>? Message;

    public TaskWindow(CoreWebView2Environment env, string mode, string? board, string? item)
    {
        this.env = env;
        Mode = mode;
        Board = board;
        Item = item;
        BackColor = Color.FromArgb(0x14, 0x16, 0x18);
        Icon = TrayIcons.App;
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(web);

        if (mode == "panel")
        {
            Text = "Ronin — Task";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = LogicalToDeviceUnits(new Size(420, 560));
            PlaceNearCursor();
        }
        else
        {
            Text = "Ronin — Tasks";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(560, 500);
            ClientSize = LogicalToDeviceUnits(new Size(780, 860));
        }
    }

    /// <summary>Beside the mouse (where the task was clicked), on the same monitor, never off-screen.</summary>
    void PlaceNearCursor()
    {
        var at = Cursor.Position;
        var wa = Screen.FromPoint(at).WorkingArea;
        int x = at.X + 24, y = at.Y - 40;
        if (x + Width > wa.Right) x = at.X - 24 - Width;
        Location = new Point(Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - Width)), Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height)));
    }

    /// <summary>Show another task in this panel (it was already open), and bring it to the mouse.</summary>
    public void ShowTask(string board, string item)
    {
        Board = board; Item = item;
        PlaceNearCursor();
        Message?.Invoke(this, new JsonObject { ["t"] = "ready" }); // the host sends a fresh init
        Activate();
    }

    /// <summary>Switch the board window to a board (and open a task's details).</summary>
    public void Focus(string? board, string? item)
    {
        Post(new JsonObject { ["t"] = "show", ["board"] = board, ["item"] = item });
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // the panel closes when you click anywhere else (after a moment, so a pending save gets through)
        if (Mode == "panel")
            BeginInvoke(async () => { await Task.Delay(120); if (!IsDisposed && !ContainsFocus) Close(); });
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
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("RONIN_DEBUG") == "1";
            core.WebMessageReceived += (_, a) =>
            {
                try { if (JsonNode.Parse(a.WebMessageAsJson) is JsonObject o) Message?.Invoke(this, o); }
                catch (Exception ex) { Log.Write("tasks message failed: " + ex.Message); }
            };
            using var s = typeof(TaskWindow).Assembly.GetManifestResourceStream("tasks.html")!;
            using var r = new StreamReader(s);
            core.NavigateToString(r.ReadToEnd());
        }
        catch (Exception ex)
        {
            Log.Write("tasks window failed: " + ex);
            MessageBox.Show(this, "The tasks window could not load:\n" + ex.Message, "Ronin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void Post(JsonObject o)
    {
        if (web.CoreWebView2 == null || IsDisposed) return;
        try { web.CoreWebView2.PostWebMessageAsJson(o.ToJsonString()); } catch (Exception e) { Log.Write("tasks post failed: " + e.Message); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) web.Dispose();
        base.Dispose(disposing);
    }
}
