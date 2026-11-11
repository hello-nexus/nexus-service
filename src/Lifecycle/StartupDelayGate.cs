using System;
using System.Threading.Tasks;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// User-configured hold on one-shot hardware enumeration: LHM's
/// <c>Computer.Open</c> and the OpenRGB subprocess spawn.
///
/// Both scan shared buses - SMBus carries DIMM SPD for LHM and RAM RGB
/// controllers for OpenRGB - and neither rescans. The bus has no software
/// arbitration, so a probe that collides with another master's transaction
/// reads as "nothing at this address", and the device stays missing for the
/// rest of the session. The window yields the boot-time bus burst from vendor
/// tools (Armoury Crate, iCUE, HWiNFO) that we cannot observe or signal on.
///
/// Zero - the default - leaves startup timing unchanged.
/// </summary>
internal static class StartupDelayGate
{
    public const int MaxSeconds = 60;

    /// <summary>
    /// A start later than this after boot is a manual or post-update restart,
    /// which comes straight up: the contention being avoided is a boot
    /// phenomenon, and a user restarting the service should not wait.
    /// </summary>
    private static readonly TimeSpan BootStartWindow = TimeSpan.FromMinutes(3);

    private static volatile Task _elapsed = Task.CompletedTask;

    /// <summary>Configured window, <see cref="TimeSpan.Zero"/> when disabled.</summary>
    public static TimeSpan Configured { get; private set; }

    /// <summary>
    /// Starts the window when this process is a boot-time start. Call once,
    /// before any enumeration begins - the deadline runs from this call, not
    /// from the first wait.
    /// </summary>
    public static void ArmForBootStart(int seconds) =>
        ArmForBootStart(seconds, TimeSpan.FromMilliseconds(SystemUptime.BootElapsedMs));

    internal static void ArmForBootStart(int seconds, TimeSpan sinceBoot)
    {
        var clamped = Math.Clamp(seconds, 0, MaxSeconds);
        Configured = TimeSpan.Zero;
        _elapsed = Task.CompletedTask;

        if (clamped <= 0)
        {
            return;
        }
        if (sinceBoot > BootStartWindow)
        {
            Console.WriteLine($"[startup-delay] {clamped}s window skipped: not a boot start");
            return;
        }

        Configured = TimeSpan.FromSeconds(clamped);
        _elapsed = Task.Delay(Configured);
        Console.WriteLine($"[startup-delay] holding hardware enumeration for {clamped}s");
    }

    /// <summary>Completes when the window has elapsed. Never throws.</summary>
    public static Task WaitAsync() => _elapsed;
}
