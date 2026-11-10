using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RoninHost;

/// <summary>The to-do system on the host side: the task store, its windows, and what the wallpapers ask for.</summary>
internal sealed partial class TrayApp
{
    readonly TaskStore tasks = new();
    TaskWindow? boardWindow, panelWindow;

    void StartTasks()
    {
        tasks.Load(MigrateTasks);
        tasks.Changed += OnTasksChanged;
    }

    /// <summary>The first board, made from the old ";"-separated list and its ticks (once, when tasks.json is created).</summary>
    TaskData MigrateTasks()
    {
        var shared = schema!.Effective(settings.Properties);
        var title = shared["todoTitle"]?.GetValue<string>() is { Length: > 0 } t ? t : "Tasks";
        var board = new TaskBoard { Name = title };
        var text = shared["todoText"]?.GetValue<string>() ?? "";
        foreach (var item in Regex.Split(text, @"\s*[;|\n]\s*").Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            bool done = settings.TodoDone.Contains(item);
            board.Items.Add(new TaskItem { Title = item, Status = done ? 2 : 0, Finished = done ? DateTime.Now : null });
        }
        // shown where the old list was shown
        foreach (var s in TargetScreens())
            if (Bool(EffectiveFor(Id(s))["showTodo"])) board.Monitors.Add(Id(s));
        return new TaskData { Boards = { board } };
    }

    JsonObject? BoardJsonFor(string device) => tasks.BoardFor(device) is { } b ? TaskStore.ForWallpaper(b) : null;

    void OnTasksChanged()
    {
        foreach (var s in slots) s.View.SendBoard(BoardJsonFor(s.Device));
        taskHud?.RefreshHud();
        var data = new JsonObject { ["t"] = "data", ["data"] = tasks.ToJson(), ["monitors"] = MonitorList() };
        boardWindow?.Post(data);
        panelWindow?.Post((JsonObject)data.DeepClone());
        RefreshOverlays(); // the task block's size may have changed while arranging
    }

    JsonArray MonitorList()
    {
        int n = 1;
        var all = Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
        var chosen = TargetScreens().Select(Id).ToHashSet();
        var list = new JsonArray();
        foreach (var s in all)
        {
            int num = n++;
            if (chosen.Contains(Id(s))) list.Add(new JsonObject { ["id"] = Id(s), ["name"] = $"Monitor {num}{(s.Primary ? " (main)" : "")}" });
        }
        return list;
    }

    // ---------------- windows ----------------

    async void OpenTaskBoard(string? board = null, string? item = null)
    {
        if (exiting) return;
        if (boardWindow != null) { boardWindow.Focus(board, item); return; }
        try
        {
            boardWindow = new TaskWindow(await EnsureEnvAsync(), "board", board, item);
            boardWindow.Message += OnTaskWindowMessage;
            boardWindow.FormClosed += (_, _) => { boardWindow?.Dispose(); boardWindow = null; ReleaseBrowserIfIdle(); };
            boardWindow.Show();
        }
        catch (Exception e) { Log.Write("tasks window failed: " + e); }
    }

    async void OpenTaskPanel(string board, string item)
    {
        if (exiting) return;
        if (panelWindow != null && !panelWindow.IsDisposed) { panelWindow.ShowTask(board, item); return; }
        try
        {
            panelWindow = new TaskWindow(await EnsureEnvAsync(), "panel", board, item);
            panelWindow.Message += OnTaskWindowMessage;
            panelWindow.FormClosed += (_, _) => { tasks.Flush(); panelWindow?.Dispose(); panelWindow = null; ReleaseBrowserIfIdle(); };
            panelWindow.Show();
            panelWindow.Activate();
        }
        catch (Exception e) { Log.Write("task panel failed: " + e); }
    }

    /// <summary>With the native renderer, the browser only runs while one of its windows is open.</summary>
    void ReleaseBrowserIfIdle()
    {
        if (settings.Renderer != "web" && settingsForm == null && boardWindow == null && panelWindow == null) env = null;
    }

    void OnTaskWindowMessage(TaskWindow w, JsonObject m)
    {
        switch (m["t"]?.GetValue<string>())
        {
            case "ready":
                w.Post(new JsonObject
                {
                    ["t"] = "init", ["mode"] = w.Mode, ["data"] = tasks.ToJson(), ["monitors"] = MonitorList(),
                    ["board"] = w.Board, ["item"] = w.Item,
                });
                break;
            case "op":
            {
                var id = tasks.Apply(m);
                w.Post(new JsonObject { ["t"] = "res", ["req"] = m["req"]?.DeepClone(), ["id"] = id });
                break;
            }
            case "close":
                w.Close();
                break;
        }
    }

    // ---------------- the task HUD (sticky note on a shortcut) ----------------

    HotKey? hudKey;
    TaskHud? taskHud;
    readonly System.Windows.Forms.Timer hudKeyTimer = new() { Interval = 700 };
    /// <summary>Settings keys that change the HUD (style keys are shared with the desktop backdrop).</summary>
    static readonly HashSet<string> HudKeys = new() { "taskHud", "taskHudKey", "taskHudOpacity", "todoBgStyle", "todoBgColor", "todoBgRound", "todoDone", "accent", "textColor" };

    string HudShortcut => schema == null ? "" : schema.Effective(settings.Properties)["taskHudKey"]?.GetValue<string>()?.Trim() ?? "";

    void StartTaskHud()
    {
        hudKey = new HotKey();
        hudKey.Pressed += ToggleTaskHud;
        hudKeyTimer.Tick += (_, _) => { hudKeyTimer.Stop(); RegisterHudKey(announce: true); };
        RegisterHudKey(announce: true);
    }

    /// <summary>(Re-)register the shortcut from the settings; a balloon says so if it can't be used.</summary>
    void RegisterHudKey(bool announce)
    {
        if (hudKey == null || schema == null) return;
        bool on = Bool(schema.Effective(settings.Properties)["taskHud"]);
        var key = HudShortcut;
        string? err = null;
        if (on) err = hudKey.Set(key); else hudKey.Clear();
        if (!on) taskHud?.HideHud();
        hudItem.Visible = on;
        hudItem.ShortcutKeyDisplayString = on ? key : "";
        Log.Write(on ? $"task HUD shortcut {key}: {err ?? "ok"}" : "task HUD off");
        if (err != null && announce) tray.ShowBalloonTip(5000, "Ronin — Task HUD", err + ". Pick another shortcut in Settings → To-do.", ToolTipIcon.Warning);
    }

    /// <summary>A setting changed in Settings.</summary>
    void TaskHudSettingChanged(string key)
    {
        if (!HudKeys.Contains(key)) return;
        if (key is "taskHud" or "taskHudKey") { hudKeyTimer.Stop(); hudKeyTimer.Start(); } // the shortcut box sends as you type
        if (taskHud != null) { taskHud.Shortcut = HudShortcut; taskHud.RefreshHud(); }
    }

    void ToggleTaskHud()
    {
        if (exiting || schema == null) return;
        if (taskHud == null)
        {
            taskHud = new TaskHud(tasks, () => schema!.Effective(settings.Properties));
            taskHud.Request += r => HandleTaskRequest(r);
            taskHud.Moved += p => { settings.TaskHudPos = new[] { p.X, p.Y }; QueueSave(); };
        }
        if (taskHud.Showing) { taskHud.HideHud(); return; }
        // the board picked in the HUD last time, else the one on the monitor under the cursor
        var board = taskHud.BoardId ?? (SlotAt(Cursor.Position) is { } s ? tasks.BoardFor(s.Device)?.Id : null);
        taskHud.Shortcut = HudShortcut;
        taskHud.ShowHud(board, settings.TaskHudPos is { Length: 2 } p ? new Point(p[0], p[1]) : null);
    }

    void StopTaskHud()
    {
        hudKeyTimer.Stop();
        taskHud?.Close();
        taskHud?.Dispose();
        hudKey?.Dispose();
    }

    // ---------------- clicks on the desktop ----------------

    /// <summary>"task:verb:board:item" from a wallpaper. True if it was one.</summary>
    bool HandleTaskRequest(string request)
    {
        var p = request.Split(':');
        if (p.Length != 4 || p[0] != "task") return false;
        string verb = p[1], board = p[2], item = p[3];
        var b = tasks.Board(board);
        if (b == null) return true;
        var it = b.Items.FirstOrDefault(i => i.Id == item);
        JsonObject Op(string op) => new() { ["op"] = op, ["board"] = board, ["item"] = item };
        switch (verb)
        {
            case "cycle": tasks.Apply(Op("item.cycle")); break;
            case "toggle": tasks.Apply(Op("item.toggle")); break;
            case "up": tasks.Apply(Op("item.up")); break;
            case "down": tasks.Apply(Op("item.down")); break;
            case "open": OpenTaskPanel(board, item); break;
            case "delete" when it != null:
            {
                // a task with notes or sub-tasks asks first; a header's tasks stay
                if (!it.IsHeader && (it.Notes.Trim().Length > 0 || it.Subtasks.Count > 0) &&
                    MessageBox.Show($"Delete \"{it.Title}\" and its notes / sub-tasks?", "Ronin", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) break;
                tasks.Apply(Op("item.delete"));
                break;
            }
            case "add":
            {
                // + on a header adds to that section; "+ ADD TASK" adds at the end
                var title = InputBox.Ask("Ronin — New task", it?.IsHeader == true ? $"Add a task to {it.Title}:" : $"Add a task to {b.Name}:");
                if (title == null) break;
                var add = new JsonObject { ["op"] = "item.add", ["board"] = board, ["kind"] = "task", ["title"] = title };
                if (it?.IsHeader == true) add["section"] = it.Id;
                tasks.Apply(add);
                break;
            }
            case "clear":
            {
                int n = b.Items.Count(i => !i.IsHeader && i.Status == 2);
                if (n > 0 && MessageBox.Show($"Remove {n} finished task{(n > 1 ? "s" : "")} from {b.Name}?", "Ronin", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    tasks.Apply(new JsonObject { ["op"] = "board.clearDone", ["board"] = board });
                break;
            }
        }
        return true;
    }

    /// <summary>A monitor's card in Settings picked which board it shows ("" = none).</summary>
    void SetMonitorBoard(string device, string boardId)
    {
        foreach (var b in tasks.Data.Boards.Where(b => b.Monitors.Contains(device) && b.Id != boardId).ToList())
            tasks.Apply(new JsonObject { ["op"] = "board.monitors", ["board"] = b.Id, ["monitors"] = new JsonArray(b.Monitors.Where(x => x != device).Select(x => (JsonNode)x).ToArray()) });
        if (tasks.Board(boardId) is { } target && !target.Monitors.Contains(device))
            tasks.Apply(new JsonObject { ["op"] = "board.monitors", ["board"] = target.Id, ["monitors"] = new JsonArray(target.Monitors.Append(device).Select(x => (JsonNode)x).ToArray()) });
        settingsForm?.SendInit();
    }
}
