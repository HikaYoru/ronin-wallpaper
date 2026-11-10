using System.Diagnostics;

namespace RoninHost.Render;

/// <summary>
/// Snow, rain, embers, mist and lightning timing: the particle simulation, without any drawing.
/// Each monitor normally has its own (sky = that screen). With "drift across monitors" on, every
/// monitor's render thread shares one sky that spans the whole desktop: whichever thread draws first in
/// a frame steps it, all of them draw their own slice, so a snowflake that leaves one screen enters the next.
/// Units: "sky units" = physical pixels / <see cref="Dpr"/> (CSS px for a single-screen sky).
/// </summary>
internal sealed class Sky
{
    public sealed class Flake { public float X, Y, Z, R, Vy, Sway, Ph, A; }
    public sealed class Drop { public float X, Y, Z, Len, Vy; public int B; }
    public sealed class Ember { public float X, Y, Z, Vy, Vx, R, Ph, F, Life, Max; }
    public sealed class Fog { public float X, Y, W, H, V, A; }

    public readonly object Gate = new();
    public readonly float W, H;          // the sky's size
    public readonly float ScreenW;       // one screen's width (mist patch sizes and speeds)
    public readonly float AreaScale;     // sky area / one screen (particle counts)
    public readonly float Dpr;           // physical pixels per sky unit
    public readonly List<Flake> Snow = new();
    public readonly List<Drop> Rain = new();
    public readonly List<Ember> Embers = new();
    public readonly List<Fog> Mist = new();
    public double T;                     // animation time (sway, flicker)
    public double Clock = -1;            // time of the last step
    public double StrikeAt = -1;         // lightning: when the current strike began (Clock time)
    double nextStrike = -1;
    readonly Random rng = new();

    public Sky(float w, float h, float screenW, float areaScale, float dpr)
    {
        W = w; H = h; ScreenW = screenW; AreaScale = Math.Max(1, areaScale); Dpr = dpr;
        int n = (int)Math.Round(7 * AreaScale);
        for (int i = 0; i < n; i++)
            Mist.Add(new Fog
            {
                X = Rand(-.3f * ScreenW, W), Y = Rand(.45f, .95f), W = Rand(.5f, .9f) * ScreenW, H = Rand(.18f, .32f),
                V = Rand(.004f, .012f) * (i % 2 == 1 ? 1 : .7f), A = Rand(.6f, 1),
            });
    }

    float Rand(float a, float b) => a + (float)rng.NextDouble() * (b - a);

    // ---------------- the shared one ----------------
    static readonly Stopwatch shared = Stopwatch.StartNew();
    static Sky? sharedSky;
    static string sharedKey = "";
    static readonly object sharedGate = new();

    /// <summary>Seconds on one clock all monitors agree on (panorama breathing, the shared sky).</summary>
    public static double SharedTime => shared.Elapsed.TotalSeconds;

    /// <summary>The desktop-wide sky for this monitor layout (made on first use, replaced when the layout changes).</summary>
    public static Sky Shared(string key, Func<Sky> make)
    {
        lock (sharedGate)
        {
            if (sharedSky == null || sharedKey != key) { sharedSky = make(); sharedKey = key; }
            return sharedSky;
        }
    }

    // ---------------- stepping ----------------

    /// <summary>
    /// Advance to <paramref name="clock"/>. Call with the lock held. A shared sky is stepped by whichever
    /// monitor gets there first; the others find it already up to date.
    /// </summary>
    public void Step(double clock, int precip, float amount, float windPct, float fogPct, bool lightning, float emberSetting, float pulse)
    {
        if (Clock >= 0 && clock - Clock < .004) return;
        float dt = Clock < 0 ? 0 : (float)Math.Min(clock - Clock, .05);
        Clock = clock;
        T += dt;

        Sync(Snow, precip == 0 ? (int)Math.Round(amount * 4.5 * AreaScale) : 0, NewFlake);
        Sync(Rain, precip == 1 ? (int)Math.Round(amount * 6 * AreaScale) : 0, NewDrop);
        Sync(Embers, (int)Math.Round(emberSetting * .7 * AreaScale), NewEmber);

        // lightning: one schedule for the whole sky, so every monitor flashes together
        if (!lightning) nextStrike = -1;
        else if (nextStrike < 0) nextStrike = clock + 7 + rng.NextDouble() * 18;
        else if (clock >= nextStrike) { StrikeAt = clock; nextStrike = clock + 7 + rng.NextDouble() * 18; }

        float wind = windPct / 100, t = (float)T;
        if (fogPct > 0)
            foreach (var f in Mist)
            {
                f.X += f.V * ScreenW * dt * (1 + wind);
                if (f.X > W + .15f * ScreenW) f.X = -f.W - .1f * ScreenW;
                if (f.X < -f.W - .2f * ScreenW) f.X = W + .1f * ScreenW;
            }

        for (int i = 0; i < Embers.Count; i++)
        {
            var e = Embers[i];
            e.Life += dt;
            e.X += (e.Vx + MathF.Sin(t * .8f + e.Ph) * 8 + windPct * .15f) * dt;
            e.Y += e.Vy * (1 + pulse * 1.5f) * dt;
            if (e.Y < -20 || e.Life > e.Max) Embers[i] = NewEmber(true);
        }

        for (int i = 0; i < Snow.Count; i++)
        {
            var p = Snow[i];
            p.Y += p.Vy * dt;
            p.X += (wind * (40 + p.Z * 110) + MathF.Sin(t * p.Sway + p.Ph) * 14 * p.Z) * dt;
            if (p.Y > H + 10) { Snow[i] = NewFlake(true); continue; }
            if (p.X > W + ScreenW * .15f) p.X = -ScreenW * .1f;
            else if (p.X < -ScreenW * .15f) p.X = W + ScreenW * .1f;
        }

        float slant = wind * .55f;
        for (int i = 0; i < Rain.Count; i++)
        {
            var d = Rain[i];
            d.Y += d.Vy * dt;
            d.X += d.Vy * slant * dt;
            if (d.Y > H + 30) { Rain[i] = NewDrop(true); continue; }
            float span = W + ScreenW * .5f;
            if (d.X > W + ScreenW * .3f) d.X -= span; else if (d.X < -ScreenW * .3f) d.X += span;
        }
    }

    Flake NewFlake(bool top)
    {
        float z = MathF.Pow((float)rng.NextDouble(), 1.6f) * .95f + .05f;
        return new Flake { X = Rand(-.1f * ScreenW, W + .1f * ScreenW), Y = top ? Rand(-40, -5) : Rand(0, H), Z = z, R = .8f + z * 3.4f, Vy = 18 + z * 75, Sway = Rand(.4f, 1.4f), Ph = Rand(0, 6.28f), A = .25f + z * .65f };
    }

    Drop NewDrop(bool top)
    {
        float z = MathF.Pow((float)rng.NextDouble(), 1.3f) * .85f + .15f;
        return new Drop { X = Rand(-.25f * ScreenW, W + .25f * ScreenW), Y = top ? Rand(-H * .3f, -10) : Rand(-H * .2f, H), Z = z, Len = 10 + z * 26, Vy = 650 + z * 750, B = z < .45f ? 0 : z < .75f ? 1 : 2 };
    }

    Ember NewEmber(bool bottom) => new()
    {
        X = Rand(0, W), Y = bottom ? H + Rand(5, 60) : Rand(H * .3f, H), Z = Rand(.3f, 1),
        Vy = -Rand(8, 26), Vx = Rand(-6, 6), R = Rand(1.2f, 3.2f), Ph = Rand(0, 6.28f), F = Rand(1.5f, 4), Life = 0, Max = Rand(9, 22),
    };

    static void Sync<T>(List<T> list, int target, Func<bool, T> make)
    {
        while (list.Count < target) list.Add(make(false));
        if (list.Count > target) list.RemoveRange(target, list.Count - target);
    }
}
