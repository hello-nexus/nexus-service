using System.Linq;
using Nexus.Service.Peripherals.Corsair.Psu;
using Xunit;

namespace Nexus.Service.Tests.Corsair.Psu;

public class CorsairPsuProtocolTests
{
    private static byte[] Trim(byte[] frame)
    {
        var end = frame.Length;
        while (end > 0 && frame[end - 1] == 0) end--;
        return frame[..end];
    }

    [Fact]
    public void Frames_are_sixty_four_bytes_with_the_documented_bytes()
    {
        Assert.Equal(new byte[] { 0xFE, 0x03 }, Trim(CorsairPsuProtocol.BuildInit()));
        Assert.Equal(new byte[] { 0x02, 0xF0, 0x01 }, Trim(CorsairPsuProtocol.BuildSetSoftwareMode()));
        Assert.Equal(new byte[] { 0x02, 0xF0 }, Trim(CorsairPsuProtocol.BuildSetHardwareMode()));
        Assert.Equal(new byte[] { 0x03, 0xF0 }, Trim(CorsairPsuProtocol.BuildReadMode()));
        Assert.Equal(new byte[] { 0x02, 0x3B, 0x50 }, Trim(CorsairPsuProtocol.BuildSetDuty(80)));
        Assert.Equal(new byte[] { 0x03, 0x90 }, Trim(CorsairPsuProtocol.BuildReadFanSpeed()));
        Assert.All(
            new[]
            {
                CorsairPsuProtocol.BuildInit(), CorsairPsuProtocol.BuildSetSoftwareMode(), CorsairPsuProtocol.BuildSetHardwareMode(),
                CorsairPsuProtocol.BuildReadMode(), CorsairPsuProtocol.BuildSetDuty(50), CorsairPsuProtocol.BuildReadFanSpeed(),
            },
            f => Assert.Equal(64, f.Length));
    }

    [Theory]
    [InlineData(-5, 30)]
    [InlineData(0, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 30)]
    [InlineData(64, 64)]
    [InlineData(100, 100)]
    [InlineData(250, 100)]
    public void Duty_is_clamped_to_the_documented_floor_and_ceiling(int requested, int expected)
    {
        Assert.Equal(expected, CorsairPsuProtocol.ClampDuty(requested));
        Assert.Equal((byte)expected, CorsairPsuProtocol.BuildSetDuty(requested)[2]);
    }

    [Fact]
    public void Reply_must_echo_the_first_two_command_bytes()
    {
        var cmd = CorsairPsuProtocol.BuildReadMode();
        var good = new byte[64];
        good[0] = 0x03;
        good[1] = 0xF0;
        Assert.True(CorsairPsuProtocol.IsEchoOf(cmd, good));

        var wrongCommand = (byte[])good.Clone();
        wrongCommand[1] = 0x3B;
        Assert.False(CorsairPsuProtocol.IsEchoOf(cmd, wrongCommand));

        var uninitialized = new byte[64];
        uninitialized[0] = 0x02;
        uninitialized[1] = 0xFE;
        Assert.False(CorsairPsuProtocol.IsEchoOf(cmd, uninitialized));

        Assert.False(CorsairPsuProtocol.IsEchoOf(cmd, good.Take(10).ToArray()));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void Mode_reply_parses(byte value, bool software)
    {
        var reply = new byte[64];
        reply[2] = value;
        Assert.Equal(software, CorsairPsuProtocol.ParseSoftwareMode(reply));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0xFF)]
    public void Mode_reply_outside_the_two_modes_is_rejected(byte value)
    {
        var reply = new byte[64];
        reply[2] = value;
        Assert.Null(CorsairPsuProtocol.ParseSoftwareMode(reply));
    }

    [Fact]
    public void Linear_rpm_decodes_mantissa_and_exponent()
    {
        // mantissa 600, exponent 1 => 1200
        int raw = (1 << 11) | 600;
        var reply = new byte[64];
        reply[2] = (byte)(raw & 0xFF);
        reply[3] = (byte)(raw >> 8);
        Assert.Equal(1200, CorsairPsuProtocol.ParseRpm(reply));

        // negative mantissa is implausible
        int neg = (0 << 11) | (2048 - 10);
        reply[2] = (byte)(neg & 0xFF);
        reply[3] = (byte)(neg >> 8);
        Assert.Null(CorsairPsuProtocol.ParseRpm(reply));
    }

    [Theory]
    [InlineData(0x1C03)]
    [InlineData(0x1C04)]
    [InlineData(0x1C05)]
    [InlineData(0x1C06)]
    [InlineData(0x1C07)]
    [InlineData(0x1C1E)]
    [InlineData(0x1C08)]
    [InlineData(0x1C1F)]
    [InlineData(0x1C09)]
    [InlineData(0x1C0A)]
    [InlineData(0x1C0B)]
    [InlineData(0x1C0C)]
    [InlineData(0x1C0D)]
    public void Listed_models_are_supported(int pid) => Assert.True(CorsairPsuProtocol.IsSupported(0x1B1C, pid));

    [Theory]
    [InlineData(0x1B1C, 0x1C02)]
    [InlineData(0x1B1C, 0x1C0E)]
    [InlineData(0x1B1D, 0x1C1F)]
    public void Other_devices_are_not_supported(int vid, int pid) => Assert.False(CorsairPsuProtocol.IsSupported(vid, pid));
}
