using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Reconciles the kiosk host when the Y70 display appears after boot. Reads the DRM
/// topology the host itself decides from, so it does not depend on the DDC scan behind
/// the overlay supervisor's detection. Idle once the Y70 kiosk is running.
/// </summary>
public sealed class LinuxY70DisplayWatcher : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly Func<bool> _wanted;
    private readonly Func<bool> _present;
    private readonly Action _reconcile;
    private bool _wasPresent;

    internal LinuxY70DisplayWatcher(LinuxPanelKioskHost host)
        : this(host.WantsY70DisplayWatch, host.Y70DisplayPresent, host.Reconcile)
    {
    }

    internal LinuxY70DisplayWatcher(Func<bool> wanted, Func<bool> present, Action reconcile)
    {
        _wanted = wanted;
        _present = present;
        _reconcile = reconcile;
    }

    /// <summary>True on the poll where the display goes from absent to present while a kiosk is wanted.</summary>
    internal static bool ShouldReconcile(bool wanted, bool present, bool wasPresent) => wanted && present && !wasPresent;

    internal void Tick()
    {
        if (!_wanted())
        {
            // Presence is not tracked while a kiosk is running or no session exists; the next poll starts from the live state.
            _wasPresent = true;
            return;
        }
        var present = _present();
        var reconcile = ShouldReconcile(true, present, _wasPresent);
        _wasPresent = present;
        if (reconcile)
        {
            Console.Error.WriteLine("[panel-kiosk] Y70 display appeared; reconciling");
            _reconcile();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { Tick(); }
            catch (Exception ex) { Console.Error.WriteLine($"[panel-kiosk] display watch failed: {ex.Message}"); }
        }
    }
}
