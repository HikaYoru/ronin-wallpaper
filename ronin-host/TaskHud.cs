using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace RoninHost;

/// <summary>
/// The task HUD: a sticky note with a board's tasks that pops up over everything on a keyboard shortcut
/// (Settings → To-do → Task HUD). Click a diamond to cycle its status, a title to open the task, a header to
/// fold it; "+ Add task" adds one. Drag it anywhere (the spot is remembered); the shortcut again or Esc hides it,
/// ← / → switch boards, the wheel scrolls a long list. Its look is the same <see cref="TaskStyle"/> as the box behind
/// the desktop task list. Drawn with per-pixel alpha (UpdateLayeredWindow) so its opacity is real.
/// </summary>
internal sealed class TaskHud : Form
{
    readonly TaskStore store;
    readonly Func<JsonObject> settings;
    /// <summary>"task:verb:board:item", the same requests the desktop task list sends.</summary>
    public event Action<string>? Request;
    /// <summary>The note was dragged to a new place (top-left, desktop pixels).</summary>
    public event Action<Point>? Moved;
    /// <summary>The shortcut, shown in the footer.</summary>
    public string Shortcut = "";
    public string? BoardId { get; private set; }

    readonly List<(RectangleF r, string act)> hits = new();
    string? hoverAct, hoverRow;
    float scroll, maxScroll;
    RectangleF listArea;
    bool dragging, dragMoved;
    Point dragFrom, winFrom;

    // fade in / out
    readonly System.Windows.Forms.Timer fade = new() { Interval = 15 };
    float alpha; int fadeDir;

    // layout, in logical px (× K)
    const float Wd = 380, Pad = 22, HeadH = 30, RuleH = 22, SecH = 34, TaskH = 34, EmptyH = 44, FootH = 44;

    public TaskHud(TaskStore store, Func<JsonObject> settings)
    {
        this.store = store;
        this.settings = settings;
        Text = "Ronin — Tasks";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        KeyPreview = true;
        fade.Tick += (_, _) => StepFade();
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

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Redraw();
    }

    float K => DeviceDpi / 96f;

    /// <summary>Shown and not fading out.</summary>
    public bool Showing => Visible && fadeDir >= 0;

    /// <summary>Show the note: <paramref name="board"/> (or the first board), at <paramref name="saved"/> if that is still on a screen,
    /// otherwise near the top middle of the monitor under the cursor.</summary>
    public void ShowHud(string? board, Point? saved)
    {
        BoardId = store.Board(board) != null ? board : store.Data.Boards.FirstOrDefault()?.Id;
        scroll = 0;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        if (!IsHandleCreated) CreateHandle();
        var size = MeasureSize(wa);
        Point at;
        if (saved is { } p && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(p, new Size(Math.Min(size.Width, 80), 40)))))
            at = p;
        else
            at = new Point(wa.Left + (wa.Width - size.Width) / 2, wa.Top + wa.Height / 6);
        Bounds = new Rectangle(at, size);
        alpha = 0; fadeDir = 1;
        Redraw();
        if (!Visible) Show();
        Activate();
        fade.Start();
    }

    public void HideHud()
    {
        if (!Visible) return;
        fadeDir = -1;
        fade.Start();
    }

    void StepFade()
    {
        alpha = Math.Clamp(alpha + fadeDir * (fadeDir > 0 ? .14f : .2f), 0, 1);
        if (fadeDir > 0 && alpha >= 1) { fadeDir = 0; fade.Stop(); }
        if (fadeDir < 0 && alpha <= 0) { fade.Stop(); fadeDir = 0; dragging = false; Hide(); return; }
        Present();
    }

    /// <summary>The tasks (or settings) changed: draw again if shown.</summary>
    public void RefreshHud(bool resize = true)
    {
        if (!Visible) return;
        if (store.Board(BoardId) == null) BoardId = store.Data.Boards.FirstOrDefault()?.Id;
        if (resize)
        {
            var size = MeasureSize(Screen.FromControl(this).WorkingArea);
            if (size != Size) Size = size;
        }
        Redraw();
    }

    // ---------------- what is shown ----------------

    sealed record Line(bool Header, TaskItem Item, int Done, int Total);

    /// <summary>Headers and tasks in order, following "Finished tasks" (todoDone) and folded sections.</summary>
    static List<Line> Lines(TaskBoard? b, int doneMode)
    {
        var lines = new List<Line>();
        if (b == null) return lines;
        var sections = new List<(TaskItem? h, List<TaskItem> t)> { (null, new()) };
        foreach (var i in b.Items) { if (i.IsHeader) sections.Add((i, new())); else sections[^1].t.Add(i); }
        foreach (var (h, all) in sections)
        {
            if (h != null)
            {
                lines.Add(new Line(true, h, all.Count(t => t.Status == 2), all.Count));
                if (h.Collapsed) continue;
            }
            var tasks = all.Where(t => !(doneMode == 2 && t.Status == 2 && t.Finished is { } f && DateTime.Now - f > TimeSpan.FromDays(1))).ToList();
            if (doneMode == 1) tasks = tasks.Where(t => t.Status != 2).Concat(tasks.Where(t => t.Status == 2)).ToList();
            lines.AddRange(tasks.Select(t => new Line(false, t, 0, 0)));
        }
        return lines;
    }

    static float LinesH(List<Line> lines) => lines.Count == 0 ? EmptyH : lines.Sum(l => l.Header ? SecH : TaskH) + 6;

    int DoneMode => (int)Num(settings()["todoDone"]);

    Size MeasureSize(Rectangle wa)
    {
        float k = K;
        float list = LinesH(Lines(store.Board(BoardId), DoneMode));
        float chrome = Pad + HeadH + RuleH + FootH + Pad * .5f;
        float maxList = Math.Max(TaskH * 3, wa.Height / k * .72f - chrome);
        float h = chrome + Math.Min(list, maxList);
        return new Size((int)MathF.Ceiling(Wd * k), (int)MathF.Ceiling(h * k));
    }

    // ---------------- input ----------------

    static bool IsRowAct(string act) => act.StartsWith("cycle:") || act.StartsWith("open:") || act.StartsWith("toggle:");

    string? ActAt(Point p)
    {
        foreach (var (r, act) in hits)
        {
            if (!r.Contains(p) || act.StartsWith("row:")) continue; // "row:" is only the hover highlight
            if (IsRowAct(act) && !listArea.Contains(p)) continue;   // list rows count only in the visible (scrolled) part
            return act;
        }
        return null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var act = ActAt(e.Location);
        if (act != null) { Do(act); return; }
        // anywhere else: drag the note
        dragging = true; dragMoved = false;
        dragFrom = Cursor.Position; winFrom = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging)
        {
            var c = Cursor.Position;
            if (!dragMoved && Math.Abs(c.X - dragFrom.X) + Math.Abs(c.Y - dragFrom.Y) < 3) return;
            dragMoved = true;
            Location = new Point(winFrom.X + c.X - dragFrom.X, winFrom.Y + c.Y - dragFrom.Y);
            Present();
            return;
        }
        var act = ActAt(e.Location);
        string? row = listArea.Contains(e.Location) ? hits.FirstOrDefault(h => h.act.StartsWith("row:") && h.r.Contains(e.Location)).act : null;
        Cursor = act != null ? Cursors.Hand : Cursors.SizeAll;
        if (act != hoverAct || row != hoverRow) { hoverAct = act; hoverRow = row; Redraw(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!dragging) return;
        dragging = false;
        Capture = false;
        if (dragMoved) Moved?.Invoke(Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverAct != null || hoverRow != null) { hoverAct = hoverRow = null; Redraw(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (maxScroll <= 0) return;
        scroll = Math.Clamp(scroll - e.Delta / 120f * TaskH * 1.5f, 0, maxScroll);
        Redraw();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Escape: HideHud(); break;
            case Keys.Left: Do("prev"); break;
            case Keys.Right or Keys.Tab: Do("next"); break;
            case Keys.Up: scroll = Math.Clamp(scroll - TaskH, 0, Math.Max(0, maxScroll)); Redraw(); break;
            case Keys.Down: scroll = Math.Clamp(scroll + TaskH, 0, Math.Max(0, maxScroll)); Redraw(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Tab || base.IsInputKey(keyData);

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RefreshHud();
    }

    void Do(string act)
    {
        var boards = store.Data.Boards;
        int bi = boards.FindIndex(b => b.Id == BoardId);
        switch (act)
        {
            case "close": HideHud(); return;
            case "prev" or "next":
                if (boards.Count < 2) return;
                BoardId = boards[(Math.Max(0, bi) + (act == "next" ? 1 : boards.Count - 1)) % boards.Count].Id;
                scroll = 0;
                RefreshHud();
                return;
            case "add":
                if (BoardId != null) Request?.Invoke($"task:add:{BoardId}:");
                return;
        }
        var p = act.Split(':', 2);
        if (p.Length == 2 && BoardId != null && IsRowAct(act)) Request?.Invoke($"task:{p[0]}:{BoardId}:{p[1]}");
    }

    // ---------------- drawing ----------------

    static double Num(JsonNode? n) => n is JsonValue v ? (v.TryGetValue(out double d) ? d : v.TryGetValue(out int i) ? i : v.TryGetValue(out bool b) ? (b ? 1 : 0) : 0) : 0;
    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : "";
    static Color ColorOf(JsonNode? n, Color fallback)
    {
        try { var s = Str(n).Trim(); return s.Length > 0 ? ColorTranslator.FromHtml(s.StartsWith('#') ? s : "#" + s) : fallback; }
        catch { return fallback; }
    }

    /// <summary>Letter-spaced capitals, like the wallpaper's small headings.</summary>
    static string Spaced(string s) => string.Join(" ", s.ToUpper(CultureInfo.CurrentCulture).EnumerateRunes().Select(r => r.ToString()));

    void Redraw()
    {
        if (!IsHandleCreated || IsDisposed) return;
        int w = Width, h = Height;
        if (w <= 0 || h <= 0) return;
        canvas.Ensure(w, h);
        float k = K;
        var eff = settings();
        Color gold = ColorOf(eff["accent"], InkRenderer.Gold), bone = ColorOf(eff["textColor"], InkRenderer.Text);
        Color ink = ColorOf(eff["todoBgColor"], Color.FromArgb(0x0c, 0x0d, 0x10));
        float opacity = Math.Clamp((float)Num(eff["taskHudOpacity"]) / 100, .05f, 1);
        float radius = Math.Clamp((float)Num(eff["todoBgRound"]), 0, 24) * k;
        var style = TaskStyle.At(Num(eff["todoBgStyle"]));
        var board = store.Board(BoardId);
        var lines = Lines(board, DoneMode);
        hits.Clear();

        using var g = Graphics.FromImage(canvas.Bitmap!);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // ----- the card -----
        DrawCard(g, new RectangleF(.5f, .5f, w - 1, h - 1), style, ink, opacity, radius, gold, k);

        using var fTitle = new Font(HudFonts.Display, 13 * k, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fSmall = new Font(HudFonts.Display, 10 * k, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fSec = new Font(HudFonts.Display, 11 * k, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fTask = new Font(HudFonts.Serif, 19 * k, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fSym = new Font("Segoe UI Symbol", 12 * k, FontStyle.Regular, GraphicsUnit.Pixel);
        var typo = StringFormat.GenericTypographic;
        using var trim = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
        using var right = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var mid = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var center = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        SolidBrush B(Color c, float a = 1) => new(Color.FromArgb((int)Math.Clamp(a * 255, 0, 255), c));

        float x0 = Pad * k, x1 = w - Pad * k, y = Pad * k * .85f;

        // ----- header: ◆ BOARD NAME            ‹ 1 / 3 ›   ✕ -----
        float cy = y + HeadH * k / 2;
        Diamond(g, new PointF(x0 + 4 * k, cy), 4.2f * k, gold, true);
        float bx;
        {
            var r = new RectangleF(x1 - 18 * k, cy - 12 * k, 22 * k, 24 * k);
            using (var b = B(hoverAct == "close" ? gold : bone, hoverAct == "close" ? 1 : .55f)) g.DrawString("✕", fSym, b, r, center);
            hits.Add((r, "close"));
            bx = r.X - 8 * k;
        }
        int nb = store.Data.Boards.Count;
        if (nb > 1)
        {
            int bi = Math.Max(0, store.Data.Boards.FindIndex(b => b.Id == BoardId));
            string count = $"{bi + 1} / {nb}";
            float cw = g.MeasureString(count, fSmall, PointF.Empty, typo).Width;
            var rn = new RectangleF(bx - 16 * k, cy - 12 * k, 18 * k, 24 * k);
            var rc = new RectangleF(rn.X - cw - 6 * k, cy - 12 * k, cw + 2 * k, 24 * k);
            var rp = new RectangleF(rc.X - 22 * k, cy - 12 * k, 18 * k, 24 * k);
            using (var b = B(gold, hoverAct == "next" ? 1 : .6f)) g.DrawString("›", fTitle, b, rn, center);
            using (var b = B(bone, .5f)) g.DrawString(count, fSmall, b, rc, mid);
            using (var b = B(gold, hoverAct == "prev" ? 1 : .6f)) g.DrawString("‹", fTitle, b, rp, center);
            hits.Add((rn, "next")); hits.Add((rp, "prev"));
            bx = rp.X - 8 * k;
        }
        using (var b = B(bone, .95f))
            g.DrawString(Spaced(board?.Name is { Length: > 0 } n ? n : "Tasks"), fTitle, b, new RectangleF(x0 + 16 * k, cy - 14 * k, Math.Max(10, bx - x0 - 16 * k), 28 * k), trim);
        y += HeadH * k;

        // ----- the rule: gold line, small diamond, gold line -----
        Rule(g, x0, x1, y + RuleH * k / 2, gold, k);
        y += RuleH * k;

        // ----- the list (scrolls) -----
        float footTop = h - (FootH + Pad * .35f) * k;
        listArea = new RectangleF(0, y, w, footTop - y);
        float full = LinesH(lines) * k;
        maxScroll = Math.Max(0, (full - listArea.Height) / k);
        scroll = Math.Clamp(scroll, 0, maxScroll);
        var clip = g.Clip;
        g.SetClip(listArea);
        float ly = y - scroll * k;
        if (lines.Count == 0)
            using (var b = B(bone, .5f)) g.DrawString(board == null ? "No boards yet" : "No tasks yet", fTask, b, new RectangleF(x0, ly, x1 - x0, EmptyH * k), mid);
        foreach (var line in lines)
        {
            var it = line.Item;
            float rh = (line.Header ? SecH : TaskH) * k, rcy = ly + rh / 2;
            var rowRect = new RectangleF(x0 - 10 * k, ly, x1 - x0 + 20 * k, rh);
            hits.Add((rowRect, "row:" + it.Id));
            if (hoverRow == "row:" + it.Id)
                using (var hb = B(gold, .07f)) g.FillRectangle(hb, rowRect.X, rowRect.Y + 2 * k, rowRect.Width, rowRect.Height - 4 * k);

            if (line.Header)
            {
                bool hot = hoverAct == "toggle:" + it.Id;
                using (var b = B(gold, hot ? 1 : .8f)) g.DrawString(it.Collapsed ? "▸" : "▾", fSym, b, new RectangleF(x0 - 2 * k, rcy - 10 * k, 14 * k, 20 * k), mid);
                string cnt = line.Total > 0 ? $"{line.Done} / {line.Total}" : "";
                float cntW = cnt.Length > 0 ? g.MeasureString(cnt, fSmall, PointF.Empty, typo).Width : 0;
                using (var b = B(gold, hot ? 1 : .92f)) g.DrawString(Spaced(it.Title), fSec, b, new RectangleF(x0 + 16 * k, rcy - 12 * k, Math.Max(10, x1 - x0 - 16 * k - cntW - 12 * k), 24 * k), trim);
                if (cnt.Length > 0)
                    using (var b = B(line.Done == line.Total ? gold : bone, line.Done == line.Total ? .85f : .5f)) g.DrawString(cnt, fSmall, b, new RectangleF(x0, rcy - 12 * k, x1 - x0, 24 * k), right);
                using (var lp = new Pen(Color.FromArgb(56, gold), 1)) g.DrawLine(lp, x0, ly + rh - 4 * k, x1, ly + rh - 4 * k);
                hits.Add((rowRect, "toggle:" + it.Id));
            }
            else
            {
                bool done = it.Status == 2, wip = it.Status == 1;
                float o = done ? .42f : 1;
                bool hotDia = hoverAct == "cycle:" + it.Id, hotTitle = hoverAct == "open:" + it.Id;
                var dc = new PointF(x0 + 4 * k, rcy);
                Diamond(g, dc, (hotDia ? 6.2f : 5.4f) * k, Color.FromArgb((int)(255 * (hotDia ? 1 : o)), gold), done);
                if (wip) Diamond(g, dc, 2.4f * k, gold, true);
                hits.Add((new RectangleF(x0 - 8 * k, ly, 26 * k, rh), "cycle:" + it.Id));

                // marks on the right: WIP, notes, sub-task progress
                float mx = x1;
                void Mark(string s, Font f, Color c, float a)
                {
                    float mw = g.MeasureString(s, f, PointF.Empty, typo).Width;
                    using var b = B(c, a * (done ? .6f : 1));
                    g.DrawString(s, f, b, new RectangleF(mx - mw, rcy - 12 * k, mw + 2 * k, 24 * k), mid);
                    mx -= mw + 9 * k;
                }
                if (it.Subtasks.Count > 0) { int sd = it.Subtasks.Count(s => s.Done); Mark($"{sd}/{it.Subtasks.Count}", fSmall, sd == it.Subtasks.Count ? gold : bone, sd == it.Subtasks.Count ? .9f : .55f); }
                if (it.Notes.Trim().Length > 0) Mark("≡", fSym, bone, .6f);
                if (wip) Mark("WIP", fSmall, gold, 1);

                float tx = x0 + 22 * k;
                var tr = new RectangleF(tx, ly, Math.Max(10, mx - tx), rh);
                using (var b = B(hotTitle ? gold : bone, hotTitle ? 1 : o)) g.DrawString(it.Title, fTask, b, tr, trim);
                if (done)
                {
                    float tw = Math.Min(tr.Width, g.MeasureString(it.Title, fTask, PointF.Empty, typo).Width);
                    using var sp = new Pen(Color.FromArgb(200, gold), 1);
                    g.DrawLine(sp, tx, rcy + 1 * k, tx + tw, rcy + 1 * k);
                }
                hits.Add((tr, "open:" + it.Id));
            }
            ly += rh;
        }
        g.Clip = clip;

        // a thin scroll bar when the list is longer than the note
        if (maxScroll > 0)
        {
            float trackH = listArea.Height - 8 * k, barH = Math.Max(24 * k, trackH * listArea.Height / full);
            float barY = listArea.Y + 4 * k + (trackH - barH) * (scroll / maxScroll);
            using var sb = B(gold, .45f);
            g.FillRectangle(sb, w - 7 * k, barY, 2 * k, barH);
        }

        // ----- footer: + ADD TASK               CTRL+ALT+T · ESC -----
        using (var lp = new Pen(Color.FromArgb(40, gold), 1)) g.DrawLine(lp, x0, footTop + 4 * k, x1, footTop + 4 * k);
        float fcy = footTop + FootH * k / 2 + 2 * k;
        if (board != null)
        {
            string add = "+  " + Spaced("Add task");
            float aw = g.MeasureString(add, fSmall, PointF.Empty, typo).Width;
            var ra = new RectangleF(x0, fcy - 12 * k, aw + 8 * k, 24 * k);
            using (var b = B(gold, hoverAct == "add" ? 1 : .7f)) g.DrawString(add, fSmall, b, ra, mid);
            hits.Add((ra, "add"));
        }
        string hint = Spaced((Shortcut.Length > 0 ? Shortcut + "  ·  " : "") + "Esc" + (nb > 1 ? "  ·  ← →" : ""));
        using (var b = B(bone, .38f)) g.DrawString(hint, fSmall, b, new RectangleF(x0, fcy - 12 * k, x1 - x0, 24 * k), right);

        Present();
    }

    /// <summary>The note's card in a <see cref="TaskStyle"/>: ink (optionally shaded), gold edge, top rule, corner marks.</summary>
    static void DrawCard(Graphics g, RectangleF r, TaskStyle st, Color ink, float opacity, float radius, Color gold, float k)
    {
        using var path = Rounded(r, radius);
        int top = (int)(255 * opacity * (1 - st.Shade)), bottom = (int)(255 * opacity);
        using (var fill = new LinearGradientBrush(new PointF(0, r.Top - 1), new PointF(0, r.Bottom + 1), Color.FromArgb(top, ink), Color.FromArgb(bottom, ink)))
            g.FillPath(fill, path);
        // the HUD always keeps a faint edge so it reads as a note, even in the plain style
        float edge = Math.Max(st.EdgeAlpha, .08f);
        using (var pen = new Pen(Color.FromArgb((int)(255 * edge), gold), 1)) g.DrawPath(pen, path);
        if (st.TopRule)
        {
            var tr = new RectangleF(r.Left + radius, r.Top, Math.Max(1, r.Width - 2 * radius), 1.2f);
            using var lb = new LinearGradientBrush(new PointF(tr.Left - 1, 0), new PointF(tr.Right + 1, 0), Color.Transparent, Color.Transparent);
            lb.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0, gold), Color.FromArgb(220, gold), Color.FromArgb(0, gold) }, Positions = new[] { 0f, .5f, 1f } };
            g.FillRectangle(lb, tr);
        }
        if (st.CornerMarks > 0)
        {
            float l = st.CornerMarks * k * 1.2f;
            using var pen = new Pen(Color.FromArgb(220, gold), Math.Max(1, 1.2f * k));
            foreach (var (cx, cy, dx, dy) in new[] { (r.Left, r.Top, 1, 1), (r.Right, r.Top, -1, 1), (r.Left, r.Bottom, 1, -1), (r.Right, r.Bottom, -1, -1) })
            {
                float x = cx + dx * 3 * k, y = cy + dy * 3 * k;
                g.DrawLine(pen, x, y, x + dx * l, y);
                g.DrawLine(pen, x, y, x, y + dy * l);
            }
        }
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d < 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void Rule(Graphics g, float x0, float x1, float y, Color gold, float k)
    {
        float cx = (x0 + x1) / 2, gap = 12 * k;
        using (var l = new LinearGradientBrush(new PointF(x0 - 1, 0), new PointF(cx - gap + 1, 0), Color.FromArgb(0, gold), Color.FromArgb(200, gold)))
            g.FillRectangle(l, x0, y, cx - gap - x0, 1);
        using (var r = new LinearGradientBrush(new PointF(cx + gap - 1, 0), new PointF(x1 + 1, 0), Color.FromArgb(200, gold), Color.FromArgb(0, gold)))
            g.FillRectangle(r, cx + gap, y, x1 - cx - gap, 1);
        Diamond(g, new PointF(cx, y + .5f), 4f * k, gold, false);
    }

    static void Diamond(Graphics g, PointF c, float r, Color color, bool filled)
    {
        var pts = new[] { new PointF(c.X, c.Y - r), new PointF(c.X + r, c.Y), new PointF(c.X, c.Y + r), new PointF(c.X - r, c.Y) };
        if (filled) using (var b = new SolidBrush(color)) g.FillPolygon(b, pts);
        using var p = new Pen(color, 1);
        g.DrawPolygon(p, pts);
    }

    // ---------------- per-pixel alpha ----------------

    readonly LayeredCanvas canvas = new();

    void Present()
    {
        if (!IsHandleCreated || IsDisposed || canvas.Bitmap == null) return;
        canvas.Present(Handle, Location, (byte)Math.Clamp(alpha * 255, 0, 255));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { fade.Dispose(); canvas.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>A 32-bit premultiplied bitmap that GDI+ draws into and UpdateLayeredWindow shows with per-pixel alpha.</summary>
internal sealed class LayeredCanvas : IDisposable
{
    public Bitmap? Bitmap { get; private set; }
    IntPtr dib, memDC;

    public void Ensure(int w, int h)
    {
        if (Bitmap != null && Bitmap.Width == w && Bitmap.Height == h) return;
        Dispose();
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var screenDC = GetDC(IntPtr.Zero);
        memDC = CreateCompatibleDC(screenDC);
        ReleaseDC(IntPtr.Zero, screenDC);
        dib = CreateDIBSection(memDC, ref bi, 0, out var bits, IntPtr.Zero, 0);
        SelectObject(memDC, dib);
        Bitmap = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
    }

    public void Present(IntPtr hwnd, Point at, byte alpha)
    {
        if (Bitmap == null) return;
        var dst = new Native.POINT { X = at.X, Y = at.Y };
        var size = new SIZE { cx = Bitmap.Width, cy = Bitmap.Height };
        var src = new Native.POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 }; // AC_SRC_ALPHA
        var screenDC = GetDC(IntPtr.Zero);
        UpdateLayeredWindow(hwnd, screenDC, ref dst, ref size, memDC, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        ReleaseDC(IntPtr.Zero, screenDC);
    }

    public void Dispose()
    {
        Bitmap?.Dispose(); Bitmap = null;
        if (memDC != IntPtr.Zero) { DeleteDC(memDC); memDC = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
    }

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

/// <summary>The wallpaper's fonts for GDI+ (the HUD): Cinzel for headings, Cormorant Garamond for tasks.</summary>
internal static class HudFonts
{
    static readonly PrivateFontCollection? Pfc;
    public static readonly FontFamily Display, Serif;

    static HudFonts()
    {
        try
        {
            Pfc = new PrivateFontCollection();
            var dir = Path.Combine(AppContext.BaseDirectory, "fonts");
            foreach (var f in new[] { "Cinzel-Variable.ttf", "CormorantGaramond-Variable.ttf" })
                if (File.Exists(Path.Combine(dir, f))) Pfc.AddFontFile(Path.Combine(dir, f));
        }
        catch (Exception e) { Log.Write("hud fonts failed: " + e.Message); }
        FontFamily Pick(string name, string fallback) =>
            Pfc?.Families.FirstOrDefault(f => f.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) ?? new FontFamily(fallback);
        Display = Pick("Cinzel", "Georgia");
        Serif = Pick("Cormorant", "Georgia");
    }
}

/// <summary>A system-wide keyboard shortcut (RegisterHotKey) heard by a hidden window.</summary>
internal sealed class HotKey : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312, Id = 0x5254;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    bool registered;
    public event Action? Pressed;

    public HotKey() => CreateHandle(new CreateParams { Caption = "RoninHostHotKey" });

    /// <summary>Register <paramref name="text"/> ("Ctrl+Alt+T"), replacing the old one. Returns why it failed, or null.</summary>
    public string? Set(string? text)
    {
        Clear();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!TryParse(text, out var mods, out var key)) return $"\"{text}\" isn't a shortcut Ronin understands (try Ctrl+Alt+T)";
        if (!RegisterHotKey(Handle, Id, mods | MOD_NOREPEAT, (uint)key)) return $"{text} is already used by another app";
        registered = true;
        return null;
    }

    public void Clear()
    {
        if (!registered) return;
        UnregisterHotKey(Handle, Id);
        registered = false;
    }

    /// <summary>"Ctrl+Alt+T", "Win+Shift+Q", "F9"… A key without a modifier is only allowed for F1–F24.</summary>
    public static bool TryParse(string text, out uint mods, out Keys key)
    {
        mods = 0; key = Keys.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win" or "windows" or "super": mods |= MOD_WIN; continue;
            }
            if (key != Keys.None) return false; // two keys
            if (part.Length == 1 && char.IsDigit(part[0])) key = Keys.D0 + (part[0] - '0');
            else if (part is "`" or "~") key = Keys.Oemtilde;
            else if (part.Length == 1 && char.IsAsciiLetter(part[0])) key = (Keys)char.ToUpperInvariant(part[0]);
            else if (!int.TryParse(part, out _) && Enum.TryParse<Keys>(part, true, out var k) && (k & Keys.Modifiers) == 0) key = k;
            else return false;
        }
        return key != Keys.None && (mods != 0 || key is >= Keys.F1 and <= Keys.F24);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == Id) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose() { Clear(); DestroyHandle(); }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
