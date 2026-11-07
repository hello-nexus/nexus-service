using System.Collections.Concurrent;
using System.Linq;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests;

public class KrakenLhmSensorsTests
{
    private static HardwareSensor Sensor(string id, string name, string type, float value) => new()
    {
        Id = id,
        Name = name,
        Type = type,
        Value = value,
        Min = 1,
        Max = 2,
        Formatted = "stale",
        FormattedMin = "stale",
        FormattedMax = "stale",
        Parent = new SensorParent { Id = "/nzxtkrakenz/0", Name = "NZXT Kraken" },
    };

    private static HardwareComponent Component(bool fans = true)
    {
        var c = new HardwareComponent { Id = "/nzxtkrakenz/0", Name = "NZXT Kraken" };
        c.Sensors.Add(Sensor("/nzxtkrakenz/0/temperature/0", "Liquid", "Temperature", 20));
        c.Sensors.Add(Sensor("/nzxtkrakenz/0/control/0", "Pump Control", "Control", 50));
        c.Sensors.Add(Sensor("/nzxtkrakenz/0/fan/0", "Pump", "Fan", 1000));
        if (fans)
        {
            c.Sensors.Add(Sensor("/nzxtkrakenz/0/control/1", "Fans Control", "Control", 40));
            c.Sensors.Add(Sensor("/nzxtkrakenz/0/fan/1", "Fans", "Fan", 800));
        }
        return c;
    }

    private static KrakenSnapshot Snap(double liquid, int pumpRpm = 2400, int pumpDuty = 75, int fanRpm = 1300, int fanDuty = 60) =>
        new(liquid, pumpRpm, pumpDuty, fanRpm, fanDuty, "1.0.0", 0, 0, KrakenDisplayMode.Liquid, new System.Collections.Generic.List<KrakenLightingChannel>());

    [Theory]
    [InlineData(31.5, true, 31.5f)]
    [InlineData(31.5, false, null)]
    [InlineData(0.0, true, null)]
    [InlineData(double.NaN, true, null)]
    public void Liquid_is_null_without_a_valid_fresh_reading(double reading, bool fresh, float? expected)
    {
        Assert.Equal(expected, KrakenLhmSensors.Liquid(reading, fresh));
    }

    [Fact]
    public void Ids_names_and_parent_are_kept_and_values_replaced()
    {
        var c = Component();
        var ids = c.Sensors.Select(s => (s.Id, s.Name, s.Type, s.Parent.Id)).ToList();

        KrakenLhmSensors.Apply(c, Snap(31.46), true, null);

        Assert.Equal(ids, c.Sensors.Select(s => (s.Id, s.Name, s.Type, s.Parent.Id)).ToList());
        Assert.Equal(31.46f, c.Sensors.Single(s => s.Name == "Liquid").Value);
        Assert.Equal(2400, c.Sensors.Single(s => s.Name == "Pump").Value);
        Assert.Equal(75, c.Sensors.Single(s => s.Name == "Pump Control").Value);
        Assert.Equal(1300, c.Sensors.Single(s => s.Name == "Fans").Value);
        Assert.Equal(60, c.Sensors.Single(s => s.Name == "Fans Control").Value);
    }

    [Fact]
    public void Formatted_follows_the_new_value_and_nothing_keeps_the_stale_text()
    {
        var c = Component();

        KrakenLhmSensors.Apply(c, Snap(31.46), true, null);

        Assert.Equal("31.5 °C", c.Sensors.Single(s => s.Name == "Liquid").Formatted);
        Assert.Equal("2400 RPM", c.Sensors.Single(s => s.Name == "Pump").Formatted);
        Assert.Equal("75.0%", c.Sensors.Single(s => s.Name == "Pump Control").Formatted);
        Assert.DoesNotContain(c.Sensors, s => s.Formatted == "stale" || s.FormattedMin == "stale" || s.FormattedMax == "stale");
    }

    [Fact]
    public void A_model_without_fans_has_only_its_own_sensors_overwritten()
    {
        var c = Component(fans: false);

        KrakenLhmSensors.Apply(c, Snap(30), true, null);

        Assert.Equal(3, c.Sensors.Count);
        Assert.DoesNotContain(c.Sensors, s => s.Name.StartsWith("Fans"));
    }

    [Fact]
    public void Unrecognised_sensors_are_left_alone()
    {
        var c = Component();
        c.Sensors.Add(Sensor("/x", "Other", "Temperature", 5));

        KrakenLhmSensors.Apply(c, Snap(30), true, null);

        var other = c.Sensors.Single(s => s.Name == "Other");
        Assert.Equal(5, other.Value);
        Assert.Equal("stale", other.Formatted);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Without_a_valid_liquid_reading_the_sensors_stay_and_read_unavailable(double liquid)
    {
        var c = Component();
        var ids = c.Sensors.Select(s => s.Id).ToList();

        KrakenLhmSensors.Apply(c, Snap(liquid), true, null);

        AssertUnavailable(c, ids);
    }

    private static void AssertUnavailable(HardwareComponent c, System.Collections.Generic.List<string> ids)
    {
        Assert.Equal(ids, c.Sensors.Select(s => s.Id).ToList());
        Assert.All(c.Sensors, s =>
        {
            Assert.Equal(0f, s.Value);
            Assert.Equal(0f, s.Min);
            Assert.Equal(0f, s.Max);
            Assert.Equal("-", s.Formatted);
            Assert.Equal("-", s.FormattedMin);
            Assert.Equal("-", s.FormattedMax);
        });
    }

    [Fact]
    public void A_stale_hub_status_keeps_the_sensors_unavailable_not_at_the_last_value()
    {
        var c = Component();
        var ids = c.Sensors.Select(s => s.Id).ToList();
        KrakenLhmSensors.Apply(c, Snap(30), true, null);

        KrakenLhmSensors.Apply(c, Snap(30), false, null);

        AssertUnavailable(c, ids);
    }

    [Fact]
    public void An_unavailable_reading_leaves_the_session_range_untouched()
    {
        var range = new ConcurrentDictionary<string, (float Min, float Max)>();
        KrakenLhmSensors.Apply(Component(), Snap(30), true, range);

        KrakenLhmSensors.Apply(Component(), Snap(30), false, range);

        Assert.Equal((30f, 30f), range["/nzxtkrakenz/0/temperature/0"]);
    }

    [Fact]
    public void No_value_written_is_ever_non_finite()
    {
        var c = Component();

        KrakenLhmSensors.Apply(c, Snap(double.MaxValue), true, null);

        Assert.All(c.Sensors, s => Assert.True(float.IsFinite(s.Value) && float.IsFinite(s.Min) && float.IsFinite(s.Max)));
    }

    [Fact]
    public void Without_a_range_min_and_max_are_cleared()
    {
        var c = Component();

        KrakenLhmSensors.Apply(c, Snap(30), true, null);

        Assert.All(c.Sensors, s => Assert.True(s.Min == 0 && s.Max == 0));
    }

    [Fact]
    public void The_range_tracks_session_extremes_across_calls()
    {
        var range = new ConcurrentDictionary<string, (float Min, float Max)>();
        foreach (var liquid in new[] { 30.0, 35.0, 28.0, 32.0 })
        {
            var c = Component();
            KrakenLhmSensors.Apply(c, Snap(liquid), true, range);
            var s = c.Sensors.Single(x => x.Name == "Liquid");
            if (liquid == 32.0)
            {
                Assert.Equal(28f, s.Min);
                Assert.Equal(35f, s.Max);
                Assert.Equal("28.0 °C", s.FormattedMin);
                Assert.Equal("35.0 °C", s.FormattedMax);
            }
        }
    }

    [Theory]
    [InlineData(0x3008, "NZXT Kraken Z3")]
    [InlineData(0x300C, "NZXT Kraken Elite")]
    [InlineData(0x3012, "NZXT Kraken Elite v2")]
    [InlineData(0x300E, "NZXT Kraken")]
    [InlineData(0x2007, "NZXT Kraken X3")]
    [InlineData(0x2014, "NZXT Kraken X3")]
    public void Each_hub_model_LHM_drives_maps_to_the_name_LHM_gives_it(int productId, string lhmName)
    {
        Assert.Equal(lhmName, KrakenLhmSensors.LhmName(productId));
        Assert.True(KrakenLhmSensors.IsHubDriven(true, productId, lhmName));
        Assert.False(KrakenLhmSensors.IsHubDriven(false, productId, lhmName));
    }

    [Fact]
    public void A_model_LHM_does_not_claim_and_a_different_name_are_not_overridden()
    {
        Assert.Null(KrakenLhmSensors.LhmName(0x3014));
        Assert.False(KrakenLhmSensors.IsHubDriven(true, 0x3014, "NZXT Kraken"));
        Assert.False(KrakenLhmSensors.IsHubDriven(true, 0x300E, "NZXT Kraken Elite"));
        Assert.False(KrakenLhmSensors.IsHubDriven(true, 0x3012, "NZXT Kraken Elite V2"));
        Assert.False(KrakenLhmSensors.IsHubDriven(null, "NZXT Kraken"));
    }

    [Fact]
    public void Every_hub_row_is_either_mapped_or_known_unclaimed()
    {
        var unclaimed = KrakenModel.All.Where(m => KrakenLhmSensors.LhmName(m.ProductId) is null).Select(m => m.ProductId);
        Assert.Equal(new[] { 0x3014 }, unclaimed);
    }

    [Fact]
    public void A_hub_that_is_not_connected_leaves_LHM_polling()
    {
        Assert.False(KrakenLhmSensors.IsHubDriven(new KrakenHub(null), "NZXT Kraken Elite v2"));
    }
}
