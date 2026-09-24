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
/// </summary>
public sealed class Slv3CoolingProvider : IFanControlProvider, ICoolingProvider
{
    private const string IdPrefix = "lianli-wireless:";

    private readonly Slv3Hub _hub;

    public Slv3CoolingProvider(Slv3Hub hub)
    {
        _hub = hub;
    }

    public static bool IsSlv3Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    // A bound Strimer Wireless cable is RGB-only: it has no fan ports, so it
    // gets no cooling channels (the lighting provider owns it).
    private static bool HasFanPorts(Slv3FanInfo fan) =>
        fan.BoundToUs && !Slv3Protocol.IsStrimerDevType((byte)fan.DevType);

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected) return Array.Empty<FanChannel>();
        var result = new List<FanChannel>();
        foreach (var fan in _hub.State.Fans)
        {
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

    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();

    public float? ReadTemperature(string sensorId) => null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyWrite(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent) =>
        ApplyWrite(channelId, Math.Clamp(dutyPercent, 0, 100));

    public void ReleaseFan(string channelId)
    {
        if (!TryParsePort(channelId, out var mac, out var port)) return;
        _hub.SetPortDuty(mac, port, null);
    }

    public void ReleaseAll()
    {
        if (!_hub.IsConnected) return;
        foreach (var fan in _hub.State.Fans)
        {
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
