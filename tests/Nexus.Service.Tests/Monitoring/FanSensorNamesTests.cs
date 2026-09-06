using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring;

namespace Nexus.Service.Tests.Monitoring;

/// <summary>
/// Covers the join that carries a cooling-page fan-header rename onto the motherboard
/// tach sensor of the same header, which is what every sensor picker and gauge reads.
/// The two sides are keyed differently on purpose: a channel id is its PWM control
/// sensor, a monitoring sensor id is the tachometer.
/// </summary>
public class FanSensorNamesTests
{
    private static HardwareSensor Sensor(string id, string name, string type = "Fan") =>
        new() { Id = id, Name = name, Type = type };

    private static FanChannel Channel(string id, string name, string? rpmSensorId) =>
        new() { Id = id, Name = name, RpmSensorId = rpmSensorId };

    private static IReadOnlyList<HardwareSensor> Renamed(
        IReadOnlyList<HardwareSensor> sensors,
        IReadOnlyList<FanChannel> channels,
        IReadOnlyDictionary<string, string> fanNames)
        => FanSensorNames.WithRenames(sensors, FanSensorNames.BuildMap(channels, fanNames));

    [Fact]
    public void Renames_the_tach_sensor_of_a_renamed_channel()
    {
        var sensors = new List<HardwareSensor>
        {
            Sensor("/lpc/nct6797d/fan/0", "Fan #1"),
            Sensor("/lpc/nct6797d/fan/1", "Fan #2"),
        };

        var renamed = Renamed(
            sensors,
            new[]
            {
                Channel("/lpc/nct6797d/control/0", "Fan #1", "/lpc/nct6797d/fan/0"),
                Channel("/lpc/nct6797d/control/1", "Fan #2", "/lpc/nct6797d/fan/1"),
            },
            new Dictionary<string, string> { ["/lpc/nct6797d/control/0"] = "Radiator Fans" });

        Assert.Equal(new[] { "Radiator Fans", "Fan #2" }, renamed.Select(s => s.Name));
    }

    [Fact]
    public void Never_matches_the_channel_id_itself()
    {
        // The control sensor is in the same motherboard list as the tach. Keying the
        // override off the channel id would rename the PWM readout, not the fan.
        var sensors = new List<HardwareSensor>
        {
            Sensor("/lpc/nct6797d/control/0", "Fan Control #1", "Control"),
            Sensor("/lpc/nct6797d/fan/0", "Fan #1"),
        };

        var renamed = Renamed(
            sensors,
            new[] { Channel("/lpc/nct6797d/control/0", "Fan #1", "/lpc/nct6797d/fan/0") },
            new Dictionary<string, string> { ["/lpc/nct6797d/control/0"] = "Radiator Fans" });

        Assert.Equal(new[] { "Fan Control #1", "Radiator Fans" }, renamed.Select(s => s.Name));
    }

    [Fact]
    public void Leaves_channels_with_no_tach_id_alone()
    {
        // Hub-attached, Linux and macOS channels carry no RpmSensorId, and none of them
        // appear in the motherboard sensor list either.
        var sensors = new List<HardwareSensor> { Sensor("/lpc/nct6797d/fan/0", "Fan #1") };

        var renamed = Renamed(
            sensors,
            new[] { Channel("np50:A:port1", "Port 1", null) },
            new Dictionary<string, string> { ["np50:A:port1"] = "Radiator Fans" });

        Assert.Equal("Fan #1", Assert.Single(renamed).Name);
    }

    [Fact]
    public void Blank_and_absent_renames_keep_the_hardware_name()
    {
        var sensors = new List<HardwareSensor>
        {
            Sensor("/lpc/nct6797d/fan/0", "Fan #1"),
            Sensor("/lpc/nct6797d/fan/1", "Fan #2"),
        };
        var channels = new[]
        {
            Channel("/lpc/nct6797d/control/0", "Fan #1", "/lpc/nct6797d/fan/0"),
            Channel("/lpc/nct6797d/control/1", "Fan #2", "/lpc/nct6797d/fan/1"),
        };

        var renamed = Renamed(sensors, channels, new Dictionary<string, string>
        {
            ["/lpc/nct6797d/control/0"] = "   ",
        });

        Assert.Equal(new[] { "Fan #1", "Fan #2" }, renamed.Select(s => s.Name));
    }

    [Fact]
    public void Trims_the_stored_name()
    {
        var sensors = new List<HardwareSensor> { Sensor("/lpc/nct6797d/fan/0", "Fan #1") };

        var renamed = Renamed(
            sensors,
            new[] { Channel("/lpc/nct6797d/control/0", "Fan #1", "/lpc/nct6797d/fan/0") },
            new Dictionary<string, string> { ["/lpc/nct6797d/control/0"] = "  Radiator Fans  " });

        Assert.Equal("Radiator Fans", Assert.Single(renamed).Name);
    }

    [Fact]
    public void No_renames_is_a_no_op()
    {
        var sensors = new List<HardwareSensor> { Sensor("/lpc/nct6797d/fan/0", "Fan #1") };

        var renamed = Renamed(
            sensors,
            new[] { Channel("/lpc/nct6797d/control/0", "Fan #1", "/lpc/nct6797d/fan/0") },
            new Dictionary<string, string>());

        Assert.Equal("Fan #1", Assert.Single(renamed).Name);
        Assert.Same(sensors, renamed);
    }

    [Fact]
    public void Never_edits_the_sensors_it_was_given()
    {
        // Providers are free to hand back a cached list; renaming one in place would stick
        // after the user cleared the rename, and would leak into every other reader.
        var original = Sensor("/lpc/nct6797d/fan/0", "Fan #1");
        var sensors = new List<HardwareSensor> { original };

        var renamed = Renamed(
            sensors,
            new[] { Channel("/lpc/nct6797d/control/0", "Fan #1", "/lpc/nct6797d/fan/0") },
            new Dictionary<string, string> { ["/lpc/nct6797d/control/0"] = "Radiator Fans" });

        Assert.Equal("Radiator Fans", Assert.Single(renamed).Name);
        Assert.Equal("Fan #1", original.Name);
        Assert.Same(original, Assert.Single(sensors));
    }

    [Fact]
    public void Copied_sensors_keep_every_other_reading()
    {
        var sensors = new List<HardwareSensor>
        {
            new()
            {
                Id = "/lpc/nct6797d/fan/0", Name = "Fan #1", Type = "Fan", Value = 900f,
                Min = 400f, Max = 1800f, Units = "RPM", Formatted = "900 RPM",
                Parent = new SensorParent { Id = "motherboard", Name = "Test Mobo" },
            },
        };

        var renamed = Assert.Single(Renamed(
            sensors,
            new[] { Channel("/lpc/nct6797d/control/0", "Fan #1", "/lpc/nct6797d/fan/0") },
            new Dictionary<string, string> { ["/lpc/nct6797d/control/0"] = "Radiator Fans" }));

        Assert.Equal("Radiator Fans", renamed.Name);
        Assert.Equal("/lpc/nct6797d/fan/0", renamed.Id);
        Assert.Equal("Fan", renamed.Type);
        Assert.Equal(900f, renamed.Value);
        Assert.Equal(400f, renamed.Min);
        Assert.Equal(1800f, renamed.Max);
        Assert.Equal("RPM", renamed.Units);
        Assert.Equal("900 RPM", renamed.Formatted);
        Assert.Equal("Test Mobo", renamed.Parent.Name);
    }

    [Fact]
    public void Single_sensor_rename_clones_rather_than_editing()
    {
        var original = Sensor("/lpc/nct6797d/fan/0", "Fan #1");
        var map = FanSensorNames.BuildMap(
            new[] { Channel("/lpc/nct6797d/control/0", "Fan #1", "/lpc/nct6797d/fan/0") },
            new Dictionary<string, string> { ["/lpc/nct6797d/control/0"] = "Radiator Fans" });

        var renamed = FanSensorNames.WithRename(original, map);

        Assert.Equal("Radiator Fans", renamed.Name);
        Assert.Equal("Fan #1", original.Name);
        Assert.NotSame(original, renamed);
    }

    [Fact]
    public void Single_sensor_without_a_rename_is_returned_as_is()
    {
        var original = Sensor("/lpc/nct6797d/fan/1", "Fan #2");
        var map = FanSensorNames.BuildMap(
            new[] { Channel("/lpc/nct6797d/control/0", "Fan #1", "/lpc/nct6797d/fan/0") },
            new Dictionary<string, string> { ["/lpc/nct6797d/control/0"] = "Radiator Fans" });

        Assert.Same(original, FanSensorNames.WithRename(original, map));
        Assert.Same(original, FanSensorNames.WithRename(original, null));
    }

    [Fact]
    public void Gpu_fan_headers_join_the_same_way()
    {
        // WindowsFanControlProvider discovers GPU fans alongside motherboard ones, so their
        // channels carry a tach id too and land in the gpu component, not the motherboard one.
        var sensors = new List<HardwareSensor> { Sensor("/gpu-nvidia/0/fan/0", "GPU Fan") };

        var renamed = Renamed(
            sensors,
            new[] { Channel("/gpu-nvidia/0/control/0", "GPU Fan", "/gpu-nvidia/0/fan/0") },
            new Dictionary<string, string> { ["/gpu-nvidia/0/control/0"] = "Card Intake" });

        Assert.Equal("Card Intake", Assert.Single(renamed).Name);
    }
}
