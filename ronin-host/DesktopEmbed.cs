using static RoninHost.Native;

namespace RoninHost;

/// <summary>
/// Puts a window behind the desktop icons.
///
/// Sending 0x052C to Progman makes Explorer create a WorkerW window that sits between
/// the wallpaper and the icons. Where it lives depends on the Windows version:
///  * Windows 10 / 11 before 24H2: a top-level WorkerW right after the one that holds SHELLDLL_DefView.
///  * Windows 11 24H2 and later: Progman's own children are [SHELLDLL_DefView (icons), WorkerW (wallpaper)].
///    There we become a child of Progman placed directly under SHELLDLL_DefView ("progman" mode),
///    or a child of that WorkerW ("workerw" mode).
/// </summary>
internal static class DesktopEmbed
{
    public static IntPtr Progman => FindWindow("Progman", null);

    public static bool Attach(IntPtr hwnd, Rectangle screenBounds, string mode)
    {
        var progman = Progman;
        if (progman == IntPtr.Zero) { Log.Write("embed: Progman not found"); return false; }

        // ask Explorer to create the WorkerW layer (both message forms, old and new)
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);

        IntPtr parent, after;
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        var innerWorker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);

        if (defView != IntPtr.Zero && innerWorker != IntPtr.Zero)
        {
            // Windows 11 24H2+ ("raised desktop")
            if (mode == "workerw") { parent = innerWorker; after = HWND_TOP; }
            else { parent = progman; after = defView; } // z-order: right below the icons, above Explorer's own wallpaper
            Log.Write($"embed: 24H2 layout, mode={mode}, progman={progman:X} defView={defView:X} workerw={innerWorker:X}");
        }
        else
        {
            // classic layout
            var worker = IntPtr.Zero;
            EnumWindows((top, _) =>
            {
                if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                    worker = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                return true;
            }, IntPtr.Zero);
            parent = worker != IntPtr.Zero ? worker : progman;
            after = HWND_TOP;
            Log.Write($"embed: classic layout, parent={parent:X}");
        }

        // turn the borderless form into a child window
        long style = Style(hwnd, GWL_STYLE);
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        style |= WS_CHILD;
        SetStyle(hwnd, GWL_STYLE, style);
        long ex = Style(hwnd, GWL_EXSTYLE) & ~WS_EX_APPWINDOW;
        SetStyle(hwnd, GWL_EXSTYLE, ex);

        if (SetParent(hwnd, parent) == IntPtr.Zero && GetParent(hwnd) != parent)
        {
            Log.Write("embed: SetParent failed");
            return false;
        }

        // the parent spans the whole virtual screen; convert the monitor rect into its client coordinates
        var r = new RECT { Left = screenBounds.Left, Top = screenBounds.Top, Right = screenBounds.Right, Bottom = screenBounds.Bottom };
        MapWindowPoints(IntPtr.Zero, parent, ref r, 2);
        SetWindowPos(hwnd, after, r.Left, r.Top, r.Width, r.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        Log.Write($"embed: placed at {r.Left},{r.Top} {r.Width}x{r.Height} for monitor {screenBounds}");
        return true;
    }

    /// <summary>Ask Explorer to repaint the normal wallpaper (after we leave), without changing it.</summary>
    public static void RefreshStaticWallpaper()
    {
        const uint SPI_GETDESKWALLPAPER = 0x73, SPI_SETDESKWALLPAPER = 0x14;
        var sb = new System.Text.StringBuilder(520);
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) && sb.Length > 0)
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, sb, 0); // 0 = don't write to the profile
    }
}
