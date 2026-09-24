using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Nollie;

/// <summary>Rescans every poll and attaches/drops controllers individually, since several commonly share a machine.</summary>
public sealed class NollieConnectionWorker : BackgroundService
{
    private const int PollMs = 5000;
    private const int MaxConsecutiveFailures = 3;

    /// <summary>Nexus Control gate id; <see cref="Devices.Handlers.NollieHandler"/> lists the device under it.</summary>
    public const string HandlerId = "nollie";

    /// <summary>Seed for a never-configured channel; matches the motherboard ARGB header seed in <c>RgbBridge</c>.</summary>
    private const int DefaultChannelLedCount = 60;

    private readonly IHidEnumerator _hid;
    private readonly NollieHub _hub;
    private readonly NollieLightingDeviceProvider _lighting;
    private readonly DeviceControlGate _gate;
    private readonly IConfigStore _store;
    private readonly HardwarePresence _presence;
    private readonly HashSet<(int, int)> _warnedUnmatched = new();

    public NollieConnectionWorker(IHidEnumerator hid, NollieHub hub, NollieLightingDeviceProvider lighting, DeviceControlGate gate, IConfigStore store, HardwarePresence presence)
    {
        _hid = hid;
        _hub = hub;
        _lighting = lighting;
        _gate = gate;
        _store = store;
        _presence = presence;
    }

    private bool AnyRealBoardAttached()
    {
        foreach (var controller in _hub.Controllers)
        {
            if (!IsSimulated(controller)) return true;
        }
        return false;
    }

    private static bool IsSimulated(NollieController controller)
        => controller.Path.StartsWith(SimulatedNollieDevice.PathPrefix, StringComparison.Ordinal);

    /// <summary>
    /// True when any Nollie vendor is on the USB bus. Reconcile() calls FindAll(),
    /// which opens every HID interface on the host and serial-queries each one.
    /// </summary>
    private bool AnyNolliePresent()
    {
        foreach (var vid in NollieProtocol.VendorIds)
        {
            if (_presence.UsbPresent(vid)) return true;
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled(HandlerId))
                {
                    if (_hub.IsConnected)
                    {
                        ReleaseAll();
                        ServiceLog.Info("[nollie] released (Nexus Control off)");
                        _lighting.OnHubStateUpdated();
                    }
                }
                // A real board first, so a removal is still noticed even if the
                // bus check misses a device that is already attached. A simulated
                // board alone does not earn the HID walk.
                else if (AnyRealBoardAttached() || AnyNolliePresent())
                {
                    if (Reconcile())
                    {
                        _lighting.OnHubStateUpdated();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[nollie] worker error: {ex.Message}");
            }

            try { await Task.Delay(PollMs, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        ReleaseAll();
    }

    /// <summary>
    /// The fast-teardown entry: the Windows service exits without running
    /// hosted-service StopAsync, so Program.cs calls this from the shutdown
    /// task set. Safe to run twice; a released board is skipped.
    /// </summary>
    public void ReleaseAllForShutdown() => ReleaseAll();

    /// <summary>
    /// Hands every board to its firmware with its standalone lighting, then
    /// drops the handles. A released controller refuses colour writes and a
    /// detached one has no handle, so a frame-writer tick landing in between
    /// cannot take a board back.
    /// </summary>
    internal void ReleaseAll()
    {
        var settings = _store.Load();
        foreach (var controller in _hub.Controllers)
        {
            if (NollieStandalone.Release(controller, settings))
            {
                ServiceLog.Info($"[nollie] {controller.Spec.Name} id={controller.DeviceId} handed to firmware");
            }
        }
        _hub.DetachAll();
    }

    /// <summary>Attaches newly present controllers and drops gone or wedged ones. Returns true when the set changed.</summary>
    internal bool Reconcile()
    {
        var changed = false;
        var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // One enumeration per poll, matched against the table; a Find() per
        // table row would walk every HID interface on the box 16 times.
        var seenVidPid = new HashSet<(int, int)>();

        foreach (var info in _hid.FindAll())
        {
            var spec = NollieProtocol.Lookup(info.VendorId, info.ProductId);
            if (spec is null) continue;
            seenVidPid.Add((info.VendorId, info.ProductId));
            if (!IsRgbInterface(info, spec)) continue;
            livePaths.Add(info.Path);
            if (_hub.HasPath(info.Path)) continue;

            var device = _hid.Open(info.Path);
            if (device is null) continue;

            Attach(new NollieController(device, spec));
            changed = true;
        }

        // A present controller none of whose interfaces matched is invisible to
        // both drivers - the OpenRGB detectors are disabled for this hardware -
        // so say so once rather than leaving the strips silently dark.
        foreach (var (vid, pid) in seenVidPid)
        {
            if (livePaths.Count > 0 && _hub.Controllers.Count > 0) break;
            if (_warnedUnmatched.Add((vid, pid)))
            {
                ServiceLog.Warn($"[nollie] {NollieProtocol.Lookup(vid, pid)!.Name} ({vid:X4}:{pid:X4}) is present but no interface matched the RGB filter; it will not be driven");
            }
        }

        foreach (var controller in _hub.Controllers)
        {
            if (IsSimulated(controller))
            {
                // Never on the bus; it leaves through DetachSimulated.
                continue;
            }
            if (!livePaths.Contains(controller.Path))
            {
                _hub.Detach(controller.DeviceId);
                changed = true;
                ServiceLog.Info($"[nollie] detached {controller.Spec.Name} id={controller.DeviceId} (no longer present)");
            }
            else if (controller.ConsecutiveWriteFailures >= MaxConsecutiveFailures)
            {
                _hub.Detach(controller.DeviceId);
                changed = true;
                ServiceLog.Warn($"[nollie] detached {controller.Spec.Name} id={controller.DeviceId} after {controller.ConsecutiveWriteFailures} failed writes; will reattach on the next poll");
            }
        }

        return changed;
    }

    /// <summary>Everything a newly present board gets: its persisted-state cleanup, port seeds and standalone settings.</summary>
    private void Attach(NollieController controller)
    {
        var spec = controller.Spec;
        _hub.Attach(controller);
        DropBundledChannelState(controller);
        SeedPorts(controller);
        NollieStandalone.Apply(controller, _store.Load());
        ServiceLog.Info($"[nollie] attached {spec.Name} ({spec.Channels}ch, max {spec.MaxLedsPerChannel} LEDs/ch) sn={(string.IsNullOrEmpty(controller.Serial) ? "-" : controller.Serial)} id={controller.DeviceId}");
    }

    /// <summary>True while the Nexus Control switch for Nollie is on; a simulated board is refused otherwise, like a real one.</summary>
    public bool ControlEnabled => _gate.IsEnabled(HandlerId);

    /// <summary>
    /// Dev tools: attaches a board with nothing behind it, so the cards, the
    /// device page and the standalone settings can be exercised without
    /// hardware. One per model; attaching the same model again is a no-op.
    /// False for an unknown model or while Nexus Control is off.
    /// </summary>
    public bool AttachSimulated(int vendorId, int productId)
    {
        var spec = NollieProtocol.Lookup(vendorId, productId);
        if (spec is null || !ControlEnabled) return false;
        var device = new SimulatedNollieDevice(spec);
        if (_hub.HasPath(device.Path)) return true;
        Attach(new NollieController(device, spec));
        _lighting.OnHubStateUpdated();
        return true;
    }

    /// <summary>
    /// Dev tools: drops every simulated board and everything it wrote to
    /// settings, so the next attach seeds fresh instead of inheriting edits
    /// made against a board that was never there.
    /// </summary>
    public void DetachSimulated()
    {
        var dropped = new List<NollieController>();
        foreach (var controller in _hub.Controllers)
        {
            if (!IsSimulated(controller)) continue;
            _hub.Detach(controller.DeviceId);
            dropped.Add(controller);
            ServiceLog.Info($"[nollie] detached simulated {controller.Spec.Name} id={controller.DeviceId}");
        }
        if (dropped.Count == 0) return;
        _store.Update(s =>
        {
            foreach (var controller in dropped)
            {
                foreach (var port in controller.Spec.Ports)
                    DropPortState(s, NollieLightingDeviceProvider.PortId(controller.DeviceId, port));
                s.Devices.Nollie.Standalone.Remove(controller.DeviceId);
            }
        });
        _lighting.OnHubStateUpdated();
    }

    /// <summary>Everything persisted under one port id: its count, chain, partition and the per-zone state of its cards.</summary>
    private static void DropPortState(NexusSettings s, string id)
    {
        var zoneIds = new List<string> { id };
        if (s.Devices.ZonePartitions.Remove(id, out var partition))
        {
            for (var i = 0; i < partition.Count; i++)
                zoneIds.Add(Nexus.Service.Lighting.Zones.ZoneResolution.CustomZoneId(id, i));
        }
        s.Devices.ZoneLedCounts.Remove(id);
        s.Devices.PortChains.Remove(Nexus.Service.Lighting.Zones.ZoneResolution.ChainKey(id, 0));
        s.Devices.DeviceLedOverrides.Remove(id);
        s.Devices.DeviceAspectRatios.Remove(id);
        Nexus.Service.Lighting.Zones.ZoneStateDrop.Drop(s, zoneIds);
        foreach (var zoneId in zoneIds) s.Lighting.DeviceNames.Remove(zoneId);
    }

    /// <summary>
    /// A build that listed the Strimer channels one card each left per-channel
    /// state under ids the port model no longer resolves. Nothing reads it
    /// again, and a chain wired lane by lane has no port equivalent, so it is
    /// dropped here rather than left behind; the port then seeds fresh.
    /// </summary>
    private void DropBundledChannelState(NollieController controller)
    {
        var ids = new List<string>();
        var counts = _store.Load().Devices.ZoneLedCounts;
        foreach (var port in controller.Spec.Ports)
        {
            if (port.Lanes == 1) continue;
            for (var lane = 0; lane < port.Lanes; lane++)
            {
                var id = $"{controller.DeviceId}:ch{port.FirstChannel + lane}";
                if (counts.ContainsKey(id)) ids.Add(id);
            }
        }
        if (ids.Count == 0) return;

        _store.Update(s =>
        {
            foreach (var id in ids) DropPortState(s, id);
        });
        ServiceLog.Info($"[nollie] dropped per-channel state for {ids.Count} Strimer channel(s) on {controller.DeviceId}; the connector is one port now");
    }

    /// <summary>
    /// Writes only ports with no persisted count, so a port the user
    /// deliberately set to 0 (or whose chain they cleared) stays that way
    /// across replugs. A plain header seeds a bare count; a Strimer
    /// connector seeds its cable as a one-product chain, which is what the
    /// Assign Devices editor then shows and lets the user swap.
    /// </summary>
    private void SeedPorts(NollieController controller)
    {
        var missing = new List<NolliePort>();
        var counts = _store.Load().Devices.ZoneLedCounts;
        foreach (var port in controller.Spec.Ports)
        {
            if (!counts.ContainsKey(NollieLightingDeviceProvider.PortId(controller.DeviceId, port))) missing.Add(port);
        }
        if (missing.Count == 0) return;

        var seed = Math.Min(DefaultChannelLedCount, controller.Spec.MaxLedsPerChannel);
        var seededCounts = 0;
        var wiredProducts = 0;
        _store.Update(s =>
        {
            foreach (var port in missing)
            {
                var id = NollieLightingDeviceProvider.PortId(controller.DeviceId, port);
                if (s.Devices.ZoneLedCounts.ContainsKey(id)) continue;
                if (port.DefaultProductKey is not null
                    && Nexus.Service.Lighting.Zones.PortChainWriter.WireProduct(s, id, port.DefaultProductKey, port.MaxLedCount))
                {
                    wiredProducts++;
                    continue;
                }
                s.Devices.ZoneLedCounts[id] = Math.Min(seed, port.MaxLedCount);
                seededCounts++;
            }
        });
        var wired = wiredProducts > 0 ? $", pre-wired {wiredProducts} Strimer port(s)" : "";
        ServiceLog.Info($"[nollie] seeded {seededCounts} unconfigured port(s) on {controller.DeviceId} at {seed} LEDs{wired}");
    }

    /// <summary>
    /// Composite parts expose several interfaces, so the vendor usage page/usage
    /// picks the RGB one; the reference driver matches those by interface number
    /// and the rest by VID/PID alone, so a non-composite part is accepted on
    /// report length only. Length also separates the 65-byte chunked parts from
    /// the 1025-byte wide ones.
    /// </summary>
    private static bool IsRgbInterface(HidDeviceInfo info, NollieDevice spec)
    {
        if (spec.InterfaceNumber >= 0
            && (info.UsagePage != NollieProtocol.VendorUsagePage || info.Usage != NollieProtocol.VendorUsage))
        {
            return false;
        }
        var expected = spec.Transport == NollieTransport.Wide
            ? NollieProtocol.WideReportSize
            : NollieProtocol.ChunkedReportSize;
        return info.OutputReportByteLength <= 0 || info.OutputReportByteLength == expected;
    }
}
