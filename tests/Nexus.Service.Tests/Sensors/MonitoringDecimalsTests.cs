using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

public class MonitoringDecimalsTests
{
    [Theory]
    [InlineData("Temperature", 65.34f, false, "65.3")]
    [InlineData("Power", 12.34f, false, "12.3")]
    [InlineData("Load", 42.26f, false, "42.3")]
    [InlineData("Control", 55f, false, "55.0")]
    [InlineData("Level", 80f, false, "80.0")]
    [InlineData("Flow", 99.64f, false, "99.6")]
    [InlineData("Noise", 31.25f, false, "31.3")]
    [InlineData("Frequency", 60f, false, "60.0")]
    [InlineData("Voltage", 0.85f, false, "0.850")]
    [InlineData("Current", 1.5f, false, "1.500")]
    [InlineData("FrameTime", 16.66f, false, "16.7")]
    [InlineData("Clock", 4200.6f, false, "4201")]
    [InlineData("Fan", 1199.5f, false, "1200")]
    [InlineData("Framerate", 143.6f, false, "144")]
    // Whole numbers on: rounded types go to integers, the rest keep their decimals.
    [InlineData("Temperature", 65.4f, true, "65")]
    [InlineData("Temperature", 65.5f, true, "66")]
    [InlineData("Power", 12.5f, true, "13")]
    [InlineData("Load", 0.5f, true, "1")]
    [InlineData("Flow", 99.6f, true, "100")]
    [InlineData("Noise", 31.4f, true, "31")]
    [InlineData("Frequency", 59.5f, true, "60")]
    [InlineData("Voltage", 0.85f, true, "0.850")]
    [InlineData("Current", 1.5f, true, "1.500")]
    [InlineData("FrameTime", 16.66f, true, "16.7")]
    [InlineData("Clock", 4200.6f, true, "4201")]
    // Negatives round half away from zero; a small negative never prints as negative zero.
    [InlineData("Temperature", -0.5f, true, "-1")]
    [InlineData("Temperature", -1.25f, false, "-1.3")]
    [InlineData("Temperature", -0.04f, false, "0.0")]
    public void FormatNumber_AppliesThePerTypeRule(string type, float value, bool whole, string expected)
    {
        Assert.Equal(expected, MonitoringDecimals.FormatNumber(value, type, whole));
    }

    [Theory]
    [InlineData("Data")]
    [InlineData("SmallData")]
    [InlineData("Throughput")]
    [InlineData("Energy")]
    [InlineData("TimeSpan")]
    [InlineData("")]
    [InlineData(null)]
    public void FormatNumber_UngovernedType_ReturnsNull(string? type)
    {
        Assert.Null(MonitoringDecimals.FormatNumber(12.34f, type, false));
        Assert.False(MonitoringDecimals.Governs(type));
    }

    [Fact]
    public void FormatNumber_NonFinite_ReturnsNull()
    {
        Assert.Null(MonitoringDecimals.FormatNumber(float.NaN, "Temperature", false));
    }
}
