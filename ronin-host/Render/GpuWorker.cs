using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoninHost.Render;

/// <summary>
/// One render thread and one <see cref="GfxDevice"/> per GPU, drawing every wallpaper monitor that GPU drives,
/// each at its own frame rate. (Before: a thread and a device per monitor, ~100 MB of driver memory each.)
/// If the device is lost (driver update, GPU reset) everything on it is rebuilt.
/// </summary>
internal sealed class GpuWorker
{
    static readonly Dictionary<long, GpuWorker> workers = new();
    static readonly object registry = new();
    static readonly Stopwatch clock = Stopwatch.StartNew();
    /// <summary>Seconds on the clock every render thread and the host's audio feed share.</summary>
    public static double Now => clock.Elapsed.TotalSeconds;

    readonly long luid;
    readonly object gate = new();
    readonly List<NativeWallpaper> members = new();
    readonly List<(NativeWallpaper m, ManualResetEventSlim done)> leaving = new();
    readonly AutoResetEvent wake = new(false);
    readonly Thread thread;
    GfxDevice? device;
    bool alive = true;

    GpuWorker(long luid)
    {
        this.luid = luid;
        thread = new Thread(Loop) { Name = $"Ronin render (GPU {luid:X})", IsBackground = true };
    }

    /// <summary>Start drawing <paramref name="m"/> on the render thread of the GPU with this LUID.</summary>
    public static GpuWorker Join(NativeWallpaper m, long luid)
    {
        lock (registry)
        {
            if (!workers.TryGetValue(luid, out var w) || !w.alive)
            {
                w = new GpuWorker(luid);
                workers[luid] = w;
                lock (w.gate) w.members.Add(m);
                w.thread.Start();
                return w;
            }
            lock (w.gate) w.members.Add(m);
            w.wake.Set();
            return w;
        }
    }

    /// <summary>Stop drawing <paramref name="m"/>; its scene and swap chain are released on the render thread (waits up to 3 s).</summary>
    public void Leave(NativeWallpaper m)
    {
        var done = new ManualResetEventSlim();
        lock (gate)
        {
            if (!members.Contains(m)) return;
            leaving.Add((m, done));
        }
        wake.Set();
        if (Thread.CurrentThread != thread) done.Wait(3000);
    }

    public void Wake() => wake.Set();

    void Loop()
    {
        int failures = 0;
        using var timer = new FrameTimer();
        while (true)
        {
            List<NativeWallpaper> ms;
            List<(NativeWallpaper m, ManualResetEventSlim done)> gone;
            lock (gate)
            {
                gone = leaving.ToList();
                leaving.Clear();
                foreach (var (m, _) in gone) members.Remove(m);
                ms = members.ToList();
            }
            foreach (var (m, done) in gone)
            {
                try { m.Destroy(); } catch (Exception e) { Log.Write("native: release failed: " + e.Message); }
                done.Set();
            }
            if (ms.Count == 0 && Retire()) break;
            if (ms.Count == 0) continue;

            try
            {
                if (device == null)
                {
                    device = new GfxDevice(luid);
                    Log.Write($"native: {device.AdapterName} draws {ms.Count} monitor(s) on one device");
                }
                foreach (var m in ms) if (!m.SceneReady) m.Create(device);

                double now = Now, soonest = double.MaxValue;
                bool active = false;
                foreach (var m in ms)
                {
                    m.Drain();
                    if (m.Paused) { m.Next = 0; continue; }
                    active = true;
                    double interval = 1.0 / m.Cap;
                    if (m.Next == 0) m.Next = now;
                    if (now >= m.Next)
                    {
                        m.Tick(Now);
                        m.Next = Math.Max(m.Next + interval, Now - interval); // don't try to catch up after a stall
                    }
                    soonest = Math.Min(soonest, m.Next);
                }
                failures = 0;
                if (!active) wake.WaitOne(250);
                else
                {
                    double wait = soonest - Now;
                    if (wait > .0005) timer.Wait(Math.Min(wait, .25));
                }
            }
            catch (Exception e)
            {
                failures++;
                Log.Write($"native: render error ({failures}): {e}");
                foreach (var m in ms) { try { m.Destroy(); } catch { } }
                try { device?.Dispose(); } catch { }
                device = null;
                if (failures >= 5)
                {
                    // give up on these: the host rebuilds them (and counts crashes)
                    lock (gate) foreach (var m in ms) members.Remove(m);
                    foreach (var m in ms) m.Fail(e);
                    failures = 0;
                    continue;
                }
                Thread.Sleep(1000); // device lost / driver update: rebuild everything
            }
        }
        try { device?.Dispose(); } catch { }
        device = null;
    }

    /// <summary>No monitors left: end this thread (unless one joined meanwhile).</summary>
    bool Retire()
    {
        lock (registry)
            lock (gate)
            {
                if (members.Count > 0 || leaving.Count > 0) return false;
                alive = false;
                if (workers.TryGetValue(luid, out var w) && w == this) workers.Remove(luid);
                return true;
            }
    }

    /// <summary>Precise sleeps for frame pacing (high-resolution waitable timer, Windows 10 1803+).</summary>
    sealed class FrameTimer : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attr, string? name, uint flags, uint access);
        [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr fn, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        readonly IntPtr h = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003);

        public void Wait(double seconds)
        {
            if (seconds <= 0) return;
            if (h == IntPtr.Zero) { Thread.Sleep((int)Math.Ceiling(seconds * 1000)); return; }
            long due = -(long)(seconds * 10_000_000); // relative, 100 ns units
            if (SetWaitableTimer(h, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(h, 1000);
        }

        public void Dispose() { if (h != IntPtr.Zero) CloseHandle(h); }
    }
}
