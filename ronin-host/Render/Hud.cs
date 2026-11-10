using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace RoninHost.Render;

/// <summary>
/// Clock, date, weather, quote, now playing and the to-do list, laid out like the page's CSS.
/// Each piece is rendered (with its text shadow) into a cached bitmap when it changes; per frame
/// only those bitmaps are drawn, with the colon pulse and quote fade as opacity.
/// </summary>
internal sealed partial class Scene
{
    bool hudDirty = true;

    static readonly string[] Fonts = { Text.Cinzel, Text.Cormorant, Text.Shippori };
    static readonly (float x, float y)[] Presets = { (100, 0), (0, 0), (100, 100), (0, 100), (50, 50) }; // index 5 = custom sliders
    static readonly (string text, string src)[] Proverbs =
    {
        ("Fall seven times, stand up eight.", "Japanese proverb"),
        ("A gem cannot be polished without friction.", "Chinese proverb"),
        ("Beginning is easy. Continuing is hard.", "Japanese proverb"),
        ("Even monkeys fall from trees.", "Japanese proverb"),
        ("Knowing others is wisdom. Knowing yourself is enlightenment.", "Laozi"),
        ("Vision without action is a daydream.", "Japanese proverb"),
        ("The journey of a thousand miles begins with a single step.", "Laozi"),
        ("When the winds of change blow, some build walls, others build windmills.", "Chinese proverb"),
        ("The bamboo that bends is stronger than the oak that resists.", "Japanese proverb"),
    };

    // layers: what gets its own cached bitmap, and its text-shadow(s)
    const int LTime = 0, LColon = 1, LInfo = 2, LQuote = 3, LMusic = 4, LTodo = 5;
    static readonly (float sigma, bool accent, float alpha, float dy)[][] Shadows =
    {
        new[] { (15f, false, .75f, 2f), (1f, false, .6f, 0f) }, // #time: 0 2px 30px .75, 0 0 2px .6
        new[] { (7f, true, 1f, 0f), (15f, true, 1f, 0f) },       // colon glow: 0 0 14px accent, 0 0 30px accent
        new[] { (6f, false, .9f, 1f) },                          // date / weather: 0 1px 12px .9
        new[] { (6f, false, .95f, 1f) },                         // quote: 0 1px 12px .95
        new[] { (5f, false, .9f, 1f) },                          // music: 0 1px 10px .9
        new[] { (6f, false, .95f, 1f) },                         // to-do: 0 1px 12px .95
        new[] { (6f, false, .95f, 1f) },                         // widgets: like the to-do list
        Array.Empty<(float, bool, float, float)>(),              // LBackdrop: the box behind the tasks, no shadow
    };
    /// <summary>The panel / boxes behind the task list (Render\TaskBackdrop.cs); drawn under every other layer.</summary>
    const int LBackdrop = 7;

    // ---------------- now playing ----------------
    string? trackTitle, trackArtist;
    public void SetTrack(string? title, string? artist) { trackTitle = title; trackArtist = artist; hudDirty = true; }

    // ---------------- clock ----------------
    string clockStamp = "";
    void StepClock()
    {
        var d = DateTime.Now;
        var stamp = d.ToString("HHmm d", CultureInfo.InvariantCulture);
        if (stamp != clockStamp) { clockStamp = stamp; hudDirty = true; }
    }

    // ---------------- quotes ----------------
    int quoteIdx = Random.Shared.Next(Proverbs.Length);
    string quoteShown = "", quoteSrcShown = "";
    (string text, string src)? quotePending;
    float quoteFade = 1;
    int quotePhase;          // 0 steady, -1 fading out, +1 fading in
    double quoteNext = -1;

    void StartQuotes()
    {
        quoteNext = -1;
        hudDirty = true;
        if (!B("showQuote")) return;
        RenderQuote();
        if (Str("quoteText").Trim().Length == 0) quoteNext = now + Math.Max(1, N("quoteMinutes")) * 60;
    }

    void RenderQuote()
    {
        var custom = Str("quoteText").Trim();
        (string text, string src) q = custom.Length > 0 ? (custom, "") : Proverbs[quoteIdx++ % Proverbs.Length];
        if (quoteShown.Length > 0 && (q.text != quoteShown || q.src != quoteSrcShown)) { quotePending = q; quotePhase = -1; }
        else { quoteShown = q.text; quoteSrcShown = q.src; quoteFade = 1; quotePhase = 0; hudDirty = true; }
    }

    void StepQuote(float dt)
    {
        if (quoteNext >= 0 && now >= quoteNext)
        {
            RenderQuote();
            quoteNext = now + Math.Max(1, N("quoteMinutes")) * 60;
        }
        if (quotePhase == -1)
        {
            quoteFade -= dt / 1.4f;
            if (quoteFade <= 0)
            {
                quoteFade = 0;
                if (quotePending is { } p) { quoteShown = p.text; quoteSrcShown = p.src; quotePending = null; }
                quotePhase = 1;
                hudDirty = true;
            }
        }
        else if (quotePhase == 1)
        {
            quoteFade += dt / 1.4f;
            if (quoteFade >= 1) { quoteFade = 1; quotePhase = 0; }
        }
    }

    // ---------------- clicks ----------------
    /// <summary>Ask the host for something only it can do: "task:cycle:board:item", "task:open:…", "task:add:…"…</summary>
    public Action<string>? Request;

    /// <summary>A click at a CSS-px point of the screen. True if it hit something (a task, a header, a control, a widget).</summary>
    public bool Click(Vector2 p)
    {
        foreach (var (r, act) in widgetHits)
        {
            if (!r.Contains(p.X, p.Y)) continue;
            act();
            hudDirty = true;
            return true;
        }
        return false;
    }

    // ---------------- layout model ----------------
    abstract class Op { public int Layer; public abstract void Draw(Scene s, Color4 text, Color4 accent); public virtual void Dispose() { } }

    sealed class TextOp : Op
    {
        public IDWriteTextLayout L = null!;
        public Vector2 P;
        public bool Accent;
        public float Opacity = 1;
        public (int start, int len)? AccentRange;
        public (float x0, float x1, float y)? Strike;
        public override void Draw(Scene s, Color4 text, Color4 accent)
        {
            var dc = s.dc;
            using var brush = dc.CreateSolidColorBrush(Accent ? accent : text);
            brush.Opacity = Opacity;
            using var ab = dc.CreateSolidColorBrush(accent);
            ab.Opacity = Opacity;
            if (AccentRange is { } r) L.SetDrawingEffect(ab, new TextRange((uint)r.start, (uint)r.len));
            dc.DrawTextLayout(P, L, brush, DrawTextOptions.None);
            if (Strike is { } st) dc.DrawLine(new Vector2(st.x0, st.y), new Vector2(st.x1, st.y), ab, 1);
        }
        public override void Dispose() => L.Dispose();
    }

    /// <summary>The ".rule": gold line, small diamond, gold line.</summary>
    sealed class RuleOp : Op
    {
        public Vector2 P; public float SpanW;
        public override void Draw(Scene s, Color4 text, Color4 a)
        {
            var dc = s.dc;
            float y = P.Y + 3, x2 = P.X + SpanW + 12 + 7 + 12;
            using (var st = s.Stops(new[] { (0f, new Color4(a.R, a.G, a.B, 0)), (1f, a) }))
            using (var b1 = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(P.X, 0), new Vector2(P.X + SpanW, 0)), st))
                dc.FillRectangle(new Vortice.RawRectF(P.X, y, P.X + SpanW, y + 1), b1);
            using (var st = s.Stops(new[] { (0f, a), (1f, new Color4(a.R, a.G, a.B, 0)) }))
            using (var b2 = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(x2, 0), new Vector2(x2 + SpanW, 0)), st))
                dc.FillRectangle(new Vortice.RawRectF(x2, y, x2 + SpanW, y + 1), b2);
            Diamond(s, new Vector2(P.X + SpanW + 12 + 3.5f, P.Y + 3.5f), 4.6f, false, a, 1);
        }
    }

    sealed class DiamondOp : Op
    {
        public Vector2 C; public float R; public bool Filled; public float Opacity = 1;
        public override void Draw(Scene s, Color4 text, Color4 accent) => Diamond(s, C, R, Filled, accent, Opacity);
    }

    static void Diamond(Scene s, Vector2 c, float r, bool filled, Color4 color, float opacity)
    {
        using var geo = s.g.Factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(new Vector2(c.X, c.Y - r), FigureBegin.Filled);
            sink.AddLine(new Vector2(c.X + r, c.Y));
            sink.AddLine(new Vector2(c.X, c.Y + r));
            sink.AddLine(new Vector2(c.X - r, c.Y));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        using var brush = s.dc.CreateSolidColorBrush(color);
        brush.Opacity = opacity;
        if (filled) s.dc.FillGeometry(geo, brush);
        s.dc.DrawGeometry(geo, brush, 1);
    }

    sealed class Row
    {
        public float W, H, Mt; public Action<float, float>? Emit;
        /// <summary>Part of a table (widgets): all table rows of a block get the same width so their columns line up.</summary>
        public bool Table;
    }

    sealed class Block
    {
        public string Id = "";            // "clock" | "quote" | "todo" (for Arrange mode)
        public float X, Y, Scale, W, H;   // anchor (0..100), scale, size in CSS px
        public string Align = "left";
        public Vector2 TopLeft;           // CSS px on screen
        public readonly List<Op> Ops = new();
        public readonly Dictionary<int, (ID2D1Bitmap1 bmp, Vector2 pos)> Layers = new();
    }

    readonly List<Block> blocks = new();

    static string AlignOf(float x) => x < 34 ? "left" : x > 66 ? "right" : "center";
    static float AlignX(string align, float boxW, float rowW) => align == "left" ? 0 : align == "right" ? boxW - rowW : (boxW - rowW) / 2;

    static Block Stack(List<Row> rows, float x, float y, float scale)
    {
        var b = new Block { X = x, Y = y, Scale = scale, Align = AlignOf(x) };
        b.W = rows.Count == 0 ? 0 : rows.Max(r => r.W);
        float cy = 0;
        foreach (var r in rows) { cy += r.Mt; r.Emit?.Invoke(AlignX(b.Align, b.W, r.W), cy); cy += r.H; }
        b.H = cy;
        return b;
    }

    // place(): x/y are 0..100 across the screen inside a 5% / 7% margin; the block's matching point is pinned there
    void Place(Block b)
    {
        var anchor = new Vector2((5 + b.X * .9f) / 100 * W, (7 + b.Y * .86f) / 100 * H);
        b.TopLeft = anchor - b.Scale * new Vector2(b.X / 100 * b.W, b.Y / 100 * b.H);
    }

    // ---------------- building the HUD ----------------
    void RebuildHud()
    {
        hudDirty = false;
        DisposeHud();
        widgetHits.Clear();
        string font = Fonts[Math.Clamp((int)N("font"), 0, 2)];
        string align;

        // ----- #hud: clock + date (+ weather) (+ quote + music when attached) -----
        bool custom = (int)N("position") == 5;
        var (cx, cy) = custom ? ((float)N("clockX"), (float)N("clockY")) : Presets[Math.Clamp((int)N("position"), 0, 4)];
        float ui = (float)N("uiScale") / 100;
        var rows = new List<Row>();
        var hud = new Block();
        var ops = hud.Ops;

        if (B("showClock"))
        {
            var d = DateTime.Now;
            int hr = d.Hour; string ampm = "";
            string h;
            if (!B("clock24")) { ampm = hr >= 12 ? "PM" : "AM"; h = (hr % 12 == 0 ? 12 : hr % 12).ToString(); }
            else h = hr.ToString("00");
            string m = d.Minute.ToString("00");

            const float size = 104, sp = size * .05f;
            var lh = Text.Layout(h, font, size, spacing: sp);
            var lc = Text.Layout(":", font, size, spacing: sp);
            var lm = Text.Layout(m, font, size, spacing: sp);
            float aSize = size * .24f;
            var la = ampm.Length > 0 ? Text.Layout(ampm, font, aSize, spacing: aSize * .25f) : null;
            var (asc, desc) = Text.Metrics(font, false);
            float baseline = (size - (asc + desc) * size) / 2 + asc * size;   // line-height: 1
            float colonMargin = size * .04f, ampmGap = aSize * .45f;
            float wH = Text.Width(lh), wC = Text.Width(lc) + 2 * colonMargin, wM = Text.Width(lm);
            float total = wH + wC + wM + (la != null ? ampmGap + Text.Width(la) : 0);
            rows.Add(new Row
            {
                W = total, H = size, Emit = (x, y) =>
                {
                    ops.Add(new TextOp { Layer = LTime, L = lh, P = new(x, y + baseline - Text.Baseline(lh)) });
                    ops.Add(new TextOp { Layer = LColon, L = lc, Accent = true, P = new(x + wH + colonMargin, y + baseline - Text.Baseline(lc)) });
                    ops.Add(new TextOp { Layer = LTime, L = lm, P = new(x + wH + wC, y + baseline - Text.Baseline(lm)) });
                    if (la != null)
                        ops.Add(new TextOp { Layer = LTime, L = la, Accent = true, P = new(x + wH + wC + wM + ampmGap, y + baseline - aSize * .3f - Text.Baseline(la)) });
                },
            });

            if (B("showDate"))
            {
                rows.Add(new Row { W = 90 * 2 + 12 * 2 + 7, H = 7, Mt = 16, Emit = (x, y) => ops.Add(new RuleOp { Layer = LInfo, P = new(x, y), SpanW = 90 }) });
                var culture = CultureInfo.CurrentCulture;
                var date = $"{d.ToString("dddd", culture)}  ·  {d.ToString(culture.DateTimeFormat.MonthDayPattern, culture)}".ToUpper(culture);
                var ld = Text.Layout(date, font, 15, spacing: 15 * .45f);
                rows.Add(new Row { W = Text.Width(ld), H = Text.Height(ld), Mt = 14, Emit = (x, y) => ops.Add(new TextOp { Layer = LInfo, L = ld, P = new(x, y), Opacity = .9f }) });
            }

            if (B("liveWeather") && B("showWeather") && (wx != null || wxErr.Length > 0))
            {
                string s; (int, int)? accentRange = null;
                if (wx != null)
                {
                    var temp = $"{wx.Temp}°{wx.Unit}";
                    s = $"{temp}  ·  {wx.Label}  ·  {wxPlace}".ToUpperInvariant();
                    accentRange = (0, temp.Length);
                }
                else s = $"Weather: {wxErr}".ToUpperInvariant();
                var lw = Text.Layout(s, font, 12, spacing: 12 * .32f);
                rows.Add(new Row { W = Text.Width(lw), H = Text.Height(lw), Mt = 10, Emit = (x, y) => ops.Add(new TextOp { Layer = LInfo, L = lw, P = new(x, y), Opacity = .75f, AccentRange = accentRange }) });
            }
        }

        bool attached = (int)N("quoteLayout") != 1;
        var quoteRows = QuoteRows(font, attached ? ops : null, out var qbOps);
        if (attached) rows.AddRange(quoteRows);

        align = AlignOf(cx);
        var hb = Stack(rows, cx, cy, ui);
        hb.Id = "clock";
        hb.Ops.AddRange(ops);
        Place(hb);
        blocks.Add(hb);

        // ----- #quoteBox on its own -----
        if (!attached && quoteRows.Count > 0)
        {
            quoteRows[0].Mt = 0; // #quoteBox:not(.attached) #quote { margin-top: 0 }
            var qb = Stack(quoteRows, (float)N("quoteX"), (float)N("quoteY"), ui);
            qb.Id = "quote";
            qb.Ops.AddRange(qbOps);
            Place(qb);
            blocks.Add(qb);
        }

        // ----- the task board (Render\Tasks.cs) -----
        BuildTasks(font);

        // ----- widgets (calendar, system, timer, album, world clocks) -----
        BuildWidgets(font);

        // ----- render each block's layers -----
        var text = Hex(Str("textColor"));
        var accent = Accent();
        foreach (var b in blocks) RenderLayers(b, text, accent);

        LayoutBoxes = blocks.Where(b => b.W > 0 && b.H > 0)
            .Select(b => new LayoutBox(b.Id, new System.Drawing.RectangleF(b.TopLeft.X * dpr, b.TopLeft.Y * dpr, b.W * b.Scale * dpr, b.H * b.Scale * dpr)))
            .ToArray();
    }

    /// <summary>The movable blocks as last laid out, in physical pixels (read from the UI thread).</summary>
    public volatile LayoutBox[] LayoutBoxes = Array.Empty<LayoutBox>();


    /// <summary>Rows for #quote and #music. Ops go to <paramref name="target"/> (the hud) or a new list.</summary>
    List<Row> QuoteRows(string font, List<Op>? target, out List<Op> ops)
    {
        ops = target ?? new List<Op>();
        var list = ops;
        var rows = new List<Row>();
        float qs = (float)N("quoteScale") / 100;
        float x = (int)N("quoteLayout") == 1 ? (float)N("quoteX") : 0;
        string qAlign = (int)N("quoteLayout") == 1 ? AlignOf(x) : AlignOf((int)N("position") == 5 ? (float)N("clockX") : Presets[Math.Clamp((int)N("position"), 0, 4)].x);
        var dwAlign = qAlign == "left" ? TextAlignment.Leading : qAlign == "right" ? TextAlignment.Trailing : TextAlignment.Center;

        if (B("showQuote") && quoteShown.Length > 0)
        {
            float size = 21 * qs, maxW = (float)N("quoteWidth") * size;
            var lq = Text.Layout($"“{quoteShown}”", Text.Cormorant, size, italic: true, lineHeight: size * 1.4f, maxWidth: maxW, align: dwAlign);
            float qw = lq.Metrics.Width;
            IDWriteTextLayout? ls = null;
            float sw = 0, sh = 0;
            if (quoteSrcShown.Length > 0)
            {
                float ss = size * .52f;
                ls = Text.Layout(quoteSrcShown.ToUpper(CultureInfo.CurrentCulture), font, ss, spacing: ss * .32f);
                sw = Text.Width(ls); sh = Text.Height(ls);
            }
            float w = Math.Max(qw, sw);
            lq.MaxWidth = Math.Max(1, w);
            float qh = Text.Height(lq);
            rows.Add(new Row
            {
                W = w, H = qh + (ls != null ? 8 + sh : 0), Mt = 26, Emit = (bx, by) =>
                {
                    list.Add(new TextOp { Layer = LQuote, L = lq, P = new(bx, by) });
                    if (ls != null) list.Add(new TextOp { Layer = LQuote, L = ls, Accent = true, P = new(bx + AlignX(qAlign, w, sw), by + qh + 8) });
                },
            });
        }

        if (B("showMusic") && !string.IsNullOrEmpty(trackTitle))
        {
            float size = 12 * qs, sp = size * .28f;
            var culture = CultureInfo.CurrentCulture;
            var s = "◆" + trackTitle!.ToUpper(culture) + (string.IsNullOrEmpty(trackArtist) ? "" : "  —  " + trackArtist!.ToUpper(culture));
            var lmu = Text.Layout(s, font, size, spacing: sp);
            using (var l1 = lmu.QueryInterface<IDWriteTextLayout1>())
                l1.SetCharacterSpacing(0, sp + size * .6f, 0, new TextRange(0, 1)); // <b> margin-right: .6em
            rows.Add(new Row { W = Text.Width(lmu), H = Text.Height(lmu), Mt = 22, Emit = (bx, by) => list.Add(new TextOp { Layer = LMusic, L = lmu, P = new(bx, by), AccentRange = (0, 1) }) });
        }
        return rows;
    }

    void RenderLayers(Block b, Color4 text, Color4 accent)
    {
        if (b.W <= 0 || b.H <= 0) return;
        float k = b.Scale * dpr;
        foreach (var layer in b.Ops.Select(o => o.Layer).Distinct())
        {
            var shadows = Shadows[layer];
            // the backdrop reaches past the block's edges by its padding
            float pad = shadows.Length == 0 ? BackdropReach : MathF.Ceiling(shadows.Max(s => s.sigma * 3 + Math.Abs(s.dy)) + 4);
            int pw = (int)MathF.Ceiling((b.W + 2 * pad) * k), ph = (int)MathF.Ceiling((b.H + 2 * pad) * k);
            using var content = g.CreateTargetBitmap(pw, ph);
            dc.Target = content;
            dc.Transform = Matrix3x2.CreateTranslation(pad, pad) * Matrix3x2.CreateScale(k);
            dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            dc.Clear(new Color4(0, 0, 0, 0));
            foreach (var op in b.Ops) if (op.Layer == layer) op.Draw(this, text, accent);

            var final = g.CreateTargetBitmap(pw, ph);
            dc.Target = final;
            dc.Transform = Matrix3x2.Identity;
            dc.Clear(new Color4(0, 0, 0, 0));
            foreach (var (sigma, isAccent, alpha, dy) in shadows)
            {
                using var sh = new Shadow(dc) { BlurStandardDeviation = sigma * k };
                var c = isAccent ? accent : new Color4(0, 0, 0, 1);
                sh.Color = new Vector4(c.R, c.G, c.B, alpha);
                sh.SetInput(0, content, true);
                dc.DrawImage(sh, new Vector2(0, MathF.Round(dy * k)), InterpolationMode.Linear, CompositeMode.SourceOver);
            }
            dc.DrawImage(content, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
            var pos = new Vector2(MathF.Round(b.TopLeft.X * dpr - pad * k), MathF.Round(b.TopLeft.Y * dpr - pad * k));
            b.Layers[layer] = (final, pos);
        }
        dc.Target = null;
    }

    void DrawHud()
    {
        dc.Transform = Matrix3x2.Identity;
        // #time .colon: pulse 2s ease-in-out infinite (1 -> .35 -> 1)
        float colon = .35f + .65f * (.5f + .5f * MathF.Cos((float)t * MathF.PI));
        foreach (var b in blocks)
        {
            if (b.Layers.TryGetValue(LBackdrop, out var back))
            {
                var bs = back.bmp.PixelSize;
                dc.DrawBitmap(back.bmp, new Vortice.RawRectF(back.pos.X, back.pos.Y, back.pos.X + bs.Width, back.pos.Y + bs.Height), 1, BitmapInterpolationMode.NearestNeighbor, null);
            }
            foreach (var (layer, (bmp, pos)) in b.Layers)
            {
                if (layer == LBackdrop) continue;
                float o = layer switch { LColon => colon, LQuote => .78f * quoteFade, LMusic => .8f, _ => 1 };
                var sz = bmp.PixelSize;
                dc.DrawBitmap(bmp, new Vortice.RawRectF(pos.X, pos.Y, pos.X + sz.Width, pos.Y + sz.Height), o, BitmapInterpolationMode.NearestNeighbor, null);
            }
        }
        if (B("guides"))
        {
            using var brush = dc.CreateSolidColorBrush(Accent());
            foreach (var b in blocks)
            {
                if (b.W <= 0) continue;
                float o = 12 * b.Scale;
                var r = new Vortice.RawRectF((b.TopLeft.X - o) * dpr, (b.TopLeft.Y - o) * dpr,
                    (b.TopLeft.X + b.W * b.Scale + o) * dpr, (b.TopLeft.Y + b.H * b.Scale + o) * dpr);
                dc.DrawRectangle(r, brush, 1, dashStroke);
            }
        }
    }

    void DisposeHud()
    {
        foreach (var b in blocks)
        {
            foreach (var op in b.Ops) op.Dispose();
            foreach (var (bmp, _) in b.Layers.Values) bmp.Dispose();
        }
        blocks.Clear();
    }
}
