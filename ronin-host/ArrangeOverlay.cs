using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace RoninHost;

/// <summary>What the overlay asks of the app while arranging.</summary>
internal interface IArrangeHost
{
    /// <summary>A drag, resize or nudge is about to change something (remembered for Undo).</summary>
    void BeginChange();
    /// <summary>The user dragged <paramref name="box"/> so its top-left is at <paramref name="topLeft"/> (monitor pixels).
    /// Returns where the box really goes (after clamping / snapping to the screen's edges and centre).</summary>
    RectangleF Drag(ArrangeOverlay o, LayoutBox box, PointF topLeft, bool snapX, bool snapY, bool final);
    /// <summary>A corner that started at <paramref name="corner"/> is now at <paramref name="mouse"/>. Returns the new box.</summary>
    RectangleF Resize(ArrangeOverlay o, LayoutBox box, PointF corner, PointF mouse, bool final);
    /// <summary>Which side edges of an element can be dragged to change its width (the quote's wrap width).</summary>
    (bool left, bool right) WidthHandles(ArrangeOverlay o, string id);
    /// <summary>A side edge that started at <paramref name="edgeX"/> is now at <paramref name="mouseX"/>. Returns the new box.</summary>
    RectangleF ResizeWidth(ArrangeOverlay o, LayoutBox box, float edgeX, bool rightEdge, float mouseX, bool final);
    /// <summary>Dropped with the cursor on another monitor (desktop coordinates). False = not a wallpaper monitor.</summary>
    bool MoveToMonitor(ArrangeOverlay from, LayoutBox box, Point screenTopLeft, Point cursor);
    /// <summary>Right-click menu entries for an element: (label, action id).</summary>
    IEnumerable<(string label, string action)> MenuFor(ArrangeOverlay o, string id);
    void Act(ArrangeOverlay o, string id, string action);
    /// <summary>Elements hidden on this monitor that could be shown: (element id, label).</summary>
    IEnumerable<(string id, string label)> Hidden(ArrangeOverlay o);
    void Undo();
    void CancelArrange();
    void EndArrange();
}

/// <summary>
/// Arrange mode on one monitor: a lightly dimmed, click-catching layer over the screen with a box around each
/// movable wallpaper element. Drag a box to move it (gold guides show when it lines up with another element or
/// the screen's thirds), drag a corner to resize it, drop it on another monitor to move it there.
/// Drawn with per-pixel alpha (UpdateLayeredWindow) so the wallpaper stays visible through it.
/// </summary>
internal sealed class ArrangeOverlay : Form
{
    public Screen Screen { get; }
    readonly IArrangeHost host;
    LayoutBox[] boxes = Array.Empty<LayoutBox>();
    (string id, string label)[] hidden = Array.Empty<(string, string)>();
    bool canUndo;

    // drag / resize state
    enum Mode { None, Move, Resize, Width }
    Mode mode;
    int dragIndex = -1, hoverIndex = -1, selected = -1, hoverCorner = -1;
    Point dragStart;
    RectangleF dragOrigin;
    RectangleF? dragShown;
    PointF resizeCorner;
    float edgeX; bool edgeRight;
    int hoverEdge = -1; // 0 left, 1 right
    readonly List<float> guidesX = new(), guidesY = new();
    long lastNudge;
    bool begun; // this drag has already been recorded for Undo

    // toolbar hit areas (client pixels)
    readonly List<(Rectangle r, string action)> buttons = new();
    readonly List<(Rectangle r, string id)> chipRects = new();
    string? hoverChip, hoverButton;

    static readonly Color Gold = InkRenderer.Gold, Bone = InkRenderer.Text, Charcoal = InkRenderer.Bg;

    public ArrangeOverlay(Screen screen, IArrangeHost host)
    {
        Screen = screen;
        this.host = host;
        Text = "Ronin — Arrange";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        Bounds = screen.Bounds;
        KeyPreview = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW);
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e) { }          // drawn through UpdateLayeredWindow
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = Screen.Bounds; // in case a DPI change while moving here rescaled it
        Redraw();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Bounds = Screen.Bounds;
        Redraw();
    }

    float K => DeviceDpi / 96f;

    public void SetBoxes(LayoutBox[] b)
    {
        if (mode != Mode.None) return; // the drag shows its own position until it ends
        boxes = b;
        if (selected >= boxes.Length) selected = -1;
        Redraw();
    }

    public void SetHidden(IEnumerable<(string id, string label)> h) { hidden = h.ToArray(); Redraw(); }
    public void SetCanUndo(bool v) { if (v != canUndo) { canUndo = v; Redraw(); } }

    // ---------------- hit testing ----------------

    RectangleF Pad(RectangleF r) { r.Inflate(10, 10); return r; }

    static PointF[] Corners(RectangleF r) => new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom), new PointF(r.Right, r.Bottom) };

    int BoxAt(Point p)
    {
        for (int i = boxes.Length - 1; i >= 0; i--) if (Pad(boxes[i].Bounds).Contains(p)) return i;
        return -1;
    }

    /// <summary>(box, corner) under the point, for resizing.</summary>
    (int box, int corner) CornerAt(Point p)
    {
        float reach = 9 * K;
        for (int i = boxes.Length - 1; i >= 0; i--)
        {
            var cs = Corners(Pad(boxes[i].Bounds));
            for (int c = 0; c < 4; c++)
                if (Math.Abs(p.X - cs[c].X) <= reach && Math.Abs(p.Y - cs[c].Y) <= reach) return (i, c);
        }
        return (-1, -1);
    }

    /// <summary>(box, edge) under the point for changing the width: edge 0 = left, 1 = right (only where allowed).</summary>
    (int box, int edge) EdgeAt(Point p)
    {
        float reach = 8 * K;
        for (int i = boxes.Length - 1; i >= 0; i--)
        {
            var (l, r) = host.WidthHandles(this, boxes[i].Id);
            if (!l && !r) continue;
            var pr = Pad(boxes[i].Bounds);
            float cy = pr.Top + pr.Height / 2;
            if (Math.Abs(p.Y - cy) > Math.Max(14 * K, pr.Height / 4)) continue;
            if (l && Math.Abs(p.X - pr.Left) <= reach) return (i, 0);
            if (r && Math.Abs(p.X - pr.Right) <= reach) return (i, 1);
        }
        return (-1, -1);
    }

    // ---------------- input ----------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            foreach (var (r, action) in buttons)
                if (r.Contains(e.Location))
                {
                    if (action == "done") host.EndArrange();
                    else if (action == "cancel") host.CancelArrange();
                    else if (action == "undo") host.Undo();
                    return;
                }
            foreach (var (r, id) in chipRects)
                if (r.Contains(e.Location)) { host.Act(this, id, "show"); return; }

            var (cb, cc) = CornerAt(e.Location);
            if (cb >= 0)
            {
                selected = dragIndex = cb;
                mode = Mode.Resize;
                resizeCorner = Corners(Pad(boxes[cb].Bounds))[cc];
                dragOrigin = boxes[cb].Bounds;
                dragShown = dragOrigin;
                Capture = true;
                begun = false;
                Redraw();
                return;
            }
            var (eb, ee) = EdgeAt(e.Location);
            if (eb >= 0)
            {
                selected = dragIndex = eb;
                mode = Mode.Width;
                edgeRight = ee == 1;
                var pr = Pad(boxes[eb].Bounds);
                edgeX = edgeRight ? pr.Right : pr.Left;
                dragOrigin = boxes[eb].Bounds;
                dragShown = dragOrigin;
                Capture = true;
                begun = false;
                Redraw();
                return;
            }
            int i = BoxAt(e.Location);
            selected = i;
            if (i >= 0)
            {
                dragIndex = i;
                mode = Mode.Move;
                dragStart = e.Location;
                dragOrigin = boxes[i].Bounds;
                dragShown = dragOrigin;
                Capture = true;
                begun = false;
            }
            Redraw();
        }
        else if (e.Button == MouseButtons.Right)
        {
            int i = BoxAt(e.Location);
            if (i < 0) return;
            selected = i;
            Redraw();
            ShowMenu(boxes[i].Id, e.Location);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (mode != Mode.None && !begun) { begun = true; host.BeginChange(); } // the first real movement is one undo step
        if (mode == Mode.Move)
        {
            var tl = new PointF(dragOrigin.X + e.X - dragStart.X, dragOrigin.Y + e.Y - dragStart.Y);
            bool snap = (ModifierKeys & Keys.Shift) == 0;
            var (aligned, sx, sy) = snap ? Align(tl, dragOrigin.Size) : (tl, false, false);
            dragShown = host.Drag(this, boxes[dragIndex] with { Bounds = dragOrigin }, aligned, snap && !sx, snap && !sy, final: false);
            Redraw();
            return;
        }
        if (mode == Mode.Width)
        {
            dragShown = host.ResizeWidth(this, boxes[dragIndex] with { Bounds = dragOrigin }, edgeX, edgeRight, e.X, final: false);
            Redraw();
            return;
        }
        if (mode == Mode.Resize)
        {
            dragShown = host.Resize(this, boxes[dragIndex] with { Bounds = dragOrigin }, resizeCorner, e.Location, final: false);
            Redraw();
            return;
        }
        var (hb, hc) = CornerAt(e.Location);
        var (heb, he) = hb >= 0 ? (-1, -1) : EdgeAt(e.Location);
        int h = hb >= 0 ? hb : heb >= 0 ? heb : BoxAt(e.Location);
        string? chip = chipRects.FirstOrDefault(c => c.r.Contains(e.Location)).id;
        string? button = buttons.FirstOrDefault(b => b.r.Contains(e.Location)).action;
        Cursor = hc is 0 or 3 ? Cursors.SizeNWSE : hc is 1 or 2 ? Cursors.SizeNESW : he >= 0 ? Cursors.SizeWE
            : h >= 0 ? Cursors.SizeAll : chip != null || button != null ? Cursors.Hand : Cursors.Default;
        if (h != hoverIndex || hc != hoverCorner || he != hoverEdge || chip != hoverChip || button != hoverButton)
        {
            hoverIndex = h; hoverCorner = hc; hoverEdge = he; hoverChip = chip; hoverButton = button;
            Redraw();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (mode == Mode.None || e.Button != MouseButtons.Left) return;
        if (!begun) { EndDrag(); return; } // a click without moving changes nothing
        var box = boxes[dragIndex];
        if (mode == Mode.Move)
        {
            var tl = new PointF(dragOrigin.X + e.X - dragStart.X, dragOrigin.Y + e.Y - dragStart.Y);
            var cursor = PointToScreen(e.Location);
            // dropped on another monitor? (the element moves there)
            if (!Screen.Bounds.Contains(cursor) &&
                host.MoveToMonitor(this, box with { Bounds = dragOrigin }, new Point(Screen.Bounds.X + (int)tl.X, Screen.Bounds.Y + (int)tl.Y), cursor))
            {
                EndDrag();
                return;
            }
            bool snap = (ModifierKeys & Keys.Shift) == 0;
            var (aligned, sx, sy) = snap ? Align(tl, dragOrigin.Size) : (tl, false, false);
            boxes[dragIndex] = box with { Bounds = host.Drag(this, box with { Bounds = dragOrigin }, aligned, snap && !sx, snap && !sy, final: true) };
        }
        else if (mode == Mode.Width)
            boxes[dragIndex] = box with { Bounds = host.ResizeWidth(this, box with { Bounds = dragOrigin }, edgeX, edgeRight, e.X, final: true) };
        else
            boxes[dragIndex] = box with { Bounds = host.Resize(this, box with { Bounds = dragOrigin }, resizeCorner, e.Location, final: true) };
        EndDrag();
    }

    void EndDrag()
    {
        mode = Mode.None;
        dragIndex = -1;
        dragShown = null;
        guidesX.Clear(); guidesY.Clear();
        Capture = false;
        Redraw();
    }

    /// <summary>
    /// Alignment: if an edge or the centre of the dragged box is within a few pixels of another element's edge or
    /// centre, or of the screen's thirds / centre, snap to it and show a guide. Returns the adjusted top-left and
    /// whether each axis snapped (then the app's own 0 / 50 / 100 snapping stays out of it).
    /// </summary>
    (PointF tl, bool x, bool y) Align(PointF tl, SizeF size)
    {
        guidesX.Clear(); guidesY.Clear();
        float reach = 8 * K;
        var W = Screen.Bounds.Width; var H = Screen.Bounds.Height;
        var tx = new List<float> { W / 3f, W / 2f, W * 2 / 3f };
        var ty = new List<float> { H / 3f, H / 2f, H * 2 / 3f };
        for (int i = 0; i < boxes.Length; i++)
        {
            if (i == dragIndex) continue;
            var b = boxes[i].Bounds;
            tx.AddRange(new[] { b.Left, b.Left + b.Width / 2, b.Right });
            ty.AddRange(new[] { b.Top, b.Top + b.Height / 2, b.Bottom });
        }
        (float offset, float guide)? Best(float start, float len, List<float> targets)
        {
            (float, float)? best = null; float bestD = reach;
            foreach (var edge in new[] { start, start + len / 2, start + len })
                foreach (var t in targets)
                {
                    float d = Math.Abs(edge - t);
                    if (d < bestD) { bestD = d; best = (t - edge, t); }
                }
            return best;
        }
        bool sx = false, sy = false;
        if (Best(tl.X, size.Width, tx) is { } bx) { tl.X += bx.offset; guidesX.Add(bx.guide); sx = true; }
        if (Best(tl.Y, size.Height, ty) is { } by) { tl.Y += by.offset; guidesY.Add(by.guide); sy = true; }
        return (tl, sx, sy);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) { host.EndArrange(); return; }
        if (e.KeyCode == Keys.Enter) { host.EndArrange(); return; }
        if (e.Control && e.KeyCode == Keys.Z) { host.Undo(); e.Handled = true; return; }
        if (selected < 0 || selected >= boxes.Length || mode != Mode.None) return;
        // arrow keys nudge the selected element (Shift: bigger steps); a burst of presses is one undo step
        int step = e.Shift ? 20 : 3;
        var (dx, dy) = e.KeyCode switch { Keys.Left => (-step, 0), Keys.Right => (step, 0), Keys.Up => (0, -step), Keys.Down => (0, step), _ => (0, 0) };
        if (dx == 0 && dy == 0) return;
        if (Environment.TickCount64 - lastNudge > 1000) host.BeginChange();
        lastNudge = Environment.TickCount64;
        var b = boxes[selected];
        var r = host.Drag(this, b, new PointF(b.Bounds.X + dx, b.Bounds.Y + dy), false, false, final: true);
        boxes[selected] = b with { Bounds = r };
        e.Handled = true;
        Redraw();
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    void ShowMenu(string id, Point at)
    {
        var menu = new ContextMenuStrip { Renderer = new InkRenderer(), ShowImageMargin = false };
        foreach (var (label, action) in host.MenuFor(this, id))
        {
            if (action == "-") { menu.Items.Add(new ToolStripSeparator()); continue; }
            var a = action;
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => host.Act(this, id, a)) { ForeColor = InkRenderer.Text });
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, at);
    }

    // ---------------- drawing ----------------

    static string Label(string id) => id switch
    {
        "clock" => "CLOCK", "quote" => "QUOTE", "todo" => "TASKS", "calendar" => "AGENDA", "system" => "SYSTEM",
        "timer" => "TIMER", "album" => "NOW PLAYING", "world" => "WORLD TIME", _ => id.ToUpperInvariant(),
    };

    Bitmap? canvas;
    IntPtr dib, memDC;

    void EnsureCanvas(int w, int h)
    {
        if (canvas != null && canvas.Width == w && canvas.Height == h) return;
        FreeCanvas();
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var screenDC = GetDC(IntPtr.Zero);
        memDC = CreateCompatibleDC(screenDC);
        ReleaseDC(IntPtr.Zero, screenDC);
        dib = CreateDIBSection(memDC, ref bi, 0, out var bits, IntPtr.Zero, 0);
        SelectObject(memDC, dib);
        // GDI+ draws straight into the DIB's memory (premultiplied BGRA, what UpdateLayeredWindow wants)
        canvas = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
    }

    void FreeCanvas()
    {
        canvas?.Dispose(); canvas = null;
        if (memDC != IntPtr.Zero) { DeleteDC(memDC); memDC = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
    }

    void Redraw()
    {
        if (!IsHandleCreated || IsDisposed) return;
        var sb = Screen.Bounds;
        int w = sb.Width, h = sb.Height;
        EnsureCanvas(w, h);
        float k = K;

        using (var g = Graphics.FromImage(canvas!))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.FromArgb(64, 6, 6, 8)); // a light dim: it says "arranging" and catches clicks everywhere
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // alignment guides
            using (var gp = new Pen(Color.FromArgb(200, Gold), 1) { DashPattern = new[] { 6f, 4f } })
            {
                foreach (var x in guidesX) g.DrawLine(gp, x, 0, x, h);
                foreach (var y in guidesY) g.DrawLine(gp, 0, y, w, y);
            }

            using var labelFont = new Font("Segoe UI Semibold", 9f * k, FontStyle.Regular, GraphicsUnit.Point);
            for (int i = 0; i < boxes.Length; i++)
            {
                var r = Pad(i == dragIndex && dragShown is { } ds ? ds : boxes[i].Bounds);
                bool hot = i == dragIndex || i == hoverIndex, sel = i == selected;
                using (var fill = new SolidBrush(Color.FromArgb(hot ? 46 : 22, Gold))) g.FillRectangle(fill, r);
                using (var pen = new Pen(Color.FromArgb(hot || sel ? 255 : 170, Gold), (hot || sel ? 2f : 1.2f) * k))
                {
                    if (!hot && !sel) pen.DashPattern = new[] { 4f, 3f };
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                }
                // corner handles: drag one to resize
                var cs = Corners(r);
                for (int c = 0; c < 4; c++)
                {
                    bool hc = i == hoverIndex && c == hoverCorner;
                    Diamond(g, cs[c], (hc ? 7f : 4.5f) * k, Gold, filled: hc);
                }
                // side handles: drag to change the width (the quote's wrap width)
                var (wl, wr) = host.WidthHandles(this, boxes[i].Id);
                for (int side = 0; side < 2; side++)
                {
                    if (side == 0 ? !wl : !wr) continue;
                    bool he = i == hoverIndex && side == hoverEdge;
                    float ex = side == 0 ? r.Left : r.Right, ey = r.Top + r.Height / 2, hh = (he ? 14 : 10) * k;
                    using var hb = new SolidBrush(he ? Gold : Color.FromArgb(240, Charcoal));
                    using var hp = new Pen(Gold, Math.Max(1, 1.4f * k));
                    var hr = new RectangleF(ex - 2.5f * k, ey - hh, 5 * k, hh * 2);
                    g.FillRectangle(hb, hr);
                    g.DrawRectangle(hp, hr.X, hr.Y, hr.Width, hr.Height);
                }

                // tag above the box (below it when the box hugs the top edge)
                var text = Label(boxes[i].Id);
                if (i == dragIndex && mode is Mode.Resize or Mode.Width && dragShown is { } rs && dragOrigin.Width > 0)
                    text += $"  ·  {rs.Width / dragOrigin.Width * 100:0}%";
                var ts = g.MeasureString(text, labelFont);
                float tx = r.X, ty = r.Y - ts.Height - 6 * k;
                if (ty < 4) ty = r.Bottom + 6 * k;
                var tag = new RectangleF(tx, ty, ts.Width + 12 * k, ts.Height + 2 * k);
                using (var bg = new SolidBrush(Color.FromArgb(220, Charcoal))) g.FillRectangle(bg, tag);
                using (var tb = new SolidBrush(Gold)) g.DrawString(text, labelFont, tb, tag.X + 6 * k, tag.Y + 1 * k);
            }

            DrawToolbar(g, w, k);
        }

        var dst = new Native.POINT { X = sb.X, Y = sb.Y };
        var size = new SIZE { cx = w, cy = h };
        var src = new Native.POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_ALPHA
        var screenDC = GetDC(IntPtr.Zero);
        UpdateLayeredWindow(Handle, screenDC, ref dst, ref size, memDC, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        ReleaseDC(IntPtr.Zero, screenDC);
    }

    void DrawToolbar(Graphics g, int w, float k)
    {
        chipRects.Clear();
        buttons.Clear();
        using var title = new Font("Segoe UI Semibold", 10f * k, FontStyle.Regular, GraphicsUnit.Point);
        using var body = new Font("Segoe UI", 9.5f * k, FontStyle.Regular, GraphicsUnit.Point);
        using var bone = new SolidBrush(Bone);
        using var dim = new SolidBrush(Color.FromArgb(170, Bone));
        using var gold = new SolidBrush(Gold);

        const string head = "ARRANGE";
        const string hint = "drag to move  ·  corners resize  ·  side bars set the quote's width  ·  drop on another screen to move it  ·  Shift: no snap";
        float pad = 14 * k, gap = 16 * k;
        var hs = g.MeasureString(head, title);
        var ns = g.MeasureString(hint, body);
        var chips = hidden.Select(hd => (hd.id, text: "+ " + hd.label, size: g.MeasureString("+ " + hd.label, body))).ToList();
        var btns = new List<(string action, string text, bool enabled)> { ("undo", "UNDO", canUndo), ("cancel", "CANCEL", true), ("done", "DONE", true) };
        var bsz = btns.Select(b => g.MeasureString(b.text, title)).ToList();
        float chipW = chips.Sum(c => c.size.Width + 16 * k + 8 * k);
        float btnW = bsz.Sum(s => s.Width + 24 * k) + 8 * k * (btns.Count - 1);
        float barH = Math.Max(hs.Height, ns.Height) + 2 * pad;
        float barW = pad + 14 * k + hs.Width + gap + ns.Width + gap + btnW + pad;
        float x = (w - barW) / 2, y = 18 * k;
        var bar = new RectangleF(x, y, barW, barH);

        using (var bg = new SolidBrush(Color.FromArgb(235, Charcoal))) g.FillRectangle(bg, bar);
        using (var pen = new Pen(Color.FromArgb(120, Gold), 1)) g.DrawRectangle(pen, bar.X, bar.Y, bar.Width, bar.Height);

        float cx = x + pad, cy = y + barH / 2;
        Diamond(g, new PointF(cx + 4 * k, cy), 4f * k, Gold, filled: true);
        cx += 14 * k;
        g.DrawString(head, title, gold, cx, cy - hs.Height / 2); cx += hs.Width + gap;
        g.DrawString(hint, body, dim, cx, cy - ns.Height / 2); cx += ns.Width + gap;

        for (int i = 0; i < btns.Count; i++)
        {
            var (action, text, enabled) = btns[i];
            var sz = bsz[i];
            var r = new RectangleF(cx, cy - sz.Height / 2 - 4 * k, sz.Width + 24 * k, sz.Height + 8 * k);
            bool hot = enabled && hoverButton == action;
            bool primary = action == "done";
            using (var fb = new SolidBrush(hot ? Gold : Color.FromArgb(primary ? 40 : 14, Gold))) g.FillRectangle(fb, r);
            using (var pen = new Pen(Color.FromArgb(enabled ? 255 : 70, Gold), 1)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            using (var tb = new SolidBrush(hot ? Charcoal : Color.FromArgb(enabled ? 255 : 90, Gold))) g.DrawString(text, title, tb, r.X + 12 * k, r.Y + 4 * k);
            if (enabled) buttons.Add((Rectangle.Round(r), action));
            cx += r.Width + 8 * k;
        }

        // hidden elements: a second row of chips under the bar, so the bar never runs off narrow screens
        if (chips.Count > 0)
        {
            float rowW = chipW - 8 * k;
            float rx = (w - rowW) / 2, ry = bar.Bottom + 8 * k;
            foreach (var (id, text, size) in chips)
            {
                var r = new RectangleF(rx, ry, size.Width + 16 * k, size.Height + 6 * k);
                bool hot = hoverChip == id;
                using (var cb = new SolidBrush(Color.FromArgb(hot ? 120 : 220, hot ? Gold : Charcoal))) g.FillRectangle(cb, r);
                using (var pen = new Pen(Color.FromArgb(hot ? 255 : 140, Gold), 1)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                g.DrawString(text, body, bone, r.X + 8 * k, r.Y + 3 * k);
                chipRects.Add((Rectangle.Round(r), id));
                rx += r.Width + 8 * k;
            }
        }
    }

    static void Diamond(Graphics g, PointF c, float r, Color color, bool filled = false)
    {
        var pts = new[] { new PointF(c.X, c.Y - r), new PointF(c.X + r, c.Y), new PointF(c.X, c.Y + r), new PointF(c.X - r, c.Y) };
        using var b = new SolidBrush(filled ? color : Color.FromArgb(240, Charcoal));
        g.FillPolygon(b, pts);
        using var p = new Pen(color, Math.Max(1, r / 3));
        g.DrawPolygon(p, pts);
    }

    protected override void Dispose(bool disposing)
    {
        FreeCanvas();
        base.Dispose(disposing);
    }

    // ---------------- Win32 ----------------
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Native.POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref Native.POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
