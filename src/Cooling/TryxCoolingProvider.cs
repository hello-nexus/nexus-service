using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Tryx.Panorama;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the Tryx Panorama's pump head into the fan-control subsystem: the small fan Kanali
/// calls the "VRM Fan" as a controllable channel. The pump is not a channel here: it runs off a
/// motherboard header, which is where it is driven, and the panel only reads its tach (Kanali's
/// FAQ: the pump is not software-controllable). That reading goes out as
/// <see cref="IPumpTachSource"/> so the composite labels the header instead of listing the pump
/// twice. The channel appears once the panel has answered a speed request, on the models that
/// carry the fan.
/// </summary>
public sealed class TryxCoolingProvider : IFanControlProvider, ICoolingProvider, IPumpTachSource
{
    public const string DeviceId = "tryx";
    public const string DeviceName = "Tryx Panorama";

    private const string FanChannelId = "tryx:fan";

    private readonly TryxPanoramaHub _hub;

    public TryxCoolingProvider(TryxPanoramaHub hub)
    {
        _hub = hub;
    }

    public static bool IsTryxId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(DeviceId + ":", StringComparison.Ordinal);

    public int? PumpRpm => _hub.IsConnected && _hub.HasCooler ? _hub.State.CoolerPumpRpm : null;

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected || !_hub.HasCooler || _hub.State.CoolerFanRpm is not { } fanRpm)
        {
            return Array.Empty<FanChannel>();
        }
        var target = _hub.CoolerFanTarget;
        return new[]
        {
            new FanChannel
            {
                Id = FanChannelId,
                Name = "VRM Fan",
                // While Nexus is not driving it the fan runs the panel's own config, whose fixed
                // power was read at connect (0 when that config is not a fixed power).
                DutyPercent = target ?? _hub.State.CoolerFanPower ?? 0,
                Rpm = fanRpm,
                Mode = target is null ? FanModes.Auto : FanModes.Manual,
                Kind = FanKinds.Fan,
                // The fan sits on the pump head.
                IsAio = true,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                PortLabel = "VRM Fan",
            },
        };
    }

    // The panel reports no temperature.
    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();

    public float? ReadTemperature(string sensorId) => null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        int clamped = Math.Clamp(dutyPercent, 0, 100);
        DriveFanSpeed(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        if (channelId == FanChannelId)
        {
            _hub.SetCoolerFanTarget(dutyPercent);
        }
    }

    /// <summary>The panel gets back the fan config it had before Nexus drove it.</summary>
    public void ReleaseFan(string channelId)
    {
        if (channelId == FanChannelId)
        {
            _hub.ReleaseCoolerFan();
        }
    }

    public void ReleaseAll() => _hub.ReleaseCoolerFan();

    // The ramp would take the fan to 0%, so it is not calibrated.
    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var channels = GetFanChannels();
        if (channels.Count == 0)
        {
            return Array.Empty<CoolingComponent>();
        }
        var fan = channels[0];
        return new[]
        {
            new CoolingComponent
            {
                Id = DeviceId,
                Name = DeviceName,
                Type = "TryxPanorama",
                Devices = new List<CoolingDevice>
                {
                    new CoolingDevice { Id = fan.Id, Name = fan.Name, Type = fan.Kind, Rpm = fan.Rpm, Pwm = fan.DutyPercent },
                },
            },
        };
    }
}
