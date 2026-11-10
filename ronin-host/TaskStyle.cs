namespace RoninHost;

/// <summary>
/// A look for the box behind the desktop task list and for the sticky-note task HUD.
/// To add a style: add an entry to <see cref="All"/> and its name to "todoBgStyle"'s items in extras.json
/// (same order; web-widgets.js reads the same index). Colour, opacity and corner rounding stay separate
/// settings so every style can use them.
/// </summary>
/// <param name="Name">Shown in Settings.</param>
/// <param name="EdgeAlpha">Accent-coloured hairline around the box (0 = none).</param>
/// <param name="CornerMarks">Gold L-shaped marks on the four corners: their length in CSS px (0 = none).</param>
/// <param name="TopRule">A gold line along the top edge that fades out at both ends, like the wallpaper's rules.</param>
/// <param name="Shade">How much more see-through the top of the box is than the bottom (0 = flat).</param>
internal sealed record TaskStyle(string Name, float EdgeAlpha, float CornerMarks, bool TopRule, float Shade)
{
    public static readonly TaskStyle[] All =
    {
        new("Ronin: ink with a gold edge", EdgeAlpha: .32f, CornerMarks: 0, TopRule: true, Shade: .25f),
        new("Ronin: ink with gold corners", EdgeAlpha: .10f, CornerMarks: 12, TopRule: false, Shade: .15f),
        new("Ronin: plain ink", EdgeAlpha: 0, CornerMarks: 0, TopRule: false, Shade: 0),
    };

    public static TaskStyle At(double index) => All[Math.Clamp((int)index, 0, All.Length - 1)];
}
