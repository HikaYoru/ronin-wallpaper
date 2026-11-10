namespace RoninHost;

internal static class Program
{
    public const string QuitEvent = @"Local\RoninHost.Quit";
    public const string ShowSettingsEvent = @"Local\RoninHost.ShowSettings";

    [STAThread]
    static int Main(string[] args)
    {
        // updater mode (the new version, started from an unpacked release): no tray, no single-instance lock
        int ai = Array.IndexOf(args, "--apply-update");
        if (ai >= 0 && ai + 1 < args.Length)
        {
            ApplicationConfiguration.Initialize();
            int wp = Array.IndexOf(args, "--wait-pid");
            int pid = wp >= 0 && wp + 1 < args.Length && int.TryParse(args[wp + 1], out var p) ? p : -1;
            return Updater.Apply(args[ai + 1], pid);
        }
        Run(args);
        return 0;
    }

    static void Run(string[] args)
    {
        using var single = new Mutex(true, @"Local\RoninHost.SingleInstance", out bool first);
        if (!first)
        {
            // already running in the tray: "RoninHost --quit" stops it, anything else opens its settings
            var name = args.Contains("--quit") ? QuitEvent : ShowSettingsEvent;
            if (EventWaitHandle.TryOpenExisting(name, out var ev)) using (ev) ev.Set();
            return;
        }
        if (args.Contains("--quit")) return;

        ApplicationConfiguration.Initialize(); // per-monitor DPI v2 etc. from the .csproj
        Application.ThreadException += (_, e) => Log.Write("ui exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);
        Application.Run(new TrayApp(openSettings: args.Contains("--settings")));
    }
}
