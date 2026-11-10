using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoninHost;

/// <summary>One sub-task: a checklist line inside a task.</summary>
internal sealed class SubTask
{
    public string Id { get; set; } = TaskStore.NewId();
    public string Title { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>A row on a board: a header (a section title) or a task.</summary>
internal sealed class TaskItem
{
    public string Id { get; set; } = TaskStore.NewId();
    /// <summary>"task" or "header".</summary>
    public string Kind { get; set; } = "task";
    public string Title { get; set; } = "";
    /// <summary>0 not started, 1 in progress, 2 done (tasks only).</summary>
    public int Status { get; set; }
    public string Notes { get; set; } = "";
    public List<SubTask> Subtasks { get; set; } = new();
    /// <summary>Headers: its tasks are folded away on the desktop.</summary>
    public bool Collapsed { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? Finished { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsHeader => Kind == "header";
}

/// <summary>A board (a project or list), shown on the monitors in <see cref="Monitors"/> (stable monitor ids).</summary>
internal sealed class TaskBoard
{
    public string Id { get; set; } = TaskStore.NewId();
    public string Name { get; set; } = "Tasks";
    public List<string> Monitors { get; set; } = new();
    public List<TaskItem> Items { get; set; } = new();
}

internal sealed class TaskData
{
    public int Version { get; set; } = 1;
    public List<TaskBoard> Boards { get; set; } = new();
}

/// <summary>
/// The to-do system: boards of headers and tasks (status, notes, sub-tasks), saved to %APPDATA%\RoninHost\tasks.json.
/// Every change (from the desktop, the task panel or the Tasks window) goes through <see cref="Apply"/>, so they all agree.
/// Used on the UI thread only.
/// </summary>
internal sealed class TaskStore
{
    public static readonly string FilePath = Path.Combine(AppSettings.Dir, "tasks.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public TaskData Data { get; private set; } = new();
    /// <summary>Something changed (save is already queued): show it everywhere.</summary>
    public event Action? Changed;
    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 500 };

    public TaskStore() => saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };

    static int ToInt(JsonNode? n) => n is JsonValue v ? (v.TryGetValue(out int i) ? i : v.TryGetValue(out double x) ? (int)x : 0) : 0;

    public static string NewId() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>Read tasks.json; if there is none yet, <paramref name="migrate"/> builds the first board from the old list.</summary>
    public void Load(Func<TaskData> migrate)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Data = JsonSerializer.Deserialize<TaskData>(File.ReadAllText(FilePath), Json) ?? new();
                return;
            }
        }
        catch (Exception e)
        {
            Log.Write("tasks.json unreadable, keeping a copy: " + e.Message);
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
        }
        Data = migrate();
        Save();
        Log.Write($"tasks: created tasks.json ({Data.Boards.Sum(b => b.Items.Count)} items)");
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e) { Log.Write("tasks save failed: " + e.Message); }
    }

    public void Flush() { if (saveTimer.Enabled) { saveTimer.Stop(); Save(); } }

    void Touch() { saveTimer.Stop(); saveTimer.Start(); Changed?.Invoke(); }

    public TaskBoard? Board(string? id) => Data.Boards.FirstOrDefault(b => b.Id == id);
    /// <summary>The board shown on this monitor (each monitor shows at most one).</summary>
    public TaskBoard? BoardFor(string monitorId) => Data.Boards.FirstOrDefault(b => b.Monitors.Contains(monitorId));

    static void SetStatus(TaskItem t, int status)
    {
        t.Status = Math.Clamp(status, 0, 2);
        t.Finished = t.Status == 2 ? t.Finished ?? DateTime.Now : null;
    }

    /// <summary>Indexes of a header's tasks (up to the next header).</summary>
    static IEnumerable<int> SectionOf(TaskBoard b, int headerIndex)
    {
        for (int i = headerIndex + 1; i < b.Items.Count && !b.Items[i].IsHeader; i++) yield return i;
    }

    // ---------------- changes ----------------

    /// <summary>
    /// One change, as the Tasks window and the task panel send them ({"op": "item.update", "board": …, …}).
    /// Returns the new item / board / sub-task id for "add" operations, "" otherwise, null if nothing changed.
    /// </summary>
    public string? Apply(JsonObject m)
    {
        string S(string k) => m[k]?.GetValue<string>() ?? "";
        var op = S("op");
        var b = Board(S("board"));
        var item = b?.Items.FirstOrDefault(i => i.Id == S("item"));
        string? result = "";
        switch (op)
        {
            case "board.add":
            {
                var nb = new TaskBoard { Name = S("name").Length > 0 ? S("name") : "New board" };
                Data.Boards.Add(nb);
                result = nb.Id;
                break;
            }
            case "board.rename" when b != null: b.Name = S("name").Trim().Length > 0 ? S("name").Trim() : b.Name; break;
            case "board.delete" when b != null: Data.Boards.Remove(b); break;
            case "board.move" when b != null:
            {
                int to = Math.Clamp(ToInt(m["to"]), 0, Data.Boards.Count - 1);
                Data.Boards.Remove(b); Data.Boards.Insert(to, b);
                break;
            }
            case "board.monitors" when b != null:
            {
                var mons = (m["monitors"] as JsonArray)?.Select(x => x!.GetValue<string>()).Distinct().ToList() ?? new();
                // a monitor shows one board: take these monitors away from the others
                foreach (var other in Data.Boards) if (other != b) other.Monitors.RemoveAll(mons.Contains);
                b.Monitors = mons;
                break;
            }
            case "board.clearDone" when b != null:
                if (b.Items.RemoveAll(i => !i.IsHeader && i.Status == 2) == 0) return null;
                break;
            case "item.add" when b != null:
            {
                var ni = new TaskItem { Kind = S("kind") == "header" ? "header" : "task", Title = S("title").Trim() };
                if (ni.Title.Length == 0) ni.Title = ni.IsHeader ? "New section" : "New task";
                // where: after a given item, at the end of a header's section, or at the end
                int at = b.Items.Count;
                if (b.Items.FindIndex(i => i.Id == S("after")) is var ai and >= 0) at = ai + 1;
                else if (b.Items.FindIndex(i => i.Id == S("section") && i.IsHeader) is var hi and >= 0) at = SectionOf(b, hi).DefaultIfEmpty(hi).Max() + 1;
                else if (S("at") == "start") at = 0;
                b.Items.Insert(at, ni);
                result = ni.Id;
                break;
            }
            case "item.update" when item != null:
            {
                if (m["title"] is JsonValue t) { var s = t.GetValue<string>().Trim(); if (s.Length > 0) item.Title = s; }
                if (m["notes"] is JsonValue n) item.Notes = n.GetValue<string>();
                if (m["status"] is JsonValue st) SetStatus(item, ToInt(st));
                if (m["collapsed"] is JsonValue c) item.Collapsed = c.GetValue<bool>();
                break;
            }
            case "item.cycle" when item != null && !item.IsHeader: SetStatus(item, (item.Status + 1) % 3); break;
            case "item.toggle" when item != null && item.IsHeader: item.Collapsed = !item.Collapsed; break;
            case "item.delete" when item != null: b!.Items.Remove(item); break;
            case "item.move" when item != null:
            {
                int to = Math.Clamp(ToInt(m["to"]), 0, b!.Items.Count - 1);
                b.Items.Remove(item); b.Items.Insert(to, item);
                break;
            }
            case "item.up" when item != null:
            {
                int i = b!.Items.IndexOf(item);
                if (i <= 0) return null;
                (b.Items[i - 1], b.Items[i]) = (b.Items[i], b.Items[i - 1]);
                break;
            }
            case "item.down" when item != null:
            {
                int i = b!.Items.IndexOf(item);
                if (i >= b.Items.Count - 1) return null;
                (b.Items[i + 1], b.Items[i]) = (b.Items[i], b.Items[i + 1]);
                break;
            }
            case "item.toBoard" when item != null && Board(S("target")) is { } target && target != b:
                b!.Items.Remove(item); target.Items.Add(item);
                break;
            case "sub.add" when item != null:
            {
                var ns = new SubTask { Title = S("title").Trim().Length > 0 ? S("title").Trim() : "New sub-task" };
                item.Subtasks.Add(ns);
                result = ns.Id;
                break;
            }
            case "sub.update" when item != null && item.Subtasks.FirstOrDefault(s => s.Id == S("sub")) is { } sub:
                if (m["title"] is JsonValue st2) { var s = st2.GetValue<string>().Trim(); if (s.Length > 0) sub.Title = s; }
                if (m["done"] is JsonValue d) sub.Done = d.GetValue<bool>();
                break;
            case "sub.delete" when item != null: item.Subtasks.RemoveAll(s => s.Id == S("sub")); break;
            case "sub.move" when item != null && item.Subtasks.FirstOrDefault(s => s.Id == S("sub")) is { } sub2:
            {
                int to = Math.Clamp(ToInt(m["to"]), 0, item.Subtasks.Count - 1);
                item.Subtasks.Remove(sub2); item.Subtasks.Insert(to, sub2);
                break;
            }
            default: return null;
        }
        Touch();
        return result;
    }

    /// <summary>Every done task back to not started (the "untick at midnight" option).</summary>
    public int ResetDone()
    {
        int n = 0;
        foreach (var t in Data.Boards.SelectMany(b => b.Items).Where(i => !i.IsHeader && i.Status == 2)) { SetStatus(t, 0); n++; }
        if (n > 0) Touch();
        return n;
    }

    // ---------------- what the windows and the wallpaper see ----------------

    /// <summary>Everything, for the Tasks window and the task panel.</summary>
    public JsonNode ToJson() => JsonSerializer.SerializeToNode(Data, Json)!;

    /// <summary>One board as the wallpaper draws it (notes are only flagged, not sent).</summary>
    public static JsonObject ForWallpaper(TaskBoard b) => new()
    {
        ["id"] = b.Id,
        ["name"] = b.Name,
        ["items"] = new JsonArray(b.Items.Select(i => (JsonNode)new JsonObject
        {
            ["id"] = i.Id,
            ["header"] = i.IsHeader,
            ["title"] = i.Title,
            ["status"] = i.Status,
            ["notes"] = i.Notes.Trim().Length > 0,
            ["subDone"] = i.Subtasks.Count(s => s.Done),
            ["subTotal"] = i.Subtasks.Count,
            ["collapsed"] = i.Collapsed,
            ["finished"] = i.Finished?.ToString("o"),
        }).ToArray()),
    };
}
