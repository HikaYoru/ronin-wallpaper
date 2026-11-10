using System.Text.Json.Nodes;

namespace RoninHost;

/// <summary>Saved layouts ("Work", "Gaming", "Stream"…) and settings import / export.</summary>
internal sealed partial class TrayApp
{
    /// <summary>Shared settings a layout keeps besides Arrange's own keys.</summary>
    static readonly string[] LayoutExtraKeys = { "background", "panorama" };

    static bool IsLayoutKey(string k) => ArrangeKeys.Contains(k) || LayoutExtraKeys.Contains(k);

    void SaveLayoutAs()
    {
        var name = InputBox.Ask("Ronin — Save layout", "Name for this layout (e.g. Work, Gaming, Stream):", settings.CurrentLayout);
        if (name == null) return;
        var shared = new JsonObject();
        foreach (var (k, v) in settings.Properties) if (IsLayoutKey(k)) shared[k] = v?.DeepClone();
        var mons = new JsonObject();
        foreach (var (id, ov) in settings.MonitorProperties) mons[id] = ov.DeepClone();
        settings.Layouts[name] = new JsonObject { ["shared"] = shared, ["monitors"] = mons };
        settings.CurrentLayout = name;
        settings.Save();
        Log.Write($"layout saved: {name}");
        tray.ShowBalloonTip(2500, "Ronin", $"Layout \"{name}\" saved.", ToolTipIcon.None);
    }

    void ApplyLayout(string name)
    {
        if (!settings.Layouts.TryGetValue(name, out var layout)) return;
        var before = slots.ToDictionary(s => s, s => EffectiveFor(s.Device));
        // layout keys come from the layout (missing = default); everything else stays as it is
        foreach (var k in settings.Properties.Select(kv => kv.Key).Where(IsLayoutKey).ToList()) settings.Properties.Remove(k);
        if (layout["shared"] is JsonObject shared) foreach (var (k, v) in shared) settings.Properties[k] = v?.DeepClone();
        settings.MonitorProperties.Clear();
        if (layout["monitors"] is JsonObject mons)
            foreach (var (id, ov) in mons) if (ov is JsonObject o) settings.MonitorProperties[id] = (JsonObject)o.DeepClone();
        settings.CurrentLayout = name;
        foreach (var s in slots) SendDiff(s, before[s]);
        settings.Save();
        settingsForm?.SendInit();
        RefreshOverlays();
        Log.Write($"layout applied: {name}");
    }

    void DeleteLayout(string name)
    {
        if (MessageBox.Show($"Delete the layout \"{name}\"?", "Ronin", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        settings.Layouts.Remove(name);
        if (settings.CurrentLayout == name) settings.CurrentLayout = "";
        settings.Save();
    }

    void FillLayoutsMenu(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        foreach (var name in settings.Layouts.Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
        {
            var n = name;
            menu.DropDownItems.Add(new ToolStripMenuItem(n, null, (_, _) => ApplyLayout(n)) { Checked = n == settings.CurrentLayout, ForeColor = InkRenderer.Text });
        }
        if (settings.Layouts.Count > 0) menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("Save current layout as…", null, (_, _) => SaveLayoutAs()) { ForeColor = InkRenderer.Text });
        if (settings.Layouts.Count > 0)
        {
            var del = new ToolStripMenuItem("Delete") { ForeColor = InkRenderer.Text };
            foreach (var name in settings.Layouts.Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            {
                var n = name;
                del.DropDownItems.Add(new ToolStripMenuItem(n, null, (_, _) => DeleteLayout(n)) { ForeColor = InkRenderer.Text });
            }
            if (del.DropDown is ToolStripDropDownMenu dd2) dd2.ShowImageMargin = false;
            del.DropDown.Renderer = new InkRenderer();
            menu.DropDownItems.Add(del);
        }
        if (menu.DropDown is ToolStripDropDownMenu dd) dd.ShowImageMargin = false;
        menu.DropDown.Renderer = new InkRenderer();
    }

    // ---------------- layouts by time of day ----------------

    /// <summary>
    /// The schedule entry whose time most recently passed (looking back up to a week, respecting each entry's days).
    /// Its layout is applied once when that moment passes; switching by hand in between is kept until the next one.
    /// </summary>
    void CheckSchedule()
    {
        if (settings.LayoutSchedule.Count == 0 || Arranging) return;
        var now = DateTime.Now;
        (DateTime when, ScheduleEntry e)? best = null;
        for (int back = 0; back < 8 && best == null; back++)
        {
            var day = now.Date.AddDays(-back);
            foreach (var e in settings.LayoutSchedule)
            {
                if (!TimeSpan.TryParse(e.Time, System.Globalization.CultureInfo.InvariantCulture, out var ts)) continue;
                if (e.Days.Count > 0 && !e.Days.Contains((int)day.DayOfWeek)) continue;
                var when = day + ts;
                if (when <= now && (best == null || when > best.Value.when)) best = (when, e);
            }
        }
        if (best is not { } b) return;
        var key = $"{b.when:yyyy-MM-ddTHH:mm}|{b.e.Layout}";
        if (key == settings.ScheduleApplied) return;
        settings.ScheduleApplied = key;
        Log.Write($"schedule: {key}");
        if (settings.Layouts.ContainsKey(b.e.Layout) && b.e.Layout != settings.CurrentLayout) ApplyLayout(b.e.Layout);
        else QueueSave();
    }

    void SetSchedule(JsonArray list)
    {
        settings.LayoutSchedule = list.OfType<JsonObject>().Select(o => new ScheduleEntry
        {
            Layout = o["layout"]?.GetValue<string>() ?? "",
            Time = o["time"]?.GetValue<string>() ?? "09:00",
            Days = (o["days"] as JsonArray)?.Select(d => (int)Num(d)).Where(d => d is >= 0 and <= 6).Distinct().OrderBy(d => d).ToList() ?? new(),
        }).Where(e => e.Layout.Length > 0).ToList();
        settings.ScheduleApplied = ""; // apply what the new schedule says for right now
        settings.Save();
        CheckSchedule();
    }

    JsonArray ScheduleJson() => new(settings.LayoutSchedule.Select(e => (JsonNode)new JsonObject
    {
        ["layout"] = e.Layout, ["time"] = e.Time, ["days"] = new JsonArray(e.Days.Select(d => (JsonNode)d).ToArray()),
    }).ToArray());

    // ---------------- import / export ----------------

    void ExportSettings()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Export Ronin settings", Filter = "Ronin settings (*.json)|*.json", FileName = $"ronin-settings-{DateTime.Now:yyyy-MM-dd}.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, settings.ToJson());
            Log.Write("settings exported to " + dlg.FileName);
        }
        catch (Exception e) { MessageBox.Show("Could not export:\n" + e.Message, "Ronin", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    void ImportSettings()
    {
        using var dlg = new OpenFileDialog { Title = "Import Ronin settings", Filter = "Ronin settings (*.json)|*.json|All files|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        AppSettings? imported;
        try { imported = AppSettings.FromJson(File.ReadAllText(dlg.FileName)); }
        catch (Exception e) { imported = null; Log.Write("import failed: " + e.Message); }
        if (imported == null)
        {
            MessageBox.Show("That file isn't a Ronin settings file.", "Ronin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show("Replace all your current Ronin settings and layouts with the imported ones?", "Ronin",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        EndArrange();
        settings.CopyFrom(imported);
        MigrateMonitorIds();
        settings.Save();
        Log.Write("settings imported from " + dlg.FileName);
        foreach (var s in slots) s.View.SendFpsLimit(FpsFor(s));
        _ = RebuildWallpaperAsync(0);
        settingsForm?.SendInit();
    }
}
