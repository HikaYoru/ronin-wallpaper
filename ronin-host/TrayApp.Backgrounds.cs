using System.Text.Json.Nodes;

namespace RoninHost;

/// <summary>
/// Settings → Background gallery: add pictures and videos (dropped on the window or picked with "Add files…"),
/// choose one, and move ones you no longer want to the Recycle Bin. Files are copied into the wallpaper's
/// images folder, so the originals stay where they were.
/// </summary>
internal sealed partial class TrayApp
{
    static readonly string[] VideoPatterns = { "*.mp4", "*.m4v", "*.mov", "*.mkv", "*.wmv", "*.webm" };

    string ImagesDir => Path.Combine(schema!.Folder, schema.Root["background"]?["folder"]?.GetValue<string>() ?? "images");

    /// <summary>Extensions the background list shows: the wallpaper's own filter plus the videos the native renderer plays.</summary>
    HashSet<string> BackgroundExtensions()
    {
        var filter = schema!.Root["background"]?["filter"]?.GetValue<string>() ?? "*.jpg|*.jpeg|*.png|*.webp|*.gif";
        return filter.Split('|').Concat(VideoPatterns).Select(p => p.TrimStart('*').ToLowerInvariant()).ToHashSet();
    }

    /// <summary>"Add files…": the usual Windows file picker, several files at once.</summary>
    void PickBackgrounds(JsonObject? target)
    {
        var exts = BackgroundExtensions();
        var pattern = string.Join(";", exts.Select(x => "*" + x));
        using var dlg = new OpenFileDialog
        {
            Title = "Add backgrounds to Ronin",
            Multiselect = true,
            Filter = $"Images and videos|{pattern}|Images|*.jpg;*.jpeg;*.png;*.webp;*.gif|Videos|{string.Join(";", VideoPatterns)}|All files|*.*",
        };
        if (dlg.ShowDialog(settingsForm) == DialogResult.OK) _ = AddBackgroundsAsync(dlg.FileNames, target);
    }

    /// <summary>Copy the files into the images folder (skipping ones already there), then select the last one.</summary>
    async Task AddBackgroundsAsync(IReadOnlyList<string> paths, JsonObject? target)
    {
        var exts = BackgroundExtensions();
        var dir = ImagesDir;
        var wrong = paths.Where(p => !exts.Contains(Path.GetExtension(p).ToLowerInvariant()) || !File.Exists(p)).ToList();
        var ok = paths.Except(wrong).ToList();
        Notice(ok.Count == 0 ? null : ok.Count == 1 ? $"Adding {Path.GetFileName(ok[0])}…" : $"Adding {ok.Count} files…", busy: true);

        var added = new List<string>();
        try
        {
            added = await Task.Run(() =>
            {
                Directory.CreateDirectory(dir);
                var names = new List<string>();
                foreach (var p in ok) names.Add(CopyIn(p, dir));
                return names;
            });
        }
        catch (Exception e)
        {
            Log.Write("adding backgrounds failed: " + e);
            Notice("Couldn't add the files: " + e.Message);
            return;
        }
        Log.Write($"backgrounds added: {string.Join(", ", added)}" + (wrong.Count > 0 ? $" (skipped {wrong.Count})" : ""));

        if (added.Count > 0) SelectBackground(added[^1], target);
        settingsForm?.SendInit(); // the gallery lists the new files

        var msg = added.Count switch { 0 => "", 1 => $"Added {added[0]}.", _ => $"Added {added.Count} backgrounds." };
        if (wrong.Count > 0)
            msg += (msg.Length > 0 ? " " : "") + $"Skipped {string.Join(", ", wrong.Select(Path.GetFileName).Take(3))}{(wrong.Count > 3 ? "…" : "")}: " +
                   "Ronin takes " + string.Join(" ", exts.OrderBy(x => x)) + " files.";
        Notice(msg);
    }

    /// <summary>Copy one file in; a file with the same name and the same contents is reused, a different one gets " (2)".</summary>
    static string CopyIn(string src, string dir)
    {
        var name = Path.GetFileName(src);
        var dest = Path.Combine(dir, name);
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int n = 2; File.Exists(dest); n++)
        {
            if (SameFile(src, dest)) return Path.GetFileName(dest);
            dest = Path.Combine(dir, $"{stem} ({n}){ext}");
        }
        File.Copy(src, dest);
        return Path.GetFileName(dest);
    }

    static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead(); using var sb = fb.OpenRead();
        var ba = new byte[81920]; var bb = new byte[81920];
        while (true)
        {
            int na = sa.ReadAtLeast(ba, ba.Length, false), nb = sb.ReadAtLeast(bb, bb.Length, false);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            if (na == 0) return true;
        }
    }

    /// <summary>
    /// Use a background: for one monitor's card ({card}), for the monitor being edited in Settings ({mon}),
    /// or for every monitor (no target).
    /// </summary>
    void SelectBackground(string file, JsonObject? target)
    {
        var card = target?["card"]?.GetValue<string>();
        var mon = target?["mon"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(card)) SetMonitorValue(card, "background", file);
        else if (!string.IsNullOrEmpty(mon)) OnSettingsMessage(new JsonObject { ["t"] = "set", ["k"] = "background", ["v"] = file, ["mon"] = mon });
        else OnSettingsMessage(new JsonObject { ["t"] = "set", ["k"] = "background", ["v"] = file });
    }

    /// <summary>Move a background to the Recycle Bin (after asking); whatever used it goes back to the default.</summary>
    void RemoveBackground(string file)
    {
        if (file.Length == 0 || file.Contains('/') || file.Contains('\\') || file.Contains("..")) return;
        var path = Path.Combine(ImagesDir, file);
        if (!File.Exists(path)) { settingsForm?.SendInit(); return; }
        var fallback = schema!.Root["background"]?["value"]?.GetValue<string>() ?? "";
        if (string.Equals(file, fallback, StringComparison.OrdinalIgnoreCase))
        {
            Notice($"{file} is the built-in default, so it stays. Pick another background to stop using it.");
            return;
        }
        if (MessageBox.Show(settingsForm, $"Move \"{file}\" to the Recycle Bin?\n\nMonitors using it go back to the default background. You can restore it from the Recycle Bin.",
                "Ronin", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

        // stop using it first, so no monitor holds the file open
        var before = slots.ToDictionary(s => s, s => EffectiveFor(s.Device)["background"]?.GetValue<string>());
        if (settings.Properties["background"]?.GetValue<string>() is { } shared && string.Equals(shared, file, StringComparison.OrdinalIgnoreCase))
            settings.Properties.Remove("background");
        foreach (var ov in settings.MonitorProperties.Values)
            if (ov["background"]?.GetValue<string>() is { } own && string.Equals(own, file, StringComparison.OrdinalIgnoreCase)) ov.Remove("background");
        PruneOverrides();
        foreach (var (s, old) in before)
        {
            var now = EffectiveFor(s.Device)["background"]?.DeepClone();
            if (now?.GetValue<string>() != old) s.View.SendProperties(new JsonObject { ["background"] = now });
        }
        QueueSave();

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            Log.Write("background moved to the Recycle Bin: " + file);
            Notice($"Moved {file} to the Recycle Bin.");
        }
        catch (Exception e)
        {
            Log.Write("removing background failed: " + e.Message);
            Notice($"Couldn't remove {file}: {e.Message}");
        }
        settingsForm?.SendInit();
    }

    /// <summary>A short line under the gallery ("Added 3 backgrounds.").</summary>
    void Notice(string? text, bool busy = false) =>
        settingsForm?.Post(new JsonObject { ["t"] = "notice", ["text"] = text ?? "", ["busy"] = busy });
}
