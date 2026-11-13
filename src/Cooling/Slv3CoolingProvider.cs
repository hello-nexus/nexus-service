using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the SLV3 wireless fan chains into the fan-control subsystem. Each
/// bound chain (identified by its RF MAC) can carry up to
/// <see cref="Slv3Protocol.PortsPerRecord"/> physical fans; each occupied
/// port is a curve-assignable channel "lianli-wireless:{mac}:port{N}".
///
/// Duty and mode read straight off the RX device-list telemetry
/// (<see cref="Slv3Hub.State"/>): the firmware itself echoes back the
/// motherboard-sync sentinel,
/// so unlike the wired hub - which has no PWM readback - no separate
/// software-controlled bookkeeping is needed to know Auto vs Manual.
/// A bound HydroShift II AIO is one pump channel "lianli-wireless:{mac}:pump"
/// whose duty is the one Nexus drives (<see cref="Slv3Hub.GetPumpDuty"/>),
/// plus its coolant temperature as a "Cooler" sensor, and a
/// "{mac}:fans" channel for its radiator fans: the AIO applies one duty to all of
/// them, so that duty is written to every fan slot and the pump slot is never a target.
/// </summary>
public sealed class Slv3CoolingProvider : IFanControlProvider, ICoolingProvider
{
    private const string IdPrefix = "lianli-wireless:";
    private const string PumpSuffix = ":pump";
    private const string FansSuffix = ":fans";
    private const string FansName = "HydroShift II Fans";
    private const string CoolantSuffix = ":coolant";
    private const string AioName = "Lian Li HydroShift II";
    private const string PumpName = "HydroShift II Pump";

    private readonly Slv3Hub _hub;

    public Slv3CoolingProvider(Slv3Hub hub)
    {
        _hub = hub;
    }

    public static bool IsSlv3Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    // A bound Strimer Wireless cable is RGB-only: it has no fan ports, so it
    // gets no cooling channels (the lighting provider owns it). A HydroShift II
    // AIO reports no fans, and its fourth PWM slot is the pump.
    private static bool HasFanPorts(Slv3FanInfo fan) =>
        fan.BoundToUs
        && !Slv3Protocol.IsStrimerDevType((byte)fan.DevType)
        && !Slv3Protocol.IsHydroShiftDevType((byte)fan.DevType);

    private static bool IsBoundHydroShift(Slv3FanInfo fan) =>
        fan.BoundToUs && Slv3Protocol.IsHydroShiftDevType((byte)fan.DevType);

    // The AIO's radiator fans are ports 0..2; slot 3 is the pump and is never a port channel.
    private static int RadiatorFanCount(Slv3FanInfo fan) =>
        Math.Clamp(fan.FanCount, 0, Slv3Protocol.HydroShiftPumpPort);

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected) return Array.Empty<FanChannel>();
        var result = new List<FanChannel>();
        foreach (var fan in _hub.State.Fans)
        {
            if (IsBoundHydroShift(fan))
            {
                var duty = _hub.GetPumpDuty(fan.Mac);
                result.Add(new FanChannel
                {
                    Id = PumpId(fan.Mac),
                    Name = PumpName,
                    DutyPercent = duty ?? 0,
                    Rpm = PumpRpmOf(fan),
                    Mode = duty is null ? FanModes.Auto : FanModes.Manual,
                    Kind = FanKinds.Pump,
                    DeviceId = DeviceId(fan.Mac),
                    DeviceName = AioName,
                    PortLabel = "Pump",
                });
                if (RadiatorFanCount(fan) > 0)
                {
                    var fansDuty = _hub.GetPortDuty(fan.Mac, 0);
                    result.Add(new FanChannel
                    {
                        Id = FansId(fan.Mac),
                        Name = FansName,
                        DutyPercent = fansDuty ?? _hub.State.MotherboardPwmPercent ?? 0,
                        Rpm = RadiatorRpm(fan),
                        Mode = fansDuty is null ? FanModes.Auto : FanModes.Manual,
                        MinDuty = Slv3Protocol.HydroShiftFanMinDuty,
                        DeviceId = DeviceId(fan.Mac),
                        DeviceName = AioName,
                        PortLabel = "Fans",
                    });
                }
                continue;
            }
            if (!HasFanPorts(fan)) continue;
            var portCount = EffectivePortCount(fan);
            var rpmUnavailable = fan.FanCount <= 0;
            var deviceName = "Lian Li Wireless Fan";
            var minDuty = Slv3Protocol.MinDutyPercentFor(Slv3Protocol.ClassifyFanFamily((byte)fan.FanType));
            for (var port = 0; port < portCount; port++)
            {
                result.Add(new FanChannel
                {
                    Id = PortId(fan.Mac, port),
                    Name = $"Wireless Fan {port + 1}",
                    DutyPercent = PortDutyPercent(fan, port),
                    Rpm = PortRpm(fan, port),
                    Mode = PortPwm(fan, port) == Slv3Protocol.PwmFollowMotherboard ? FanModes.Auto : FanModes.Manual,
                    MinDuty = minDuty,
                    RpmUnavailable = rpmUnavailable,
                    DeviceId = DeviceId(fan.Mac),
                    DeviceName = deviceName,
                    PortLabel = $"Fan {port + 1}",
                });
            }
        }
        return result;
    }

    // Ports to expose for a bound chain. A controller that enumerates its fans
    // reports FanCount; one that does not (fan interlock tach line open) reports
    // 0 while still accepting PWM, so fall back to the controller's physical
    // port count and let the user drive them open-loop (no RPM read-back).
    private static int EffectivePortCount(Slv3FanInfo fan) =>
        fan.FanCount > 0 ? fan.FanCount : Slv3Protocol.PortsPerRecord;

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        if (!_hub.IsConnected) return Array.Empty<TemperatureSource>();
        List<TemperatureSource>? sources = null;
        foreach (var fan in _hub.State.Fans)
        {
            if (!IsBoundHydroShift(fan) || fan.CoolantTempC is not { } coolant) continue;
            (sources ??= new List<TemperatureSource>()).Add(new TemperatureSource
            {
                Id = $"{IdPrefix}{fan.Mac}{CoolantSuffix}",
                Name = "Liquid",
                Category = "Cooler",
                Value = coolant,
                DeviceId = DeviceId(fan.Mac),
                DeviceName = AioName,
            });
        }
        return sources is null ? Array.Empty<TemperatureSource>() : sources;
    }

    public float? ReadTemperature(string sensorId)
    {
        if (!TryParseSuffixed(sensorId, CoolantSuffix, out var mac) || !_hub.IsConnected) return null;
        foreach (var fan in _hub.State.Fans)
        {
            if (IsBoundHydroShift(fan) && string.Equals(fan.Mac, mac, StringComparison.OrdinalIgnoreCase))
            {
                return fan.CoolantTempC;
            }
        }
        return null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyWrite(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent) =>
        ApplyWrite(channelId, Math.Clamp(dutyPercent, 0, 100));

    // A cleared port target makes the hub send the follow-motherboard PWM byte again; the AIO pump is left out.
    public bool ReleaseRestoresHardwareControl(string channelId) =>
        TryParsePort(channelId, out _, out _) || TryParseSuffixed(channelId, FansSuffix, out _);

    public void ReleaseFan(string channelId)
    {
        if (TryParseSuffixed(channelId, PumpSuffix, out var aioMac))
        {
            _hub.SetPumpDuty(aioMac, null);
            return;
        }
        if (TryParseSuffixed(channelId, FansSuffix, out var aioFans))
        {
            SetRadiatorFans(aioFans, null);
            return;
        }
        if (!TryParsePort(channelId, out var mac, out var port)) return;
        _hub.SetPortDuty(mac, port, null);
    }

    // The pump and the radiator fans all go back to L-Connect defaults together.
    private void ReleaseHydroShift(Slv3FanInfo fan, bool releaseFans = true)
    {
        _hub.SetPumpDuty(fan.Mac, null);
        if (!releaseFans) return;
        SetRadiatorFans(fan.Mac, null);
    }

    // One duty for every radiator fan slot (the AIO applies a single duty to all three); the pump slot is never a target.
    private void SetRadiatorFans(string macHex, int? percent)
    {
        for (var port = 0; port < Slv3Protocol.HydroShiftPumpPort; port++)
        {
            _hub.SetPortDuty(macHex, port, percent);
        }
    }

    private static int RadiatorRpm(Slv3FanInfo fan)
    {
        var sum = 0;
        var spinning = 0;
        for (var port = 0; port < RadiatorFanCount(fan); port++)
        {
            var rpm = PortRpm(fan, port);
            if (rpm <= 0) continue;
            sum += rpm;
            spinning++;
        }
        return spinning == 0 ? 0 : sum / spinning;
    }

    public void ReleaseAll()
    {
        if (!_hub.IsConnected) return;
        foreach (var fan in _hub.State.Fans)
        {
            if (Slv3Protocol.IsHydroShiftDevType((byte)fan.DevType))
            {
                ReleaseHydroShift(fan);
                continue;
            }
            if (!HasFanPorts(fan)) continue;
            // EffectivePortCount, not FanCount, so a zero-count chain (whose
            // ports GetFanChannels exposes and the user can drive) is released
            // too; FanCount would iterate zero ports and leave it pinned.
            for (var port = 0; port < EffectivePortCount(fan); port++)
            {
                _hub.SetPortDuty(fan.Mac, port, null);
            }
        }
    }

    // Shutdown hand-back. A measured header duty of 0 cannot tell a missing cable from a real 0, and a
    // receiver with no header signal stops a port on the follow-motherboard byte, so ports Nexus drives
    // go back to the header only when it measures above zero; otherwise they keep the last duty (a bind
    // frame holds it without keepalives). A port with no target already follows the board. The AIO pump is released as usual.
    public void ReleaseAllKeepingDrivenPorts()
    {
        if (!_hub.IsConnected) return;
        var headerLive = _hub.State.MotherboardPwmPercent is > 0;
        foreach (var fan in _hub.State.Fans)
        {
            if (Slv3Protocol.IsHydroShiftDevType((byte)fan.DevType))
            {
                // Radiator fans follow the same header rule as any chain; the pump is always released.
                ReleaseHydroShift(fan, releaseFans: headerLive);
                continue;
            }
            if (!headerLive || !HasFanPorts(fan)) continue;
            for (var port = 0; port < EffectivePortCount(fan); port++)
            {
                _hub.SetPortDuty(fan.Mac, port, null);
            }
        }
    }

    // Neither RPM nor duty tracks a ramp reliably enough for a calibration
    // sweep to add value over the live telemetry already shown; matches the
    // wired hub / other first-party providers.
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
        if (!_hub.IsConnected) return Array.Empty<CoolingComponent>();
        var components = new List<CoolingComponent>();
        foreach (var fan in _hub.State.Fans)
        {
            if (IsBoundHydroShift(fan))
            {
                components.Add(new CoolingComponent
                {
                    Id = DeviceId(fan.Mac),
                    Name = AioName,
                    Type = "LianLiWireless",
                    Devices = new List<CoolingDevice>
                    {
                        new CoolingDevice
                        {
                            Id = PumpId(fan.Mac),
                            Name = PumpName,
                            Type = "Pump",
                            Rpm = PumpRpmOf(fan),
                            Pwm = _hub.GetPumpDuty(fan.Mac) ?? 0,
                        },
                    },
                });
                if (RadiatorFanCount(fan) > 0)
                {
                    components[^1].Devices.Add(new CoolingDevice
                    {
                        Id = FansId(fan.Mac),
                        Name = FansName,
                        Type = "Fan",
                        Rpm = RadiatorRpm(fan),
                        Pwm = _hub.GetPortDuty(fan.Mac, 0) ?? _hub.State.MotherboardPwmPercent ?? 0,
                    });
                }
                continue;
            }
            if (!HasFanPorts(fan)) continue;
            var portCount = EffectivePortCount(fan);
            var rpmUnavailable = fan.FanCount <= 0;
            var devices = new List<CoolingDevice>(portCount);
            for (var port = 0; port < portCount; port++)
            {
                devices.Add(new CoolingDevice
                {
                    Id = PortId(fan.Mac, port),
                    Name = $"Wireless Fan {port + 1}",
                    Type = "Fan",
                    Rpm = rpmUnavailable ? null : PortRpm(fan, port),
                    Pwm = PortDutyPercent(fan, port),
                });
            }
            components.Add(new CoolingComponent
            {
                Id = DeviceId(fan.Mac),
                Name = "Lian Li Wireless Fan",
                Type = "LianLiWireless",
                Devices = devices,
            });
        }
        return components;
    }

    // ── Internals ──

    // Persisted manual duties are replayed by CurveEngine as bound chains
    // surface in GetFanChannels; a chain never given a saved duty stays on
    // the hub's motherboard-sync default.

    private void ApplyWrite(string channelId, int dutyPercent)
    {
        if (TryParseSuffixed(channelId, PumpSuffix, out var aioMac))
        {
            if (!_hub.SetPumpDuty(aioMac, dutyPercent))
            {
                ServiceLog.Warn($"[lianli-wireless-cooling] SetPumpDuty {channelId} duty {dutyPercent} returned false");
            }
            return;
        }
        if (TryParseSuffixed(channelId, FansSuffix, out var aioFans))
        {
            SetRadiatorFans(aioFans, dutyPercent);
            return;
        }
        if (!TryParsePort(channelId, out var mac, out var port))
        {
            return;
        }
        if (!_hub.SetPortDuty(mac, port, dutyPercent))
        {
            ServiceLog.Warn($"[lianli-wireless-cooling] SetPortDuty {channelId} duty {dutyPercent} returned false");
        }
    }

    private static int PortPwm(Slv3FanInfo fan, int port) => port < fan.Pwm.Length ? fan.Pwm[port] : 0;

    // fans_pwm is the chain's echo of the bind-frame byte on the firmware's
    // 0..255 scale; the sentinel 6 (follow the motherboard header) has no
    // percent of its own and surfaces as the header duty the RX measured.
    private int PortDutyPercent(Slv3FanInfo fan, int port)
    {
        var raw = PortPwm(fan, port);
        return raw == Slv3Protocol.PwmFollowMotherboard
            ? _hub.State.MotherboardPwmPercent ?? 0
            : Slv3Protocol.DecodeDuty(raw);
    }

    private static int PortRpm(Slv3FanInfo fan, int port)
    {
        var rpm = port < fan.Rpm.Length ? fan.Rpm[port] : 0;
        return rpm >= 0 ? rpm : 0;
    }

    private static int PumpRpmOf(Slv3FanInfo fan) => PortRpm(fan, Slv3Protocol.HydroShiftPumpPort);

    private static string FansId(string macHex) => $"{IdPrefix}{macHex}{FansSuffix}";

    private static string PumpId(string macHex) => $"{IdPrefix}{macHex}{PumpSuffix}";

    private static bool TryParseSuffixed(string id, string suffix, out string macHex)
    {
        macHex = "";
        if (!IsSlv3Id(id) || !id.EndsWith(suffix, StringComparison.Ordinal)) return false;
        macHex = id.Substring(IdPrefix.Length, id.Length - IdPrefix.Length - suffix.Length);
        return macHex.Length == Slv3Protocol.MacLength * 2;
    }

    private static string DeviceId(string macHex) => $"{IdPrefix}{macHex}";

    private static string PortId(string macHex, int port) => $"{IdPrefix}{macHex}:port{port}";

    private static bool TryParsePort(string channelId, out string macHex, out int port)
    {
        macHex = "";
        port = 0;
        if (!IsSlv3Id(channelId)) return false;
        var rest = channelId.AsSpan(IdPrefix.Length);
        var marker = rest.LastIndexOf(":port");
        if (marker < 0) return false;
        macHex = rest.Slice(0, marker).ToString();
        return int.TryParse(rest.Slice(marker + 5), out port)
            && port >= 0 && port < Slv3Protocol.PortsPerRecord;
    }
}
