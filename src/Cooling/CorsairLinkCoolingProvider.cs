using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the Corsair iCUE LINK System Hub into the fan-control subsystem.
/// Channel IDs are "corsair:ch{N}" (other hubs: "corsair:{serial}:ch{N}") where N
/// is the 1-based daisy-chain position of a speed-capable device. Temperature
/// probes (QX fans, liquid loops, pump blocks) are curve-input sources "{channel id}:temp".
///
/// The hub has no motherboard-PWM fallback: in software mode every speed channel
/// is driven by the host or it runs the hub's default. So uncontrolled channels
/// are held at a safe default duty each poll rather than "released to BIOS".
/// </summary>
public sealed class CorsairLinkCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private const int DefaultFanDuty = 50;
    private const int DefaultPumpDuty = 70;
    // Pump/AIO min-duty floors mirror OpenLinkHub (lsh.go): a manual write floors
    // any pump at 50; a curve write floors a standard AIO at 70, a Titan AIO at 31,
    // and a standalone pump (XD5/XD6 etc.) at 30. Fans are never floored.
    private const int PumpManualFloor = 50;
    private const int AioCurveFloor = 70;
    private const int TitanAioCurveFloor = 31;
    private const int PumpCurveFloor = 30;
    private const int TitanAioType = 17;

    private readonly CorsairLinkHubs _hubs;

    private readonly object _ctrlLock = new();
    // Keyed by channel id, so a duty survives its hub's reconnect.
    private readonly Dictionary<string, int> _pendingDuty = new(StringComparer.Ordinal);
    private readonly HashSet<string> _softwareControlled = new(StringComparer.Ordinal);

    public CorsairLinkCoolingProvider(CorsairLinkHubs hubs)
    {
        _hubs = hubs;
    }

    private static string HubName(CorsairLinkHub hub) =>
        hub.Number == 1 ? "Corsair iCUE LINK" : $"Corsair iCUE LINK (Hub {hub.Number})";

    public static bool IsCorsairId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("corsair:", StringComparison.Ordinal);

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var result = new List<FanChannel>();
        foreach (var hub in _hubs.All)
        {
            if (!hub.IsConnected) continue;
            foreach (var d in hub.State.Devices)
            {
                if (!d.HasSpeed) continue;
                var id = hub.ChannelId(d.Channel);
                bool sw;
                int duty;
                lock (_ctrlLock)
                {
                    sw = _softwareControlled.Contains(id);
                    duty = _pendingDuty.TryGetValue(id, out var p) ? p : DefaultDutyFor(d);
                }
                var isPump = d.Class is CorsairLinkClass.Pump or CorsairLinkClass.Aio;
                result.Add(new FanChannel
                {
                    Id = id,
                    Name = d.Name,
                    DutyPercent = duty,
                    Rpm = d.Rpm >= 0 ? d.Rpm : 0,
                    Mode = sw ? FanModes.Manual : FanModes.Auto,
                    Kind = isPump ? FanKinds.Pump : FanKinds.Fan,
                    DeviceId = hub.DeviceId,
                    DeviceName = HubName(hub),
                    PortLabel = hub.PortLabel(d.Channel),
                    FanModel = d.Name,
                });
            }
        }
        return result;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var result = new List<TemperatureSource>();
        foreach (var hub in _hubs.All)
        {
            if (!hub.IsConnected) continue;
            foreach (var d in hub.State.Devices)
            {
                if (!d.HasTemperature || float.IsNaN(d.TempC)) continue;
                result.Add(new TemperatureSource
                {
                    Id = $"{hub.ChannelId(d.Channel)}:temp",
                    Name = $"{d.Name} probe ({hub.PortLabel(d.Channel)})",
                    Category = "Hub",
                    Value = d.TempC,
                    DeviceId = hub.DeviceId,
                    DeviceName = HubName(hub),
                });
            }
        }
        return result;
    }

    public float? ReadTemperature(string sensorId)
    {
        if (!_hubs.TryResolve(sensorId, out var hub, out var ch) || !hub.IsConnected) return null;
        foreach (var d in hub.State.Devices)
        {
            if (d.Channel != ch) continue;
            return d.HasTemperature && !float.IsNaN(d.TempC) ? d.TempC : null;
        }
        return null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = ApplyPumpFloor(channelId, Math.Clamp(dutyPercent, 0, 100), curve: false);
        ApplyChannelWrite(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        ApplyChannelWrite(channelId, ApplyPumpFloor(channelId, Math.Clamp(dutyPercent, 0, 100), curve: true));
    }

    public void ReleaseFan(string channelId)
    {
        if (!IsCorsairId(channelId)) return;
        lock (_ctrlLock)
        {
            _softwareControlled.Remove(channelId);
            _pendingDuty.Remove(channelId);
        }
        if (_hubs.TryResolve(channelId, out var hub, out _)) PushAll(hub);
    }

    public void ReleaseAll()
    {
        lock (_ctrlLock)
        {
            _softwareControlled.Clear();
            _pendingDuty.Clear();
        }
        foreach (var hub in _hubs.All) PushAll(hub);
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var components = new List<CoolingComponent>();
        foreach (var hub in _hubs.All)
        {
            if (!hub.IsConnected) continue;
            var devices = new List<CoolingDevice>();
            foreach (var d in hub.State.Devices)
            {
                if (!d.HasSpeed) continue;
                var id = hub.ChannelId(d.Channel);
                int duty;
                lock (_ctrlLock) duty = _pendingDuty.TryGetValue(id, out var p) ? p : DefaultDutyFor(d);
                devices.Add(new CoolingDevice
                {
                    Id = id,
                    Name = d.Name,
                    Type = d.Class is CorsairLinkClass.Pump or CorsairLinkClass.Aio ? "Pump" : "Fan",
                    Rpm = d.Rpm >= 0 ? d.Rpm : 0,
                    Temperature = float.IsNaN(d.TempC) ? null : d.TempC,
                    Pwm = duty,
                });
            }
            components.Add(new CoolingComponent
            {
                Id = hub.HubId,
                Name = HubName(hub),
                Type = "CorsairLink",
                Devices = devices,
            });
        }
        return components;
    }

    /// <summary>
    /// Re-push every speed channel's duty: software-controlled channels hold their
    /// commanded duty, the rest are held at a safe default. Called each poll so a
    /// fan never drifts to the hub's software-mode default after takeover.
    /// </summary>
    public void ReassertControl(CorsairLinkHub hub)
    {
        if (!hub.IsConnected) return;
        PushAll(hub);
    }

    // ── internals ──

    private void ApplyChannelWrite(string channelId, int dutyPercent)
    {
        if (!IsCorsairId(channelId)) return;
        if (!_hubs.TryResolve(channelId, out var hub, out _) || !hub.IsConnected)
        {
            ServiceLog.Warn($"[corsair-cooling] write to {channelId} dropped: hub not connected");
            return;
        }
        lock (_ctrlLock)
        {
            _softwareControlled.Add(channelId);
            _pendingDuty[channelId] = dutyPercent;
        }
        PushAll(hub);
    }

    // Build one duty packet for every speed-capable channel and send it. The hub
    // sets all channels in a single write, so there is no per-channel re-entry.
    private void PushAll(CorsairLinkHub hub)
    {
        if (!hub.IsConnected || hub.Redetecting) return;
        var items = new List<(int channel, int duty)>();
        lock (_ctrlLock)
        {
            foreach (var d in hub.State.Devices)
            {
                if (!d.HasSpeed) continue;
                var duty = _pendingDuty.TryGetValue(hub.ChannelId(d.Channel), out var p) ? p : DefaultDutyFor(d);
                items.Add((d.Channel, duty));
            }
        }
        if (items.Count == 0) return;
        if (!hub.SetDuties(items))
        {
            ServiceLog.Warn("[corsair-cooling] SetDuties returned false");
        }
    }

    private static int DefaultDutyFor(CorsairLinkDevice d) =>
        d.Class is CorsairLinkClass.Pump or CorsairLinkClass.Aio ? DefaultPumpDuty : DefaultFanDuty;

    // Raise a pump/AIO duty to its safe minimum so a liquid cooler never runs too
    // slow. Fans pass through unchanged. curve=false is a manual user write.
    private int ApplyPumpFloor(string channelId, int duty, bool curve)
    {
        if (!_hubs.TryResolve(channelId, out var hub, out var ch) || !hub.IsConnected) return duty;
        foreach (var d in hub.State.Devices)
        {
            if (d.Channel != ch) continue;
            if (d.Class is not (CorsairLinkClass.Pump or CorsairLinkClass.Aio)) return duty;
            var floor = !curve ? PumpManualFloor
                : d.Class == CorsairLinkClass.Aio ? (d.Type == TitanAioType ? TitanAioCurveFloor : AioCurveFloor)
                : PumpCurveFloor;
            return Math.Max(duty, floor);
        }
        return duty;
    }
}
