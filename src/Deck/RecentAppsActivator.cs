using System.Threading.Tasks;
using Nexus.Service.Actions;
using Nexus.Service.Activity;
using Nexus.Service.Persistence;

namespace Nexus.Service.Deck;

/// <summary>
/// Press-time activation for a Recent Apps entry: switch to a live window
/// when one exists, else launch by shortcut id, else open the remembered exe
/// path directly. Shared by the physical worker (direct in-process call) and
/// POST /deck/recent-apps/activate (widget presses). Callers decide whether a
/// press on the currently-focused key is a no-op - this class only knows how
/// to bring one entry forward.
/// </summary>
public sealed class RecentAppsActivator
{
    private readonly IProcessActionsProvider _processActions;
    private readonly IShortcutsProvider _shortcuts;
    private readonly SystemActions _system;
    private readonly IWindowSetProvider? _windowSet;

    public RecentAppsActivator(
        IProcessActionsProvider processActions,
        IShortcutsProvider shortcuts,
        SystemActions system,
        IWindowSetProvider? windowSet = null)
    {
        _processActions = processActions;
        _shortcuts = shortcuts;
        _system = system;
        _windowSet = windowSet;
    }

    public async Task ActivateAsync(RecentApp entry)
    {
        // IWindowSetProvider is Windows-only (registered nowhere else); macOS
        // always falls through to Launch(shortcutId), which "open -a" already
        // resolves to activating a running instance instead of a relaunch.
        if (entry.Pid is int pid && (_windowSet?.IsWindowed(pid) ?? false)
            && await _processActions.ActivateWindowAsync(pid).ConfigureAwait(false))
        {
            return;
        }
        if (!string.IsNullOrEmpty(entry.ShortcutId))
        {
            _shortcuts.Launch(entry.ShortcutId);
            return;
        }
        if (!string.IsNullOrEmpty(entry.ExePath))
        {
#if LINUX
            // xdg-open on an ELF binary is a document open, not a launch: spawn it in the user's session.
            if (OperatingSystem.IsLinux())
            {
                LaunchExecutable(entry.ExePath);
                return;
            }
#endif
            await _system.OpenPathAsync(entry.ExePath).ConfigureAwait(false);
        }
    }

#if LINUX
    private static void LaunchExecutable(string exePath)
    {
        try
        {
            var (file, args) = Nexus.Service.Platform.Linux.LinuxSession.WrapSpawnAsSessionUser(exePath, new System.Collections.Generic.List<string>());
            var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (System.Exception ex)
        {
            System.Console.Error.WriteLine($"[recent-apps] launch {exePath} failed: {ex.Message}");
        }
    }
#endif
}
