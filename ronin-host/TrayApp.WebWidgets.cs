using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RoninHost.Render;

namespace RoninHost;

/// <summary>
/// Data for the widgets in the web renderer (web-widgets.js draws them): the same sources the native renderer
/// uses (calendar feed, system sampler, focus timer, media session), sent ready to show once a second.
/// </summary>
internal sealed partial class TrayApp
{
    readonly System.Windows.Forms.Timer webWidgetTimer = new() { Interval = 1000 };
    byte[]? artFor; string? artUrl;

    void PushWebWidgets()
    {
        foreach (var s in slots)
            if (s.View is WallpaperForm wf && !s.Paused && wf.IsReady)
                wf.SendWidgetData(WebWidgetData(s.Device));
    }

    JsonObject WebWidgetData(string device)
    {
        var eff = EffectiveFor(device);
        var o = new JsonObject();
        bool h24 = Bool(eff["clock24"]);
        string Time(DateTime d) => h24 ? d.ToString("HH:mm", CultureInfo.InvariantCulture) : d.ToString("h:mm", CultureInfo.InvariantCulture) + (d.Hour < 12 ? "a" : "p");

        if (Bool(eff["showCalendar"]))
        {
            var url = eff["calendarUrl"]?.GetValue<string>()?.Trim() ?? "";
            var cal = new JsonObject();
            if (url.Length == 0) cal["note"] = "Add a calendar link in Settings";
            else
            {
                var (all, err, version) = CalendarFeed.Get(url);
                var up = CalendarFeed.Upcoming(all, (int)Num(eff["calendarDays"], 3), Math.Max(1, (int)Num(eff["calendarMax"], 6)));
                if (up.Count == 0) cal["note"] = err.Length > 0 ? err : version == 0 ? "Reading your calendar…" : "Nothing scheduled";
                var rows = new JsonArray();
                DateTime? day = null;
                foreach (var e in up)
                {
                    var d = e.Start < DateTime.Today ? DateTime.Today : e.Start.Date;
                    if (day != d)
                    {
                        day = d;
                        rows.Add(new JsonObject { ["day"] = (d == DateTime.Today ? "Today" : d == DateTime.Today.AddDays(1) ? "Tomorrow" : d.ToString("ddd  ·  MMM d", CultureInfo.CurrentCulture)).ToUpper(CultureInfo.CurrentCulture) });
                    }
                    bool nowOn = !e.AllDay && e.Start <= DateTime.Now && e.End > DateTime.Now;
                    rows.Add(new JsonObject { ["time"] = e.AllDay ? "ALL DAY" : nowOn ? "NOW" : Time(e.Start).ToUpperInvariant(), ["title"] = e.Title, ["now"] = nowOn });
                }
                cal["rows"] = rows;
            }
            o["calendar"] = cal;
        }

        if (Bool(eff["showSystem"]))
        {
            var snap = SystemSampler.Read();
            var rows = new JsonArray();
            JsonArray H(float[] h) => new(h.Select(v => (JsonNode)Math.Round(v, 1)).ToArray());
            rows.Add(new JsonObject { ["label"] = "CPU", ["value"] = snap.Cpu.Length > 0 ? $"{snap.Cpu[^1]:0}%" : "…", ["hist"] = H(snap.Cpu) });
            if (snap.GpuOk) rows.Add(new JsonObject { ["label"] = "GPU", ["value"] = snap.Gpu.Length > 0 ? $"{snap.Gpu[^1]:0}%" : "…", ["hist"] = H(snap.Gpu) });
            rows.Add(new JsonObject { ["label"] = "MEMORY", ["value"] = snap.RamTotalGb > 0 ? $"{snap.RamUsedGb:0.0} / {snap.RamTotalGb:0} GB" : "…", ["hist"] = H(snap.Ram) });
            o["system"] = new JsonObject { ["rows"] = rows };
        }

        if (Bool(eff["showTimer"]))
        {
            var st = FocusTimer.State();
            var left = st.phase == FocusTimer.Phase.Ready ? TimeSpan.FromMinutes(Math.Max(1, (int)Num(eff["timerMinutes"], 25))) : st.left;
            o["timer"] = new JsonObject
            {
                ["clock"] = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}",
                ["phase"] = st.phase == FocusTimer.Phase.Ready ? "FOCUS" : !st.running ? "PAUSED" : st.phase == FocusTimer.Phase.Focus ? "FOCUS" : "BREAK",
                ["frac"] = st.phase == FocusTimer.Phase.Ready || st.length.TotalSeconds <= 0 ? 1 : left.TotalSeconds / st.length.TotalSeconds,
                ["dim"] = st.phase == FocusTimer.Phase.Ready || !st.running,
                ["hint"] = st.phase == FocusTimer.Phase.Ready ? "CLICK TO BEGIN" : st.running ? "CLICK TO PAUSE" : "CLICK TO RESUME",
                ["reset"] = st.phase != FocusTimer.Phase.Ready && !st.running,
            };
        }

        if (Bool(eff["showAlbum"]))
        {
            if (!ReferenceEquals(artFor, media?.Art))
            {
                artFor = media?.Art;
                artUrl = artFor == null ? null
                    : $"data:{(artFor.Length > 4 && artFor[0] == 0x89 && artFor[1] == 0x50 ? "image/png" : "image/jpeg")};base64,{Convert.ToBase64String(artFor)}";
            }
            o["album"] = new JsonObject { ["title"] = media?.Title, ["artist"] = media?.Artist, ["art"] = media?.Title == null ? null : artUrl };
        }

        if (Bool(eff["showWorld"]))
        {
            var rows = new JsonArray();
            foreach (var entry in Regex.Split(eff["worldClocks"]?.GetValue<string>() ?? "", @"\s*[;|\n]\s*").Where(s => s.Trim().Length > 0).Take(8))
            {
                var parts = entry.Split('=', 2);
                string name = parts[0].Trim(), zone = (parts.Length > 1 ? parts[1] : parts[0]).Trim();
                var r = new JsonObject { ["name"] = name.ToUpper(CultureInfo.CurrentCulture), ["time"] = "—" };
                try
                {
                    var t = TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById(zone));
                    r["time"] = h24 ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : t.ToString("h:mm", CultureInfo.InvariantCulture);
                    if (!h24) r["ampm"] = t.Hour < 12 ? "AM" : "PM";
                    int days = (t.Date - DateTime.Today).Days;
                    if (days != 0) r["offset"] = days > 0 ? $"+{days}" : $"−{-days}";
                }
                catch { r["ampm"] = "?"; }
                rows.Add(r);
            }
            o["world"] = new JsonObject { ["rows"] = rows };
        }
        return o;
    }

    /// <summary>A click on a web widget: the focus timer's ring or its reset link.</summary>
    bool HandleTimerRequest(Slot slot, string request)
    {
        if (request is not ("timer:toggle" or "timer:reset")) return false;
        var eff = EffectiveFor(slot.Device);
        if (request == "timer:reset") FocusTimer.Reset();
        else FocusTimer.Toggle((int)Num(eff["timerMinutes"], 25), (int)Num(eff["timerBreak"], 5));
        PushWebWidgets();
        return true;
    }
}
