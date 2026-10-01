using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Conflicts;

namespace Nexus.Service.Devices;

/// <summary>Holds a device the user has on off while its competing app runs, and hands it to Nexus once the app exits (ended by the user, Resolve all, or the startup shutdown); two drivers on one hub interleave commands on its shared handles.</summary>
public sealed class VendorAppControlPause : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly DeviceControlGate _gate;
    private readonly IConflictDetector _detector;

    public VendorAppControlPause(DeviceControlGate gate, IConflictDetector detector)
    {
        _gate = gate;
        _detector = detector;
        // Every hosted service is constructed before any starts, so this lands
        // before a connection worker can claim a device.
        TryRefresh();
        // Turning a device on while its app runs must not hand it over until the app exits.
        _gate.Changed += (_, _) => TryRefresh();
    }

    /// <summary>Scans only while the user has a device with a competing app on.</summary>
    internal void Refresh()
    {
        var paused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in DeviceControlPolicy.HandlersWithConflictApp())
        {
            if (!_gate.IsChosenOn(handler)) continue;
            var appId = DeviceControlPolicy.ConflictAppFor(handler)!;
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
