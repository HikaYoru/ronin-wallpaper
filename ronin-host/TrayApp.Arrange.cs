using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RoninHost;

/// <summary>Arrange mode: dragging, resizing and hiding the wallpaper's elements, live, on every monitor, with undo.</summary>
internal sealed partial class TrayApp
{
    readonly List<ArrangeOverlay> overlays = new();
    readonly Stopwatch dragClock = Stopwatch.StartNew();
    long lastDragSend;

    bool Arranging => overlays.Count > 0;

    /// <summary>Movable elements: their "show" key, label, position keys (last two are x / y) and size key.</summary>
    static readonly Dictionary<string, (string show, string label, string[] keys, string scale)> Elements = new()
    {
        ["clock"] = ("showClock", "Clock", new[] { "position", "clockX", "clockY" }, "uiScale"),
        ["quote"] = ("showQuote", "Quote", new[] { "quoteLayout", "quoteX", "quoteY" }, "quoteScale"),
        ["todo"] = ("showTodo", "Tasks", new[] { "todoX", "todoY" }, "todoScale"),
        ["calendar"] = ("showCalendar", "Agenda", new[] { "calX", "calY" }, "calScale"),
        ["system"] = ("showSystem", "System", new[] { "sysX", "sysY" }, "sysScale"),
        ["timer"] = ("showTimer", "Timer", new[] { "timerX", "timerY" }, "timerScale"),
        ["album"] = ("showAlbum", "Now playing", new[] { "albumX", "albumY" }, "albumScale"),
        ["world"] = ("showWorld", "World time", new[] { "worldX", "worldY" }, "worldScale"),
    };

    /// <summary>Every key Arrange can change (also what a saved layout keeps).</summary>
    static readonly HashSet<string> ArrangeKeys = Elements.Values.SelectMany(e => e.keys.Append(e.show).Append(e.scale)).Append("quoteWidth").ToHashSet();

    async void StartArrange()
    {
        if (Arranging || slots.Count == 0 || schema == null) return;
        Log.Write("arrange: start");
        undo.Clear();
        arrangeStart = Snapshot();
        foreach (var s in slots)
        {
            var o = new ArrangeOverlay(s.View.Screen, this);
            overlays.Add(o);
            o.Show();
        }
        // the monitor under the cursor gets the keyboard (Esc, arrow keys, Ctrl+Z)
        if (Native.GetCursorPos(out var c))
            overlays.FirstOrDefault(o => o.Screen.Bounds.Contains(c.X, c.Y))?.Activate();
        await ApplyPauseAsync(); // wake wallpapers that sleep behind a maximized app, so they can be arranged
        RefreshOverlays();
    }

    public void EndArrange()
    {
        if (!Arranging) return;
        Log.Write("arrange: done");
        var list = overlays.ToList();
        overlays.Clear();
        foreach (var o in list) { o.Close(); o.Dispose(); }
        undo.Clear();
        arrangeStart = null;
        if (saveTimer.Enabled) { saveTimer.Stop(); settings.Save(); }
        settingsForm?.SendInit(); // its sliders show the new shared positions
        _ = ApplyPauseAsync();
    }

    /// <summary>Put everything back the way it was when Arrange opened, and close it.</summary>
    public void CancelArrange()
    {
        if (arrangeStart is { } start) { Log.Write("arrange: cancelled"); Restore(start); }
        EndArrange();
    }

    async void RefreshOverlays()
    {
        foreach (var o in overlays.ToList())
        {
            var s = SlotFor(o.Screen);
            if (s == null) continue;
            var boxes = await s.View.GetLayoutAsync();
            if (o.IsDisposed) continue;
            o.SetBoxes(boxes);
            o.SetHidden(Hidden(o));
            o.SetCanUndo(undo.Count > 0);
        }
    }

    // ---------------- undo ----------------

    sealed record UndoState(JsonObject Shared, Dictionary<string, JsonObject> PerMonitor);
    readonly List<UndoState> undo = new();
    UndoState? arrangeStart;

    UndoState Snapshot() => new((JsonObject)settings.Properties.DeepClone(),
        settings.MonitorProperties.ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value.DeepClone()));

    /// <summary>Called before each change (a drag, a resize, a menu action): remembers the state for Undo.</summary>
    public void BeginChange()
    {
        undo.Add(Snapshot());
        if (undo.Count > 60) undo.RemoveAt(0);
        foreach (var o in overlays) o.SetCanUndo(true);
    }

    public bool CanUndo => undo.Count > 0;

    public void Undo()
    {
        if (undo.Count == 0) return;
        var s = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        Log.Write($"arrange: undo ({undo.Count} left)");
        Restore(s);
        _ = RefreshAfterAction();
    }

    /// <summary>Put saved settings back and send each monitor only what changed for it.</summary>
    void Restore(UndoState s)
    {
        var before = slots.ToDictionary(sl => sl, sl => EffectiveFor(sl.Device));
        settings.Properties = (JsonObject)s.Shared.DeepClone();
        settings.MonitorProperties = s.PerMonitor.ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value.DeepClone());
        foreach (var sl in slots) SendDiff(sl, before[sl]);
        QueueSave();
    }

    void SendDiff(Slot slot, JsonObject before)
    {
        var after = EffectiveFor(slot.Device);
        var diff = new JsonObject();
        foreach (var (k, v) in after)
            if (!JsonNode.DeepEquals(v, before[k])) diff[k] = v?.DeepClone();
        if (diff.Count > 0) slot.View.SendProperties(diff);
    }

    // ---------------- writing ----------------

    /// <summary>
    /// With one monitor, arranging changes the shared settings (so the settings window shows it);
    /// with several, each monitor keeps its own positions.
    /// </summary>
    bool PerMonitor => slots.Count > 1;

    void WriteLayout(Slot slot, JsonObject changes, bool send)
    {
        if (PerMonitor)
        {
            if (!settings.MonitorProperties.TryGetValue(slot.Device, out var ov))
                settings.MonitorProperties[slot.Device] = ov = new JsonObject();
            foreach (var (k, v) in changes) ov[k] = v?.DeepClone();
        }
        else
        {
            foreach (var (k, v) in changes) settings.Properties[k] = v?.DeepClone();
            if (settings.MonitorProperties.TryGetValue(slot.Device, out var ov))
                foreach (var (k, _) in changes) ov.Remove(k); // an old per-monitor value would hide the shared one
            PruneOverrides();
        }
        if (send) slot.View.SendProperties((JsonObject)changes.DeepClone());
        QueueSave();
    }

    // values are parsed JSON (any number) or C# literals set this session
    static double Num(JsonNode? n, double def = 0)
    {
        if (n is not JsonValue v) return def;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out float f)) return f;
        return double.TryParse(n.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : def;
    }

    static bool Bool(JsonNode? n) => n is JsonValue v && (v.TryGetValue(out bool b) ? b : Num(n) != 0);

    // place() maps 0..100 to the element's pinned point inside a 5% (x) / 7% (y) margin:
    //   topLeft = size * (margin + v * (1 - 2 * margin) / 100) - boxSize * v / 100
    static float? Solve(float topLeft, float size, float box, float margin)
    {
        float den = size * (1 - 2 * margin) / 100 - box / 100;
        if (den <= 0.001f) return null; // the element is as wide as the screen: it can't move this way
        return Math.Clamp((topLeft - margin * size) / den, 0, 100);
    }

    static float Project(float v, float size, float box, float margin) => size * (margin + v * (1 - 2 * margin) / 100) - box * v / 100;

    static double Snap(double v, bool snap)
    {
        if (snap) foreach (var t in new[] { 0.0, 50, 100 }) if (Math.Abs(v - t) < 1.6) return t;
        return Math.Round(v, 1);
    }

    /// <summary>The element's position (0..100 each way); a clock on a preset gives that preset's point.</summary>
    static (float x, float y) PositionOf(string id, JsonObject eff)
    {
        var el = Elements[id];
        if (id == "clock" && (int)Num(eff["position"]) != 5)
        {
            var presets = new (float x, float y)[] { (100, 0), (0, 0), (100, 100), (0, 100), (50, 50) };
            return presets[Math.Clamp((int)Num(eff["position"]), 0, 4)];
        }
        return ((float)Num(eff[el.keys[^2]]), (float)Num(eff[el.keys[^1]]));
    }

    bool LiveSend(bool final)
    {
        // at most ~30 updates a second while dragging; always the final one
        long nowMs = dragClock.ElapsedMilliseconds;
        bool send = final || nowMs - lastDragSend >= 33;
        if (send) lastDragSend = nowMs;
        return send;
    }

    // ---------------- drag, resize, move to another monitor ----------------

    public RectangleF Drag(ArrangeOverlay o, LayoutBox box, PointF topLeft, bool snapX, bool snapY, bool final)
    {
        var slot = SlotFor(o.Screen);
        if (slot == null || !Elements.TryGetValue(box.Id, out var el)) return box.Bounds;
        var eff = EffectiveFor(slot.Device);
        float W = o.Screen.Bounds.Width, H = o.Screen.Bounds.Height, bw = box.Bounds.Width, bh = box.Bounds.Height;
        var (curX, curY) = PositionOf(box.Id, eff);
        double x = Snap(Solve(topLeft.X, W, bw, .05f) ?? curX, snapX);
        double y = Snap(Solve(topLeft.Y, H, bh, .07f) ?? curY, snapY);

        var changes = new JsonObject { [el.keys[^2]] = x, [el.keys[^1]] = y };
        if (box.Id == "clock") changes["position"] = 5.0; // "Custom (use sliders)"
        WriteLayout(slot, changes, LiveSend(final));
        if (final) _ = RefreshOverlayLater(o);
        return new RectangleF(Project((float)x, W, bw, .05f), Project((float)y, H, bh, .07f), bw, bh);
    }

    /// <summary>
    /// Dragging a corner: the element grows or shrinks around the point it is pinned at (as its size slider does).
    /// <paramref name="corner"/> is where the corner was when the drag began, <paramref name="mouse"/> where it is now.
    /// </summary>
    (string id, ArrangeOverlay overlay, double scale)? resizeBase;

    public RectangleF Resize(ArrangeOverlay o, LayoutBox box, PointF corner, PointF mouse, bool final)
    {
        var slot = SlotFor(o.Screen);
        if (slot == null || !Elements.TryGetValue(box.Id, out var el)) return box.Bounds;
        var eff = EffectiveFor(slot.Device);
        var (px, py) = PositionOf(box.Id, eff);
        var b = box.Bounds;
        var pin = new PointF(b.X + px / 100 * b.Width, b.Y + py / 100 * b.Height);
        double d0 = Math.Max(1, Math.Sqrt(Math.Pow(corner.X - pin.X, 2) + Math.Pow(corner.Y - pin.Y, 2)));
        double d1 = Math.Sqrt(Math.Pow(mouse.X - pin.X, 2) + Math.Pow(mouse.Y - pin.Y, 2));
        // the size when this resize began (live updates have already changed the setting since)
        if (resizeBase is not { } rb || rb.id != box.Id || rb.overlay != o) resizeBase = rb = (box.Id, o, Num(eff[el.scale], 100));
        double oldScale = rb.scale;
        if (final) resizeBase = null;
        var def = schema!.Root[el.scale] as JsonObject;
        double min = Num(def?["min"], 25), max = Num(def?["max"], 300);
        double scale = Math.Clamp(Math.Round(oldScale * d1 / d0), min, max);
        float r = (float)(scale / oldScale);

        WriteLayout(slot, new JsonObject { [el.scale] = scale }, LiveSend(final));
        if (final) _ = RefreshOverlayLater(o);
        return new RectangleF(pin.X - (pin.X - b.X) * r, pin.Y - (pin.Y - b.Y) * r, b.Width * r, b.Height * r);
    }

    /// <summary>The free-standing quote's width can be changed from whichever side isn't the one it is pinned to.</summary>
    public (bool left, bool right) WidthHandles(ArrangeOverlay o, string id)
    {
        if (id != "quote" || SlotFor(o.Screen) is not { } slot) return (false, false);
        var (px, _) = PositionOf("quote", EffectiveFor(slot.Device));
        return (px > 5, px < 95);
    }

    (ArrangeOverlay overlay, double width)? widthBase;

    /// <summary>
    /// Dragging a side edge of the quote: its wrap width ("quoteWidth", in multiples of the font size) grows or shrinks
    /// so that edge follows the mouse, while the point it is pinned at stays put.
    /// </summary>
    public RectangleF ResizeWidth(ArrangeOverlay o, LayoutBox box, float edgeX, bool rightEdge, float mouseX, bool final)
    {
        var slot = SlotFor(o.Screen);
        if (slot == null || box.Id != "quote") return box.Bounds;
        var eff = EffectiveFor(slot.Device);
        var (px, _) = PositionOf("quote", eff);
        if (widthBase is not { } wb || wb.overlay != o) widthBase = wb = (o, Num(eff["quoteWidth"], 30));
        if (final) widthBase = null;
        var b = box.Bounds;
        // growing the box by dW moves this edge by dW * (share of the box on this side of the pin)
        float share = rightEdge ? 1 - px / 100 : px / 100;
        if (share < .05f) return b;
        float delta = rightEdge ? mouseX - edgeX : edgeX - mouseX;
        float newW = Math.Max(20, b.Width + delta / share);
        var def = schema!.Root["quoteWidth"] as JsonObject;
        double width = Math.Clamp(Math.Round(wb.width * newW / b.Width), Num(def?["min"], 10), Num(def?["max"], 60));
        float r = (float)(width / wb.width);
        WriteLayout(slot, new JsonObject { ["quoteWidth"] = width }, LiveSend(final));
        if (final) _ = RefreshOverlayLater(o);
        float pinX = b.X + px / 100 * b.Width;
        return new RectangleF(pinX - px / 100 * b.Width * r, b.Y, b.Width * r, b.Height);
    }

    /// <summary>
    /// An element was dropped on another monitor: it is hidden here and shown there, at the drop point.
    /// Returns false if the point isn't on another wallpaper monitor.
    /// </summary>
    public bool MoveToMonitor(ArrangeOverlay from, LayoutBox box, Point screenTopLeft, Point cursor)
    {
        var src = SlotFor(from.Screen);
        var dst = SlotAt(cursor);
        if (src == null || dst == null || dst == src || !Elements.TryGetValue(box.Id, out var el)) return false;
        var tb = dst.View.Screen.Bounds;
        float bw = box.Bounds.Width, bh = box.Bounds.Height;
        var dstEff = EffectiveFor(dst.Device);
        var (curX, curY) = PositionOf(box.Id, dstEff);
        double x = Snap(Solve(screenTopLeft.X - tb.X, tb.Width, bw, .05f) ?? curX, true);
        double y = Snap(Solve(screenTopLeft.Y - tb.Y, tb.Height, bh, .07f) ?? curY, true);

        var there = new JsonObject { [el.show] = true, [el.keys[^2]] = x, [el.keys[^1]] = y };
        if (box.Id == "clock") there["position"] = 5.0;
        if (box.Id == "quote") there["quoteLayout"] = 1.0;
        // the element keeps its size
        var srcEff = EffectiveFor(src.Device);
        there[el.scale] = Num(srcEff[el.scale], 100);
        WriteLayout(src, new JsonObject { [el.show] = false }, true);
        WriteLayout(dst, there, true);
        Log.Write($"arrange: moved {box.Id} from {src.Device} to {dst.Device}");
        _ = RefreshAfterAction();
        return true;
    }

    async Task RefreshOverlayLater(ArrangeOverlay o)
    {
        var s = SlotFor(o.Screen);
        if (s == null) return;
        var boxes = await s.View.GetLayoutAsync();
        if (!o.IsDisposed && overlays.Contains(o)) o.SetBoxes(boxes);
    }

    // ---------------- right-click menu, hidden elements ----------------

    public IEnumerable<(string label, string action)> MenuFor(ArrangeOverlay o, string id)
    {
        var slot = SlotFor(o.Screen);
        if (slot == null) yield break;
        var eff = EffectiveFor(slot.Device);
        bool attached = (int)Num(eff["quoteLayout"]) != 1;

        if (id == "clock" && attached && Bool(eff["showQuote"])) yield return ("Move the quote on its own", "detach");
        if (id == "quote") yield return ("Put the quote back under the clock", "attach");
        yield return (PerMonitor ? "Hide on this monitor" : "Hide", "hide");
        yield return ("Reset size", "size");
        yield return ("-", "-");
        if (PerMonitor)
        {
            yield return ("Use the shared position", "reset");
            yield return ("Use this monitor's layout on every monitor", "everywhere");
        }
        else yield return ("Reset to the default position", "reset");
    }

    public void Act(ArrangeOverlay o, string id, string action)
    {
        var slot = SlotFor(o.Screen);
        if (slot == null || !Elements.TryGetValue(id, out var el)) return;
        BeginChange();
        var eff = EffectiveFor(slot.Device);
        switch (action)
        {
            case "show": WriteLayout(slot, new JsonObject { [el.show] = true }, true); break;
            case "hide": WriteLayout(slot, new JsonObject { [el.show] = false }, true); break;
            case "attach": WriteLayout(slot, new JsonObject { ["quoteLayout"] = 0.0 }, true); break;
            case "size":
                WriteLayout(slot, new JsonObject { [el.scale] = Num((schema!.Root[el.scale] as JsonObject)?["value"], 100) }, true);
                break;
            case "detach":
            {
                // start it under the clock's column, near the bottom
                var (cx, _) = PositionOf("clock", eff);
                WriteLayout(slot, new JsonObject { ["quoteLayout"] = 1.0, ["quoteX"] = (double)cx, ["quoteY"] = 92.0 }, true);
                break;
            }
            case "reset":
            {
                var keys = el.keys.Where(k => k != "quoteLayout").ToArray();
                var before = EffectiveFor(slot.Device);
                if (PerMonitor)
                {
                    if (settings.MonitorProperties.TryGetValue(slot.Device, out var ov)) foreach (var k in keys) ov.Remove(k);
                    PruneOverrides();
                }
                else foreach (var k in keys) settings.Properties.Remove(k);
                SendDiff(slot, before);
                QueueSave();
                break;
            }
            case "everywhere":
            {
                // this monitor's arrangement becomes the shared one; every monitor's own positions are dropped
                var before = slots.ToDictionary(s => s, s => EffectiveFor(s.Device));
                foreach (var k in ArrangeKeys) settings.Properties[k] = eff[k]?.DeepClone();
                foreach (var ov in settings.MonitorProperties.Values) foreach (var k in ArrangeKeys) ov.Remove(k);
                PruneOverrides();
                foreach (var s in slots) SendDiff(s, before[s]);
                QueueSave();
                break;
            }
        }
        _ = RefreshAfterAction();
    }

    async Task RefreshAfterAction()
    {
        await Task.Delay(60);
        RefreshOverlays();
    }

    public IEnumerable<(string id, string label)> Hidden(ArrangeOverlay o)
    {
        var slot = SlotFor(o.Screen);
        if (slot == null) yield break;
        var eff = EffectiveFor(slot.Device);
        foreach (var (id, (show, label, _, _)) in Elements)
        {
            if (Bool(eff[show])) continue;
            if (id == "todo" && tasks.BoardFor(slot.Device) == null) continue; // no board on this monitor (pick one in the Tasks window)
            yield return (id, label);
        }
    }
}
