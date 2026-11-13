using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Reconciles the kiosk host when the Y70 display appears after boot. Decides from the DRM
/// topology the host uses, not the DDC scan behind the overlay supervisor. The full topology is read
/// only when the set of connected connectors changes.
/// </summary>
public sealed class LinuxY70DisplayWatcher : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly Func<bool> _wanted;
    private readonly Func<bool> _present;
    private readonly Action _reconcile;
    private readonly Func<string> _connectors;
    private bool _wasPresent;
    private string? _lastConnectors;
    private int _recheck;
    private const int RecheckTicks = 2;

    internal LinuxY70DisplayWatcher(LinuxPanelKioskHost host)
        : this(host.WantsY70DisplayWatch, host.Y70DisplayPresent, host.Reconcile, () => ReadConnectedConnectors())
    {
    }

    internal LinuxY70DisplayWatcher(Func<bool> wanted, Func<bool> present, Action reconcile, Func<string> connectors)
    {
        _connectors = connectors;
        _wanted = wanted;
        _present = present;
        _reconcile = reconcile;
    }

    /// <summary>True on the poll where the display goes from absent to present while a kiosk is wanted.</summary>
    internal static bool ShouldReconcile(bool wanted, bool present, bool wasPresent) => wanted && present && !wasPresent;

    /// <summary>Connected DRM connector names, sorted; reads only each connector's status file.</summary>
    internal static string ReadConnectedConnectors(string drmRoot = "/sys/class/drm")
    {
        try
        {
            if (!Directory.Exists(drmRoot))
                return "";
            var connected = Directory.GetDirectories(drmRoot, "card*-*")
                .Where(d => LinuxSysfs.ReadText(Path.Combine(d, "status")) == "connected")
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal);
            return string.Join(',', connected);
        }
        catch (Exception)
        {
            return "";
        }
    }

    internal void Tick()
    {
        if (!_wanted())
        {
            // Presence is not tracked while a kiosk is running or no session exists; the next wanted tick rescans.
            _wasPresent = true;
            _lastConnectors = null;
            _recheck = 0;
            return;
        }
        var connectors = _connectors();
        if (connectors != _lastConnectors)
        {
            _lastConnectors = connectors;
            _recheck = RecheckTicks;
        }
        else if (_recheck <= 0)
        {
            return;
        }
        // The topology is cached for a few seconds, so a read right after a change can predate it; recheck on later ticks.
        var present = _present();
        _recheck--;
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
