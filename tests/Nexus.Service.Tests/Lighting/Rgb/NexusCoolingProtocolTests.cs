using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// The payload bytes below are the ones CorsairCommanderCoreCoolingTest.cpp in the headless OpenRGB
/// fork pins from the daemon side, so a change to either layout fails one of the two suites.
/// </summary>
public class NexusCoolingProtocolTests
{
    private const string StatusHex = "01011c0cfa0003" + "0135094b" + "019603ff" + "000000ff" + "03" + "017b00" + "000000" + "01f1ff";

    [Fact]
    public void Parses_the_status_the_daemon_serializes()
    {
        Assert.True(NexusCoolingProtocol.TryParseStatus(Convert.FromHexString(StatusHex), out var status));

        Assert.NotNull(status);
        Assert.True(status!.Valid);
        Assert.Equal(0x0C1C, status.ProductId);
        Assert.Equal(250, status.AgeMs);
        Assert.Equal(3, status.Channels.Count);
        Assert.Equal(new CommanderCoreChannel(true, 2357, 75), status.Channels[0]);
        Assert.Equal(new CommanderCoreChannel(true, 918, null), status.Channels[1]);
        Assert.Equal(new CommanderCoreChannel(false, 0, null), status.Channels[2]);
        Assert.Equal(3, status.Temperatures.Count);
        Assert.Equal(new CommanderCoreTemperature(true, 12.3f), status.Temperatures[0]);
        Assert.False(status.Temperatures[1].Connected);
        Assert.Equal(-1.5f, status.Temperatures[2].Celsius);
    }

    [Fact]
    public void A_status_without_the_valid_flag_parses_as_no_data()
    {
        var bytes = Convert.FromHexString(StatusHex);
        bytes[1] = 0;

        Assert.True(NexusCoolingProtocol.TryParseStatus(bytes, out var status));

        Assert.False(status!.Valid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01011c0cfa00")]
    // Unknown version.
    [InlineData("02011c0cfa000000")]
    // Channel count above the cap.
    [InlineData("01011c0cfa000900")]
    // Channel list cut short.
    [InlineData("01011c0cfa0002013509ff")]
    // Temperature list cut short.
    [InlineData("01011c0cfa000002017b00")]
    // Trailing byte after the temperature list.
    [InlineData("01011c0cfa000001017b0000")]
    public void Rejects_a_malformed_status(string hex)
    {
        Assert.False(NexusCoolingProtocol.TryParseStatus(Convert.FromHexString(hex), out var status));

        Assert.Null(status);
    }

    [Fact]
    public void Builds_the_set_duties_request_the_daemon_parses()
    {
        var body = NexusCoolingProtocol.BuildSetDutiesBody(new[] { (0, 75), (3, 100) });

        Assert.Equal("0102004b0364", Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void Clamps_a_duty_into_range()
    {
        var body = NexusCoolingProtocol.BuildSetDutiesBody(new[] { (1, 140), (2, -5) });

        Assert.Equal("01020164" + "0200", Convert.ToHexString(body).ToLowerInvariant());
    }
}
