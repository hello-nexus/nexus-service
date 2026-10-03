using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Conflicts;
using Nexus.Service.Persistence;

namespace Nexus.Service.Devices;

/// <summary>Holds a device the user has on off while its whitelisted competing app runs, and hands it to Nexus once the app exits. A non-whitelisted app does not pause the device: the user chose Nexus over it, and the launch notice offers to end it.</summary>
public sealed class VendorAppControlPause : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly DeviceControlGate _gate;
    private readonly IConflictDetector _detector;
    private readonly IConfigStore _store;

    public VendorAppControlPause(DeviceControlGate gate, IConflictDetector detector, IConfigStore store)
    {
        _gate = gate;
        _detector = detector;
        _store = store;
        // Every hosted service is constructed before any starts, so this lands
        // before a connection worker can claim a device.
        TryRefresh();
        // A device choice re-checks at once rather than on the next poll.
        _gate.Changed += (_, _) => TryRefresh();
    }

    /// <summary>Scans only while the user has a device on whose competing app is whitelisted.</summary>
    internal void Refresh()
    {
        var whitelist = _store.Load().Ui.ConflictAutoKillExclusions;
        var paused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in DeviceControlPolicy.HandlersWithConflictApp())
        {
            if (!_gate.IsChosenOn(handler)) continue;
            var appId = DeviceControlPolicy.ConflictAppFor(handler)!;
            if (!whitelist.Contains(appId, StringComparer.OrdinalIgnoreCase)) continue;
            if (_detector.IsAppRunning(appId)) paused[handler] = appId;
        }
        _gate.SetPausedByApp(paused);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            TryRefresh();
        }
    }

    private void TryRefresh()
    {
        try { Refresh(); }
        catch (Exception ex) { Console.Error.WriteLine($"[device-control] app pause check failed: {ex.Message}"); }
    }
}
