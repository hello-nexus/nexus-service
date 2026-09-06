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

    /// <summary>Nexus Control gate id. No IDeviceHandler is registered yet, so the gate reads its brand default (on).</summary>
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
                        _hub.DetachAll();
                        ServiceLog.Info("[nollie] released (Nexus Control off)");
                        _lighting.OnHubStateUpdated();
                    }
                }
                // IsConnected first, so a removal is still noticed even if the bus
                // check misses a device that is already attached.
                else if (_hub.IsConnected || AnyNolliePresent())
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

        _hub.DetachAll();
    }

    /// <summary>Attaches newly present controllers and drops gone or wedged ones. Returns true when the set changed.</summary>
    private bool Reconcile()
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

            var controller = new NollieController(device, spec);
            _hub.Attach(controller);
            SeedChannelCounts(controller);
            changed = true;
            ServiceLog.Info($"[nollie] attached {spec.Name} ({spec.Channels}ch, max {spec.MaxLedsPerChannel} LEDs/ch) sn={(string.IsNullOrEmpty(controller.Serial) ? "-" : controller.Serial)} id={controller.DeviceId}");
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

    /// <summary>Writes only absent keys, so a channel the user deliberately set to 0 stays 0 across replugs.</summary>
    private void SeedChannelCounts(NollieController controller)
    {
        var seed = Math.Min(DefaultChannelLedCount, controller.Spec.MaxLedsPerChannel);
        var missing = new List<string>();
        var counts = _store.Load().Devices.ZoneLedCounts;
        for (var ch = 0; ch < controller.Spec.Channels; ch++)
        {
            var id = NollieLightingDeviceProvider.ChannelId(controller.DeviceId, ch);
            if (!counts.ContainsKey(id)) missing.Add(id);
        }
        if (missing.Count == 0) return;

        _store.Update(s =>
        {
            foreach (var id in missing)
            {
                if (!s.Devices.ZoneLedCounts.ContainsKey(id)) s.Devices.ZoneLedCounts[id] = seed;
            }
        });
        ServiceLog.Info($"[nollie] seeded {missing.Count} unconfigured channel(s) on {controller.DeviceId} at {seed} LEDs");
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
