using Nexus.Service.Models.Sensors;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxPanoramaSensorFormatTests
{
    private static HardwareSensor Sensor(string type, float value) => new()
    {
        Id = "t/s",
        Name = "S",
        Type = type,
        Value = value,
        Parent = new SensorParent(),
    };

    [Theory]
    [InlineData("Temperature", 65.34f, "65.3°C")]
    [InlineData("Load", 42.26f, "42.3%")]
    [InlineData("Power", 12.34f, "12.3W")]
    [InlineData("Voltage", 0.85f, "0.850V")]
    [InlineData("Current", 1.5f, "1.500A")]
    [InlineData("Clock", 4200.4f, "4200MHz")]
    [InlineData("Fan", 1200.4f, "1200RPM")]
    [InlineData("Framerate", 143.6f, "144fps")]
    [InlineData("FrameTime", 16.66f, "16.7ms")]
    [InlineData("Data", 12.34f, "12.3GB")]
    [InlineData("Throughput", 2048f, "2.0KB/s")]
    public void FormatSensorValue_UsesTheSharedRule(string type, float value, string expected)
    {
        Assert.Equal(expected, TryxPanoramaHub.FormatSensorValue(Sensor(type, value)));
    }

    [Theory]
    [InlineData("Temperature", 65.5f, "66°C")]
    [InlineData("Temperature", -0.5f, "-1°C")]
    [InlineData("Power", 12.5f, "13W")]
    [InlineData("Load", 42.4f, "42%")]
    [InlineData("Voltage", 0.85f, "0.850V")]
    [InlineData("FrameTime", 16.66f, "16.7ms")]
    public void FormatSensorValue_WholeNumbers_RoundsOnlyTheRoundedTypes(string type, float value, string expected)
    {
        Assert.Equal(expected, TryxPanoramaHub.FormatSensorValue(Sensor(type, value), wholeNumbers: true));
    }
}
