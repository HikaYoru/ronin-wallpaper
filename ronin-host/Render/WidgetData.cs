using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RoninHost.Render;

/// <summary>
/// The focus timer. One for the whole app, so it shows the same on every monitor and a click on any of them
/// starts or pauses it.
/// </summary>
internal static class FocusTimer
{
    public enum Phase { Ready, Focus, Break }
    static readonly object Gate = new();
    static Phase phase = Phase.Ready;
    static bool running;
    static DateTime endsUtc;
    static TimeSpan left, length;
    static int breakMinutes = 5;

    public static (Phase phase, bool running, TimeSpan left, TimeSpan length) State()
    {
        lock (Gate)
        {
            if (running && DateTime.UtcNow >= endsUtc)
            {
                // focus -> break -> ready
                if (phase == Phase.Focus) Begin(Phase.Break, TimeSpan.FromMinutes(breakMinutes));
                else { phase = Phase.Ready; running = false; left = length = TimeSpan.Zero; }
            }
            var l = running ? endsUtc - DateTime.UtcNow : left;
            return (phase, running, l < TimeSpan.Zero ? TimeSpan.Zero : l, length);
        }
    }

    static void Begin(Phase p, TimeSpan len)
    {
        phase = p; length = len; left = len; running = true; endsUtc = DateTime.UtcNow + len;
    }

    /// <summary>Click on the ring: start (when ready), pause, or resume.</summary>
    public static void Toggle(int focusMinutes, int breakMins)
    {
        lock (Gate)
        {
            State();
            breakMinutes = Math.Max(1, breakMins);
            if (phase == Phase.Ready) Begin(Phase.Focus, TimeSpan.FromMinutes(Math.Max(1, focusMinutes)));
            else if (running) { left = endsUtc - DateTime.UtcNow; running = false; }
            else { endsUtc = DateTime.UtcNow + left; running = true; }
        }
    }

    public static void Reset()
    {
        lock (Gate) { phase = Phase.Ready; running = false; left = length = TimeSpan.Zero; }
    }
}

/// <summary>CPU, GPU and memory use, sampled every 2 s on a background thread while any monitor shows them.</summary>
internal static class SystemSampler
{
    public const int History = 60;
    static readonly object Gate = new();
    static readonly float[] cpu = new float[History], gpu = new float[History], ram = new float[History];
    static int count;
    static double ramUsedGb, ramTotalGb;
    static long lastUse;
    static System.Threading.Timer? timer;
    public static int Version { get; private set; }

    public sealed record Snapshot(float[] Cpu, float[] Gpu, float[] Ram, double RamUsedGb, double RamTotalGb, bool GpuOk);

    /// <summary>Latest values (and starts sampling if it isn't running).</summary>
    public static Snapshot Read()
    {
        lock (Gate)
        {
            lastUse = Environment.TickCount64;
            timer ??= new System.Threading.Timer(_ => Sample(), null, 0, 2000);
            int n = Math.Min(count, History);
            float[] Tail(float[] a) => a.Skip(History - n).ToArray();
            return new Snapshot(Tail(cpu), Tail(gpu), Tail(ram), ramUsedGb, ramTotalGb, gpuOk);
        }
    }

    static void Push(float[] a, float v) { Array.Copy(a, 1, a, 0, History - 1); a[History - 1] = v; }

    static long prevIdle, prevTotal;
    static bool gpuOk;

    static void Sample()
    {
        try
        {
            if (Environment.TickCount64 - lastUse > 15000)
            {
                // nobody is looking: stop until asked again
                lock (Gate) { timer?.Dispose(); timer = null; count = 0; }
                DisposeGpu();
                return;
            }
            float c = 0;
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                long total = kernel + user; // kernel time includes idle time
                if (prevTotal > 0 && total > prevTotal) c = 100f * (1 - (idle - prevIdle) / (float)(total - prevTotal));
                prevIdle = idle; prevTotal = total;
            }
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref m);
            double totalGb = m.ullTotalPhys / 1073741824.0, usedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            float g = SampleGpu();
            lock (Gate)
            {
                Push(cpu, Math.Clamp(c, 0, 100));
                Push(gpu, Math.Clamp(g, 0, 100));
                Push(ram, totalGb > 0 ? (float)(usedGb / totalGb * 100) : 0);
                ramUsedGb = usedGb; ramTotalGb = totalGb;
                count++;
                Version++;
            }
        }
        catch (Exception e) { Log.Write("system sampler: " + e.Message); }
    }

    // GPU: Windows' "GPU Engine" counters (what Task Manager shows), summed over 3D engines of all processes
    static PerformanceCounter[] gpuCounters = Array.Empty<PerformanceCounter>();
    static long gpuListAt;

    static float SampleGpu()
    {
        try
        {
            if (Environment.TickCount64 - gpuListAt > 10000)
            {
                gpuListAt = Environment.TickCount64;
                DisposeGpu();
                var cat = new PerformanceCounterCategory("GPU Engine");
                gpuCounters = cat.GetInstanceNames().Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true)).ToArray();
                foreach (var pc in gpuCounters) try { pc.NextValue(); } catch { }
                gpuOk = gpuCounters.Length > 0;
                return 0;
            }
            float sum = 0;
            foreach (var pc in gpuCounters) try { sum += pc.NextValue(); } catch { }
            return sum;
        }
        catch { gpuOk = false; return 0; }
    }

    static void DisposeGpu()
    {
        foreach (var pc in gpuCounters) pc.Dispose();
        gpuCounters = Array.Empty<PerformanceCounter>();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}

/// <summary>
/// Calendar events from an .ics link (Google, Outlook, iCloud all offer one), fetched every 15 minutes and shared
/// by every monitor that shows the same link. Handles all-day events, time zones, cancelled events, moved single
/// occurrences and the usual repeat rules (daily / weekly / monthly / yearly with interval, count, until, by-day).
/// </summary>
internal static class CalendarFeed
{
    public sealed record Event(DateTime Start, DateTime End, bool AllDay, string Title);

    sealed class Entry { public List<Event> Events = new(); public string Error = ""; public DateTime Fetched; public bool Busy; public int Version; }
    static readonly Dictionary<string, Entry> cache = new();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>What we have for this link (starts a download when it is missing or older than 15 minutes).</summary>
    public static (List<Event> events, string error, int version) Get(string url)
    {
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        lock (cache)
        {
            if (!cache.TryGetValue(url, out var e)) cache[url] = e = new Entry();
            if (!e.Busy && DateTime.UtcNow - e.Fetched > TimeSpan.FromMinutes(15))
            {
                e.Busy = true;
                _ = Task.Run(async () =>
                {
                    List<Event>? list = null; string err = "";
                    try { list = Parse(await Http.GetStringAsync(url)); }
                    catch (Exception ex) { err = ex is HttpRequestException or TaskCanceledException ? "Calendar unavailable" : "Couldn't read this calendar"; Log.Write("calendar: " + ex.Message); }
                    lock (cache)
                    {
                        if (list != null) { e.Events = list; e.Error = ""; }
                        else e.Error = err;
                        e.Fetched = DateTime.UtcNow;
                        e.Busy = false;
                        e.Version++;
                    }
                });
            }
            return (e.Events, e.Error, e.Version);
        }
    }

    /// <summary>Upcoming events from now to the end of day <paramref name="days"/> - 1, in local time.</summary>
    public static List<Event> Upcoming(List<Event> all, int days, int max)
    {
        var now = DateTime.Now;
        var to = DateTime.Today.AddDays(Math.Max(1, days));
        return all.Where(e => e.End > now && e.Start < to).OrderBy(e => e.Start).ThenBy(e => !e.AllDay).Take(max).ToList();
    }

    // ---------------- iCalendar ----------------

    sealed class VEvent
    {
        public string Uid = "", Title = "", Rrule = "", Status = "";
        public DateTime Start, End; public bool AllDay; public TimeZoneInfo? Zone; public bool Utc;
        public DateTime? RecurrenceId;
        public readonly List<DateTime> ExDates = new();
    }

    static List<Event> Parse(string ics)
    {
        // unfold continuation lines
        var lines = Regex.Split(ics.Replace("\r\n", "\n"), "\n(?=[^ \t])").Select(l => l.Replace("\n ", "").Replace("\n\t", "")).ToList();
        var events = new List<VEvent>();
        VEvent? cur = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line == "BEGIN:VEVENT") { cur = new VEvent(); continue; }
            if (line == "END:VEVENT") { if (cur != null) events.Add(cur); cur = null; continue; }
            if (cur == null) continue;
            int colon = IndexOfValue(line);
            if (colon < 0) continue;
            var head = line[..colon]; var value = line[(colon + 1)..];
            var parts = head.Split(';');
            var name = parts[0].ToUpperInvariant();
            var prm = parts.Skip(1).Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].Trim('"'));
            switch (name)
            {
                case "UID": cur.Uid = value; break;
                case "SUMMARY": cur.Title = Unescape(value); break;
                case "STATUS": cur.Status = value.ToUpperInvariant(); break;
                case "RRULE": cur.Rrule = value; break;
                case "DTSTART":
                    (cur.Start, cur.AllDay, cur.Zone, cur.Utc) = ParseTime(value, prm);
                    break;
                case "DTEND":
                    cur.End = ParseTime(value, prm).t;
                    break;
                case "DURATION":
                    if (TryDuration(value, out var dur)) cur.End = DateTime.MinValue.Add(dur); // resolved below
                    break;
                case "RECURRENCE-ID": cur.RecurrenceId = ToLocal(ParseTime(value, prm)); break;
                case "EXDATE":
                    foreach (var v in value.Split(',')) cur.ExDates.Add(ToLocal(ParseTime(v, prm)));
                    break;
            }
        }

        var result = new List<Event>();
        var horizonStart = DateTime.Today.AddDays(-1);
        var horizonEnd = DateTime.Today.AddDays(15);
        // single occurrences that were moved or cancelled: (uid, original start)
        var overridden = events.Where(e => e.RecurrenceId != null).Select(e => (e.Uid, e.RecurrenceId!.Value)).ToHashSet();

        foreach (var e in events)
        {
            if (e.Start == default) continue;
            TimeSpan length;
            if (e.End == default) length = e.AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;
            else if (e.End < new DateTime(1000, 1, 1)) length = e.End - DateTime.MinValue; // DURATION
            else length = e.End - e.Start;
            if (e.Status == "CANCELLED") continue;
            var title = e.Title.Length > 0 ? e.Title : "(no title)";

            foreach (var occ in Occurrences(e, horizonEnd))
            {
                var startLocal = ToLocal((occ, e.AllDay, e.Zone, e.Utc));
                if (e.RecurrenceId == null && e.Rrule.Length > 0 && overridden.Contains((e.Uid, startLocal))) continue;
                if (e.ExDates.Contains(startLocal)) continue;
                var endLocal = e.AllDay ? startLocal + length : ToLocal((occ + length, false, e.Zone, e.Utc));
                if (endLocal < horizonStart) continue;
                result.Add(new Event(startLocal, endLocal, e.AllDay, title));
            }
        }
        return result;
    }

    static int IndexOfValue(string line)
    {
        // the first ':' that is not inside a quoted parameter
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') q = !q;
            else if (line[i] == ':' && !q) return i;
        }
        return -1;
    }

    static string Unescape(string s) => s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    static (DateTime t, bool allDay, TimeZoneInfo? zone, bool utc) ParseTime(string v, Dictionary<string, string> prm)
    {
        v = v.Trim();
        if (v.Length == 8 || (prm.TryGetValue("VALUE", out var vt) && vt == "DATE"))
            return (DateTime.ParseExact(v[..8], "yyyyMMdd", CultureInfo.InvariantCulture), true, null, false);
        bool utc = v.EndsWith('Z');
        var t = DateTime.ParseExact(v.TrimEnd('Z')[..15], "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        TimeZoneInfo? zone = null;
        if (!utc && prm.TryGetValue("TZID", out var tz))
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(tz); } catch { zone = null; } // unknown zone: treat as local
        return (t, false, zone, utc);
    }

    static DateTime ToLocal((DateTime t, bool allDay, TimeZoneInfo? zone, bool utc) p)
    {
        if (p.allDay) return p.t.Date;
        if (p.utc) return DateTime.SpecifyKind(p.t, DateTimeKind.Utc).ToLocalTime();
        if (p.zone != null)
            try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(p.t, DateTimeKind.Unspecified), p.zone, TimeZoneInfo.Local); } catch { }
        return p.t; // floating time: already local
    }

    static bool TryDuration(string v, out TimeSpan d)
    {
        d = TimeSpan.Zero;
        var m = Regex.Match(v, @"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success) return false;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        d = new TimeSpan(G(2) * 7 + G(3), G(4), G(5), G(6));
        return true;
    }

    static readonly string[] Days = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

    /// <summary>Start times (in the event's own wall-clock time) up to <paramref name="until"/> (local).</summary>
    static IEnumerable<DateTime> Occurrences(VEvent e, DateTime until)
    {
        if (e.Rrule.Length == 0 || e.RecurrenceId != null) { yield return e.Start; yield break; }
        var r = e.Rrule.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].ToUpperInvariant());
        string freq = r.GetValueOrDefault("FREQ", "");
        int interval = int.TryParse(r.GetValueOrDefault("INTERVAL", "1"), out var iv) ? Math.Max(1, iv) : 1;
        int? count = int.TryParse(r.GetValueOrDefault("COUNT", ""), out var c) ? c : null;
        DateTime? untilRule = null;
        if (r.TryGetValue("UNTIL", out var u))
        {
            var p = ParseTime(u, new());
            untilRule = p.allDay ? p.t.AddDays(1).AddTicks(-1) : p.utc ? DateTime.SpecifyKind(p.t, DateTimeKind.Utc).ToLocalTime() : p.t;
        }
        var byDay = r.TryGetValue("BYDAY", out var bd) ? bd.Split(',').Select(x => x.Trim()).ToList() : new List<string>();
        var byMonthDay = r.TryGetValue("BYMONTHDAY", out var bm) ? bm.Split(',').Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n != 0).ToList() : new List<int>();

        int emitted = 0;
        var tod = e.Start.TimeOfDay;
        bool Done(DateTime t) => (count != null && emitted >= count) || (untilRule != null && t > untilRule) || t > until.AddDays(1);

        if (freq == "DAILY")
        {
            for (var t = e.Start; !Done(t); t = t.AddDays(interval)) { emitted++; yield return t; }
        }
        else if (freq == "WEEKLY")
        {
            var days = byDay.Count > 0 ? byDay.Select(d => Array.IndexOf(Days, d[^2..])).Where(i => i >= 0).OrderBy(i => i).ToList() : new List<int> { (int)e.Start.DayOfWeek };
            var weekStart = e.Start.Date.AddDays(-(int)e.Start.DayOfWeek);
            for (int w = 0; w < 2000; w += interval)
            {
                foreach (var d in days)
                {
                    var t = weekStart.AddDays(w * 7 + d) + tod;
                    if (t < e.Start) continue;
                    if (Done(t)) yield break;
                    emitted++; yield return t;
                }
            }
        }
        else if (freq is "MONTHLY" or "YEARLY")
        {
            int step = freq == "MONTHLY" ? interval : interval * 12;
            var first = new DateTime(e.Start.Year, e.Start.Month, 1);
            for (int k = 0; k < 2400; k += step)
            {
                var month = first.AddMonths(k);
                var dates = new List<DateTime>();
                if (byDay.Count > 0)
                {
                    foreach (var d in byDay)
                    {
                        int dow = Array.IndexOf(Days, d[^2..]);
                        if (dow < 0) continue;
                        var all = Enumerable.Range(0, DateTime.DaysInMonth(month.Year, month.Month)).Select(i => month.AddDays(i)).Where(x => (int)x.DayOfWeek == dow).ToList();
                        if (d.Length > 2 && int.TryParse(d[..^2], out var nth)) { var pick = nth > 0 ? all.ElementAtOrDefault(nth - 1) : all.ElementAtOrDefault(all.Count + nth); if (pick != default) dates.Add(pick); }
                        else dates.AddRange(all);
                    }
                }
                else if (byMonthDay.Count > 0)
                {
                    int dim = DateTime.DaysInMonth(month.Year, month.Month);
                    foreach (var n in byMonthDay) { int day = n > 0 ? n : dim + n + 1; if (day >= 1 && day <= dim) dates.Add(month.AddDays(day - 1)); }
                }
                else if (e.Start.Day <= DateTime.DaysInMonth(month.Year, month.Month)) dates.Add(month.AddDays(e.Start.Day - 1));

                foreach (var dd in dates.Distinct().OrderBy(x => x))
                {
                    var t = dd.Date + tod;
                    if (t < e.Start) continue;
                    if (Done(t)) yield break;
                    emitted++; yield return t;
                }
            }
        }
        else yield return e.Start;
    }
}
