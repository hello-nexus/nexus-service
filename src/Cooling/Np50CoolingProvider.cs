using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Models.Cooling;
using Qos.Service.Peripherals.Hyte.Np50;

namespace Qos.Service.Cooling;

/// <summary>
/// Bridges the NP50 hub into the cooling subsystem.
///
/// <para>
/// Channel IDs are namespaced per device + port so they stay stable across
/// reboots and hot-plugs:
/// </para>
/// <code>
///   np50:&lt;serial&gt;:port&lt;N&gt;:dev&lt;M&gt;   — one of the daisy-chained Nexus Link fans
///   np50:&lt;serial&gt;:legacy            — the single legacy 4-pin PWM channel
/// </code>
///
/// <para>
/// All writes are batched at the port level — the spec only accepts
/// per-port "set all fan speeds" frames, not individual fan addressing. We
/// keep a per-port pending-write cache so a curve engine call for "fan N"
/// updates only that fan's slot in the next port-level write.
/// </para>
/// </summary>
public sealed class Np50CoolingProvider : IFanControlProvider, ICoolingProvider
{
    private readonly Np50Hub _hub;

    // Per-port last-applied duty per fan slot. Index 0 → port 1, etc.
    // The inner array is exactly Np50Protocol.MaxDevicesPerPort entries; fan
    // slots beyond the connected fan list are forced to 0% in the write.
    private readonly int[][] _pendingPortDuties =
    {
        new int[Np50Protocol.MaxDevicesPerPort],
        new int[Np50Protocol.MaxDevicesPerPort],
        new int[Np50Protocol.MaxDevicesPerPort],
    };

    // Pending duty for the legacy 4-pin channel. Tracked separately because
    // it uses a different protocol command.
    private int _pendingLegacyDuty;

    // Cached "is hub in software mode?" so we don't send the mode-switch
    // command on every fan write. Hub may revert to motherboard mode if the
    // 5s heartbeat lapses; we re-assert on reconnect.
    private bool _softwareModeAsserted;
    private string _lastConnectedSerial = "";

    // Channels under user-set software control. Used to surface Mode="Manual"
    // back to the panel — without it the panel snaps a freshly Manual-clicked
    // hub fan back to BIOS on the next cooling-topic refresh (see
    // CoolingView.refreshCoolingConfig which derives Manual purely from
    // FanChannel.Mode for channels not bound to a curve).
    private readonly HashSet<string> _softwareControlled = new();

    public Np50CoolingProvider(Np50Hub hub)
    {
        _hub = hub;
    }

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected) return Array.Empty<FanChannel>();
        var result = new List<FanChannel>();
        var serial = _hub.State.Serial;
        var deviceId = _hub.DeviceId;

        // Legacy 4-pin first so it groups at the top of the device's UI block.
        var legacyId = $"np50:{serial}:legacy";
        result.Add(new FanChannel
        {
            Id = legacyId,
            Name = "Legacy 4-pin",
            DutyPercent = _pendingLegacyDuty,
            Rpm = _hub.State.HubInfo.LegacyFanRpm,
            Mode = _softwareControlled.Contains(legacyId) ? FanModes.Manual : FanModes.Auto,
            DeviceId = deviceId,
            DeviceName = Np50Hub.ProductName,
            PortLabel = "Legacy 4-pin",
            FanModel = null,
            Orientation = null,
        });

        foreach (var port in _hub.State.Ports)
        {
            foreach (var fan in port.Devices)
            {
                // Only FP12 is a real fan. LS10/LS30 are light strips —
                // they carry a temp probe (surfaced as a sensor below) but
                // no fan blade and no RPM, so they don't belong here.
                if (!IsFanModule(fan.Model)) continue;

                var id = $"np50:{serial}:port{port.Index}:dev{fan.Index}";
                var slot = fan.Index - 1;
                var duty = slot >= 0 && slot < Np50Protocol.MaxDevicesPerPort
                    ? _pendingPortDuties[port.Index - 1][slot]
                    : 0;
                result.Add(new FanChannel
                {
                    Id = id,
                    Name = $"{fan.Model} (Port {port.Index} #{fan.Index})",
                    DutyPercent = duty,
                    Rpm = fan.Rpm,
                    Mode = _softwareControlled.Contains(id) ? FanModes.Manual : FanModes.Auto,
                    DeviceId = deviceId,
                    DeviceName = Np50Hub.ProductName,
                    PortLabel = $"Port {port.Index}",
                    FanModel = fan.Model,
                    Orientation = fan.Orientation,
                });
            }
        }

        return result;
    }

    /// <summary>True for Nexus Link modules that carry an actual fan blade. LS-series are light strips only.</summary>
    private static bool IsFanModule(string model) =>
        string.Equals(model, "FP12", System.StringComparison.Ordinal);

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        if (!_hub.IsConnected) return Array.Empty<TemperatureSource>();
        var result = new List<TemperatureSource>();
        var serial = _hub.State.Serial;
        var deviceId = _hub.DeviceId;

        // Optional cable temp on the legacy 4-pin path.
        if (_hub.State.HubInfo.CableTempC is { } cableC)
        {
            result.Add(new TemperatureSource
            {
                Id = $"np50:{serial}:legacy:cable",
                Name = "NP50 cable probe",
                Category = "Hub",
                Value = cableC,
                DeviceId = deviceId,
            });
        }

        // Per-fan probes from each LS10/LS30 module that reports one.
        foreach (var port in _hub.State.Ports)
        {
            foreach (var fan in port.Devices)
            {
                if (fan.TempC is not { } t) continue;
                result.Add(new TemperatureSource
                {
                    Id = $"np50:{serial}:port{port.Index}:dev{fan.Index}:temp",
                    Name = $"{fan.Model} probe (Port {port.Index} #{fan.Index})",
                    Category = "Hub",
                    Value = t,
                    DeviceId = deviceId,
                });
            }
        }

        return result;
    }

    public float? ReadTemperature(string sensorId)
    {
        if (!sensorId.StartsWith("np50:", StringComparison.Ordinal)) return null;
        if (!_hub.IsConnected) return null;
        foreach (var s in GetTemperatureSources())
            if (s.Id == sensorId) return s.Value;
        return null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyChannelWrite(channelId, clamped, isUserIntent: true);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyChannelWrite(channelId, clamped, isUserIntent: false);
    }

    public void ReleaseFan(string channelId)
    {
        // NP50 cooling mode is hub-level; flip back to Motherboard only when
        // every channel on the hub has been released, so a wire-DnD
        // disconnect on one fan doesn't yank PWM out from under sibling
        // fans that are still actively driven.
        if (!channelId.StartsWith("np50:", StringComparison.Ordinal)) return;
        _softwareControlled.Remove(channelId);
        if (!_hub.IsConnected) return;
        if (_softwareControlled.Count == 0)
        {
            _hub.SetDesiredCoolingMode(Np50Protocol.ModeMotherboard);
            _softwareModeAsserted = false;
        }
    }

    public void ReleaseAll()
    {
        _softwareControlled.Clear();
        if (!_hub.IsConnected) return;
        _hub.SetDesiredCoolingMode(Np50Protocol.ModeMotherboard);
        _softwareModeAsserted = false;
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        // Calibration is meaningful for the LHM-driven Windows path, where
        // RPM range varies wildly per fan. NP50 fans report stable RPM at
        // known PWM, and there's no obvious win in ramping them on a panel
        // load. Defer; return empty so the route call is a no-op.
        return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        if (!_hub.IsConnected) return Array.Empty<CoolingComponent>();
        var serial = _hub.State.Serial;
        var deviceId = _hub.DeviceId;

        var devices = new List<CoolingDevice>
        {
            new()
            {
                Id = $"np50:{serial}:legacy",
                Name = "Legacy 4-pin",
                Type = "Fan",
                Rpm = _hub.State.HubInfo.LegacyFanRpm,
                Pwm = _pendingLegacyDuty,
            },
        };
        foreach (var port in _hub.State.Ports)
        {
            foreach (var fan in port.Devices)
            {
                devices.Add(new CoolingDevice
                {
                    Id = $"np50:{serial}:port{port.Index}:dev{fan.Index}",
                    Name = $"{fan.Model} (Port {port.Index} #{fan.Index})",
                    Type = "Fan",
                    Rpm = fan.Rpm,
                    Temperature = fan.TempC,
                });
            }
        }

        return new[]
        {
            new CoolingComponent
            {
                Id = deviceId,
                Name = Np50Hub.ProductName,
                Type = "NP50",
                Devices = devices,
            },
        };
    }

    // ── Internals ──

    private void ApplyChannelWrite(string channelId, int dutyPercent, bool isUserIntent)
    {
        _ = isUserIntent; // NP50 doesn't distinguish; future ManualSpeeds wiring can.
        if (!channelId.StartsWith("np50:", StringComparison.Ordinal)) return;
        if (!_hub.IsConnected)
        {
            System.Console.Error.WriteLine($"[np50-cooling] write to {channelId} dropped: hub not connected");
            return;
        }
        // Driving a channel implies software control; record so the next
        // GetFanChannels reports Mode="Manual" and the panel doesn't snap
        // the user's Manual selection back to BIOS on the cooling-topic
        // refresh.
        _softwareControlled.Add(channelId);

        // Latch desired mode to Software so the heartbeat re-asserts it on
        // every tick (firmware 2.0.3.1 occasionally needs the mode-switch
        // command repeated to actually flip). SetDesiredCoolingMode also
        // sends the first attempt immediately, so the next duty write often
        // lands while the hub is already in Software mode.
        _hub.SetDesiredCoolingMode(Np50Protocol.ModeSoftware);
        var modeOk = true;

        if (channelId.EndsWith(":legacy", StringComparison.Ordinal))
        {
            _pendingLegacyDuty = dutyPercent;
            var writeOk = _hub.SetLegacyFanSpeed(dutyPercent);
            System.Console.Error.WriteLine(
                $"[np50-cooling] legacy 4-pin -> {dutyPercent}% (modeOk={modeOk} writeOk={writeOk} hubMode={_hub.State.HubInfo.CoolingMode})");
            return;
        }

        // Expect np50:<serial>:portN:devM
        if (!TryParsePortDev(channelId, out var port, out var dev)) return;
        if (port < 1 || port > Np50Protocol.PortCount) return;
        if (dev < 1 || dev > Np50Protocol.MaxDevicesPerPort) return;

        _pendingPortDuties[port - 1][dev - 1] = dutyPercent;
        // The spec only accepts a single all-fans-on-this-port frame; resend
        // the whole port. Per the heartbeat-driven model this happens often
        // anyway, so we don't try to dedupe-by-change here.
        var portList = _pendingPortDuties[port - 1].ToArray();
        // Trim to the actual connected fan count so the spec-required
        // device-count byte matches what the hub actually has plugged in.
        var connected = _hub.State.Ports.FirstOrDefault(p => p.Index == port)?.Devices.Count ?? 0;
        if (connected > 0 && connected < portList.Length)
        {
            // Zero out trailing slots so we don't claim to drive non-existent fans.
            for (var i = connected; i < portList.Length; i++) portList[i] = 0;
        }
        _hub.SetPortFanSpeeds(port, portList);
    }

    // Kept for ReleaseFan/ReleaseAll to track that software-mode was once
    // asserted. ApplyChannelWrite now re-asserts on every write directly so
    // a 5s-heartbeat-lapse doesn't strand the hub in motherboard mode.
    private void AssertSoftwareModeIfNeeded()
    {
        if (_softwareModeAsserted && _lastConnectedSerial == _hub.State.Serial) return;
        if (_hub.SetCoolingMode(Np50Protocol.ModeSoftware))
        {
            _softwareModeAsserted = true;
            _lastConnectedSerial = _hub.State.Serial;
        }
    }

    private static bool TryParsePortDev(string channelId, out int port, out int dev)
    {
        port = 0;
        dev = 0;
        // channelId shape: "np50:<serial>:port<N>:dev<M>"
        var lastColon = channelId.LastIndexOf(':');
        if (lastColon < 0) return false;
        var devSegment = channelId.AsSpan(lastColon + 1);
        if (!devSegment.StartsWith("dev")) return false;
        if (!int.TryParse(devSegment.Slice(3), out dev)) return false;

        var middle = channelId.AsSpan(0, lastColon);
        var prevColon = middle.LastIndexOf(':');
        if (prevColon < 0) return false;
        var portSegment = middle.Slice(prevColon + 1);
        if (!portSegment.StartsWith("port")) return false;
        if (!int.TryParse(portSegment.Slice(4), out port)) return false;
        return true;
    }
}
