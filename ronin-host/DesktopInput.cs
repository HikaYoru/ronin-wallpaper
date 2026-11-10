using System.Diagnostics;
using System.Runtime.InteropServices;
using static RoninHost.Native;

namespace RoninHost;

/// <summary>
/// The wallpaper sits under the desktop icons, so it never gets mouse clicks itself.
/// A low-level mouse hook watches left clicks; when one lands on empty desktop
/// (not on an icon, not on any app window) we replay it into the page.
/// The hook never blocks or swallows a click.
/// </summary>
internal sealed class DesktopClicks : IDisposable
{
    readonly HookProc proc; // keep the delegate alive while the hook is installed
    IntPtr hook;
    readonly SynchronizationContext ui;
    POINT downPt;
    uint downTime;
    bool downValid;

    /// <summary>Raised on the UI thread with the click position in physical screen pixels.</summary>
    public event Action<Point>? Click;
    public Func<IntPtr, bool>? IsOwnWindow;

    public DesktopClicks(SynchronizationContext ui)
    {
        this.ui = ui;
        proc = Callback;
        hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) Log.Write("mouse hook failed: " + Marshal.GetLastWin32Error());
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP)
            {
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (msg == WM_LBUTTONDOWN) { downPt = m.pt; downTime = m.time; downValid = true; }
                else if (downValid)
                {
                    downValid = false;
                    // a click, not a drag (rubber-band selection) or a long press
                    if (Math.Abs(m.pt.X - downPt.X) <= 6 && Math.Abs(m.pt.Y - downPt.Y) <= 6 && m.time - downTime < 800)
                    {
                        var p = m.pt;
                        ui.Post(_ => Check(p), null); // do the slow checks outside the hook
                    }
                }
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    void Check(POINT p)
    {
        try
        {
            if (IsEmptyDesktop(p)) Click?.Invoke(new Point(p.X, p.Y));
        }
        catch (Exception e) { Log.Write("click check failed: " + e.Message); }
    }

    bool IsEmptyDesktop(POINT p)
    {
        var h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return false;
        if (IsOwnWindow?.Invoke(h) == true) return true;
        var cls = ClassOf(h);
        switch (cls)
        {
            case "SysListView32":
                return ClassOf(GetParent(h)) == "SHELLDLL_DefView" && IconAt(h, p) < 0;
            case "SHELLDLL_DefView":
                return true;
            case "WorkerW":
            case "Progman":
                return IsDesktopFamily(h);
            default:
                return false;
        }
    }

    static bool IsDesktopFamily(IntPtr h)
    {
        var root = GetAncestor(h, 2); // GA_ROOT
        var rc = ClassOf(root);
        return rc == "Progman" || rc == "WorkerW";
    }

    // ---- LVM_HITTEST inside Explorer's process ----
    IntPtr lvProc, lvMem;
    uint lvPid;

    int IconAt(IntPtr listView, POINT screenPt)
    {
        const uint LVM_HITTEST = 0x1000 + 18;
        const int Size = 24; // LVHITTESTINFO: POINT pt; UINT flags; int iItem, iSubItem, iGroup

        GetWindowThreadProcessId(listView, out var pid);
        if (pid != lvPid || lvProc == IntPtr.Zero)
        {
            FreeRemote();
            lvProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
            if (lvProc == IntPtr.Zero) return -1; // can't tell; treat as empty
            lvMem = VirtualAllocEx(lvProc, IntPtr.Zero, (UIntPtr)Size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            lvPid = pid;
        }
        if (lvMem == IntPtr.Zero) return -1;

        var cp = screenPt;
        ScreenToClient(listView, ref cp);
        var buf = new byte[Size];
        BitConverter.GetBytes(cp.X).CopyTo(buf, 0);
        BitConverter.GetBytes(cp.Y).CopyTo(buf, 4);
        if (!WriteProcessMemory(lvProc, lvMem, buf, (UIntPtr)Size, out _)) return -1;
        if (SendMessageTimeout(listView, LVM_HITTEST, IntPtr.Zero, lvMem, SMTO_ABORTIFHUNG, 200, out var res) == IntPtr.Zero) return -1;
        return res.ToInt32();
    }

    void FreeRemote()
    {
        if (lvProc != IntPtr.Zero)
        {
            if (lvMem != IntPtr.Zero) VirtualFreeEx(lvProc, lvMem, UIntPtr.Zero, MEM_RELEASE);
            CloseHandle(lvProc);
        }
        lvProc = lvMem = IntPtr.Zero;
        lvPid = 0;
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        FreeRemote();
    }
}

/// <summary>Is another app fullscreen or maximized on the wallpaper's monitor?</summary>
internal static class CoverCheck
{
    static readonly HashSet<string> ShellClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };
    static readonly int OwnPid = Environment.ProcessId;

    /// <summary>The monitor whose whole area the focused window fills (a fullscreen game or video), or null.</summary>
    public static Screen? FullscreenForeground()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero || !IsWindowVisible(h) || IsIconic(h)) return null;
        var cls = ClassOf(h);
        if (ShellClasses.Contains(cls)) return null;
        GetWindowThreadProcessId(h, out var pid);
        if (pid == OwnPid) return null;
        if (!GetWindowRect(h, out var r)) return null;
        var s = Screen.FromHandle(h);
        var b = s.Bounds;
        // exactly the monitor (borderless fullscreen or exclusive); a maximized window stops at the taskbar
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom ? s : null;
    }

    public static string? CoveringWindow(Screen screen)
    {
        var mon = MonitorFromPoint(new POINT { X = screen.Bounds.X + screen.Bounds.Width / 2, Y = screen.Bounds.Y + screen.Bounds.Height / 2 }, 2);
        var b = screen.Bounds;
        string? found = null;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true; // DWMWA_CLOAKED
            long ex = Style(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_TRANSPARENT) != 0 || (ex & WS_EX_NOACTIVATE) != 0) return true; // overlays
            if (MonitorFromWindow(h, 2) != mon) return true;
            var cls = ClassOf(h);
            if (ShellClasses.Contains(cls)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == OwnPid) return true;

            bool covered = IsZoomed(h);
            if (!covered)
            {
                if (DwmGetWindowAttribute(h, 9, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(h, out r); // DWMWA_EXTENDED_FRAME_BOUNDS
                covered = r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
            }
            if (covered)
            {
                string name = "";
                try { name = Process.GetProcessById((int)pid).ProcessName; } catch { }
                found = $"{name} [{cls}]";
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
