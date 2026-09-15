namespace Nexus.Service.Lifecycle;

// Early-exit CLI dispatch + flag parsing for Program.cs. Each handler
// short-circuits the daemon startup; the rest of the binary never builds
// the WebApplication. Kept here so Program.cs reads as the run-the-daemon
// path, not a switch on argv[0].
internal static class CommandLineEntry
{
#if WINDOWS
    private static readonly Dictionary<string, Func<string[], int>> WindowsHandlers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["--install"] = WindowsServiceInstaller.RunInstall,
            ["--uninstall"] = WindowsServiceInstaller.RunUninstall,
            ["--start-service"] = static _ => WindowsServiceInstaller.RunStartService(),
            ["--helper"] = WindowsUserHelper.Run,
            // Throwaway render-GPU probe (see GpuProbe): optionally set the
            // GpuPreference class, create a GL context, print the bound renderer.
            ["--gpu-probe"] = GpuProbe.Run,
            // One-shot invoked by the service via schtasks when the desktop
            // widget context menu's "Open dashboard" item is clicked.
            ["--open-app"] = static _ =>
            {
                Nexus.Service.Platform.Windows.TrayIcon.OpenLocalWindow();
                return 0;
            },
            // One-shot invoked by the service via schtasks to switch the default
            // audio endpoint in the user session (per-user setting; IPolicyConfig
            // can't change it from Session 0).
            ["--set-audio-default"] = static a =>
            {
                if (a.Length < 2 || string.IsNullOrEmpty(a[1])) return 1;
                return new Nexus.Service.Activity.WindowsAudioDeviceProvider().SetDefaultDirect(a[1]) ? 0 : 1;
            },
            // One-shot invoked by the service via schtasks after a start-mode
            // change: syncs the per-user sign-in Run key to the new service
            // start type (HKCU is the real user here, not the LocalSystem service).
            ["--sync-autostart"] = static _ =>
            {
                WindowsStartupProvider.SyncHelperAutostart(Environment.ProcessPath ?? string.Empty);
                return 0;
            },
        };
#endif

    // Returns the process exit code if argv matched a short-circuit path,
    // otherwise null. Must run before the single-instance mutex because the
    // PawnIO install is briefly a second instance during the elevated install.
    public static int? TryEarlyExit(string[] args)
    {
        if (args.Length > 0 && args[0] == "--install-pawnio")
        {
            return PawnIoInstaller.RunElevatedInstall(
                upgrade: args.Contains("--upgrade"),
                repair: args.Contains("--repair"));
        }

        // Detached finalizer spawned by POST /service/factory-reset: waits for
        // the live service to exit, wipes every Nexus data dir, then restarts.
        // A second instance by design, so - like --install-pawnio - it must run
        // before the single-instance mutex.
        if (args.Length > 0 && (args[0] == FactoryReset.FinalizeFlag || args[0] == FactoryReset.RestartFlag))
            return FactoryReset.Finalize(args);

#if WINDOWS
        if (args.Length > 0 && WindowsHandlers.TryGetValue(args[0], out var handler))
            return handler(args);

#if !DEBUG
        // No-args means the user double-clicked Nexus.exe. With SCM owning the
        // daemon, the launcher just detects service state, spawns the tray if
        // missing, and opens the dashboard - no cold-start self-elevation.
        if (args.Length == 0)
            return WindowsLauncher.Run();
#endif
    
        // Protocol-handler URLs from the dashboard. start-admin is kept as a
        // legacy alias mapping onto restart-service; the service is already
        // LocalSystem so "restart as admin" is a no-op naming-wise.
        if (args.Length > 0 && (
                args[0].StartsWith("nexus://restart-service", StringComparison.OrdinalIgnoreCase)
                || args[0].StartsWith("nexus://start-admin", StringComparison.OrdinalIgnoreCase)))
        {
            return WindowsServiceInstaller.RunStartService();
        }
#endif
        return null;
    }

    // Strips the lifecycle flags Program.cs uses to gate behaviour from the
    // forwarded args, returning them as named booleans. The flag-removal order
    // mirrors the original sequence so --relaunch-elevated is checked against
    // args[0] only after --service has been pulled out.
    public static (string[] Args, bool ServiceMode, bool SuppressStartupWindow, bool RelaunchElevated)
        StripLifecycleFlags(string[] args)
    {
        var serviceMode = args.Any(static a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase));
        if (serviceMode)
            args = args.Where(static a => !string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase)).ToArray();

        var relaunchElevated = args.Length > 0 && args[0] == "--relaunch-elevated";
        if (relaunchElevated) args = args.Skip(1).ToArray();

        var suppressStartupWindow = args.Any(static a => string.Equals(a, "--no-window", StringComparison.OrdinalIgnoreCase));
        if (suppressStartupWindow)
            args = args.Where(static a => !string.Equals(a, "--no-window", StringComparison.OrdinalIgnoreCase)).ToArray();

        return (args, serviceMode, suppressStartupWindow, relaunchElevated);
    }
}
