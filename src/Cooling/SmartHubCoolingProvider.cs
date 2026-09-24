using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hyte.SmartHub;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the HYTE Smart Hub into the cooling subsystem. The hub has four
/// independent PWM-fan ports, each with its own duty + tach. We surface one
/// <see cref="FanChannel"/> per populated port (firmware reports the port
/// "enabled" or it has a live tach reading); empty ports never appear on the
/// cooling page.
///
/// Channel IDs: <c>smarthub:&lt;serial&gt;:fan&lt;0..3&gt;</c>.
///
/// Unlike the MiniHub, each port is written independently
/// (<c>FF CC 02 &lt;ch&gt; &lt;duty&gt; &lt;en&gt;</c>), so a per-port write
/// touches only that port. The hub has no onboard-curve handoff command, so
/// "release" just drops the channel from software control - the port holds
/// its last commanded duty.
/// </summary>
public sealed class SmartHubCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private const string IdPrefix = "smarthub:";

    private readonly SmartHubHub _hub;

    // Channels the user has placed under software control. The hub has no
    // per-port mode flag to query back, so we track it here - same reason as
    // MiniHubCoolingProvider: without it GetFanChannels would report "Auto"
    // for a freshly Manual-clicked channel and the panel would snap back to
    // BIOS on the next cooling broadcast. Guarded by _ctrlLock: REST handlers,
    // the cooling broadcaster, and the heartbeat's manual-speed restore touch
    // it from different threads.
    private readonly HashSet<string> _softwareControlled = new();
    private readonly object _ctrlLock = new();

    public SmartHubCoolingProvider(SmartHubHub hub)
    {
        _hub = hub;
    }

    public static bool IsSmartHubId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected) return Array.Empty<FanChannel>();
        var state = _hub.State;
        var serial = state.Serial;
        if (string.IsNullOrEmpty(serial)) return Array.Empty<FanChannel>();
        var deviceId = _hub.DeviceId;
        var result = new List<FanChannel>(SmartHubProtocol.FanChannelCount);

        foreach (var fan in state.Fans)
        {
            if (!IsPopulated(fan)) continue;
            var id = FanId(serial, fan.Index);
            result.Add(new FanChannel
            {
                Id = id,
                Name = $"Fan Port {fan.Index + 1}",
                DutyPercent = fan.Duty,
                Rpm = fan.Rpm,
                Mode = IsSoftwareControlled(id) ? FanModes.Manual : FanModes.Auto,
                DeviceId = deviceId,
                DeviceName = SmartHubHub.ProductName,
                PortLabel = $"Port {fan.Index + 1}",
            });
        }
        return result;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        // The Smart Hub firmware exposes no temperature probes - its tach +
        // PWM are all the cooling-relevant data it surfaces.
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
        if (!IsSmartHubId(channelId)) return;
        lock (_ctrlLock) _softwareControlled.Remove(channelId);
    }

    public void ReleaseAll()
    {
        lock (_ctrlLock) _softwareControlled.Clear();
    }

    /// <summary>
    /// Restore a persisted manual speed on (re)connect. Same write as
    /// <see cref="SetFanSpeed"/> but reports wire success so the heartbeat can
    /// retry next tick instead of leaving the port at the firmware's 100%
    /// power-on default.
    /// </summary>
    public bool RestoreManualSpeed(string channelId, int dutyPercent)
    {
        if (!IsSmartHubId(channelId) || !TryParseChannel(channelId, out var index)) return false;
        var ok = _hub.WriteFanSpeed(index, Math.Clamp(dutyPercent, 0, 100), enabled: true);
        if (ok) lock (_ctrlLock) _softwareControlled.Add(channelId);
        return ok;
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        // Hub-driven fans report stable RPM at known PWM and don't benefit
        // from a calibration ramp (same as NP50 / MiniHub).
        return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        if (!_hub.IsConnected) return Array.Empty<CoolingComponent>();
        var state = _hub.State;
        var serial = state.Serial;
        if (string.IsNullOrEmpty(serial)) return Array.Empty<CoolingComponent>();

        var devices = new List<CoolingDevice>(SmartHubProtocol.FanChannelCount);
        foreach (var fan in state.Fans)
        {
            if (!IsPopulated(fan)) continue;
            devices.Add(new CoolingDevice
            {
                Id = FanId(serial, fan.Index),
                Name = $"Fan Port {fan.Index + 1}",
                Type = "Fan",
                Rpm = fan.Rpm,
                Pwm = fan.Duty,
            });
        }
        if (devices.Count == 0) return Array.Empty<CoolingComponent>();

        return new[]
        {
            new CoolingComponent
            {
                Id = _hub.DeviceId,
                Name = SmartHubHub.ProductName,
                Type = "SmartHub",
                Devices = devices,
            },
        };
    }

    // ── Internals ──

    // A port is shown only when a fan has shown a live tach this connection.
    // The firmware's "enabled" flag is useless for presence (0x01 for every
    // port, fan or not - bench-verified on fw 1.0.0.1); SeenFan latches so a
    // fan parked at 0% duty doesn't vanish off the cooling page.
    private static bool IsPopulated(SmartHubFanChannel fan) => fan.Rpm > 0 || fan.SeenFan;

    private void ApplyWrite(string channelId, int dutyPercent)
    {
        if (!IsSmartHubId(channelId)) return;
        if (!_hub.IsConnected)
        {
            ServiceLog.Warn($"[smarthub-cooling] write to {channelId} dropped: hub not connected");
            return;
        }
        if (!TryParseChannel(channelId, out var index))
        {
            ServiceLog.Warn($"[smarthub-cooling] write to {channelId} dropped: unparseable channel id");
            return;
        }

        // Driving this channel implies software control; record so the next
        // GetFanChannels reports Mode="Manual" and the panel doesn't snap the
        // selection back to BIOS on the cooling-topic refresh.
        lock (_ctrlLock) _softwareControlled.Add(channelId);
        // Silent on success, as in MiniHubCoolingProvider: commanded duty is
        // already traceable via the heartbeat's periodic State.Fans[].Duty line.
        var ok = _hub.WriteFanSpeed(index, dutyPercent, enabled: true);
        if (!ok)
        {
            ServiceLog.Warn($"[smarthub-cooling] write to {channelId} -> {dutyPercent}% failed");
        }
    }

    private bool IsSoftwareControlled(string id)
    {
        lock (_ctrlLock) return _softwareControlled.Contains(id);
    }

    /// <summary>Channel id for one PWM port (<c>smarthub:&lt;serial&gt;:fan&lt;0..3&gt;</c>). Shared with the heartbeat's saved-manual-speed restore.</summary>
    public static string FanId(string serial, int index) =>
        $"{IdPrefix}{serial}:fan{index.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryParseChannel(string channelId, out int index)
    {
        index = -1;
        var marker = ":fan";
        var at = channelId.LastIndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return false;
        var tail = channelId.Substring(at + marker.Length);
        if (!int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) return false;
        return index >= 0 && index < SmartHubProtocol.FanChannelCount;
    }
}
