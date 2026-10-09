using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Monitoring;

namespace Nexus.Service.Tests.Monitoring;

/// <summary>
/// Covers the projection that puts cooling-hub probes into the monitoring extras topic, which is
/// what makes them selectable in the widget sensor picker's Cooler category.
/// </summary>
public class HubCoolerSensorsTests
{
    private static TemperatureSource Src(string id, string name, float value, string? deviceId, string? deviceName) =>
        new() { Id = id, Name = name, Category = "Hub", Value = value, DeviceId = deviceId, DeviceName = deviceName };

    [Fact]
    public void Groups_sources_into_one_component_per_device_preserving_order()
    {
        var components = HubCoolerSensors.Build(new[]
        {
            Src("np50:A:legacy:cable", "NP50 cable probe", 33f, "np50:A", "HYTE NP50"),
            Src("np50:A:port1:dev1:temp", "FP12 probe (Port 1 #1)", 35f, "np50:A", "HYTE NP50"),
            Src("qseries:B:coolant-in", "Coolant in", 30f, "qseries:B", "HYTE Q60"),
        });

        Assert.Collection(components,
            np50 =>
            {
                Assert.Equal("np50:A", np50.Id);
                Assert.Equal("HYTE NP50", np50.Name);
                Assert.Equal(new[] { "NP50 cable probe", "FP12 probe (Port 1 #1)" }, np50.Sensors.Select(s => s.Name));
            },
            q =>
            {
                Assert.Equal("qseries:B", q.Id);
                Assert.Equal("HYTE Q60", q.Name);
                Assert.Equal("Coolant in", Assert.Single(q.Sensors).Name);
            });
    }

    [Fact]
    public void Sensor_keeps_the_curve_source_id_and_is_typed_as_a_temperature()
    {
        var sensor = Assert.Single(Assert.Single(HubCoolerSensors.Build(new[]
        {
            Src("np50:A:port1:dev1:temp", "FP12 probe (Port 1 #1)", 35.4f, "np50:A", "HYTE NP50"),
        })).Sensors);

        // Same id as the curve picker, so one probe is one sensor in both places.
        Assert.Equal("np50:A:port1:dev1:temp", sensor.Id);
        Assert.Equal("Temperature", sensor.Type);
        Assert.Equal("°C", sensor.Units);
        Assert.Equal(35.4f, sensor.Value);
        Assert.Equal("35.4 °C", sensor.Formatted);
        Assert.Equal("np50:A", sensor.Parent.Id);
        Assert.Equal("HYTE NP50", sensor.Parent.Name);
    }

    [Fact]
    public void Skips_motherboard_and_cpu_sources_that_carry_no_device()
    {
        // The platform sensor provider already reports these; re-emitting would duplicate them.
        Assert.Empty(HubCoolerSensors.Build(new[]
        {
            Src("/lpc/nct6687d/temperature/0", "CPU", 53f, null, null),
            Src("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", 46f, "", ""),
        }));
    }

    [Fact]
    public void Drops_a_non_finite_reading_rather_than_failing_the_whole_envelope()
    {
        var components = HubCoolerSensors.Build(new[]
        {
            Src("np50:A:port1:dev1:temp", "bad", float.NaN, "np50:A", "HYTE NP50"),
            Src("np50:A:port1:dev2:temp", "good", 31f, "np50:A", "HYTE NP50"),
        });

        Assert.Equal("good", Assert.Single(Assert.Single(components).Sensors).Name);
    }

    [Fact]
    public void Falls_back_to_the_device_id_when_no_product_name_is_supplied()
    {
        var component = Assert.Single(HubCoolerSensors.Build(new[]
        {
            Src("corsair:ch1:temp", "QX probe (Fan 1)", 28f, "corsair:hub", null),
        }));

        Assert.Equal("corsair:hub", component.Name);
    }

    [Fact]
    public void Handles_an_empty_source_list()
    {
        Assert.Empty(HubCoolerSensors.Build(new List<TemperatureSource>()));
    }

    [Fact]
    public void A_device_pump_joins_its_coolant_probe_as_an_rpm_sensor()
    {
        var component = Assert.Single(HubCoolerSensors.Build(
            new[] { Src("lianli-galahad2-lcd:coolant", "Liquid", 31.4f, "lianli-galahad2-lcd", "Lian Li Galahad II LCD") },
            new[]
            {
                new FanChannel
                {
                    Id = "lianli-galahad2-lcd:pump", Name = "Galahad II LCD Pump", Rpm = 3580, Kind = FanKinds.Pump,
                    DeviceId = "lianli-galahad2-lcd", DeviceName = "Lian Li Galahad II LCD",
                },
            }));

        Assert.Equal(new[] { "Liquid", "Galahad II LCD Pump" }, component.Sensors.Select(s => s.Name));
        var pump = component.Sensors[1];
        Assert.Equal("lianli-galahad2-lcd:pump", pump.Id);
        Assert.Equal("Fan", pump.Type);
        Assert.Equal("RPM", pump.Units);
        Assert.Equal(3580f, pump.Value);
        Assert.Equal("3580 RPM", pump.Formatted);
    }

    [Fact]
    public void A_pump_without_a_device_or_a_readable_speed_is_skipped()
    {
        Assert.Empty(HubCoolerSensors.Build(
            new List<TemperatureSource>(),
            new[]
            {
                new FanChannel { Id = "/lpc/nct6687d/control/1", Name = "Pump Fan #1", Rpm = 2127, Kind = FanKinds.Pump },
                new FanChannel { Id = "minihub:pump", Name = "Pump", Kind = FanKinds.Pump, DeviceId = "minihub", RpmUnavailable = true },
            }));
    }
}
