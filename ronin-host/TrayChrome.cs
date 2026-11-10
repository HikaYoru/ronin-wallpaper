using System.Drawing.Drawing2D;

namespace RoninHost;

/// <summary>Hidden top-level window that hears "TaskbarCreated" (sent when Explorer restarts).</summary>
internal sealed class ShellWatcher : NativeWindow, IDisposable
{
    readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    readonly Action onRestart;
    public ShellWatcher(Action onRestart) { this.onRestart = onRestart; CreateHandle(new CreateParams { Caption = "RoninHostShellWatcher" }); }
    protected override void WndProc(ref Message m)
    {
        if ((uint)m.Msg == taskbarCreated) onRestart();
        base.WndProc(ref m);
    }
    public void Dispose() => DestroyHandle();
}

/// <summary>Charcoal / bone / gold tray menu.</summary>
internal sealed class InkRenderer : ToolStripProfessionalRenderer
{
    public static readonly Color Bg = Color.FromArgb(0x1b, 0x1d, 0x20), Hover = Color.FromArgb(0x2a, 0x26, 0x1c),
        Text = Color.FromArgb(0xe6, 0xe0, 0xd2), Gold = Color.FromArgb(0xc9, 0xa2, 0x4a), Line = Color.FromArgb(0x33, 0x35, 0x3a);
    public InkRenderer() : base(new Colors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected ? Gold : Text;
        base.OnRenderItemText(e);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Gold; base.OnRenderArrow(e); }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // a small gold diamond instead of a tick
        var r = e.ImageRectangle; int s = 4; var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(Gold);
        e.Graphics.FillPolygon(b, new[] { new Point(c.X, c.Y - s), new Point(c.X + s, c.Y), new Point(c.X, c.Y + s), new Point(c.X - s, c.Y) });
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color CheckBackground => Bg;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}

/// <summary>The app icon: a gold diamond on charcoal (ronin.ico, made by tools\make-icon.ps1; drawn here if it's missing).</summary>
internal static class TrayIcons
{
    static Icon? app, tray;
    /// <summary>For windows: every size, so the title bar, taskbar and Alt+Tab each pick a sharp one.</summary>
    public static Icon App => app ??= Load(null) ?? Make(32);
    /// <summary>For the notification area: the small size for this DPI.</summary>
    public static Icon Tray => tray ??= Load(SystemInformation.SmallIconSize) ?? Make(SystemInformation.SmallIconSize.Width);

    static Icon? Load(Size? size)
    {
        using var s = typeof(TrayIcons).Assembly.GetManifestResourceStream("ronin.ico");
        if (s == null) return null;
        return size is { } z ? new Icon(s, z) : new Icon(s);
    }

    static Icon Make(int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float c = size / 2f, r = size * 0.42f;
            var outer = new[] { new PointF(c, c - r), new PointF(c + r, c), new PointF(c, c + r), new PointF(c - r, c) };
            using (var bg = new SolidBrush(Color.FromArgb(0x16, 0x17, 0x1a))) g.FillPolygon(bg, outer);
            using (var pen = new Pen(InkRenderer.Gold, size / 14f)) g.DrawPolygon(pen, outer);
            float r2 = r * 0.42f;
            var inner = new[] { new PointF(c, c - r2), new PointF(c + r2, c), new PointF(c, c + r2), new PointF(c - r2, c) };
            using var gold = new SolidBrush(InkRenderer.Gold);
            g.FillPolygon(gold, inner);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
