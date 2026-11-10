using System.Runtime.InteropServices;

namespace RoninHost;

/// <summary>
/// Stable names for monitors. Windows' "\\.\DISPLAY5" can change after a driver update or when a cable is
/// replugged; the monitor's device interface path ("DISPLAY#GSM5B7F#5&amp;2a8e5b3&amp;0&amp;UID4352") names the model
/// and the port it is on, and stays the same. Settings are saved under these names.
/// </summary>
internal static class MonitorIds
{
    static readonly Dictionary<string, string> cache = new();

    /// <summary>The stable id for a screen (its DeviceName if Windows doesn't give one).</summary>
    public static string For(Screen s)
    {
        lock (cache)
        {
            if (cache.TryGetValue(s.DeviceName, out var id)) return id;
            id = Lookup(s.DeviceName) ?? s.DeviceName;
            cache[s.DeviceName] = id;
            return id;
        }
    }

    /// <summary>Forget cached ids (monitors were added, removed or rearranged).</summary>
    public static void Reset() { lock (cache) cache.Clear(); }

    static string? Lookup(string deviceName)
    {
        try
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            // the first monitor on this output, with its interface path instead of the "MONITOR\..." id
            if (!EnumDisplayDevices(deviceName, 0, ref dd, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */)) return null;
            var path = dd.DeviceID ?? "";
            if (path.Length == 0) return null;
            // \\?\DISPLAY#GSM5B7F#5&2a8e5b3&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7} -> DISPLAY#GSM5B7F#5&2a8e5b3&0&UID4352
            path = path.TrimStart('\\', '?');
            int brace = path.IndexOf("#{", StringComparison.Ordinal);
            if (brace > 0) path = path[..brace];
            return path;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);
}
