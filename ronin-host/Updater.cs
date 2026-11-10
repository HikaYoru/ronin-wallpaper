using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace RoninHost;

/// <summary>
/// Updates from a releases folder or URL (made by tools\publish.ps1): version.json says the newest version and its
/// zip ({"version":"0.6.1","zip":"Ronin-0.6.1.zip","notes":"…"}). Installing: the zip is unpacked to %TEMP%, the new
/// RoninHost.exe is started with --apply-update, waits for this one to exit, swaps the app folder (the old one is kept as
/// app.previous) and starts the new version.
/// </summary>
internal static class Updater
{
    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
    /// <summary>The folder that holds app\ and ronin-wallpaper\ (the app folder's parent).</summary>
    static string InstallRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    static string AppDir => Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');

    public sealed record Release(Version Version, string Zip, string Notes);

    /// <summary>Where to look: the setting, else update-source.txt next to the app (written by tools\install.ps1).</summary>
    public static string Source(AppSettings s)
    {
        if (s.UpdateSource.Trim().Length > 0) return s.UpdateSource.Trim();
        var file = Path.Combine(AppContext.BaseDirectory, "update-source.txt");
        return File.Exists(file) ? File.ReadAllText(file).Trim() : "";
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static bool IsUrl(string s) => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    static string Join(string source, string name) => IsUrl(source) ? source.TrimEnd('/') + "/" + Uri.EscapeDataString(name) : Path.Combine(source, name);

    /// <summary>The newest release if it is newer than this app, else null. Throws when the source can't be read.</summary>
    public static async Task<Release?> CheckAsync(string source)
    {
        if (source.Length == 0) return null;
        var json = IsUrl(source) ? await Http.GetStringAsync(Join(source, "version.json")) : await File.ReadAllTextAsync(Join(source, "version.json"));
        if (JsonNode.Parse(json) is not JsonObject o || !Version.TryParse(o["version"]?.GetValue<string>(), out var v)) throw new InvalidDataException("version.json isn't valid");
        var zip = o["zip"]?.GetValue<string>() ?? "";
        if (zip.Length == 0 || zip.Contains("..")) throw new InvalidDataException("version.json has no zip");
        return v > Current ? new Release(v, zip, o["notes"]?.GetValue<string>() ?? "") : null;
    }

    /// <summary>Unpack the release and start the new version's updater. Returns false (and logs) if it couldn't.</summary>
    public static async Task<bool> InstallAsync(string source, Release r)
    {
        try
        {
            var work = Path.Combine(Path.GetTempPath(), "RoninUpdate", r.Version.ToString());
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            var zipPath = Path.Combine(work, "release.zip");
            if (IsUrl(source))
            {
                await using var s = await Http.GetStreamAsync(Join(source, r.Zip));
                await using var f = File.Create(zipPath);
                await s.CopyToAsync(f);
            }
            else File.Copy(Join(source, r.Zip), zipPath, true);
            var unpacked = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(zipPath, unpacked);
            var exe = Path.Combine(unpacked, "app", "RoninHost.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("the release has no app\\RoninHost.exe");
            Log.Write($"update: starting {r.Version} installer");
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                ArgumentList = { "--apply-update", InstallRoot, "--wait-pid", Environment.ProcessId.ToString() },
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
            return true;
        }
        catch (Exception e) { Log.Write("update failed: " + e); return false; }
    }

    /// <summary>
    /// Runs in the NEW version, from the unpacked release: wait for the old app to quit, put the new files in place
    /// (the old app folder becomes app.previous), start the installed app. Returns the process exit code.
    /// </summary>
    public static int Apply(string installRoot, int waitPid)
    {
        try
        {
            try { using var old = Process.GetProcessById(waitPid); old.WaitForExit(30_000); } catch (ArgumentException) { }
            Thread.Sleep(500);
            var from = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")); // the unpacked release (app\, ronin-wallpaper\)
            var appDir = Path.Combine(installRoot, "app");
            var previous = Path.Combine(installRoot, "app.previous");
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
            if (Directory.Exists(appDir)) CopyDir(appDir, previous); // a copy, so the old app folder can stay in use if something is locked
            CopyDir(Path.Combine(from, "app"), appDir);
            // keep update-source.txt from the old install
            var src = Path.Combine(previous, "update-source.txt");
            if (File.Exists(src)) File.Copy(src, Path.Combine(appDir, "update-source.txt"), true);
            // wallpaper files: add new or changed ones, never delete the user's own images
            var wp = Path.Combine(from, "ronin-wallpaper");
            if (Directory.Exists(wp)) CopyDir(wp, Path.Combine(installRoot, "ronin-wallpaper"));
            Log.Write($"update: installed {Current} into {appDir}");
            Process.Start(new ProcessStartInfo(Path.Combine(appDir, "RoninHost.exe")) { UseShellExecute = false, WorkingDirectory = appDir });
            return 0;
        }
        catch (Exception e)
        {
            Log.Write("update: apply failed: " + e);
            MessageBox.Show("The Ronin update could not be installed:\n\n" + e.Message + "\n\nYour previous version is still in place.", "Ronin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }
}
