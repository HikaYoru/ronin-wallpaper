using System.Diagnostics;
namespace RoninHost;

/// <summary>Checking for and installing updates (see <see cref="Updater"/>).</summary>
internal sealed partial class TrayApp
{
    Updater.Release? updateAvailable;
    string updateStatus = "";
    DateTime lastUpdateCheck = DateTime.MinValue;
    bool checkingUpdates;
    readonly DateTime appStarted = DateTime.Now;

    /// <summary>Once a day (and a minute after start), if updates are on and a source is set.</summary>
    void MaybeCheckUpdates()
    {
        if (!settings.CheckUpdates || checkingUpdates) return;
        if (DateTime.Now - lastUpdateCheck < TimeSpan.FromHours(24)) return;
        if (DateTime.Now - appStarted < TimeSpan.FromMinutes(1)) return; // let the wallpaper settle first
        _ = CheckForUpdatesAsync(manual: false);
    }

    async Task CheckForUpdatesAsync(bool manual)
    {
        var source = Updater.Source(settings);
        if (source.Length == 0)
        {
            updateStatus = "No update source set";
            if (manual) tray.ShowBalloonTip(3000, "Ronin", "Set where to look for updates in Settings → Display → Updates.", ToolTipIcon.None);
            settingsForm?.SendInit();
            return;
        }
        checkingUpdates = true;
        lastUpdateCheck = DateTime.Now;
        try
        {
            updateAvailable = await Updater.CheckAsync(source);
            updateStatus = updateAvailable != null ? $"Version {updateAvailable.Version} is available" : $"Up to date (checked {DateTime.Now:t})";
            Log.Write("update check: " + updateStatus);
            if (updateAvailable != null)
                tray.ShowBalloonTip(5000, "Ronin", $"Version {updateAvailable.Version} is ready to install (tray → Install update).", ToolTipIcon.Info);
            else if (manual)
                tray.ShowBalloonTip(2500, "Ronin", $"Ronin {Updater.Current.ToString(3)} is up to date.", ToolTipIcon.None);
        }
        catch (Exception e)
        {
            updateStatus = "Couldn't check: " + (e.Message.Length > 80 ? e.Message[..80] + "…" : e.Message);
            Log.Write("update check failed: " + e.Message);
            if (manual) tray.ShowBalloonTip(4000, "Ronin", updateStatus, ToolTipIcon.Warning);
        }
        finally { checkingUpdates = false; settingsForm?.SendInit(); }
    }

    /// <summary>Diagnostics: RONIN_DEBUG_UPDATE=install checks a few seconds after start and installs without asking.</summary>
    async void DebugUpdate()
    {
        await Task.Delay(5000);
        await CheckForUpdatesAsync(manual: false);
        if (updateAvailable is { } r && await Updater.InstallAsync(Updater.Source(settings), r)) Quit();
    }

    async void InstallUpdate()
    {
        if (updateAvailable is not { } r) { _ = CheckForUpdatesAsync(manual: true); return; }
        if (MessageBox.Show($"Install Ronin {r.Version}?\n\n{(r.Notes.Length > 0 ? r.Notes + "\n\n" : "")}Ronin closes, updates and starts again. Your settings stay as they are; the current version is kept as app.previous.",
                "Ronin update", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        updateStatus = "Installing…";
        settingsForm?.SendInit();
        if (await Updater.InstallAsync(Updater.Source(settings), r)) Quit();
        else
        {
            updateStatus = "The update couldn't be prepared (see log.txt)";
            settingsForm?.SendInit();
            MessageBox.Show("The update couldn't be prepared. Your current version keeps running.", "Ronin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void RefreshUpdateItem() =>
        updateItem.Text = updateAvailable != null ? $"Install update {updateAvailable.Version.ToString(3)}…" : "Check for updates";
}
