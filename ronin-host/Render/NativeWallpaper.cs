using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using static RoninHost.Native;

namespace RoninHost.Render;

/// <summary>
/// The wallpaper drawn natively (Direct2D + DirectComposition) on one monitor. The drawing happens on its GPU's
/// shared render thread (<see cref="GpuWorker"/>); this window only holds the state and the host's messages.
/// No browser: one small process instead of WebView2's six.
/// </summary>
internal sealed class NativeWallpaper : Form, IWallpaperView
{
    public Screen Screen { get; }
    public bool IsReady { get; private set; }
    public event Action? Ready;
    public event Action? Gone;
    public event Action<string>? Crashed;
    public event Action<string>? Request;

    readonly string folder, embedMode;
    readonly SynchronizationContext ui;
    readonly ConcurrentQueue<Action<Scene>> queue = new();
    readonly object audioGate = new();
    readonly float[] audio = new float[128];
    double audioAt = -1;
    bool audioNew;
    volatile bool paused;
    volatile int fpsLimit = 60;
    readonly int refreshRate;
    volatile string debugState = "{}";
    readonly string? shotDir = Environment.GetEnvironmentVariable("RONIN_DEBUG_SHOT") is { Length: > 0 } d && Directory.Exists(d) ? d : null;
    double lastShotCheck;
    DateTime lastShotReq = DateTime.UtcNow; // only requests made after this wallpaper started
    TaskCompletionSource? firstFrame;
    GpuWorker? worker;
    IntPtr hwnd;
    float dpr = 1;

    public NativeWallpaper(Screen screen, string folder, string embedMode)
    {
        Screen = screen;
        this.folder = folder;
        this.embedMode = embedMode;
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        refreshRate = RefreshRate(screen.DeviceName);

        Text = "Ronin Wallpaper (native)";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, screen.Bounds.Width, screen.Bounds.Height);
        BackColor = Color.FromArgb(0x0f, 0x10, 0x12);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // no GDI surface: everything is drawn through DirectComposition
            cp.ExStyle |= (int)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) | 0x00200000; // WS_EX_NOREDIRECTIONBITMAP
            return cp;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { } // nothing to paint with GDI

    public async Task StartAsync()
    {
        Show();
        if (!DesktopEmbed.Attach(Handle, Screen.Bounds, embedMode))
            throw new InvalidOperationException("Could not find the desktop window to draw behind the icons.");

        firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hwnd = Handle;
        dpr = Dpr();
        long luid = GfxDevice.LuidFor(Screen.Bounds, out var gpu);
        Log.Write($"native: {Screen.Bounds.Width}x{Screen.Bounds.Height} on {gpu}, scale {dpr}, refresh {refreshRate} Hz");
        worker = GpuWorker.Join(this, luid);
        await firstFrame.Task;
        IsReady = true;
        Ready?.Invoke();
    }

    float Dpr()
    {
        var c = new POINT { X = Screen.Bounds.X + Screen.Bounds.Width / 2, Y = Screen.Bounds.Y + Screen.Bounds.Height / 2 };
        var mon = MonitorFromPoint(c, 2);
        return GetDpiForMonitor(mon, 0, out var dx, out _) == 0 && dx > 0 ? dx / 96f : 1f;
    }

    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);

    // ---------------- on the GPU's render thread (called by GpuWorker) ----------------

    Gfx? gfx;
    Scene? scene;
    double fpsT0; int fpsFrames;

    internal bool SceneReady => scene != null;
    internal bool Paused => paused;
    /// <summary>Frames per second to draw at: the host's limit, or the monitor's refresh rate.</summary>
    internal double Cap => fpsLimit > 0 ? fpsLimit : refreshRate;
    /// <summary>When this monitor's next frame is due (worker clock; 0 = now).</summary>
    internal double Next;

    internal void Create(GfxDevice device)
    {
        gfx = new Gfx(device, hwnd, Screen.Bounds);
        scene = new Scene(gfx, folder, dpr, Screen.Bounds, a => queue.Enqueue(a));
        scene.Request = r => ui.Post(_ => Request?.Invoke(r), null);
        // after a device reset: restore what the host already sent
        JsonObject? replay;
        lock (queue) replay = (JsonObject?)props?.DeepClone();
        if (replay != null) scene.SetProperties(replay);
        if (track.t != null) scene.SetTrack(track.t, track.a);
        if (albumArt != null) scene.SetAlbumArt(albumArt);
        scene.SetBoard(board);
        Next = 0;
    }

    internal void Destroy()
    {
        scene?.Dispose(); scene = null;
        gfx?.Dispose(); gfx = null;
    }

    internal void Drain()
    {
        if (scene == null) return;
        while (queue.TryDequeue(out var a))
        {
            try { a(scene); }
            catch (Exception e) { Log.Write("native: " + e.Message); }
        }
    }

    internal void Tick(double nowS)
    {
        if (scene == null || gfx == null) return;
        lock (audioGate)
            if (audioNew) { audioNew = false; scene.Audio(audio, audioAt); }
        if (GetCursorPos(out var cp)) scene.CursorScreen(cp.X, cp.Y);

        scene.Frame(nowS);
        while (layoutWaiters.TryDequeue(out var lw)) lw.TrySetResult(scene.LayoutBoxes);
        if (shotDir != null && nowS - lastShotCheck > 1)
        {
            // diagnostics: create (or touch) <dir>\shot.req and every monitor saves its next frame as <dir>\shot_DISPLAYn.png
            lastShotCheck = nowS;
            var req = Path.Combine(shotDir, "shot.req");
            if (File.Exists(req) && File.GetLastWriteTimeUtc(req) is var stamp && stamp > lastShotReq)
            {
                lastShotReq = stamp;
                var name = "shot_" + Screen.DeviceName.TrimStart('\\', '.') + ".png";
                gfx.SaveBackBuffer(Path.Combine(shotDir, name));
                Log.Write("native: snapshot saved: " + name);
            }
        }
        gfx.Present();
        firstFrame?.TrySetResult();

        fpsFrames++;
        if (nowS - fpsT0 >= 1)
        {
            scene.fpsShown = (int)Math.Round(fpsFrames / (nowS - fpsT0));
            fpsFrames = 0; fpsT0 = nowS;
            debugState = new JsonObject
            {
                ["renderer"] = "native",
                ["fps"] = scene.fpsShown,
                ["gpu"] = gfx.AdapterName,
                ["audio"] = scene.AudioInfo,
                ["tasks"] = new JsonArray(scene.TaskState().Select(s => (JsonNode)s).ToArray()),
                ["music"] = track.t == null ? "" : $"{track.t} — {track.a}",
            }.ToJsonString();
        }
    }

    /// <summary>The render thread gave up on this monitor (the device kept failing).</summary>
    internal void Fail(Exception e)
    {
        worker = null;
        firstFrame?.TrySetException(e);
        ui.Post(_ => Crashed?.Invoke(e.Message), null);
    }

    // what the host has sent so far (replayed after a device reset)
    JsonObject? props;
    (string? t, string? a) track;

    // ---------------- from the host (UI thread) ----------------
    public void SendProperties(JsonObject p)
    {
        var copy = (JsonObject)p.DeepClone();
        lock (queue)
        {
            props ??= new JsonObject();
            foreach (var (k, v) in copy) props[k] = v?.DeepClone();
        }
        queue.Enqueue(s => s.SetProperties(copy));
        Wake();
    }

    void Wake() => worker?.Wake();

    public void SendTrack(string? title, string? artist)
    {
        track = (title, artist);
        queue.Enqueue(s => s.SetTrack(title, artist));
        Wake();
    }

    volatile byte[]? albumArt;
    public void SendAlbumArt(byte[]? image)
    {
        albumArt = image;
        queue.Enqueue(s => s.SetAlbumArt(image));
        Wake();
    }

    public void SendFpsLimit(int fps) { fpsLimit = Math.Max(0, fps); Next = 0; Wake(); }

    public void SendAudio(float[] bins)
    {
        lock (audioGate)
        {
            Array.Copy(bins, audio, Math.Min(bins.Length, audio.Length));
            audioAt = GpuWorker.Now;
            audioNew = true;
        }
    }

    public void SendClick(Point screenPt)
    {
        var p = new Vector2((screenPt.X - Screen.Bounds.X) / dpr, (screenPt.Y - Screen.Bounds.Y) / dpr);
        queue.Enqueue(s => s.Click(p));
        Wake();
    }

    public void SendMouseMove(Point screenPt) { } // the render thread reads the cursor itself every frame

    public Task SetPausedAsync(bool p)
    {
        if (paused == p) return Task.CompletedTask;
        paused = p;
        Wake();
        Log.Write(p ? $"native: paused {Screen.DeviceName}" : $"native: resumed {Screen.DeviceName}");
        return Task.CompletedTask;
    }

    volatile JsonObject? board;
    public void SendBoard(JsonObject? b)
    {
        var copy = (JsonObject?)b?.DeepClone();
        board = copy;
        queue.Enqueue(s => s.SetBoard(copy));
        Wake();
    }

    // answered by the render thread after the next frame (so a property change just sent is already laid out)
    readonly ConcurrentQueue<TaskCompletionSource<LayoutBox[]>> layoutWaiters = new();
    LayoutBox[] lastLayout = Array.Empty<LayoutBox>();

    public async Task<LayoutBox[]> GetLayoutAsync()
    {
        var tcs = new TaskCompletionSource<LayoutBox[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        layoutWaiters.Enqueue(tcs);
        Wake();
        // paused (no frames): fall back to the last layout we saw
        if (await Task.WhenAny(tcs.Task, Task.Delay(600)) == tcs.Task) lastLayout = tcs.Task.Result;
        return lastLayout;
    }

    public bool Owns(IntPtr h) => h == Handle;

    public Task<string> DebugStateAsync() => Task.FromResult(debugState);

    public void CloseView() => Close();

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        LeaveWorker();
        base.OnFormClosed(e);
        Gone?.Invoke();
    }

    void LeaveWorker()
    {
        var w = worker;
        worker = null;
        w?.Leave(this); // releases the swap chain on the render thread before the window goes away
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) LeaveWorker();
        base.Dispose(disposing);
    }

    // ---------------- display refresh rate ----------------
    static int RefreshRate(string deviceName)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(deviceName, -1 /* ENUM_CURRENT_SETTINGS */, ref dm) && dm.dmDisplayFrequency > 1 ? dm.dmDisplayFrequency : 60;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE dm);
}
