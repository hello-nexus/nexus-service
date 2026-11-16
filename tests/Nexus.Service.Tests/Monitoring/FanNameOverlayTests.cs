using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Diagnostics.Alerts;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Monitoring;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Monitoring;

public class FanNameOverlayTests
{
    private sealed class Fans : IFanControlProvider
    {
        public List<FanChannel> Channels { get; } = new();
        public IReadOnlyList<FanChannel> GetFanChannels() => Channels.ToList();
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => new List<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public System.Threading.Tasks.Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, System.IProgress<FanCalibrationProgress> progress, System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult<IReadOnlyList<FanCalibration>>(new List<FanCalibration>());
    }

    private static (InMemoryConfigStore Store, Fans Fans) Rig()
    {
        var fans = new Fans();
        fans.Channels.Add(new FanChannel { Id = "np50:1/port1", Name = "Port 1", DeviceId = "np50:1", DeviceName = "NP50 Hub" });
        fans.Channels.Add(new FanChannel { Id = "/lpc/ctl/1", Name = "Fan #1", RpmSensorId = "/lpc/fan/1" });
        return (new InMemoryConfigStore(), fans);
    }

    private static void Rename(InMemoryConfigStore store, string key, string? name) => store.Update(s =>
    {
        if (name is null)
        {
            s.Cooling.FanNames.Remove(key);
        }
        else
        {
            s.Cooling.FanNames[key] = name;
        }
    });

    private static SensorAlertRule HubRule() => new()
    {
        Id = "builtin:fan:np50:1-port1",
        BuiltIn = BuiltInSensorRules.FanSpeed,
        Sensor = new SensorRef { Category = BuiltInSensorRules.Category, Id = "fan:np50:1-port1", Name = "Port 1", Type = "Fan" },
        Source = new BuiltInSource { Kind = "channel", HardwareName = "NP50 Hub" },
    };

    private static SensorAlertRule BoardRule() => new()
    {
        Id = "builtin:fan:lpc-ctl-1",
        BuiltIn = BuiltInSensorRules.FanSpeed,
        Sensor = new SensorRef { Category = BuiltInSensorRules.Category, Id = "fan:lpc-ctl-1", Name = "Fan #1", Type = "Fan" },
        Source = new BuiltInSource
        {
            Kind = "sensor",
            HardwareName = "Fan #1",
            Sensor = new SensorRef { Category = "motherboard", Id = "/lpc/fan/1", Name = "Fan #1", Type = "Fan", ParentName = "Board" },
        },
    };

    [Fact]
    public void Nothing_renamed_builds_no_overlay()
    {
        var (store, fans) = Rig();

        Assert.Null(FanNameOverlay.Build(store, fans));
    }

    [Fact]
    public void A_blank_rename_builds_no_label()
    {
        var (store, fans) = Rig();
        Rename(store, "/lpc/ctl/1", "  ");

        var overlay = FanNameOverlay.Build(store, fans)!;

        Assert.Equal("Fan #1", overlay.Project(new List<SensorAlertRule> { BoardRule() })[0].Sensor.Name);
        Assert.Null(overlay.SeriesName("fan:lpc-ctl-1"));
    }

    [Fact]
    public void The_projection_shows_channel_and_group_renames_on_a_copy()
    {
        var (store, fans) = Rig();
        Rename(store, "np50:1/port1", " Front intake ");
        Rename(store, "np50:1", "Radiator hub");
        var rule = HubRule();

        var shown = FanNameOverlay.Build(store, fans)!.Project(new List<SensorAlertRule> { rule }).Single();

        Assert.Equal("Front intake", shown.Sensor.Name);
        Assert.Equal("Radiator hub", shown.Source!.HardwareName);
        Assert.Equal(rule.Id, shown.Id);
        Assert.Equal("Port 1", rule.Sensor.Name);
        Assert.Equal("NP50 Hub", rule.Source!.HardwareName);
    }

    [Fact]
    public void The_motherboard_block_name_labels_board_channels_and_the_tach_source_takes_the_channel_name()
    {
        var (store, fans) = Rig();
        Rename(store, "/lpc/ctl/1", "CPU fan");
        Rename(store, "motherboard", "My board");
        var rule = BoardRule();

        var shown = FanNameOverlay.Build(store, fans)!.Project(new List<SensorAlertRule> { rule }).Single();

        Assert.Equal("CPU fan", shown.Sensor.Name);
        Assert.Equal("CPU fan", shown.Source!.Sensor!.Name);
        Assert.Equal("My board", shown.Source.HardwareName);
        Assert.Equal("/lpc/fan/1", shown.Source.Sensor.Id);
        Assert.Equal("Fan #1", rule.Source!.Sensor!.Name);
        Assert.Equal("Fan #1", rule.Source.HardwareName);
    }

    [Fact]
    public void Clearing_the_renames_restores_the_hardware_names()
    {
        var (store, fans) = Rig();
        Rename(store, "np50:1/port1", "Front intake");
        Rename(store, "np50:1", "Radiator hub");
        Assert.NotNull(FanNameOverlay.Build(store, fans));

        Rename(store, "np50:1/port1", null);
        Rename(store, "np50:1", null);

        Assert.Null(FanNameOverlay.Build(store, fans));
    }

    [Fact]
    public void The_history_overlay_renames_fan_and_duty_series_and_leaves_ids_and_points()
    {
        var (store, fans) = Rig();
        Rename(store, "/lpc/ctl/1", "CPU fan");
        var points = new List<MetricPoint> { new(1000, 1200, 1300) };
        var response = new MetricsHistoryResponse
        {
            Series = new List<MetricSeriesWire>
            {
                new() { Id = "fan:lpc-ctl-1", Kind = "fan", Name = "Fan #1", Points = points },
                new() { Id = "fan-duty:lpc-ctl-1", Kind = "fan-duty", Name = "Fan #1", Points = points },
                new() { Id = "fan:np50:1-port1", Kind = "fan", Name = "Port 1", Points = points },
            },
        };

        var shown = FanNameOverlay.Build(store, fans)!.Apply(response);

        Assert.Equal(new[] { "fan:lpc-ctl-1", "fan-duty:lpc-ctl-1", "fan:np50:1-port1" }, shown.Series.Select(x => x.Id));
        Assert.Equal(new[] { "CPU fan", "CPU fan", "Port 1" }, shown.Series.Select(x => x.Name));
        Assert.Same(points, shown.Series[0].Points);
        Assert.Equal("Fan #1", response.Series[0].Name);
    }
}
