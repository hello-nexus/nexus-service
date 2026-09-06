using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Ibp;

/// <summary>
/// Keeps <see cref="IbpPeripheralHub"/> in step with the bus and the Nexus
/// Control gates: opens each iBUYPOWER keyboard / mouse while it is
/// enumerated and its handler's gate is on, closes it (LEDs handed back to
/// firmware) otherwise, and tells the lighting provider when the attached
/// set changes. Frames are pushed separately by
/// <see cref="IbpPeripheralLightingFrameWriter"/>. Mirrors
/// <see cref="Hyte.Keeb.KeebConnectionWorker"/>.
/// </summary>
public sealed class IbpPeripheralConnectionWorker : BackgroundService
{
    private const int PollMs = 1000;

    /// <summary>Wait before re-enumerating on behalf of an allowed unit that did not open, or that the hub dropped.</summary>
    internal const int OpenRetryMs = 10_000;

    private readonly IbpPeripheralHub _hub;
    private readonly HardwarePresence _presence;
    private readonly DeviceControlGate _gate;
    private readonly IbpPeripheralLightingDeviceProvider? _lighting;
    private readonly Func<long> _clock;
    private readonly Dictionary<IbpPeripheralModel, long> _retryAt = new();
    private readonly HashSet<IbpPeripheralModel> _openLastTick = new();
    private int _lastVersion;

    public IbpPeripheralConnectionWorker(IbpPeripheralHub hub, HardwarePresence presence, DeviceControlGate gate, IbpPeripheralLightingDeviceProvider? lighting = null, Func<long>? clock = null)
    {
        _hub = hub;
        _presence = presence;
        _gate = gate;
        _lighting = lighting;
        _clock = clock ?? (static () => Environment.TickCount64);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollMs));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                ServiceLog.Error($"[ibp-conn] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _hub.Disconnect();
    }

    /// <summary>One reconcile/notify cycle. Single-threaded: the worker loop is its only caller.</summary>
    internal void Tick()
    {
        // USB presence is the fast unplug signal (bus-level, cached): a dead HID
        // handle can keep accepting writes for a while.
        var allowed = new HashSet<IbpPeripheralModel>();
        foreach (var model in IbpPeripheralProtocol.Models)
        {
            if (_gate.IsEnabled(model.HandlerId)
                && _presence.UsbPresent(IbpPeripheralProtocol.VendorId, model.ProductId))
            {
                allowed.Add(model);
            }
        }

        var now = _clock();
        var attached = _hub.Attached;

        // A unit the hub dropped (write failures) while still allowed goes on
        // the same backoff as one that never opened, else it would be reopened
        // and dropped again every tick.
        foreach (var model in _openLastTick)
        {
            if (allowed.Contains(model) && !IsOpen(attached, model)) _retryAt[model] = now + OpenRetryMs;
        }

        var reconcile = false;
        foreach (var a in attached)
        {
            if (!allowed.Contains(a.Model)) { reconcile = true; break; }
        }
        if (!reconcile)
        {
            foreach (var model in allowed)
            {
                if (IsOpen(attached, model)) { _retryAt.Remove(model); continue; }
                if (_retryAt.TryGetValue(model, out var at) && now < at) continue;
                reconcile = true;
                break;
            }
        }

        if (reconcile)
        {
            _hub.Reconcile(allowed);
            var after = _hub.Attached;
            foreach (var model in allowed)
            {
                if (!IsOpen(after, model)) _retryAt[model] = now + OpenRetryMs;
            }
        }

        // A unit that left or was gated off retries immediately when it is back.
        if (_retryAt.Count > 0)
        {
            var stale = new List<IbpPeripheralModel>();
            foreach (var model in _retryAt.Keys)
            {
                if (!allowed.Contains(model)) stale.Add(model);
            }
            foreach (var model in stale) _retryAt.Remove(model);
        }

        _openLastTick.Clear();
        foreach (var a in _hub.Attached) _openLastTick.Add(a.Model);

        var version = _hub.Version;
        if (version != _lastVersion)
        {
            _lastVersion = version;
            _lighting?.OnConnectionChanged();
        }
    }

    private static bool IsOpen(IReadOnlyList<IbpAttachedPeripheral> attached, IbpPeripheralModel model)
    {
        foreach (var a in attached)
        {
            if (ReferenceEquals(a.Model, model)) return true;
        }
        return false;
    }
}
