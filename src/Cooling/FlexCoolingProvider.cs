using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.LianLiFlex;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the Flex fans into fan control: one curve-assignable channel per unit,
/// "lianli-flex:{MAC}:port0". The receiver has a single duty byte, so only port 0 is driven.
/// Mode and duty read back from the receiver's own echo; releasing a channel hands the fan back
/// to the motherboard header.
/// </summary>
public sealed class FlexCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private const string IdPrefix = "lianli-flex:";
    private const string PortSuffix = ":port0";

    private readonly FlexHub _hub;
    private readonly Nexus.Service.Persistence.IConfigStore? _store;

    private readonly FlexScreens? _screens;

    public FlexCoolingProvider(FlexHub hub, Nexus.Service.Persistence.IConfigStore? store = null, FlexScreens? screens = null)
    {
        _hub = hub;
        _store = store;
        _screens = screens;
    }

    private string NameOf(FlexUnit unit, Func<string, int> order) =>
        FlexNames.UnitName(_hub.Units, unit, order, _screens?.ScreenOnlySerials(unit.Family).Count ?? 0);

    private Func<string, int> SortIndexOf()
    {
        var looks = _store?.Load().Devices.FlexUnits;
        return mac => looks is not null && looks.TryGetValue(mac, out var look) ? look.SortIndex : 0;
    }

    private IReadOnlyList<FlexUnit> Units() => FlexNames.Ordered(_hub.Units, SortIndexOf());

    public static bool IsFlexId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    public static string ChannelId(string mac) => $"{IdPrefix}{mac}{PortSuffix}";

    private static string DeviceId(string mac) => $"{IdPrefix}{mac}";


    private static int MinDuty(FlexFamily family) => family == FlexFamily.SlInfFlex ? 10 : 11;

    private static bool Follows(FlexUnit unit) =>
        unit.Info.Pwm.Length > 0 && unit.Info.Pwm[0] == FlexProtocol.PwmFollowMotherboard;

    private static int Duty(FlexUnit unit) =>
        Follows(unit) ? 0 : FlexProtocol.DecodeDuty(unit.Info.Pwm.Length > 0 ? unit.Info.Pwm[0] : 0);

    private static int Rpm(FlexUnit unit) => unit.Info.Rpm.Length > 0 ? unit.Info.Rpm[0] : 0;

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var result = new List<FanChannel>();
        var order = SortIndexOf();
        foreach (var unit in Units())
        {
            if (!unit.Controllable)
            {
                continue;
            }
            var unitName = NameOf(unit, order);
            var pending = _hub.PendingDuty(unit.Mac);
            result.Add(new FanChannel
            {
                Id = ChannelId(unit.Mac),
                Name = "Fan",
                DutyPercent = pending is { } p ? Math.Max(p, 0) : Duty(unit),
                Rpm = Rpm(unit),
                Mode = pending is { } wanted ? (wanted == -1 ? FanModes.Auto : FanModes.Manual) : (Follows(unit) ? FanModes.Auto : FanModes.Manual),
                MinDuty = MinDuty(unit.Family),
                DeviceId = DeviceId(unit.Mac),
                DeviceName = unitName,
                PortLabel = "Fan 1",
            });
        }
        return result;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();

    public float? ReadTemperature(string sensorId) => null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        Write(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent) => Write(channelId, Math.Clamp(dutyPercent, 0, 100));

    public void ReleaseFan(string channelId)
    {
        if (TryParse(channelId, out var mac))
        {
            _hub.SetDuty(mac, null);
        }
    }

    public void ReleaseAll()
    {
        _hub.ReleaseAllDuty();
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var components = new List<CoolingComponent>();
        var order = SortIndexOf();
        foreach (var unit in Units())
        {
            if (!unit.Controllable)
            {
                continue;
            }
            components.Add(new CoolingComponent
            {
                Id = DeviceId(unit.Mac),
                Name = NameOf(unit, order),
                Type = "LianLiFlex",
                Devices = new List<CoolingDevice>
                {
                    new CoolingDevice
                    {
                        Id = ChannelId(unit.Mac),
                        Name = "Fan",
                        Type = "Fan",
                        Rpm = Rpm(unit),
                        Pwm = _hub.PendingDuty(unit.Mac) is { } shown ? Math.Max(shown, 0) : Duty(unit),
                    },
                },
            });
        }
        return components;
    }

    private void Write(string channelId, int percent)
    {
        if (TryParse(channelId, out var mac))
        {
            _hub.SetDuty(mac, percent);
        }
    }

    private static bool TryParse(string id, out string mac)
    {
        mac = "";
        if (!IsFlexId(id) || !id.EndsWith(PortSuffix, StringComparison.Ordinal))
        {
            return false;
        }
        mac = id.Substring(IdPrefix.Length, id.Length - IdPrefix.Length - PortSuffix.Length);
        return mac.Length == 12;
    }
}
