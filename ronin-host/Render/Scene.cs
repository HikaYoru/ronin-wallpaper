using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;

namespace RoninHost.Render;

/// <summary>
/// The Ronin wallpaper drawn with Direct2D: a port of ronin-wallpaper/index.html.
/// Units follow the page: "CSS px" (W x H), multiplied by <see cref="dpr"/> when drawing.
/// Everything here runs on the render thread.
/// </summary>
internal sealed partial class Scene : IDisposable
{
    readonly Gfx g;
    ID2D1DeviceContext dc => g.DC;
    readonly string folder;
    readonly float dpr;
    readonly float W, H;
    readonly Action<Action<Scene>> post;      // run something on the render thread later (e.g. after a download)
    readonly Random rng = new();

    // ---------------- settings (same keys and defaults as the page's S object) ----------------
    readonly Dictionary<string, JsonNode?> S = new()
    {
        ["background"] = "images/ink-mountains.jpg", ["dim"] = 15, ["desaturate"] = 30,
        ["precip"] = 0, ["snow"] = 45, ["wind"] = 25, ["fog"] = 40, ["embers"] = 35,
        ["lightning"] = false, ["parallax"] = true, ["vignette"] = 60, ["grain"] = true,
        ["liveWeather"] = false, ["city"] = "", ["units"] = 0, ["showWeather"] = true,
        ["timeOfDay"] = true, ["todStrength"] = 60,
        ["viz"] = true, ["vizY"] = 6, ["vizHeight"] = 70, ["vizThickness"] = 2, ["vizSensitivity"] = 100, ["vizReact"] = true,
        ["accent"] = "#c9a24a", ["textColor"] = "#d9d6cf",
        ["showClock"] = true, ["clock24"] = false, ["showDate"] = true,
        ["position"] = 0, ["clockX"] = 100, ["clockY"] = 0, ["font"] = 0, ["uiScale"] = 100, ["guides"] = false,
        ["showQuote"] = true, ["quoteText"] = "", ["quoteMinutes"] = 10, ["quoteScale"] = 100, ["quoteWidth"] = 30,
        ["quoteLayout"] = 0, ["quoteX"] = 50, ["quoteY"] = 92,
        ["showMusic"] = true,
        ["showTodo"] = true, ["todoTitle"] = "Tasks", ["todoText"] = "", ["todoX"] = 0, ["todoY"] = 55, ["todoScale"] = 100,
        // host extras (extras.json)
        ["panorama"] = false, ["sharedWeather"] = true, ["todoEdit"] = true, ["todoDaily"] = false, ["todoDone"] = 0,
        ["todoBg"] = 0, ["todoBgStyle"] = 0, ["todoBgColor"] = "#0c0d10", ["todoBgOpacity"] = 55, ["todoBgRound"] = 3,
        ["showCalendar"] = false, ["calendarUrl"] = "", ["calendarDays"] = 3, ["calendarMax"] = 6, ["calX"] = 0, ["calY"] = 0, ["calScale"] = 100,
        ["showSystem"] = false, ["sysX"] = 100, ["sysY"] = 100, ["sysScale"] = 100,
        ["showTimer"] = false, ["timerMinutes"] = 25, ["timerBreak"] = 5, ["timerX"] = 100, ["timerY"] = 50, ["timerScale"] = 100,
        ["showAlbum"] = false, ["albumX"] = 0, ["albumY"] = 100, ["albumScale"] = 100,
        ["showWorld"] = false, ["worldClocks"] = "", ["worldX"] = 50, ["worldY"] = 0, ["worldScale"] = 100,
        // from the host, not a setting: [x, y, w, h, count] of all wallpaper monitors together (physical px)
        ["__span"] = null,
    };

    double N(string k)
    {
        if (!S.TryGetValue(k, out var v) || v is not JsonValue jv) return 0;
        // values are either parsed JSON (any number) or C# literals (int / double / bool / string)
        if (jv.TryGetValue(out double d)) return d;
        if (jv.TryGetValue(out int i)) return i;
        if (jv.TryGetValue(out long l)) return l;
        if (jv.TryGetValue(out float f)) return f;
        if (jv.TryGetValue(out bool b)) return b ? 1 : 0;
        if (jv.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return 0;
    }
    bool B(string k)
    {
        if (!S.TryGetValue(k, out var v) || v is not JsonValue jv) return false;
        if (jv.TryGetValue(out bool b)) return b;
        if (jv.TryGetValue(out string? s)) return s is "true" or "1";
        return N(k) != 0;
    }
    string Str(string k) => S.TryGetValue(k, out var v) && v is JsonValue jv && jv.TryGetValue(out string? s) ? s : "";

    static Color4 Hex(string c, float alpha = 1)
    {
        c = (c ?? "").Trim().TrimStart('#');
        if (c.Length == 8) c = c[2..];                         // #AARRGGBB -> RRGGBB
        if (c.Length == 3) c = string.Concat(c.Select(x => $"{x}{x}"));
        int n = int.TryParse(c, NumberStyles.HexNumber, null, out var v) ? v : 0;
        return new Color4(((n >> 16) & 255) / 255f, ((n >> 8) & 255) / 255f, (n & 255) / 255f, alpha);
    }
    Color4 Accent(float a = 1) => Hex(Str("accent"), a);

    // ---------------- state ----------------
    double t, last = -1, now;
    Vector2 mouse, mouseTarget;
    readonly float[] audioRaw = new float[64], audioSm = new float[64];
    double lastAudioAt = -1e9; int audioFrames; float audioPeak, bass;
    bool propsReceived;

    readonly System.Drawing.Rectangle monitor; // this screen, physical px in desktop coordinates
    readonly Sky ownSky;

    public Scene(Gfx g, string folder, float dpr, System.Drawing.Rectangle monitor, Action<Action<Scene>> post)
    {
        this.g = g;
        this.folder = folder;
        this.dpr = dpr;
        this.monitor = monitor;
        this.post = post;
        W = g.Width / dpr;
        H = g.Height / dpr;
        ownSky = new Sky(W, H, W, 1, dpr);

        fx = g.CreateTargetBitmap(g.Width, g.Height);
        frame = g.CreateTargetBitmap(g.Width, g.Height);
        roundStroke = g.Factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round, LineJoin = LineJoin.Round,
            MiterLimit = 10, // a C# default of 0 makes the stroke style draw nothing
        });
        dashStroke = g.Factory.CreateStrokeStyle(new StrokeStyleProperties { DashStyle = DashStyle.Dash, MiterLimit = 10 });

        bgMatrix = new ColorMatrix(dc) { AlphaMode = ColorMatrixAlphaMode.Premultiplied };
        bgXform = new AffineTransform2D(dc) { InterPolationMode = AffineTransform2DInterpolationMode.Linear, BorderMode = BorderMode.Hard };
        bgXform.SetInputEffect(0, bgMatrix, true);
        glowBlend = new Vortice.Direct2D1.Effects.Blend(dc) { Mode = BlendMode.Screen };
        glowBlend.SetInputEffect(0, bgXform, true);

        BuildGrain();

        dc.BeginDraw();
        snowSprite = Sprite(64, (0, new Color4(1, 1, 1, 1)), (.35f, new Color4(240 / 255f, 243 / 255f, 248 / 255f, .75f)), (1, new Color4(230 / 255f, 235 / 255f, 245 / 255f, 0)));
        fogSprite = Sprite(256, (0, new Color4(196 / 255f, 201 / 255f, 210 / 255f, .55f)), (.5f, new Color4(180 / 255f, 186 / 255f, 196 / 255f, .22f)), (1, new Color4(170 / 255f, 176 / 255f, 188 / 255f, 0)));
        dc.EndDraw().CheckError();
        dc.Target = null;

        ApplyAll();
    }

    float Rand(float a, float b) => a + (float)rng.NextDouble() * (b - a);

    // ---------------- settings from the host ----------------

    /// <summary>A batch of settings: all values are stored first, then applied (so the to-do list
    /// never sees a half-updated state and forgets ticks).</summary>
    public void SetProperties(JsonObject props)
    {
        var changed = new List<string>();
        foreach (var (k, v) in props)
            if (S.ContainsKey(k)) { S[k] = v?.DeepClone(); changed.Add(k); }
        bool first = !propsReceived;
        propsReceived = true;
        if (first) ApplyAll();
        else foreach (var k in changed) Apply(k);
    }

    static readonly HashSet<string> LayoutKeys = new() { "position", "clockX", "clockY", "uiScale", "quoteLayout", "quoteX", "quoteY", "quoteScale", "quoteWidth", "todoX", "todoY", "todoScale" };
    static readonly HashSet<string> WeatherKeys = new() { "liveWeather", "city", "units" };

    void ApplyAll() => Apply(null);

    void Apply(string? name)
    {
        bool all = name == null;
        if (all || name == "background") SetBackground();
        if (all || name is "accent") BuildEmberSprite();
        if (all || name == "font") Text.UseShippori((int)N("font") == 2);
        if (all || name == "showQuote" || name == "quoteText" || name == "quoteMinutes") StartQuotes();
        if (all || (name != null && WeatherKeys.Contains(name)))
        {
            if (name == "liveWeather") ComputeEffective();
            ScheduleWeather(all || name == "liveWeather" ? 0 : 1.5);
        }
        if (all || name is "timeOfDay" or "todStrength") UpdateTimeOfDay();
        if (all || name is "precip" or "snow" or "wind" or "fog" or "lightning") ComputeEffective();
        if (name == "parallax" && !B("parallax")) mouseTarget = Vector2.Zero;
        if (all || name is "__span" or "panorama" or "sharedWeather") UpdateSpan();
        if (all || name is "calendarUrl" or "showCalendar") calendarVersion = -1;
        // anything else that is shown as text or depends on layout: rebuild the HUD
        if (all || name is "accent" or "textColor" or "font" or "guides" or "showClock" or "showDate" or "clock24" or "showMusic"
                or "showWeather" or "showTodo" or "todoText" or "todoTitle" or "todoEdit" or "todoDone" or "todoBg" or "todoBgStyle" or "todoBgColor" or "todoBgOpacity" or "todoBgRound" or "showQuote" or "liveWeather"
            || (name != null && (LayoutKeys.Contains(name) || WidgetKeys.Contains(name))))
            hudDirty = true;
    }

    // ---------------- several monitors: the desktop span, panorama, shared sky ----------------
    System.Drawing.Rectangle span;  // every wallpaper monitor together, physical px
    int spanCount = 1;
    bool panorama, sharedSky;
    /// <summary>The panorama's area in this screen's CSS px (the screen itself when not in panorama).</summary>
    Vortice.RawRectF world;

    void UpdateSpan()
    {
        span = monitor; spanCount = 1;
        if (S.TryGetValue("__span", out var v) && v is JsonArray a && a.Count >= 5)
        {
            // numbers may be parsed JSON or C# ints / doubles
            int I(int i) => a[i] is JsonValue jv ? (jv.TryGetValue(out int n) ? n : jv.TryGetValue(out double d) ? (int)d : 0) : 0;
            span = new System.Drawing.Rectangle(I(0), I(1), I(2), I(3));
            spanCount = I(4);
        }
        bool multi = spanCount > 1 && span.Width > 0 && span.Height > 0;
        panorama = multi && B("panorama");
        sharedSky = multi && B("sharedWeather");
        world = panorama
            ? new Vortice.RawRectF((span.X - monitor.X) / dpr, (span.Y - monitor.Y) / dpr, (span.Right - monitor.X) / dpr, (span.Bottom - monitor.Y) / dpr)
            : new Vortice.RawRectF(0, 0, W, H);
        builtGlowOpacity = -1; // the glow follows the panorama
    }

    /// <summary>The sky to step and draw: this screen's own, or the one shared by all monitors.</summary>
    Sky CurrentSky()
    {
        if (!sharedSky) return ownSky;
        float d = dpr;
        var key = $"{span} {spanCount} {d}";
        return Sky.Shared(key, () => new Sky(span.Width / d, span.Height / d, monitor.Width / d, span.Width * (float)span.Height / Math.Max(1f, monitor.Width * (float)monitor.Height), d));
    }

    // ---------------- background ----------------
    ID2D1Bitmap1? bg, bgPending;
    IAnimatedBackground? gif; // when the background is an animated GIF or a video
    string bgFile = "";
    float bgAlpha = 1;
    int bgFade; // 0 none, -1 fading out, +1 fading in
    readonly ColorMatrix bgMatrix;
    readonly AffineTransform2D bgXform;
    readonly Vortice.Direct2D1.Effects.Blend glowBlend;

    void SetBackground()
    {
        var v = Str("background").Replace('\\', '/');
        if (!v.Contains('/')) v = "images/" + v;
        var full = Path.GetFullPath(Path.Combine(folder, v));
        if (full == bgFile) return;
        bgFile = full;
        ID2D1Bitmap1 bmp;
        gif?.Dispose(); gif = null;
        try
        {
            // animated GIFs play; everything else (and a one-frame GIF) is a still image
            if (VideoBackground.IsVideo(full)) { if (VideoBackground.TryOpen(full, g) is not { } video) return; gif = video; bmp = video.Bitmap; }
            else if (full.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) && GifBackground.TryOpen(full, g) is { } anim) { gif = anim; bmp = anim.Bitmap; }
            else bmp = g.LoadImage(full);
        }
        catch (Exception e) { Log.Write($"native: background '{full}' failed: {e.Message}"); return; }
        if (bg == null) { bg = bmp; bgAlpha = 1; bgMatrix.SetInput(0, bg, true); }
        else { bgPending?.Dispose(); bgPending = bmp; bgFade = -1; }
    }

    void StepBackground(float dt)
    {
        if (bgFade == -1)
        {
            bgAlpha -= dt / .8f;
            if (bgAlpha <= 0 && bgPending != null)
            {
                bgAlpha = 0;
                bg?.Dispose();
                bg = bgPending; bgPending = null;
                bgMatrix.SetInput(0, bg, true);
                bgFade = 1;
            }
        }
        else if (bgFade == 1)
        {
            bgAlpha += dt / .8f;
            if (bgAlpha >= 1) { bgAlpha = 1; bgFade = 0; }
        }
    }

    /// <summary>CSS filter: brightness() saturate() contrast(1.06), then the time-of-day multiply tint.</summary>
    void UpdateBgMatrix()
    {
        float b = 1 - (float)N("dim") / 100, s = 1 - (float)N("desaturate") / 100, c = 1.06f;
        // saturate(s) as out = A * in (rows = output channel)
        float[,] A =
        {
            { .213f + .787f * s, .715f - .715f * s, .072f - .072f * s },
            { .213f - .213f * s, .715f + .285f * s, .072f - .072f * s },
            { .213f - .213f * s, .715f - .715f * s, .072f + .928f * s },
        };
        float[] mul = { tintMul.X, tintMul.Y, tintMul.Z };
        // out_i = mul_i * (c * sum_j A_ij * b * in_j + 0.5 * (1 - c));  D2D: out = [r g b a 1] * M, so M[j][i] = coefficient
        float K(int j, int i) => mul[i] * c * A[i, j] * b;
        float O(int i) => mul[i] * .5f * (1 - c);
        bgMatrix.Matrix = new Matrix5x4(
            K(0, 0), K(0, 1), K(0, 2), 0,
            K(1, 0), K(1, 1), K(1, 2), 0,
            K(2, 0), K(2, 1), K(2, 2), 0,
            0, 0, 0, bgAlpha,
            O(0), O(1), O(2), 0);
    }

    Matrix3x2 BgTransform()
    {
        if (bg == null) return Matrix3x2.Identity;
        var sz = bg.Size;
        // panorama: the image covers every monitor together and this screen shows its own part of it
        float ww = world.Right - world.Left, wh = world.Bottom - world.Top;
        float sw = ww * 1.05f, sh = wh * 1.05f;                     // #scene is inset -2.5%
        float cover = MathF.Max(sw / sz.Width, sh / sz.Height);     // background-size: cover
        double time = panorama ? Sky.SharedTime : t;                // all monitors breathe in step
        double p = (time % 140) / 70; if (p > 1) p = 2 - p;         // 70 s, alternate
        float breathe = 1 + .06f * (float)(.5 - .5 * Math.Cos(Math.PI * p)); // ease-in-out 1 -> 1.06
        var par = new Vector2(-mouse.X * 16, -mouse.Y * 10);         // mouse parallax on #scene
        return Matrix3x2.CreateTranslation(-sz.Width / 2, -sz.Height / 2)
             * Matrix3x2.CreateScale(cover * breathe)
             * Matrix3x2.CreateTranslation(world.Left + ww / 2 + par.X, world.Top + wh / 2 + par.Y)
             * Matrix3x2.CreateScale(dpr);
    }

    // ---------------- time of day ----------------
    Vector3 tintMul = Vector3.One;
    float glowOpacity, builtGlowOpacity = -1;
    ID2D1CommandList? glowImage;
    double todNext;

    static readonly (string name, float[] c, float a, float g)[] TOD =
    {
        ("night", new float[] { 70, 86, 140 }, .55f, 0),
        ("dawn", new float[] { 220, 170, 185 }, .30f, .12f),
        ("day", new float[] { 255, 255, 255 }, 0, 0),
        ("dusk", new float[] { 235, 155, 95 }, .42f, .2f),
    };

    void UpdateTimeOfDay()
    {
        todNext = now + 60;
        if (!B("timeOfDay")) { tintMul = Vector3.One; glowOpacity = 0; return; }
        var d = DateTime.Now;
        int m = d.Hour * 60 + d.Minute;
        int sr = wxSunrise ?? 390, ss = wxSunset ?? 1110;
        var K = new (int t, int p)[] { (0, 0), (sr - 90, 0), (sr, 1), (sr + 90, 2), (ss - 90, 2), (ss, 3), (ss + 90, 0), (1440, 0) };
        int i = 0;
        while (i < K.Length - 2 && m >= K[i + 1].t) i++;
        var (t0, p0) = K[i]; var (t1, p1) = K[i + 1];
        float f = t1 > t0 ? Math.Clamp((m - t0) / (float)(t1 - t0), 0, 1) : 0;
        var A = TOD[p0]; var Bt = TOD[p1];
        float s = (float)N("todStrength") / 100;
        var c = new Vector3(A.c[0] + (Bt.c[0] - A.c[0]) * f, A.c[1] + (Bt.c[1] - A.c[1]) * f, A.c[2] + (Bt.c[2] - A.c[2]) * f) / 255f;
        float a = (A.a + (Bt.a - A.a) * f) * s;
        tintMul = Vector3.One - new Vector3(a) + a * c;                // mix-blend-mode: multiply at opacity a
        glowOpacity = (A.g + (Bt.g - A.g) * f) * s;
    }

    void BuildGlow()
    {
        if (Math.Abs(glowOpacity - builtGlowOpacity) < .002f) return;
        builtGlowOpacity = glowOpacity;
        glowImage?.Dispose(); glowImage = null;
        if (glowOpacity <= 0) return;
        glowImage = dc.CreateCommandList();
        dc.Target = glowImage;
        dc.BeginDraw();
        dc.Transform = Matrix3x2.CreateScale(dpr);
        float ww = world.Right - world.Left, wh = world.Bottom - world.Top;
        using var brush = Radial(new Vector2(world.Left + ww * .5f, world.Top - wh * .15f), ww * .9f, wh * .7f,
            (0, new Color4(1, 160 / 255f, 80 / 255f, .85f)), (.45f, new Color4(1, 120 / 255f, 60 / 255f, .25f)), (.75f, new Color4(0, 0, 0, 0)));
        brush.Opacity = glowOpacity;
        dc.FillRectangle(new Vortice.RawRectF(0, 0, W, H), brush);
        dc.EndDraw();
        glowImage.Close();
        dc.Target = null;
        glowBlend.SetInput(1, glowImage, true);
    }

    // ---------------- effective weather (live or manual) ----------------
    struct Eff { public int Precip; public float Amount, Wind, Fog; public bool Lightning; }
    Eff E = new() { Precip = 0, Amount = 45, Wind = 25, Fog = 40 };

    void ComputeEffective()
    {
        bool wasStorm = E.Lightning;
        if (B("liveWeather") && wx != null)
            E = new Eff { Precip = wx.Precip, Amount = wx.Amount, Wind = wx.Wind, Fog = wx.Fog, Lightning = wx.Storm };
        else
            E = new Eff { Precip = (int)N("precip"), Amount = (float)N("snow"), Wind = (float)N("wind"), Fog = (float)N("fog"), Lightning = B("lightning") };
        _ = wasStorm; // the sky schedules strikes
    }

    // ---------------- lightning (timed by the sky, so shared monitors flash together) ----------------
    float flash;
    static readonly (double t, float o)[] StrikeSeq = { (0, .5f), (.09, 0), (.17, .3f), (.26, 0), (.33, .15f), (.8, 0) };

    void StepLightning(float dt, Sky sky)
    {
        float target = 0;
        if (sky.StrikeAt >= 0)
        {
            double e = sky.Clock - sky.StrikeAt;
            foreach (var (st, o) in StrikeSeq) if (e >= st) target = o;
        }
        // transition: opacity .09s linear
        float step = dt / .09f;
        flash = flash < target ? Math.Min(target, flash + step) : Math.Max(target, flash - step);
    }

    // ---------------- particles (simulated by a Sky, drawn here) ----------------
    readonly ID2D1Bitmap1 snowSprite, fogSprite;
    ID2D1Bitmap1? emberSprite;
    readonly ID2D1Bitmap1 fx, frame;
    readonly ID2D1StrokeStyle roundStroke, dashStroke;

    ID2D1Bitmap1 Sprite(int size, params (float pos, Color4 c)[] stops)
    {
        // call between BeginDraw/EndDraw; leaves the sprite as the target
        var bmp = g.CreateTargetBitmap(size, size);
        dc.Target = bmp;
        dc.Transform = Matrix3x2.Identity;
        dc.Clear(new Color4(0, 0, 0, 0));
        using var brush = Radial(new Vector2(size / 2f), size / 2f, size / 2f, stops);
        dc.FillRectangle(new Vortice.RawRectF(0, 0, size, size), brush);
        return bmp;
    }

    void BuildEmberSprite()
    {
        var a = Accent();
        emberSprite?.Dispose();
        dc.BeginDraw();
        emberSprite = Sprite(64, (0, new Color4(1, 245 / 255f, 215 / 255f, 1)), (.12f, new Color4(a.R, a.G, a.B, .95f)),
            (.4f, new Color4(a.R, a.G, a.B, .28f)), (1, new Color4(a.R, a.G, a.B, 0)));
        dc.EndDraw().CheckError();
        dc.Target = null;
    }

    ID2D1RadialGradientBrush Radial(Vector2 center, float rx, float ry, params (float pos, Color4 c)[] stops)
    {
        using var coll = Stops(stops);
        return dc.CreateRadialGradientBrush(new RadialGradientBrushProperties(center, Vector2.Zero, rx, ry), coll);
    }

    ID2D1GradientStopCollection1 Stops((float pos, Color4 c)[] stops) =>
        dc.CreateGradientStopCollection(stops.Select(s => new GradientStop(s.pos, s.c)).ToArray(), (uint)stops.Length,
            ColorSpace.Srgb, ColorSpace.Srgb, BufferPrecision.PerChannel8Unorm, ExtendMode.Clamp, ColorInterpolationMode.Premultiplied);

    // ---------------- grain (SVG fractal noise, overlay at 7%) ----------------
    ID2D1Bitmap1? noise;
    Tile? grainTile;
    AffineTransform2D? grainXform;
    ColorMatrix? grainAlpha;
    Vortice.Direct2D1.Effects.Blend? grainBlend;

    void BuildGrain()
    {
        const int N = 180;
        var px = new byte[N * N * 4];
        var raw = new float[N * N];
        for (int i = 0; i < raw.Length; i++) raw[i] = (float)rng.NextDouble();
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                // light blur across a wrapping neighbourhood ~ baseFrequency .85, two octaves
                float v = raw[y * N + x] * .5f + (raw[y * N + (x + 1) % N] + raw[((y + 1) % N) * N + x]) * .25f;
                float gray = .5f + (v - .5f) * .7f;
                float a = .45f + (raw[((y + 7) % N) * N + (x + 13) % N] - .5f) * .3f;
                byte gp = (byte)(gray * a * 255), ap = (byte)(a * 255);
                int o = (y * N + x) * 4;
                px[o] = gp; px[o + 1] = gp; px[o + 2] = gp; px[o + 3] = ap;
            }
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { noise = g.CreateBitmap(N, N, handle.AddrOfPinnedObject(), N * 4); }
        finally { handle.Free(); }

        grainTile = new Tile(dc) { Rectangle = new Vector4(0, 0, N, N) };
        grainTile.SetInput(0, noise, true);
        grainXform = new AffineTransform2D(dc) { InterPolationMode = AffineTransform2DInterpolationMode.NearestNeighbor };
        grainXform.SetInputEffect(0, grainTile, true);
        grainAlpha = new ColorMatrix(dc) { AlphaMode = ColorMatrixAlphaMode.Premultiplied };
        grainAlpha.Matrix = new Matrix5x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, .07f, 0, 0, 0, 0);
        grainAlpha.SetInputEffect(0, grainXform, true);
        grainBlend = new Vortice.Direct2D1.Effects.Blend(dc) { Mode = BlendMode.Overlay };
        grainBlend.SetInput(0, frame, true);
        grainBlend.SetInputEffect(1, grainAlpha, true);
    }

    static readonly Vector2[] GrainKeys = { new(0, 0), new(-3, 2), new(2, -3), new(-2, -1), new(3, 3), new(0, 0) };
    int grainStep = -1;

    void StepGrain()
    {
        int step = (int)Math.Floor(t * 6) % 6;              // animation: grain 1s steps(6) infinite
        if (step == grainStep) return;
        grainStep = step;
        float p = step / 6f * 5;                             // progress across the 5 keyframe gaps
        int k = Math.Min(4, (int)p); float f = p - k;
        var pct = Vector2.Lerp(GrainKeys[k], GrainKeys[k + 1], f);
        // #grain is inset -50% (2W x 2H); translate is % of that box
        var off = new Vector2(-.5f * W + pct.X / 100 * 2 * W, -.5f * H + pct.Y / 100 * 2 * H);
        grainXform!.TransformMatrix = Matrix3x2.CreateTranslation(off) * Matrix3x2.CreateScale(dpr);
    }

    // ---------------- audio + mouse from the host ----------------
    public void Audio(float[] bins, double receivedAt)
    {
        audioFrames++;
        float pk = 0;
        for (int i = 0; i < bins.Length; i++) if (bins[i] > pk) pk = bins[i];
        audioPeak = Math.Max(pk, audioPeak * .9f);
        for (int i = 0; i < 64; i++) audioRaw[i] = Math.Min(1.5f, (bins[i] + bins[i + 64]) * .5f);
        lastAudioAt = receivedAt;
    }

    /// <summary>The cursor in desktop physical px (anywhere). With a panorama or a shared sky, parallax follows the
    /// cursor across all monitors so their slices stay lined up; otherwise only while it is on this screen.</summary>

    public void CursorScreen(int x, int y)
    {
        // hovering a task shows its edit controls
        if (B("todoEdit") && B("showTodo"))
            HoverTasks(monitor.Contains(x, y) ? new Vector2((x - monitor.X) / dpr, (y - monitor.Y) / dpr) : new Vector2(-1e6f));
        if (!B("parallax")) return;
        if (panorama || sharedSky)
        {
            if (!span.Contains(x, y)) return;
            mouseTarget = new Vector2((x - span.X) / (float)span.Width * 2 - 1, (y - span.Y) / (float)span.Height * 2 - 1);
        }
        else if (monitor.Contains(x, y))
            mouseTarget = new Vector2((x - monitor.X) / (float)monitor.Width * 2 - 1, (y - monitor.Y) / (float)monitor.Height * 2 - 1);
    }

    // ---------------- one frame ----------------
    public void Frame(double time)
    {
        now = time;
        float dt = last < 0 ? 0 : (float)Math.Min(time - last, .05);
        last = time;
        t += dt;

        mouse += (mouseTarget - mouse) * Math.Min(1, dt * 2.5f);
        StepBackground(dt);
        gif?.Step(now);
        StepGrain();
        StepQuote(dt);
        StepWeather();
        if (now >= todNext) UpdateTimeOfDay();
        StepClock();
        StepWidgets();

        // audio smoothing: fast attack, slow release; fall to silence if data stops
        if ((now - lastAudioAt) > .3) Array.Clear(audioRaw);
        float sens = (float)N("vizSensitivity") / 100, bsum = 0;
        for (int i = 0; i < 64; i++)
        {
            float target = audioRaw[i] * sens * (1 + i / 40f);
            float rate = target > audioSm[i] ? 18 : 5;
            audioSm[i] += (target - audioSm[i]) * Math.Min(1, dt * rate);
            if (i < 6) bsum += audioSm[i];
        }
        bass = Math.Min(1.2f, bsum / 6);
        float pulse = B("vizReact") ? bass : 0;

        var sky = CurrentSky();
        lock (sky.Gate)
        {
            sky.Step(sky == ownSky ? now : Sky.SharedTime, E.Precip, E.Amount, E.Wind, E.Fog, E.Lightning, (float)N("embers"), pulse);
            StepLightning(dt, sky);
        }

        BuildGlow();
        UpdateBgMatrix();

        dc.BeginDraw();
        if (hudDirty) RebuildHud();
        DrawFx(sky, pulse);

        bool grain = B("grain");
        dc.Target = grain ? frame : g.BackBuffer;
        dc.Transform = Matrix3x2.Identity;
        dc.Clear(new Color4(0x0f / 255f, 0x10 / 255f, 0x12 / 255f, 1));
        if (bg != null)
        {
            bgXform.TransformMatrix = BgTransform();
            if (glowImage != null) dc.DrawImage(glowBlend, InterpolationMode.Linear, CompositeMode.SourceOver);
            else dc.DrawImage(bgXform, InterpolationMode.Linear, CompositeMode.SourceOver);
        }
        dc.DrawImage(fx, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);

        dc.Transform = Matrix3x2.CreateScale(dpr);
        // panorama: flash and vignette are laid over all monitors together, not each screen
        float wx0 = world.Left, wy0 = world.Top, ww = world.Right - world.Left, wh = world.Bottom - world.Top;
        if (flash > .001f)
        {
            using var fb = Radial(new Vector2(wx0 + ww * .6f, wy0), ww * .6f * 1.4142f, wh * 1.4142f,
                (0, Hex("#eef2ff")), (.6f, Hex("#a9b4cc")), (1, Hex("#6c7486")));
            fb.Opacity = flash;
            dc.FillRectangle(new Vortice.RawRectF(0, 0, W, H), fb);
        }
        float vig = (float)N("vignette") / 100;
        if (vig > 0)
        {
            using var vb = Radial(new Vector2(wx0 + ww * .5f, wy0 + wh * .45f), ww * .5f * 1.4142f, wh * .55f * 1.4142f,
                (0, new Color4(0, 0, 0, 0)), (.4f, new Color4(0, 0, 0, 0)), (1, new Color4(5 / 255f, 5 / 255f, 7 / 255f, .85f)));
            vb.Opacity = vig;
            dc.FillRectangle(new Vortice.RawRectF(0, 0, W, H), vb);
        }

        if (grain)
        {
            dc.Target = g.BackBuffer;
            dc.Transform = Matrix3x2.Identity;
            dc.DrawImage(grainBlend!, InterpolationMode.NearestNeighbor, CompositeMode.SourceCopy);
        }

        DrawHud();
        if (B("guides")) DrawDiagnostics();

        dc.EndDraw().CheckError();
        frames++;
    }

    public int frames;

    void DrawFx(Sky sky, float pulse)
    {
        dc.Target = fx;
        dc.Transform = Matrix3x2.CreateScale(dpr);
        dc.Clear(new Color4(0, 0, 0, 0));
        var interp = BitmapInterpolationMode.Linear;

        lock (sky.Gate)
        {
            // sky units -> this screen's CSS px (a shared sky spans the desktop; ours is just this screen)
            float k = sky.Dpr / dpr;
            Vector2 off = sky == ownSky ? Vector2.Zero : new Vector2((monitor.X - span.X) / dpr, (monitor.Y - span.Y) / dpr);
            Vector2 L(float x, float y) => new Vector2(x * k, y * k) - off;
            bool Visible(Vector2 p, float m) => p.X > -m && p.X < W + m && p.Y > -m && p.Y < H + m;
            float st = (float)sky.T;

            // mist
            if (E.Fog > 0)
            {
                foreach (var f in sky.Mist)
                {
                    float breathe = .75f + .25f * MathF.Sin(st * .15f + f.Y * 10);
                    float a = E.Fog / 100 * .45f * f.A * breathe;
                    var p = L(f.X - mouse.X * .006f * sky.ScreenW, f.Y * sky.H - f.H * sky.H / 2);
                    float w = f.W * k, h = f.H * sky.H * k;
                    if (p.X > W || p.X + w < 0) continue;
                    dc.DrawBitmap(fogSprite, new Vortice.RawRectF(p.X, p.Y, p.X + w, p.Y + h), a, interp, null);
                }
            }

            // embers (additive, pulse with the bass)
            if (emberSprite != null && sky.Embers.Count > 0)
            {
                dc.PrimitiveBlend = PrimitiveBlend.Add;
                foreach (var e in sky.Embers)
                {
                    float fadeIn = Math.Min(1, e.Life / 2), fadeOut = Math.Min(1, (e.Max - e.Life) / 3);
                    float flicker = .55f + .45f * MathF.Sin(st * e.F + e.Ph);
                    float alpha = Math.Clamp(fadeIn * fadeOut * flicker * e.Z * (1 + pulse * 1.6f), 0, 1);
                    float s = e.R * 5 * e.Z * (1 + pulse * .6f) * k;
                    var p = L(e.X, e.Y) - new Vector2(mouse.X * 10 * e.Z + s / 2, mouse.Y * 6 * e.Z + s / 2);
                    if (!Visible(p, s)) continue;
                    dc.DrawBitmap(emberSprite, new Vortice.RawRectF(p.X, p.Y, p.X + s, p.Y + s), alpha, interp, null);
                }
                dc.PrimitiveBlend = PrimitiveBlend.SourceOver;
            }

            // snow
            foreach (var f in sky.Snow)
            {
                float s = f.R * 2 * k;
                var p = L(f.X, f.Y) - new Vector2(mouse.X * 22 * f.Z + s / 2, mouse.Y * 12 * f.Z + s / 2);
                if (!Visible(p, s)) continue;
                dc.DrawBitmap(snowSprite, new Vortice.RawRectF(p.X, p.Y, p.X + s, p.Y + s), f.A, interp, null);
            }

            // rain: three depth layers, each one batched path
            if (sky.Rain.Count > 0)
            {
                float slant = E.Wind / 100 * .55f;
                var layers = new (float a, float w)[] { (.12f, .6f), (.22f, 1), (.36f, 1.5f) };
                using var brush = dc.CreateSolidColorBrush(new Color4(200 / 255f, 208 / 255f, 222 / 255f, 1));
                for (int b = 0; b < 3; b++)
                {
                    using var geo = g.Factory.CreatePathGeometry();
                    using (var sink = geo.Open())
                    {
                        foreach (var d in sky.Rain)
                        {
                            if (d.B != b) continue;
                            var p = L(d.X, d.Y) - new Vector2(mouse.X * 22 * d.Z, mouse.Y * 12 * d.Z);
                            if (!Visible(p, 60)) continue;
                            sink.BeginFigure(p, FigureBegin.Filled);
                            sink.AddLine(new Vector2(p.X - d.Len * k * slant, p.Y - d.Len * k));
                            sink.EndFigure(FigureEnd.Open);
                        }
                        sink.Close();
                    }
                    brush.Opacity = layers[b].a;
                    dc.DrawGeometry(geo, brush, layers[b].w, roundStroke);
                }
            }
        }

        if (B("viz")) DrawInk();
    }

    // ---------------- music ink line ----------------
    const int VizN = 120;
    readonly Vector2[] vizPts = new Vector2[VizN + 1];

    void DrawInk()
    {
        float baseY = H * (1 - (float)N("vizY") / 100), amp = (float)N("vizHeight");
        for (int j = 0; j <= VizN; j++)
        {
            float u = j / (float)VizN, d = Math.Abs(u - .5f) * 2;   // bass in the middle, highs at the edges
            float f = d * 63; int i0 = (int)f, i1 = Math.Min(63, i0 + 1); float fr = f - i0;
            float v = Math.Min(1.4f, audioSm[i0] * (1 - fr) + audioSm[i1] * fr);
            // fade into the screen edges (clamped: float sin(pi) is slightly negative, and pow() of that is NaN)
            float env = MathF.Pow(MathF.Max(0, MathF.Sin(MathF.PI * u)), .8f);
            float idle = MathF.Sin(u * 14 + (float)t * .9f) * 1.6f + MathF.Sin(u * 31 - (float)t * 1.3f) * .8f;
            vizPts[j] = new Vector2(u * W, baseY - (v * amp + idle) * env);
        }
        var a = Accent();
        var hi = new Color4(a.R + (1 - a.R) * .45f, a.G + (1 - a.G) * .45f, a.B + (1 - a.B) * .45f, 1);
        using var stops = Stops(new[] {
            (0f, new Color4(a.R, a.G, a.B, 0)), (.18f, new Color4(a.R, a.G, a.B, .85f)), (.5f, hi),
            (.82f, new Color4(a.R, a.G, a.B, .85f)), (1f, new Color4(a.R, a.G, a.B, 0)) });
        using var brush = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, new Vector2(W, 0)), stops);
        float th = (float)N("vizThickness");

        dc.PrimitiveBlend = PrimitiveBlend.Add;
        void Stroke(float scaleY, float off, float alpha, float width)
        {
            using var geo = g.Factory.CreatePathGeometry();
            using (var sink = geo.Open())
            {
                Vector2 P(int j) => new(vizPts[j].X, baseY + (vizPts[j].Y - baseY) * scaleY + off);
                // canvas quadraticCurveTo(control = point j, end = midpoint to j+1), flattened into short lines
                var start = P(0);
                sink.BeginFigure(start, FigureBegin.Filled);
                for (int j = 1; j < VizN; j++)
                {
                    var c = P(j); var e = (c + P(j + 1)) / 2;
                    for (int s = 1; s <= 4; s++)
                    {
                        float u = s / 4f, iu = 1 - u;
                        sink.AddLine(iu * iu * start + 2 * iu * u * c + u * u * e);
                    }
                    start = e;
                }
                sink.AddLine(P(VizN));
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            brush.Opacity = alpha;
            dc.DrawGeometry(geo, brush, width, roundStroke);
        }
        Stroke(1, 0, .1f + bass * .18f, th * 7);                // glow
        Stroke(1, 0, .95f, th);                                 // main stroke
        Stroke(1.06f, th * 1.3f, .35f, Math.Max(.6f, th * .45f)); // brush bristle
        Stroke(-.45f, 0, .16f, th * .8f);                       // reflection
        dc.PrimitiveBlend = PrimitiveBlend.SourceOver;
    }

    void DrawDiagnostics()
    {
        bool recent = now - lastAudioAt < 1;
        string msg = audioFrames > 0
            ? (recent ? $"AUDIO: receiving · peak {audioPeak:0.00}{(audioPeak < .01 ? " (silent — is anything playing?)" : "")}" : "AUDIO: data stopped")
            : "AUDIO: connected, no data received yet";
        msg += $"  ·  media: on  ·  native renderer  ·  {fpsShown} fps";
        using var layout = Text.Layout(msg, Text.Cinzel, 12);
        using var brush = dc.CreateSolidColorBrush(Accent(.9f));
        dc.Transform = Matrix3x2.CreateScale(dpr);
        dc.DrawTextLayout(new Vector2(20, H * (1 - (float)N("vizY") / 100) - 14 - Text.Baseline(layout)), layout, brush);
    }

    public int fpsShown;
    public string AudioInfo => $"frames {audioFrames}, peak {audioPeak:0.00}, bass {bass:0.00}";

    public void Dispose()
    {
        DisposeHud();
        albumArt?.Dispose();
        gif?.Dispose();
        grainBlend?.Dispose(); grainAlpha?.Dispose(); grainXform?.Dispose(); grainTile?.Dispose(); noise?.Dispose();
        glowBlend.Dispose(); bgXform.Dispose(); bgMatrix.Dispose(); glowImage?.Dispose();
        bg?.Dispose(); bgPending?.Dispose();
        snowSprite.Dispose(); fogSprite.Dispose(); emberSprite?.Dispose();
        fx.Dispose(); frame.Dispose();
        roundStroke.Dispose(); dashStroke.Dispose();
    }
}
