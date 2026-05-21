using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Models.Cooling;
using Qos.Service.Peripherals.Hyte.MiniHub;

namespace Qos.Service.Cooling;

/// <summary>
/// Bridges the HYTE MiniHub into the cooling subsystem. The MiniHub has
/// only two physical fan ports — port 1 (1 fan) and port 2 (up to 3
/// daisy-chained fans sharing one tach + one PWM). We surface one
/// <see cref="FanChannel"/> per populated port, mirroring HYTE's own
/// MinihubComponent shape. Empty ports never appear on the cooling page.
///
/// Channel IDs:
/// <code>
///   minihub:&lt;serial&gt;:port1   — port 1 PWM / tach
///   minihub:&lt;serial&gt;:port2   — port 2 PWM / tach (shared across daisy chain)
/// </code>
///
/// Both ports are set in a single firmware command, so any per-port write
/// re-sends the cached "other port" duty. The hub firmware clamps duties
/// below 10% to 0%, so a curve emitting 0% still pins to 10% — there is no
/// firmware-supported "stop" speed.
/// </summary>
public sealed class MiniHubCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private readonly MiniHubHub _hub;
    // Diagnostic: log GetFanChannels return shape once per minute so a
    // missing-on-cooling-page report can be diagnosed without a redeploy.
    private DateTime _nextTraceUtc = DateTime.MinValue;

    // Channels the user has placed under software control (Manual mode or
    // curve-bound). Tracked here because the MiniHub firmware only has a
    // hub-level Software/Motherboard mode switch — there is no per-port
    // "Manual" flag we can query back. Without this, GetFanChannels would
    // return Mode="Auto" for a freshly Manual-clicked channel, and the
    // panel's refreshCoolingConfig would snap the UI back to BIOS the
    // moment the next cooling-topic broadcast fires (the panel decides
    // Manual vs BIOS purely from FanChannel.Mode + saved curves output
    // list — see CoolingView.refreshCoolingConfig).
    private readonly HashSet<string> _softwareControlled = new();

    public MiniHubCoolingProvider(MiniHubHub hub)
    {
        _hub = hub;
    }

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var connected = _hub.IsConnected;
        var state = _hub.State;
        var serial = state.Serial;
        TraceIfDue(connected, serial, state);
        if (!connected) return Array.Empty<FanChannel>();
        if (string.IsNullOrEmpty(serial)) return Array.Empty<FanChannel>();
        var deviceId = _hub.DeviceId;
        var result = new List<FanChannel>(2);

        if (state.Port1Fans > 0)
        {
            var id = Port1Id(serial);
            result.Add(new FanChannel
            {
                Id = id,
                Name = "Port 1 Fan",
                DutyPercent = state.Port1Duty,
                Rpm = state.Port1Rpm,
                Mode = _softwareControlled.Contains(id) ? FanModes.Manual : FanModes.Auto,
                DeviceId = deviceId,
                DeviceName = MiniHubHub.ProductName,
                PortLabel = "Port 1",
            });
        }
        if (state.Port2Fans > 0)
        {
            var id = Port2Id(serial);
            var label = state.Port2Fans == 1 ? "Port 2 Fan" : $"Port 2 Fans ({state.Port2Fans})";
            result.Add(new FanChannel
            {
                Id = id,
                Name = label,
                DutyPercent = state.Port2Duty,
                Rpm = state.Port2Rpm,
                Mode = _softwareControlled.Contains(id) ? FanModes.Manual : FanModes.Auto,
                DeviceId = deviceId,
                DeviceName = MiniHubHub.ProductName,
                PortLabel = "Port 2",
            });
        }
        return result;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        // The MiniHub firmware does not expose temperature probes — its
        // tach + PWM are all the cooling-relevant data it surfaces.
        return Array.Empty<TemperatureSource>();
    }

    public float? ReadTemperature(string sensorId) => null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyWrite(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyWrite(channelId, clamped);
    }

    public void ReleaseFan(string channelId)
    {
        // MiniHub control mode is hub-level, not per-port. Drop the channel
        // from the software-control tracker; only flip the entire hub back
        // to motherboard mode once nothing else needs software-driven PWM,
        // so a wire-DnD disconnect on one port doesn't yank PWM out from
        // under another port that is still actively driven.
        if (!IsMiniHubId(channelId)) return;
        _softwareControlled.Remove(channelId);
        if (!_hub.IsConnected) return;
        if (_softwareControlled.Count == 0)
        {
            _hub.SetFanControlMode(MiniHubProtocol.FanModeMotherboard);
        }
    }

    public void ReleaseAll()
    {
        _softwareControlled.Clear();
        if (!_hub.IsConnected) return;
        _hub.SetFanControlMode(MiniHubProtocol.FanModeMotherboard);
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        // Same rationale as NP50: hub-driven fans report stable RPM at known
        // PWM and don't benefit from a calibration ramp.
        return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        if (!_hub.IsConnected) return Array.Empty<CoolingComponent>();
        var state = _hub.State;
        var serial = state.Serial;
        if (string.IsNullOrEmpty(serial)) return Array.Empty<CoolingComponent>();

        var devices = new List<CoolingDevice>(2);
        if (state.Port1Fans > 0)
        {
            devices.Add(new CoolingDevice
            {
                Id = Port1Id(serial),
                Name = "Port 1 Fan",
                Type = "Fan",
                Rpm = state.Port1Rpm,
                Pwm = state.Port1Duty,
            });
        }
        if (state.Port2Fans > 0)
        {
            devices.Add(new CoolingDevice
            {
                Id = Port2Id(serial),
                Name = state.Port2Fans == 1 ? "Port 2 Fan" : $"Port 2 Fans ({state.Port2Fans})",
                Type = "Fan",
                Rpm = state.Port2Rpm,
                Pwm = state.Port2Duty,
            });
        }
        if (devices.Count == 0) return Array.Empty<CoolingComponent>();

        return new[]
        {
            new CoolingComponent
            {
                Id = _hub.DeviceId,
                Name = MiniHubHub.ProductName,
                Type = "MiniHub",
                Devices = devices,
            },
        };
    }

    // ── Internals ──

    public static bool IsMiniHubId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("minihub:", StringComparison.Ordinal);

    private void ApplyWrite(string channelId, int dutyPercent)
    {
        if (!IsMiniHubId(channelId)) return;
        if (!_hub.IsConnected)
        {
            Console.Error.WriteLine($"[minihub-cooling] write to {channelId} dropped: hub not connected");
            return;
        }
        // Driving this channel implies software control; record so the next
        // GetFanChannels returns Mode="Manual" and the panel doesn't snap
        // the user's selection back to BIOS on the cooling-topic refresh.
        _softwareControlled.Add(channelId);
        var state = _hub.State;
        var port1Target = state.Port1Duty;
        var port2Target = state.Port2Duty;
        if (channelId.EndsWith(":port1", StringComparison.Ordinal)) port1Target = dutyPercent;
        else if (channelId.EndsWith(":port2", StringComparison.Ordinal)) port2Target = dutyPercent;
        else return; // unknown sub-id; ignore rather than risk a wrong port write

        // Coerce never-driven ports to the firmware floor so we don't send
        // 0% (which the firmware would clamp anyway, but starting from a
        // clean known value keeps the heartbeat-asserted state predictable).
        if (port1Target < MiniHubProtocol.FanMinDutyPercent) port1Target = MiniHubProtocol.FanMinDutyPercent;
        if (port2Target < MiniHubProtocol.FanMinDutyPercent) port2Target = MiniHubProtocol.FanMinDutyPercent;

        _hub.SetFanControlMode(MiniHubProtocol.FanModeSoftware);
        var writeOk = _hub.WriteFanSpeed(port1Target, port2Target);
        Console.Error.WriteLine(
            $"[minihub-cooling] {channelId} -> p1={port1Target}% p2={port2Target}% (writeOk={writeOk})");
    }

    private static string Port1Id(string serial) => $"minihub:{serial}:port1";
    private static string Port2Id(string serial) => $"minihub:{serial}:port2";

    private void TraceIfDue(bool connected, string serial, MiniHubState state)
    {
        var now = DateTime.UtcNow;
        if (now < _nextTraceUtc) return;
        _nextTraceUtc = now.AddSeconds(60);
        Console.Error.WriteLine(
            $"[minihub-cooling] GetFanChannels connected={connected} serial={serial} " +
            $"port1Fans={state.Port1Fans} port2Fans={state.Port2Fans} " +
            $"port1Rpm={state.Port1Rpm} port2Rpm={state.Port2Rpm}");
    }
}
