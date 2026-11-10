using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace RoninHost.Render;

/// <summary>
/// Extra blocks for the side screens: calendar, system ink, focus timer, album art, world clocks.
/// Each is a movable block like the to-do list (ids for Arrange: calendar, system, timer, album, world).
/// </summary>
internal sealed partial class Scene
{
    const int LWidget = 6;
    static readonly HashSet<string> WidgetKeys = new()
    {
        "showCalendar", "calendarUrl", "calendarDays", "calendarMax", "calX", "calY", "calScale",
        "showSystem", "sysX", "sysY", "sysScale",
        "showTimer", "timerMinutes", "timerBreak", "timerX", "timerY", "timerScale",
        "showAlbum", "albumX", "albumY", "albumScale",
        "showWorld", "worldClocks", "worldX", "worldY", "worldScale",
    };

    /// <summary>Anything drawn with code instead of text (sparklines, the timer ring, album art).</summary>
    sealed class FnOp : Op
    {
        public Action<Scene, Color4, Color4> F = null!;
        public override void Draw(Scene s, Color4 text, Color4 accent) => F(s, text, accent);
    }

    readonly List<(System.Drawing.RectangleF rect, Action act)> widgetHits = new(); // CSS px, screen space

    // what the HUD showed last, to know when to redraw
    int calendarVersion = -1, sysSeen = -1;
    string timerSeen = "";
    double widgetCheck;

    void StepWidgets()
    {
        if (now < widgetCheck) return;
        widgetCheck = now + .25;
        if (B("showSystem") && SystemSampler.Version != sysSeen) hudDirty = true;
        if (B("showTimer") && TimerKey() != timerSeen) hudDirty = true;
        if (B("showCalendar") && Str("calendarUrl").Trim().Length > 0 && CalendarFeed.Get(Str("calendarUrl")).version != calendarVersion) hudDirty = true;
    }

    static string TimerKey()
    {
        var s = FocusTimer.State();
        return $"{s.phase}{s.running}{(int)Math.Ceiling(s.left.TotalSeconds)}";
    }

    // ---------------- shared pieces ----------------

    /// <summary>Title in spaced capitals and the gold rule under it (as on the to-do list).</summary>
    static void Header(List<Row> rows, List<Op> ops, string title, string font)
    {
        var lt = Text.Layout(title.ToUpper(CultureInfo.CurrentCulture), font, 14, spacing: 14 * .5f);
        rows.Add(new Row { W = Text.Width(lt), H = Text.Height(lt), Emit = (x, y) => ops.Add(new TextOp { Layer = LWidget, L = lt, P = new(x, y) }) });
        rows.Add(new Row { W = 60 * 2 + 12 * 2 + 7, H = 7, Mt = 12, Emit = (x, y) => ops.Add(new RuleOp { Layer = LWidget, P = new(x, y), SpanW = 60 }) });
    }

    static Row Line(List<Op> ops, string s, string font, float size, float spacing, float opacity, bool accent = false, float mt = 10)
    {
        var l = Text.Layout(s, font, size, spacing: spacing);
        return new Row { W = Text.Width(l), H = Text.Height(l), Mt = mt, Emit = (x, y) => ops.Add(new TextOp { Layer = LWidget, L = l, P = new(x, y), Opacity = opacity, Accent = accent }) };
    }

    Block PlaceWidget(string id, List<Row> rows, List<Op> ops, string kx, string ky, string ks)
    {
        if (rows.Any(r => r.Table)) { float tw = rows.Where(r => r.Table).Max(r => r.W); foreach (var r in rows) if (r.Table) r.W = tw; }
        var b = Stack(rows, (float)N(kx), (float)N(ky), (float)N(ks) / 100);
        b.Id = id;
        b.Ops.AddRange(ops);
        Place(b);
        blocks.Add(b);
        return b;
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    string TimeText(DateTime d) => B("clock24") ? d.ToString("HH:mm", CultureInfo.InvariantCulture) : d.ToString("h:mm", CultureInfo.InvariantCulture) + (d.Hour < 12 ? "a" : "p");

    void BuildWidgets(string font)
    {
        if (B("showCalendar")) BuildCalendar(font);
        if (B("showSystem")) BuildSystem(font);
        if (B("showTimer")) BuildTimer(font);
        if (B("showAlbum")) BuildAlbum(font);
        if (B("showWorld")) BuildWorld(font);
    }

    // ---------------- calendar ----------------
    void BuildCalendar(string font)
    {
        var rows = new List<Row>(); var ops = new List<Op>();
        Header(rows, ops, "Agenda", font);
        var url = Str("calendarUrl").Trim();
        if (url.Length == 0)
            rows.Add(Line(ops, "Add a calendar link in Settings", Text.Cormorant, 18, 0, .6f, mt: 14));
        else
        {
            var (all, err, version) = CalendarFeed.Get(url);
            calendarVersion = version;
            var up = CalendarFeed.Upcoming(all, (int)N("calendarDays"), Math.Max(1, (int)N("calendarMax")));
            if (up.Count == 0)
                rows.Add(Line(ops, err.Length > 0 ? err : version == 0 ? "Reading your calendar…" : "Nothing scheduled", Text.Cormorant, 18, 0, .6f, mt: 14));
            var culture = CultureInfo.CurrentCulture;
            DateTime? day = null;
            bool first = true;
            foreach (var e in up)
            {
                var d = e.Start < DateTime.Today ? DateTime.Today : e.Start.Date;
                if (day != d)
                {
                    day = d;
                    string label = d == DateTime.Today ? "Today" : d == DateTime.Today.AddDays(1) ? "Tomorrow" : d.ToString("ddd  ·  MMM d", culture);
                    rows.Add(Line(ops, label.ToUpper(culture), font, 11, 11 * .35f, .85f, accent: true, mt: first ? 14 : 12));
                    first = false;
                }
                bool nowOn = !e.AllDay && e.Start <= DateTime.Now && e.End > DateTime.Now;
                var lt = Text.Layout(e.AllDay ? "ALL DAY" : nowOn ? "NOW" : TimeText(e.Start).ToUpperInvariant(), Text.Cinzel, 12, spacing: 12 * .12f);
                var ln = Text.Layout(Clip(e.Title, 42), Text.Cormorant, 20);
                const float col = 70;
                bool accent = nowOn;
                rows.Add(new Row
                {
                    W = col + Text.Width(ln), H = 27, Mt = 2, Table = true, Emit = (x, y) =>
                    {
                        ops.Add(new TextOp { Layer = LWidget, L = lt, P = new(x, y + 27 / 2f - Text.Height(lt) / 2 + 1), Opacity = accent ? 1 : .7f, Accent = accent });
                        ops.Add(new TextOp { Layer = LWidget, L = ln, P = new(x + col, y + 27 / 2f - Text.Height(ln) / 2) });
                    },
                });
            }
        }
        PlaceWidget("calendar", rows, ops, "calX", "calY", "calScale");
    }

    // ---------------- system ink ----------------
    void BuildSystem(string font)
    {
        var snap = SystemSampler.Read();
        sysSeen = SystemSampler.Version;
        var rows = new List<Row>(); var ops = new List<Op>();
        Header(rows, ops, "System", font);
        var metrics = new List<(string label, float[] hist, string value)>
        {
            ("CPU", snap.Cpu, snap.Cpu.Length > 0 ? $"{snap.Cpu[^1]:0}%" : "…"),
        };
        if (snap.GpuOk) metrics.Add(("GPU", snap.Gpu, snap.Gpu.Length > 0 ? $"{snap.Gpu[^1]:0}%" : "…"));
        metrics.Add(("MEMORY", snap.Ram, snap.RamTotalGb > 0 ? $"{snap.RamUsedGb:0.0} / {snap.RamTotalGb:0} GB" : "…"));

        const float labelW = 78, sparkW = 150, sparkH = 22, gap = 14, rowH = 30;
        bool firstRow = true;
        foreach (var (label, hist, value) in metrics)
        {
            var ll = Text.Layout(label, font, 11, spacing: 11 * .3f);
            var lv = Text.Layout(value, font, 13, spacing: 13 * .08f);
            var h = hist;
            rows.Add(new Row
            {
                W = labelW + sparkW + gap + Text.Width(lv), H = rowH, Mt = firstRow ? 14 : 2, Table = true, Emit = (x, y) =>
                {
                    ops.Add(new TextOp { Layer = LWidget, L = ll, P = new(x, y + rowH / 2 - Text.Height(ll) / 2), Opacity = .8f });
                    float sx = x + labelW, sy = y + (rowH - sparkH) / 2;
                    ops.Add(new FnOp { Layer = LWidget, F = (s, text, accent) => s.Spark(h, sx, sy, sparkW, sparkH, text, accent) });
                    ops.Add(new TextOp { Layer = LWidget, L = lv, P = new(sx + sparkW + gap, y + rowH / 2 - Text.Height(lv) / 2), Accent = true });
                },
            });
            firstRow = false;
        }
        PlaceWidget("system", rows, ops, "sysX", "sysY", "sysScale");
    }

    /// <summary>A brush-stroke sparkline: faint baseline, soft glow, then the ink itself.</summary>
    void Spark(float[] hist, float x, float y, float w, float h, Color4 text, Color4 accent)
    {
        using var faint = dc.CreateSolidColorBrush(new Color4(text.R, text.G, text.B, .16f));
        dc.DrawLine(new Vector2(x, y + h), new Vector2(x + w, y + h), faint, .8f);
        if (hist.Length < 2) return;
        using var geo = g.Factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            int n = SystemSampler.History;
            float step = w / (n - 1), x0 = x + w - (hist.Length - 1) * step; // newest at the right edge
            for (int i = 0; i < hist.Length; i++)
            {
                var p = new Vector2(x0 + i * step, y + h - Math.Clamp(hist[i], 0, 100) / 100 * h);
                if (i == 0) sink.BeginFigure(p, FigureBegin.Hollow); else sink.AddLine(p);
            }
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        using var glow = dc.CreateSolidColorBrush(new Color4(accent.R, accent.G, accent.B, .18f));
        dc.DrawGeometry(geo, glow, 4.5f, roundStroke);
        using var ink = dc.CreateSolidColorBrush(accent);
        dc.DrawGeometry(geo, ink, 1.4f, roundStroke);
    }

    // ---------------- focus timer ----------------
    void BuildTimer(string font)
    {
        var st = FocusTimer.State();
        timerSeen = TimerKey();
        var rows = new List<Row>(); var ops = new List<Op>();
        var culture = CultureInfo.CurrentCulture;
        int focusMin = Math.Max(1, (int)N("timerMinutes")), breakMin = Math.Max(1, (int)N("timerBreak"));

        var left = st.phase == FocusTimer.Phase.Ready ? TimeSpan.FromMinutes(focusMin) : st.left;
        string clock = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
        string phase = st.phase switch
        {
            FocusTimer.Phase.Ready => "FOCUS",
            _ when !st.running => "PAUSED",
            FocusTimer.Phase.Focus => "FOCUS",
            _ => "BREAK",
        };
        float frac = st.phase == FocusTimer.Phase.Ready || st.length.TotalSeconds <= 0 ? 1 : (float)(left.TotalSeconds / st.length.TotalSeconds);
        bool dimArc = st.phase == FocusTimer.Phase.Ready || !st.running;

        const float D = 150, R = 64;
        var lc = Text.Layout(clock, Text.Cinzel, 30, spacing: 30 * .04f);
        var lp = Text.Layout(phase, font, 10, spacing: 10 * .45f);
        var ringHit = new List<System.Drawing.RectangleF>();
        rows.Add(new Row
        {
            W = D, H = D, Emit = (x, y) =>
            {
                var c = new Vector2(x + D / 2, y + D / 2);
                ops.Add(new FnOp { Layer = LWidget, F = (s, text, accent) => s.Ring(c, R, frac, dimArc, text, accent) });
                ops.Add(new TextOp { Layer = LWidget, L = lc, P = new(c.X - Text.Width(lc) / 2, c.Y - Text.Height(lc) / 2 - 6) });
                ops.Add(new TextOp { Layer = LWidget, L = lp, P = new(c.X - Text.Width(lp) / 2, c.Y + 18), Accent = true, Opacity = .9f });
                ringHit.Add(new System.Drawing.RectangleF(x, y, D, D));
            },
        });

        string hint = st.phase == FocusTimer.Phase.Ready ? "CLICK TO BEGIN" : st.running ? "CLICK TO PAUSE" : "CLICK TO RESUME";
        rows.Add(Line(ops, hint, font, 10, 10 * .35f, .72f, mt: 10));
        var resetHit = new List<System.Drawing.RectangleF>();
        if (st.phase != FocusTimer.Phase.Ready && !st.running)
        {
            var lr = Text.Layout("◇  RESET", font, 11, spacing: 11 * .35f);
            rows.Add(new Row
            {
                W = Text.Width(lr), H = Text.Height(lr) + 6, Mt = 8, Emit = (x, y) =>
                {
                    ops.Add(new TextOp { Layer = LWidget, L = lr, P = new(x, y + 3), Accent = true });
                    resetHit.Add(new System.Drawing.RectangleF(x - 8, y - 4, Text.Width(lr) + 16, Text.Height(lr) + 14));
                },
            });
        }
        var b = PlaceWidget("timer", rows, ops, "timerX", "timerY", "timerScale");
        System.Drawing.RectangleF ToScreen(System.Drawing.RectangleF r) => new(b.TopLeft.X + r.X * b.Scale, b.TopLeft.Y + r.Y * b.Scale, r.Width * b.Scale, r.Height * b.Scale);
        foreach (var r in ringHit) widgetHits.Add((ToScreen(r), () => FocusTimer.Toggle(focusMin, breakMin)));
        foreach (var r in resetHit) widgetHits.Add((ToScreen(r), FocusTimer.Reset));
    }

    void Ring(Vector2 c, float r, float frac, bool dim, Color4 text, Color4 accent)
    {
        using var faint = dc.CreateSolidColorBrush(new Color4(text.R, text.G, text.B, .16f));
        dc.DrawEllipse(new Ellipse(c, r, r), faint, 1.2f);
        frac = Math.Clamp(frac, 0, 1);
        if (frac <= .001f) return;
        // the arc that is left, clockwise from the top
        float a0 = -MathF.PI / 2, a1 = a0 + frac * MathF.PI * 2;
        Vector2 P(float a) => c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
        using var geo = g.Factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(P(a0), FigureBegin.Hollow);
            if (frac >= .999f) { sink.AddArc(new ArcSegment(P(a0 + MathF.PI), new Vortice.Mathematics.Size(r, r), 0, SweepDirection.Clockwise, ArcSize.Small)); sink.AddArc(new ArcSegment(P(a0 + 2 * MathF.PI - .0001f), new Vortice.Mathematics.Size(r, r), 0, SweepDirection.Clockwise, ArcSize.Small)); }
            else sink.AddArc(new ArcSegment(P(a1), new Vortice.Mathematics.Size(r, r), 0, SweepDirection.Clockwise, frac > .5f ? ArcSize.Large : ArcSize.Small));
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        float o = dim ? .55f : 1;
        using var glow = dc.CreateSolidColorBrush(new Color4(accent.R, accent.G, accent.B, .2f * o));
        dc.DrawGeometry(geo, glow, 7, roundStroke);
        using var ink = dc.CreateSolidColorBrush(new Color4(accent.R, accent.G, accent.B, o));
        dc.DrawGeometry(geo, ink, 2.6f, roundStroke);
        Diamond(this, P(a1), 4.5f, true, accent, o);
    }

    // ---------------- album art ----------------
    ID2D1Bitmap1? albumArt;

    /// <summary>The playing track's cover (any image format; null = none).</summary>
    public void SetAlbumArt(byte[]? data)
    {
        albumArt?.Dispose(); albumArt = null;
        hudDirty = true;
        if (data == null || data.Length == 0) return;
        try
        {
            using var ms = new MemoryStream(data);
            using var src = new System.Drawing.Bitmap(ms);
            int size = 256; // plenty for a ~100 px cover on a 4K screen at 200 %
            using var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var gr = System.Drawing.Graphics.FromImage(bmp))
            {
                gr.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                // centre-crop to a square
                int s = Math.Min(src.Width, src.Height);
                gr.DrawImage(src, new System.Drawing.Rectangle(0, 0, size, size), new System.Drawing.Rectangle((src.Width - s) / 2, (src.Height - s) / 2, s, s), System.Drawing.GraphicsUnit.Pixel);
            }
            var bits = bmp.LockBits(new System.Drawing.Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try { albumArt = g.CreateBitmap(size, size, bits.Scan0, bits.Stride); }
            finally { bmp.UnlockBits(bits); }
        }
        catch (Exception e) { Log.Write("native: album art failed: " + e.Message); }
    }

    void BuildAlbum(string font)
    {
        var rows = new List<Row>(); var ops = new List<Op>();
        var culture = CultureInfo.CurrentCulture;
        if (string.IsNullOrEmpty(trackTitle))
        {
            rows.Add(Line(ops, "◆  NOTHING PLAYING", font, 11, 11 * .35f, .5f, mt: 0));
        }
        else
        {
            const float A = 96, gap = 18;
            var art = albumArt;
            var lt = Text.Layout(Clip(trackTitle!, 60), Text.Cormorant, 23, lineHeight: 23 * 1.15f, maxWidth: 300);
            var la = string.IsNullOrEmpty(trackArtist) ? null : Text.Layout(Clip(trackArtist!, 40).ToUpper(culture), font, 11, spacing: 11 * .32f);
            float tw = Math.Max(Text.Width(lt) > 300 ? 300 : lt.Metrics.Width, la != null ? Text.Width(la) : 0);
            float th = Text.Height(lt) + (la != null ? 8 + Text.Height(la) : 0);
            float aw = art != null ? A + gap : 0;
            float h = Math.Max(art != null ? A : 0, th);
            rows.Add(new Row
            {
                W = aw + tw, H = h, Emit = (x, y) =>
                {
                    if (art != null)
                        ops.Add(new FnOp
                        {
                            Layer = LWidget, F = (s, text, accent) =>
                            {
                                var r = new Vortice.RawRectF(x, y + (h - A) / 2, x + A, y + (h - A) / 2 + A);
                                s.dc.DrawBitmap(art, r, 1, BitmapInterpolationMode.Linear, null);
                                using var border = s.dc.CreateSolidColorBrush(new Color4(accent.R, accent.G, accent.B, .55f));
                                s.dc.DrawRectangle(r, border, 1);
                            },
                        });
                    float ty = y + (h - th) / 2;
                    ops.Add(new TextOp { Layer = LWidget, L = lt, P = new(x + aw, ty) });
                    if (la != null) ops.Add(new TextOp { Layer = LWidget, L = la, P = new(x + aw, ty + Text.Height(lt) + 8), Accent = true, Opacity = .9f });
                },
            });
        }
        PlaceWidget("album", rows, ops, "albumX", "albumY", "albumScale");
    }

    // ---------------- world clocks ----------------
    void BuildWorld(string font)
    {
        var rows = new List<Row>(); var ops = new List<Op>();
        Header(rows, ops, "World time", font);
        var culture = CultureInfo.CurrentCulture;
        var entries = Regex.Split(Str("worldClocks"), @"\s*[;|\n]\s*").Where(s => s.Trim().Length > 0).Take(8).ToList();
        if (entries.Count == 0) rows.Add(Line(ops, "Add cities in Settings (Name=Time zone)", Text.Cormorant, 18, 0, .6f, mt: 14));
        bool first = true;
        foreach (var entry in entries)
        {
            var parts = entry.Split('=', 2);
            string name = parts[0].Trim(), zoneId = (parts.Length > 1 ? parts[1] : parts[0]).Trim();
            string time = "—", ampm = "", offset = "";
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                var t = TimeZoneInfo.ConvertTime(DateTime.Now, zone);
                if (B("clock24")) time = t.ToString("HH:mm", CultureInfo.InvariantCulture);
                else { time = t.ToString("h:mm", CultureInfo.InvariantCulture); ampm = t.Hour < 12 ? "AM" : "PM"; }
                int days = (t.Date - DateTime.Today).Days;
                if (days != 0) offset = days > 0 ? $"+{days}" : $"−{-days}";
            }
            catch { ampm = "?"; }
            const float col = 128;
            var ln = Text.Layout(Clip(name, 16).ToUpper(culture), font, 11, spacing: 11 * .35f);
            var lt = Text.Layout(time, Text.Cinzel, 26, spacing: 26 * .03f);
            var la = ampm.Length > 0 ? Text.Layout(ampm, font, 9, spacing: 9 * .25f) : null;
            var lo = offset.Length > 0 ? Text.Layout(offset, font, 10) : null;
            float w = col + Text.Width(lt) + (la != null ? 6 + Text.Width(la) : 0) + (lo != null ? 8 + Text.Width(lo) : 0);
            const float rh = 36;
            rows.Add(new Row
            {
                W = w, H = rh, Mt = first ? 12 : 0, Table = true, Emit = (x, y) =>
                {
                    ops.Add(new TextOp { Layer = LWidget, L = ln, P = new(x, y + rh / 2 - Text.Height(ln) / 2 + 2), Opacity = .8f });
                    float tx = x + col;
                    ops.Add(new TextOp { Layer = LWidget, L = lt, P = new(tx, y + rh / 2 - Text.Height(lt) / 2) });
                    tx += Text.Width(lt);
                    if (la != null) { ops.Add(new TextOp { Layer = LWidget, L = la, P = new(tx + 6, y + rh / 2 - Text.Height(la) / 2 + 4), Accent = true }); tx += 6 + Text.Width(la); }
                    if (lo != null) ops.Add(new TextOp { Layer = LWidget, L = lo, P = new(tx + 8, y + rh / 2 - Text.Height(lo) / 2 - 6), Accent = true });
                },
            });
            first = false;
        }
        PlaceWidget("world", rows, ops, "worldX", "worldY", "worldScale");
    }
}
