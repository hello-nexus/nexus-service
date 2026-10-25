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
    private const string PortMarker = ":port";

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

    public static string ChannelId(string mac, int port = 0) => $"{IdPrefix}{mac}{PortMarker}{port}";

    private static string DeviceId(string mac) => $"{IdPrefix}{mac}";


    private static int MinDuty(FlexFamily family) => family switch
    {
        FlexFamily.SlInfFlex => 10,
        FlexFamily.P28V2 => FlexProtocol.P28MinDutyPercent,
        _ => 11,
    };

    private static bool Follows(FlexUnit unit) =>
        unit.Info.Pwm.Length > 0 && unit.Info.Pwm[0] == FlexProtocol.PwmFollowMotherboard;

    private static int Duty(FlexUnit unit) =>
        Follows(unit) ? 0 : FlexProtocol.DecodeDuty(unit.Info.Pwm.Length > 0 ? unit.Info.Pwm[0] : 0);

    private static int Rpm(FlexUnit unit, int port = 0) => port < unit.Info.Rpm.Length ? unit.Info.Rpm[port] : 0;

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
            // One control channel; a P28 reports its per-fan tachs through the component's devices and the state route.
            result.Add(new FanChannel
            {
                Id = ChannelId(unit.Mac),
                Name = "Fan",
                DutyPercent = pending is { } p ? Math.Max(p, 0) : Duty(unit),
                Rpm = Rpm(unit, 0),
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
                Devices = DevicesOf(unit),
            });
        }
        return components;
    }

    private List<CoolingDevice> DevicesOf(FlexUnit unit)
    {
        var pwm = _hub.PendingDuty(unit.Mac) is { } shown ? Math.Max(shown, 0) : Duty(unit);
        var ports = unit.Family == FlexFamily.P28V2 ? Math.Min(unit.Fans, unit.Info.Rpm.Length) : 1;
        var devices = new List<CoolingDevice>();
        for (var i = 0; i < ports; i++)
        {
            devices.Add(new CoolingDevice { Id = i == 0 ? ChannelId(unit.Mac) : $"{ChannelId(unit.Mac)}:fan{i}", Name = ports > 1 ? $"Fan {i + 1}" : "Fan", Type = "Fan", Rpm = Rpm(unit, i), Pwm = pwm });
        }
        return devices;
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
        var at = id.LastIndexOf(PortMarker, StringComparison.Ordinal);
        if (!IsFlexId(id) || at < 0 || !int.TryParse(id.AsSpan(at + PortMarker.Length), out var port) || port is < 0 or > 3)
        {
            return false;
        }
        mac = id.Substring(IdPrefix.Length, at - IdPrefix.Length);
        return mac.Length == 12;
    }
}
