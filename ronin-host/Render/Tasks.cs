using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Vortice.DirectWrite;
using RectangleF = System.Drawing.RectangleF;

namespace RoninHost.Render;

/// <summary>
/// The task board on the desktop (the "todo" block): a board's headers and tasks, sent by the host.
/// Clicks: the diamond cycles the status, the title opens the task panel, a header folds its section;
/// hovering a row shows ↑ ↓ ✕ (and + on headers). The host does every change (TaskStore) and sends the board back.
/// </summary>
internal sealed partial class Scene
{
    JsonObject? board;
    /// <summary>The board for this monitor (null = none).</summary>
    public void SetBoard(JsonObject? b) { board = b; hudDirty = true; }

    /// <summary>Rows of the task block (screen CSS px) for hover: item id.</summary>
    readonly List<(RectangleF rect, string id)> rowHits = new();
    /// <summary>The row under the mouse (its controls show), or null.</summary>
    string? rowHover;
    static readonly bool DebugLog = Environment.GetEnvironmentVariable("RONIN_DEBUG") == "1";

    void HoverTasks(Vector2 p)
    {
        string? h = null;
        foreach (var (r, id) in rowHits) if (r.Contains(p.X, p.Y)) { h = id; break; }
        // diagnostics: RONIN_DEBUG_HOVER=<n> shows row n's controls as if the mouse were on it
        if (int.TryParse(Environment.GetEnvironmentVariable("RONIN_DEBUG_HOVER"), out var dh) && dh >= 0 && dh < rowHits.Count) h = rowHits[dh].id;
        if (h != rowHover) { rowHover = h; hudDirty = true; }
    }

    sealed record TaskRow(string Id, bool Header, string Title, int Status, bool Notes, int SubDone, int SubTotal, bool Collapsed, DateTime? Finished);

    // numbers arrive as C# ints from the host, or as parsed JSON
    static int Int(JsonNode? n) => n is JsonValue v ? (v.TryGetValue(out int i) ? i : v.TryGetValue(out double d) ? (int)d : 0) : 0;

    static List<TaskRow> ParseBoard(JsonObject b) =>
        (b["items"] as JsonArray ?? new()).OfType<JsonObject>().Select(o => new TaskRow(
            o["id"]?.GetValue<string>() ?? "", o["header"]?.GetValue<bool>() == true, o["title"]?.GetValue<string>() ?? "",
            Int(o["status"]), o["notes"]?.GetValue<bool>() == true,
            Int(o["subDone"]), Int(o["subTotal"]),
            o["collapsed"]?.GetValue<bool>() == true,
            DateTime.TryParse(o["finished"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var f) ? f : null)).ToList();

    /// <summary>A summary for the RONIN_DEBUG log.</summary>
    public IEnumerable<string> TaskState() => board == null ? Array.Empty<string>()
        : ParseBoard(board).Select(r => r.Header ? $"# {r.Title}" : $"[{" ~x"[r.Status]}] {r.Title}");

    void BuildTasks(string font)
    {
        rowHits.Clear();
        if (!B("showTodo") || board == null) return;
        string boardId = board["id"]?.GetValue<string>() ?? "";
        var all = ParseBoard(board);
        int doneMode = (int)N("todoDone"); // 0 dim in place, 1 at the bottom of their section, 2 hidden a day after
        bool editable = B("todoEdit");
        var culture = CultureInfo.CurrentCulture;

        // sections: [header?, tasks…]; tasks before the first header form a section without one
        var sections = new List<(TaskRow? header, List<TaskRow> tasks)> { (null, new()) };
        foreach (var r in all)
        {
            if (r.Header) sections.Add((r, new()));
            else sections[^1].tasks.Add(r);
        }

        var tOps = new List<Op>();
        var rows = new List<Row>();
        float tx = (float)N("todoX");
        string talign = AlignOf(tx);
        bool right = talign == "right";

        var lt = Text.Layout((board["name"]?.GetValue<string>() is { Length: > 0 } n ? n : "Tasks").ToUpper(culture), font, 14, spacing: 14 * .5f);
        rows.Add(new Row { W = Text.Width(lt), H = Text.Height(lt), Emit = (x, y) => tOps.Add(new TextOp { Layer = LTodo, L = lt, P = new(x, y) }) });
        rows.Add(new Row { W = 60 * 2 + 12 * 2 + 7, H = 7, Mt = 12, Emit = (x, y) => tOps.Add(new RuleOp { Layer = LTodo, P = new(x, y), SpanW = 60 }) });

        // hover controls (space kept on every row so nothing jumps when they appear)
        IDWriteTextLayout G(string s) => Text.Layout(s, "Segoe UI Symbol", 15);
        var taskCtrls = editable ? new[] { (G("↑"), "up"), (G("↓"), "down"), (G("✕"), "delete") } : Array.Empty<(IDWriteTextLayout, string)>();
        var headCtrls = editable ? new[] { (G("+"), "add"), (G("↑"), "up"), (G("↓"), "down"), (G("✕"), "delete") } : Array.Empty<(IDWriteTextLayout, string)>();
        const float ctrlGap = 18, glyphGap = 12;
        float CtrlW((IDWriteTextLayout l, string)[] cs) => cs.Length == 0 ? 0 : cs.Sum(c => Text.Width(c.l)) + glyphGap * (cs.Length - 1);
        float ctrlW = Math.Max(CtrlW(taskCtrls), CtrlW(headCtrls));

        var hits = new List<(RectangleF r, string request, int priority)>(); // block-local; lower priority wins first
        var hoverRows = new List<(float y, float h, string id)>();
        string Req(string verb, string id) => $"task:{verb}:{boardId}:{id}";

        void Controls(float y, float h, float cy, float gx, (IDWriteTextLayout l, string verb)[] cs, string id)
        {
            foreach (var (gl, verb) in cs)
            {
                float gw = Text.Width(gl), gh = Text.Height(gl);
                tOps.Add(new TextOp { Layer = LTodo, L = gl, P = new(gx, cy - gh / 2), Accent = true, Opacity = .9f });
                hits.Add((new RectangleF(gx - glyphGap / 2, y, gw + glyphGap, h), Req(verb, id), 0));
                gx += gw + glyphGap;
            }
        }

        bool firstRow = true;
        int shown = 0;
        foreach (var (header, tasksIn) in sections)
        {
            var tasks = tasksIn.Where(t => !(doneMode == 2 && t.Status == 2 && t.Finished is { } f && DateTime.Now - f > TimeSpan.FromDays(1))).ToList();
            if (doneMode == 1) tasks = tasks.Where(t => t.Status != 2).Concat(tasks.Where(t => t.Status == 2)).ToList();

            if (header != null)
            {
                var h = header;
                var lh = Text.Layout(h.Title.ToUpper(culture), font, 12, spacing: 12 * .4f);
                int doneN = tasksIn.Count(t => t.Status == 2);
                var lc = tasksIn.Count > 0 ? Text.Layout($"{doneN} / {tasksIn.Count}", font, 10, spacing: 10 * .2f) : null;
                var lf = G(h.Collapsed ? "▸" : "▾");
                float wh = Text.Width(lh), wc = lc != null ? Text.Width(lc) : 0, wf = Text.Width(lf), rh = 30;
                float contentW = wf + 10 + wh + (lc != null ? 12 + wc : 0);
                bool hov = editable && rowHover == h.Id;
                var row = new Row { W = contentW + (editable ? ctrlGap + ctrlW : 0), H = rh, Mt = firstRow ? 16 : 14 };
                row.Emit = (x, y) =>
                {
                    float cy = y + rh / 2, full = row.W;
                    // the fold arrow sits in the diamond column (the outer edge)
                    float fx = right ? x + full - wf : x;
                    float hx = right ? fx - 10 - wh : fx + wf + 10;
                    float cx = right ? hx - 12 - wc : hx + wh + 12;
                    tOps.Add(new TextOp { Layer = LTodo, L = lf, P = new(fx, cy - Text.Height(lf) / 2), Accent = true, Opacity = .8f });
                    tOps.Add(new TextOp { Layer = LTodo, L = lh, P = new(hx, cy - Text.Height(lh) / 2), Accent = true, Opacity = .95f });
                    if (lc != null) tOps.Add(new TextOp { Layer = LTodo, L = lc, P = new(cx, cy - Text.Height(lc) / 2 + 1), Opacity = doneN == tasksIn.Count ? .8f : .5f, Accent = doneN == tasksIn.Count });
                    // a thin line under the header, across the row
                    tOps.Add(new FnOp { Layer = LTodo, F = (s, text, accent) =>
                    {
                        using var br = s.dc.CreateSolidColorBrush(new Vortice.Mathematics.Color4(accent.R, accent.G, accent.B, .22f));
                        float lx0 = right ? Math.Min(cx, hx) : fx, lx1 = right ? x + full : (lc != null ? cx + wc : hx + wh);
                        s.dc.DrawLine(new Vector2(lx0, y + rh - 2), new Vector2(lx1, y + rh - 2), br, .8f);
                    } });
                    if (hov) Controls(y, rh, cy, right ? Math.Min(cx, hx) - ctrlGap - ctrlW : (lc != null ? cx + wc : hx + wh) + ctrlGap, headCtrls, h.Id);
                    hits.Add((new RectangleF(x, y, full, rh), Req("toggle", h.Id), 2));
                    hoverRows.Add((y, rh, h.Id));
                };
                rows.Add(row);
                firstRow = false;
                if (h.Collapsed) continue;
            }

            foreach (var t in tasks)
            {
                shown++;
                var task = t;
                const float size = 21, lineH = size * 1.25f, rh = 5 + lineH + 5;
                var li = Text.Layout(task.Title, Text.Cormorant, size, lineHeight: lineH);
                float tw = Text.Width(li);
                bool done = task.Status == 2, wip = task.Status == 1;
                // after the title: WIP tag, notes mark, sub-task progress
                var extras = new List<(IDWriteTextLayout l, bool accent, float opacity)>();
                if (wip) extras.Add((Text.Layout("WIP", font, 9, spacing: 9 * .35f), true, 1));
                if (task.Notes) extras.Add((G("≡"), false, .6f));
                if (task.SubTotal > 0) extras.Add((Text.Layout($"{task.SubDone}/{task.SubTotal}", font, 10, spacing: 10 * .1f), task.SubDone == task.SubTotal, task.SubDone == task.SubTotal ? .9f : .55f));
                float ew = extras.Sum(e => Text.Width(e.l) + 9);
                bool hov = editable && rowHover == task.Id;
                var row = new Row { W = 8 + 14 + tw + ew + (editable ? ctrlGap + ctrlW : 0), H = rh, Mt = firstRow ? 14 : 0 };
                firstRow = false;
                row.Emit = (x, y) =>
                {
                    float full = row.W, cy = y + 5 + lineH / 2;
                    float boxX = right ? x + full - 8 : x;
                    float textX = right ? boxX - 14 - tw : x + 22;
                    float o = done ? .4f : 1;
                    tOps.Add(new DiamondOp { Layer = LTodo, C = new(boxX + 4, cy), R = 5.4f, Filled = done, Opacity = o });
                    if (wip) tOps.Add(new DiamondOp { Layer = LTodo, C = new(boxX + 4, cy), R = 2.4f, Filled = true, Opacity = 1 });
                    float bl = y + 5 + Text.Baseline(li);
                    tOps.Add(new TextOp { Layer = LTodo, L = li, P = new(textX, y + 5), Opacity = o, Strike = done ? (textX, textX + tw, bl - size * .26f) : null });
                    // extras go on the far side of the title from the diamond
                    float ex = right ? textX - 9 : textX + tw + 9;
                    foreach (var (el, acc, eo) in extras)
                    {
                        float w = Text.Width(el);
                        if (right) ex -= w;
                        tOps.Add(new TextOp { Layer = LTodo, L = el, P = new(ex, cy - Text.Height(el) / 2 + 1), Accent = acc, Opacity = eo * (done ? .6f : 1) });
                        ex = right ? ex - 9 : ex + w + 9;
                    }
                    float textL = right ? ex + 9 : textX, textR = right ? textX + tw : ex - 9;
                    if (hov) Controls(y, rh, cy, right ? textL - ctrlGap - ctrlW : textR + ctrlGap, taskCtrls, task.Id);
                    // the diamond (with room around it) cycles the status; the title and its marks open the task
                    hits.Add((new RectangleF(boxX - 9, y, 26, rh), Req("cycle", task.Id), 1));
                    hits.Add((new RectangleF(textL - 4, y, textR - textL + 8, rh), Req("open", task.Id), 1));
                    hoverRows.Add((y, rh, task.Id));
                };
                rows.Add(row);
            }
        }

        if (all.Count == 0) rows.Add(Line(tOps, "No tasks yet", Text.Cormorant, 18, 0, .55f, mt: 14));
        else if (shown == 0 && sections.All(s => s.header == null)) rows.Add(Line(tOps, "All done", Text.Cormorant, 18, 0, .55f, mt: 14));

        // "+ ADD TASK  ·  CLEAR DONE"
        if (editable)
        {
            var la = Text.Layout("+  ADD TASK", font, 10, spacing: 10 * .35f);
            bool anyDone = all.Any(t => !t.Header && t.Status == 2);
            var lc = anyDone ? Text.Layout("CLEAR DONE", font, 10, spacing: 10 * .35f) : null;
            var ld = anyDone ? Text.Layout("·", font, 10) : null;
            float wa = Text.Width(la), wc = lc != null ? Text.Width(lc) : 0, gap = 14, rh = Text.Height(la);
            rows.Add(new Row
            {
                W = wa + (lc != null ? gap * 2 + Text.Width(ld!) + wc : 0), H = rh + 6, Mt = 12, Emit = (x, y) =>
                {
                    tOps.Add(new TextOp { Layer = LTodo, L = la, P = new(x, y + 3), Opacity = .6f, Accent = true });
                    hits.Add((new RectangleF(x - 6, y - 4, wa + 12, rh + 14), Req("add", ""), 0));
                    if (lc != null)
                    {
                        float cx = x + wa + gap;
                        tOps.Add(new TextOp { Layer = LTodo, L = ld!, P = new(cx, y + 3), Opacity = .4f });
                        cx += Text.Width(ld!) + gap;
                        tOps.Add(new TextOp { Layer = LTodo, L = lc, P = new(cx, y + 3), Opacity = .6f });
                        hits.Add((new RectangleF(cx - 6, y - 4, wc + 12, rh + 14), Req("clear", ""), 0));
                    }
                },
            });
        }

        // "A box behind each line": note where every line lands (not the gold rule under the title)
        var lineRects = new List<RectangleF>();
        if (BackdropMode == 2)
            foreach (var r in rows.Where((_, i) => i != 1))
            {
                var row = r; var emit = r.Emit;
                r.Emit = (x, y) => { lineRects.Add(LineRect(x, y, row.W, row.H)); emit?.Invoke(x, y); };
            }

        var tb = Stack(rows, tx, (float)N("todoY"), (float)N("todoScale") / 100);
        tb.Id = "todo";
        if (BackdropMode == 1) AddBackdrop(tb.Ops, new[] { PanelRect(tb.W, tb.H) });
        else if (BackdropMode == 2) AddBackdrop(tb.Ops, lineRects);
        tb.Ops.AddRange(tOps);
        Place(tb);
        blocks.Add(tb);
        RectangleF ToScreen(RectangleF r) => new(tb.TopLeft.X + r.X * tb.Scale, tb.TopLeft.Y + r.Y * tb.Scale, r.Width * tb.Scale, r.Height * tb.Scale);
        foreach (var (r, req, _) in hits.OrderBy(h => h.priority))
        {
            var rq = req;
            widgetHits.Add((ToScreen(r), () => Request?.Invoke(rq)));
        }
        foreach (var (y, h, id) in hoverRows)
            rowHits.Add((ToScreen(new RectangleF(0, y, tb.W, h)), id));
    }
}
