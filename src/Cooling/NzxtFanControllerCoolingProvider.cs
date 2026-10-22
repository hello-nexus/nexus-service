using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the NZXT RGB & Fan Controller's three fan headers into the fan-control
/// subsystem. Channel ids: <c>nzxt-fan-controller:fan&lt;0..2&gt;</c>.
/// </summary>
public sealed class NzxtFanControllerCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private const string IdPrefix = NzxtFanControllerHub.DeviceId + ":";
    private const string FanMarker = IdPrefix + "fan";

    // Polls in a row a driven channel may read off its commanded duty before it is written
    // again, so a reading that lags a fresh write does not trigger a second one.
    private const int ReassertAfterPolls = 3;

    private readonly NzxtFanControllerHub _hub;
    private readonly object _ctrlLock = new();
    private readonly bool[] _softwareControlled = new bool[NzxtFanControllerProtocol.FanChannelCount];
    private readonly int[] _commanded = new int[NzxtFanControllerProtocol.FanChannelCount];
    private readonly int[] _offTarget = new int[NzxtFanControllerProtocol.FanChannelCount];

    public NzxtFanControllerCoolingProvider(NzxtFanControllerHub hub)
    {
        _hub = hub;
    }

    public static bool IsNzxtFanControllerId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    public static string FanId(int channel) =>
        FanMarker + channel.ToString(CultureInfo.InvariantCulture);

    // IFanControlProvider

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var fans = _hub.Fans;
        if (!_hub.IsConnected || fans.Length == 0)
        {
            return Array.Empty<FanChannel>();
        }
        var result = new List<FanChannel>(fans.Length);
        for (int i = 0; i < fans.Length; i++)
        {
            result.Add(new FanChannel
            {
                Id = FanId(i),
                Name = $"Fan {i + 1}",
                DutyPercent = fans[i].DutyPercent,
                Rpm = fans[i].Rpm,
                Mode = IsSoftwareControlled(i) ? FanModes.Manual : FanModes.Auto,
                Kind = FanKinds.Fan,
                DeviceId = NzxtFanControllerHub.DeviceId,
                DeviceName = NzxtFanControllerHub.ProductName,
                PortLabel = $"Fan {i + 1}",
            });
        }
        return result;
    }

    // The controller has no temperature probes.
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

    // The controller has no hand-back command: a released channel just stops being driven
    // and holds its last duty.
    public void ReleaseFan(string channelId)
    {
        if (TryParseChannel(channelId, out var channel))
        {
            lock (_ctrlLock) _softwareControlled[channel] = false;
        }
    }

    public void ReleaseAll()
    {
        lock (_ctrlLock) Array.Clear(_softwareControlled);
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    // ICoolingProvider

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var fans = _hub.Fans;
        if (!_hub.IsConnected || fans.Length == 0)
        {
            return Array.Empty<CoolingComponent>();
        }
        var devices = new List<CoolingDevice>(fans.Length);
        for (int i = 0; i < fans.Length; i++)
        {
            devices.Add(new CoolingDevice
            {
                Id = FanId(i),
                Name = $"Fan {i + 1}",
                Type = "Fan",
                Rpm = fans[i].Rpm,
                Pwm = fans[i].DutyPercent,
            });
        }
        return new[]
        {
            new CoolingComponent
            {
                Id = NzxtFanControllerHub.DeviceId,
                Name = NzxtFanControllerHub.ProductName,
                Type = "NzxtFanController",
                Devices = devices,
            },
        };
    }

    /// <summary>
    /// Called by the worker after each status report. Writes a driven channel again once its
    /// reported duty has stayed off the commanded one for several reports, which is what a
    /// controller that power-cycled or dropped the setting looks like.
    /// </summary>
    public void ReassertControl()
    {
        var fans = _hub.Fans;
        if (!_hub.IsConnected)
        {
            return;
        }
        for (int i = 0; i < fans.Length; i++)
        {
            int want;
            bool resend = false;
            lock (_ctrlLock)
            {
                if (!_softwareControlled[i])
                {
                    _offTarget[i] = 0;
                    continue;
                }
                want = _commanded[i];
                if (fans[i].DutyPercent == want)
                {
                    _offTarget[i] = 0;
                }
                else if (++_offTarget[i] >= ReassertAfterPolls)
                {
                    _offTarget[i] = 0;
                    resend = true;
                }
            }
            if (resend && !_hub.SetFanDuty(i, want))
            {
                ServiceLog.Warn($"[nzxt-fan-controller-cooling] reassert fan {i} duty {want} failed");
            }
        }
    }

    private void ApplyWrite(string channelId, int dutyPercent)
    {
        if (!TryParseChannel(channelId, out var channel))
        {
            return;
        }
        if (!_hub.IsConnected)
        {
            ServiceLog.Warn($"[nzxt-fan-controller-cooling] write to {channelId} dropped: not connected");
            return;
        }
        lock (_ctrlLock)
        {
            _softwareControlled[channel] = true;
            _commanded[channel] = dutyPercent;
            _offTarget[channel] = 0;
        }
        if (!_hub.SetFanDuty(channel, dutyPercent))
        {
            ServiceLog.Warn($"[nzxt-fan-controller-cooling] write to {channelId} -> {dutyPercent}% failed");
        }
    }

    private bool IsSoftwareControlled(int channel)
    {
        lock (_ctrlLock) return _softwareControlled[channel];
    }

    private static bool TryParseChannel(string channelId, out int channel)
    {
        channel = -1;
        return !string.IsNullOrEmpty(channelId)
            && channelId.StartsWith(FanMarker, StringComparison.Ordinal)
            && int.TryParse(channelId.AsSpan(FanMarker.Length), NumberStyles.None, CultureInfo.InvariantCulture, out channel)
            && channel < NzxtFanControllerProtocol.FanChannelCount;
    }
}
