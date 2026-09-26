using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Conflicts;

namespace Nexus.Service.Devices;

/// <summary>
/// Flips a never-manually-set, conflict-mapped third-party hub to Nexus
/// Control ON the moment it is connected and its competing brand app is not
/// running. The check only decides the first default: once
/// <see cref="DeviceControlGate.TryAdopt"/> puts a device on the Enabled
/// list, the gate's own stickiness keeps it on even if the app later
/// launches (the sidebar conflict badge is the only remaining signal).
/// A user's explicit on/off choice is never overridden: TryAdopt re-checks
/// both lists inside the same store mutation that writes.
/// </summary>
public sealed class DeviceAdoptionService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly DeviceManager _deviceManager;
    private readonly DeviceControlGate _gate;
    private readonly IConflictDetector _detector;
    private readonly DeviceBroadcaster _broadcaster;

    public DeviceAdoptionService(
        DeviceManager deviceManager,
        DeviceControlGate gate,
        IConflictDetector detector,
        DeviceBroadcaster broadcaster)
    {
        _deviceManager = deviceManager;
        _gate = gate;
        _detector = detector;
        _broadcaster = broadcaster;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                try
                {
                    RunAdoptionPass();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[device-adoption] pass failed: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One pass over every device; adopts each eligible one and broadcasts once if anything changed.</summary>
    internal void RunAdoptionPass()
    {
        // Cheap pre-check first: adoption can only ever change something for a
        // device that is connected, mapped to a competing app, and never
        // explicitly set. When nothing qualifies - the common steady state - the
        // conflict data cannot affect the outcome, so don't ask for it. Reading
        // DetectionReady forces a process enumeration when the cache is stale.
        var anyCandidate = false;
        foreach (var device in _deviceManager.GetAll())
        {
            if (device.Connected
                && DeviceControlPolicy.AdoptionConflictAppFor(device.Id) is not null
                && _gate.IsUnset(device.Id)
                && !_gate.IsAdoptionAppWhitelisted(device.Id))
            {
                anyCandidate = true;
                break;
            }
        }
        if (!anyCandidate)
        {
            return;
        }

        if (!_detector.DetectionReady)
        {
            return;
        }

        bool changed = false;
        foreach (var device in _deviceManager.GetAll())
        {
            if (!ShouldAdopt(device.Id, device.Connected, _gate, _detector))
            {
                continue;
            }
            if (_gate.TryAdopt(device.Id))
            {
                changed = true;
            }
        }

        if (changed)
        {
            _broadcaster.BroadcastNow();
        }
    }

    /// <summary>
    /// True when <paramref name="deviceId"/> should be adopted onto the
    /// Enabled list this pass: connected, mapped to a competing app the user
    /// has not whitelisted, never explicitly set (on, off, or previously
    /// adopted), and that app is not currently running.
    /// </summary>
    internal static bool ShouldAdopt(string deviceId, bool connected, DeviceControlGate gate, IConflictDetector detector)
    {
        if (!detector.DetectionReady)
        {
            return false;
        }
        if (!connected)
        {
            return false;
        }
        var conflictApp = DeviceControlPolicy.AdoptionConflictAppFor(deviceId);
        if (conflictApp is null)
        {
            return false;
        }
        if (!gate.IsUnset(deviceId) || gate.IsAdoptionAppWhitelisted(deviceId))
        {
            return false;
        }
        return !detector.IsAppRunning(conflictApp);
    }
}
