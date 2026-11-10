using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace RoninHost;

/// <summary>
/// Everything saved to %APPDATA%\RoninHost\settings.json:
/// host options plus the wallpaper's own properties (keys from LivelyProperties.json).
/// </summary>
internal sealed class AppSettings
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoninHost");
    public static readonly string FilePath = Path.Combine(Dir, "settings.json");

    /// <summary>Legacy single-monitor choice (before multi-monitor). Used only when <see cref="Monitors"/> is empty.</summary>
    public string Monitor { get; set; } = "";
    /// <summary>Screen.DeviceName of every monitor to draw on, e.g. \\.\DISPLAY1. Empty = <see cref="Monitor"/> (or primary).</summary>
    public List<string> Monitors { get; set; } = new();
    /// <summary>
    /// Per-monitor overrides of wallpaper properties (positions dragged in Arrange mode, elements hidden on one
    /// monitor). Key: Screen.DeviceName. Missing keys use <see cref="Properties"/>.
    /// </summary>
    public Dictionary<string, JsonObject> MonitorProperties { get; set; } = new();
    /// <summary>Saved layouts by name: {"shared": {positions, sizes, shown elements, background}, "monitors": {id: {...}}}.</summary>
    public Dictionary<string, JsonObject> Layouts { get; set; } = new();
    /// <summary>The layout last applied or saved (shown ticked in the tray menu).</summary>
    public string CurrentLayout { get; set; } = "";
    /// <summary>Switch layouts by time of day: at each entry's time (on its days) its layout is applied.</summary>
    public List<ScheduleEntry> LayoutSchedule { get; set; } = new();
    /// <summary>Which scheduled switch was applied last ("2026-11-09T09:00|Work"), so it happens once.</summary>
    public string ScheduleApplied { get; set; } = "";
    /// <summary>Where to look for updates: a folder or an http(s) URL holding version.json (empty = the default releases folder).</summary>
    public string UpdateSource { get; set; } = "";
    /// <summary>Look for updates once a day.</summary>
    public bool CheckUpdates { get; set; } = true;
    /// <summary>Frame-rate cap for monitors other than the main one (0 = same as the main one).</summary>
    public int SideFpsLimit { get; set; } = 0;
    /// <summary>While a fullscreen app (a game) has focus, the wallpaper on other monitors drops to 15 fps.</summary>
    public bool SlowWhenFullscreen { get; set; } = true;
    /// <summary>Where the task HUD was last dragged to: [x, y] desktop pixels (null = top middle of the screen under the cursor).</summary>
    public int[]? TaskHudPos { get; set; }
    /// <summary>The day the to-do ticks belong to (for "untick every day").</summary>
    public string TodoDate { get; set; } = "";
    /// <summary>Folder with index.html + LivelyProperties.json. Empty = look for ronin-wallpaper next to the app.</summary>
    public string WallpaperFolder { get; set; } = "";
    /// <summary>Pause while another app is fullscreen or maximized on the wallpaper's monitor.</summary>
    public bool PauseWhenCovered { get; set; } = true;
    /// <summary>"native" (Direct2D, light, default) or "web" (the original index.html in WebView2).</summary>
    public string Renderer { get; set; } = "native";
    /// <summary>Ticked to-do items (native renderer; the web renderer keeps them in the page's own storage).</summary>
    public List<string> TodoDone { get; set; } = new();
    /// <summary>Frame-rate cap for the wallpaper's animation loop. 0 = match the display.</summary>
    public int FpsLimit { get; set; } = 60;
    /// <summary>How to sit behind the icons on Windows 11 24H2+: "progman" (default) or "workerw".</summary>
    public string EmbedMode { get; set; } = "progman";
    /// <summary>Wallpaper properties the user changed (missing keys use LivelyProperties.json defaults).</summary>
    public JsonObject Properties { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception e) { Log.Write("settings load failed: " + e.Message); }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e) { Log.Write("settings save failed: " + e.Message); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Settings from an exported file, or null if it doesn't look like one.</summary>
    public static AppSettings? FromJson(string text)
    {
        var node = JsonNode.Parse(text) as JsonObject;
        if (node == null || !node.ContainsKey("properties")) return null;
        return JsonSerializer.Deserialize<AppSettings>(text, Json);
    }

    /// <summary>Take everything from <paramref name="o"/> except where the wallpaper lives on this PC.</summary>
    public void CopyFrom(AppSettings o)
    {
        Monitor = o.Monitor; Monitors = o.Monitors.ToList();
        MonitorProperties = o.MonitorProperties.ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value.DeepClone());
        Layouts = o.Layouts.ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value.DeepClone());
        CurrentLayout = o.CurrentLayout;
        LayoutSchedule = o.LayoutSchedule.Select(e => new ScheduleEntry { Layout = e.Layout, Time = e.Time, Days = e.Days.ToList() }).ToList();
        ScheduleApplied = ""; UpdateSource = o.UpdateSource; CheckUpdates = o.CheckUpdates;
        PauseWhenCovered = o.PauseWhenCovered; Renderer = o.Renderer; TodoDone = o.TodoDone.ToList(); TodoDate = o.TodoDate;
        FpsLimit = o.FpsLimit; SideFpsLimit = o.SideFpsLimit; SlowWhenFullscreen = o.SlowWhenFullscreen; EmbedMode = o.EmbedMode;
        Properties = (JsonObject)o.Properties.DeepClone();
    }

    // ---- start with Windows (per-user Run key) ----
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "RoninHost";

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(RunName, false);
        }
    }
}

/// <summary>One scheduled layout switch: <see cref="Layout"/> at <see cref="Time"/> ("HH:mm") on <see cref="Days"/> (0 = Sunday; empty = every day).</summary>
internal sealed class ScheduleEntry
{
    public string Layout { get; set; } = "";
    public string Time { get; set; } = "09:00";
    public List<int> Days { get; set; } = new();
}

/// <summary>The wallpaper's LivelyProperties.json, read in file order.</summary>
internal sealed class PropertySchema
{
    public JsonObject Root { get; }
    public string Folder { get; }

    public PropertySchema(string wallpaperFolder)
    {
        Folder = wallpaperFolder;
        var path = Path.Combine(wallpaperFolder, "LivelyProperties.json");
        Root = File.Exists(path) ? (JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new()) : new();

        // the host's own extras (widgets, panorama, shared weather); the wallpaper package itself is never changed
        try
        {
            using var s = typeof(PropertySchema).Assembly.GetManifestResourceStream("extras.json");
            if (s != null && JsonNode.Parse(s) is JsonObject extras)
                foreach (var (k, v) in extras)
                    if (!Root.ContainsKey(k)) Root[k] = v?.DeepClone();
        }
        catch (Exception e) { Log.Write("extras.json failed: " + e.Message); }
    }

    /// <summary>Saved value if any, otherwise the default from the schema.</summary>
    public JsonObject Effective(JsonObject saved)
    {
        var o = new JsonObject();
        foreach (var (key, def) in Root)
        {
            if (def is not JsonObject d || d["type"]?.GetValue<string>() is "button" or "label") continue;
            var v = saved.TryGetPropertyValue(key, out var s) && s != null ? s : d["value"];
            o[key] = v?.DeepClone();
        }
        return o;
    }

    /// <summary>Files for each folderDropdown property, e.g. background -> [throne.webp, ...].</summary>
    public JsonObject FolderChoices()
    {
        var o = new JsonObject();
        foreach (var (key, def) in Root)
        {
            if (def is not JsonObject d || d["type"]?.GetValue<string>() != "folderDropdown") continue;
            var dir = Path.Combine(Folder, d["folder"]?.GetValue<string>() ?? "");
            var patterns = (d["filter"]?.GetValue<string>() ?? "*.*").Split('|').ToList();
            // the native renderer also plays videos as backgrounds (the wallpaper package lists images only)
            if (key == "background") patterns.AddRange(new[] { "*.mp4", "*.m4v", "*.mov", "*.mkv", "*.wmv", "*.webm" });
            var files = new JsonArray();
            if (Directory.Exists(dir))
                foreach (var f in patterns.SelectMany(p => Directory.EnumerateFiles(dir, p)).Distinct().OrderBy(f => f))
                    files.Add(Path.GetFileName(f));
            o[key] = files;
        }
        return o;
    }
}

internal static class Log
{
    static readonly object Gate = new();
    static readonly string PathName = System.IO.Path.Combine(AppSettings.Dir, "log.txt");
    static bool trimmed;

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Dir);
                if (!trimmed) { trimmed = true; if (File.Exists(PathName) && new FileInfo(PathName).Length > 256_000) File.Delete(PathName); }
                File.AppendAllText(PathName, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
