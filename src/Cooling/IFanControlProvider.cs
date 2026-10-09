using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Cooling;

/// <summary>
/// Write-path interface for fan control. Discovers controllable fan channels,
/// reads temperatures, and sets fan speeds via the hardware abstraction layer.
///
/// On Windows this is backed by LibreHardwareMonitor (motherboard SuperIO +
/// GPU fan headers). On macOS/Linux this returns empty data (no kernel-level
/// fan control APIs available).
/// </summary>
public interface IFanControlProvider
{
    /// <summary>All controllable fan channels discovered from hardware.</summary>
    IReadOnlyList<FanChannel> GetFanChannels();

    /// <summary>All available temperature sensors that can serve as curve input.</summary>
    IReadOnlyList<TemperatureSource> GetTemperatureSources();

    /// <summary>
    /// Only the sources owned by an external device (hub probes), for callers that must not pay
    /// for a platform sensor refresh. The default filters the full list; the composite overrides
    /// it to skip the motherboard provider entirely.
    /// </summary>
    IReadOnlyList<TemperatureSource> GetDeviceTemperatureSources()
    {
        var result = new List<TemperatureSource>();
        foreach (var s in GetTemperatureSources())
            if (!string.IsNullOrEmpty(s.DeviceId)) result.Add(s);
        return result;
    }

    /// <summary>Pump heads on an external device. The composite overrides it to skip the motherboard provider.</summary>
    IReadOnlyList<FanChannel> GetDevicePumpChannels()
    {
        var result = new List<FanChannel>();
        foreach (var c in GetFanChannels())
            if (c.Kind == FanKinds.Pump && !string.IsNullOrEmpty(c.DeviceId)) result.Add(c);
        return result;
    }

    /// <summary>Read current temperature by sensor ID. Returns null if sensor not found.</summary>
    float? ReadTemperature(string sensorId);

    /// <summary>Set duty cycle (0-100) on a fan channel as a user-intent manual
    /// override. Recorded in Cooling.ManualSpeeds so it survives a restart and
    /// counts toward "user broke out of the preset" detection.</summary>
    int SetFanSpeed(string channelId, int dutyPercent);

    /// <summary>Engine-driven duty write (curve engine). Drives the hardware
    /// the same way as <see cref="SetFanSpeed"/> but does NOT record to
    /// Cooling.ManualSpeeds - those entries are reserved for explicit user
    /// overrides. Otherwise the curve engine's per-tick writes would pollute
    /// the override dict and trip preset-derivation logic.</summary>
    void DriveFanSpeed(string channelId, int dutyPercent);

    /// <summary>Release fan channel back to BIOS/automatic control and drop
    /// its Cooling.ManualSpeeds entry - the user's explicit per-fan choice.</summary>
    void ReleaseFan(string channelId);

    /// <summary>Release all fans back to BIOS/automatic control. Preserves
    /// Cooling.ManualSpeeds: this runs on shutdown and profile switch, where
    /// the persisted intent must survive for CurveEngine's replay.</summary>
    void ReleaseAll();

    /// <summary>
    /// Calibrate the given fans by ramping duty 100%→0% and measuring RPM at
    /// each step. Empty fanIds = calibrate all discovered fans. Results are
    /// persisted to IConfigStore. Runs all fans in parallel.
    /// </summary>
    Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct);
}
