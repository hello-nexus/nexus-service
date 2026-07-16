using System;
using System.Threading.Tasks;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Orders the boot-time PawnIO install/repair before LibreHardwareMonitor's
/// one-shot Computer.Open. LHM enumerates SuperIO exactly once, inside Open();
/// if that runs while the PawnIO device is still being installed or repaired,
/// motherboard sensors and fan controls stay missing until the next service
/// restart. Program.cs arms the gate on Windows hosts (where
/// TrayBootstrap.WireAppWindowAndPawnIo will signal it after
/// PawnIoInstaller.EnsureInstalledAsync finishes, whatever the outcome);
/// LhmComputer waits on it, capped, before opening. Unarmed hosts (tests,
/// macOS, Linux) skip the wait entirely.
/// </summary>
public static class PawnIoBootGate
{
    private static readonly TaskCompletionSource Done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly TaskCompletionSource LhmOpened =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static volatile bool _armed;

    public static bool IsArmed => _armed;

    public static void Arm() => _armed = true;

    /// <summary>Idempotent; safe to call whether or not the gate is armed.</summary>
    public static void Signal() => Done.TrySetResult();

    /// <summary>
    /// Completes when the gate is signaled or the cap elapses, whichever comes
    /// first. The cap keeps a wedged installer (or an unanswered UAC prompt on
    /// an interactive run) from holding the GPU/CPU sensors hostage. Returns
    /// immediately when the gate was never armed.
    /// </summary>
    public static Task WaitAsync(TimeSpan cap)
    {
        if (!_armed || Done.Task.IsCompleted)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAny(Done.Task, Task.Delay(cap));
    }

    /// <summary>
    /// Marks LHM's Computer.Open as finished (success or failure). Signaled by
    /// LhmComputer's background open; consumed by AutoRestoreOnStart so a
    /// preset apply never rebuilds its outputs from a channel list the delayed
    /// open has not populated yet.
    /// </summary>
    public static void SignalLhmOpened() => LhmOpened.TrySetResult();

    /// <summary>
    /// Completes when LHM's open has finished or the cap elapses. The cap
    /// covers hosts where LhmComputer never constructs. Returns immediately
    /// when the gate was never armed.
    /// </summary>
    public static Task WaitForLhmOpenAsync(TimeSpan cap)
    {
        if (!_armed || LhmOpened.Task.IsCompleted)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAny(LhmOpened.Task, Task.Delay(cap));
    }
}
