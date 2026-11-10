using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using RectangleF = System.Drawing.RectangleF;

namespace RoninHost.Render;

/// <summary>
/// The optional background behind the desktop task list ("todoBg"): one panel behind the whole list, or a box
/// behind each line. Its look comes from <see cref="TaskStyle"/> (shared with the task HUD), plus the colour,
/// opacity and rounding settings. Drawn into its own layer, without the text shadow, under the text.
/// </summary>
internal sealed partial class Scene
{
    /// <summary>How far past the block's edges the backdrop may reach (CSS px): the layer's bitmap padding.</summary>
    const float BackdropReach = 30;
    const float PanelPadX = 20, PanelPadY = 16, LinePadX = 12, LineGap = 1.5f;

    /// <summary>0 off, 1 one panel, 2 a box per line.</summary>
    int BackdropMode => Math.Clamp((int)N("todoBg"), 0, 2);

    /// <summary>The panel around a whole block (block-local CSS px).</summary>
    static RectangleF PanelRect(float w, float h) => new(-PanelPadX, -PanelPadY, w + 2 * PanelPadX, h + 2 * PanelPadY);

    /// <summary>A box around one line of the block.</summary>
    static RectangleF LineRect(float x, float y, float w, float h) => new(x - LinePadX, y + LineGap, w + 2 * LinePadX, h - 2 * LineGap);

    void AddBackdrop(List<Op> ops, IReadOnlyList<RectangleF> rects)
    {
        if (rects.Count == 0) return;
        var style = TaskStyle.At(N("todoBgStyle"));
        var fill = Hex(Str("todoBgColor").Length > 0 ? Str("todoBgColor") : "#0c0d10");
        float opacity = Math.Clamp((float)N("todoBgOpacity") / 100, 0, 1);
        float radius = Math.Clamp((float)N("todoBgRound"), 0, 24);
        var list = rects.ToArray();
        ops.Add(new FnOp { Layer = LBackdrop, F = (s, _, accent) => { foreach (var r in list) s.DrawBackdrop(r, style, fill, opacity, radius, accent); } });
    }

    void DrawBackdrop(RectangleF r, TaskStyle st, Color4 fill, float opacity, float radius, Color4 a)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var rr = new RoundedRectangle { Rect = new Vortice.RawRectF(r.Left, r.Top, r.Right, r.Bottom), RadiusX = radius, RadiusY = radius };
        Color4 F(float alpha) => new(fill.R, fill.G, fill.B, alpha);

        // the ink: flat, or a little more see-through at the top
        if (opacity > 0)
        {
            if (st.Shade > 0)
            {
                using var stops = Stops(new[] { (0f, F(opacity * (1 - st.Shade))), (1f, F(opacity)) });
                using var br = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(0, r.Top), new Vector2(0, r.Bottom)), stops);
                dc.FillRoundedRectangle(rr, br);
            }
            else
            {
                using var br = dc.CreateSolidColorBrush(F(opacity));
                dc.FillRoundedRectangle(rr, br);
            }
        }

        // the gold follows the opacity too, so 0 % really is nothing (and low values stay subtle)
        float gold = Math.Min(1, opacity * 2);
        if (gold <= 0) return;
        Color4 A(float alpha) => new(a.R, a.G, a.B, alpha * gold);

        if (st.EdgeAlpha > 0)
        {
            using var br = dc.CreateSolidColorBrush(A(st.EdgeAlpha));
            var inner = rr; // a hairline just inside the box
            inner.Rect = new Vortice.RawRectF(r.Left + .5f, r.Top + .5f, r.Right - .5f, r.Bottom - .5f);
            dc.DrawRoundedRectangle(inner, br, 1);
        }
        if (st.TopRule)
        {
            using var stops = Stops(new[] { (0f, A(0)), (.5f, A(.85f)), (1f, A(0)) });
            using var br = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(r.Left, 0), new Vector2(r.Right, 0)), stops);
            dc.FillRectangle(new Vortice.RawRectF(r.Left + radius, r.Top, r.Right - radius, r.Top + 1), br);
        }
        if (st.CornerMarks > 0)
        {
            float l = Math.Min(st.CornerMarks, Math.Min(r.Width, r.Height) / 3);
            using var br = dc.CreateSolidColorBrush(A(.85f));
            foreach (var (cx, cy, dx, dy) in new[] { (r.Left, r.Top, 1, 1), (r.Right, r.Top, -1, 1), (r.Left, r.Bottom, 1, -1), (r.Right, r.Bottom, -1, -1) })
            {
                float x = cx + dx * .5f, y = cy + dy * .5f;
                dc.DrawLine(new Vector2(x, y), new Vector2(x + dx * l, y), br, 1);
                dc.DrawLine(new Vector2(x, y), new Vector2(x, y + dy * l), br, 1);
            }
        }
    }
}
