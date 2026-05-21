using System;
using Qos.Service.Peripherals.Hyte.MiniHub;

namespace Qos.Service.Tests.MiniHub;

/// <summary>
/// Wire-protocol coverage for the IBP MiniHub. Reference for every
/// behaviour pinned below is HYTE's shipping nexus-control-service —
/// specifically <c>LightDancing/Hardware/Devices/HYTE/Hub/IBPMiniHubController.cs</c>
/// (the working production agent against the same firmware). Where
/// <c>hyte-refs/hyte-documents/firmware-protocol/MiniHub/main.md</c>
/// disagrees with the shipping code, the code wins — that's what the
/// hardware actually accepts.
/// </summary>
public class MiniHubProtocolTests
{
    // ── Control commands ──

    [Fact]
    public void BuildGetFirmwareVersion_emits_FF_DD_02()
    {
        Assert.Equal(new byte[] { 0xFF, 0xDD, 0x02 }, MiniHubProtocol.BuildGetFirmwareVersion());
    }

    [Theory]
    [InlineData(MiniHubProtocol.RgbModeSoftware)]
    [InlineData(MiniHubProtocol.RgbModeMotherboard)]
    public void BuildSetRgbControlMode_emits_FF_DD_03_mode(byte mode)
    {
        Assert.Equal(new byte[] { 0xFF, 0xDD, 0x03, mode }, MiniHubProtocol.BuildSetRgbControlMode(mode));
    }

    [Fact]
    public void BuildSetRgbControlMode_rejects_unknown_mode_byte()
    {
        Assert.Throws<ArgumentException>(() => MiniHubProtocol.BuildSetRgbControlMode(0x99));
    }

    // ── LED streaming framing ──

    [Theory]
    [InlineData(1, 157)]
    [InlineData(2, 157)]
    [InlineData(3, 157)]
    [InlineData(4, 307)]
    public void BuildLightingStream_emits_fixed_padded_buffer_per_channel(int channel, int expectedLength)
    {
        var buf = MiniHubProtocol.BuildLightingStream(channel, new[] { new RgbColor(1, 2, 3) });
        Assert.Equal(expectedLength, buf.Length);
        Assert.Equal(0xFF, buf[0]);
        Assert.Equal(0xEE, buf[1]);
        Assert.Equal(0x03, buf[2]);
        Assert.Equal((byte)channel, buf[3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void BuildLightingStream_rejects_out_of_range_channels(int channel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MiniHubProtocol.BuildLightingStream(channel, ReadOnlySpan<RgbColor>.Empty));
    }

    [Fact]
    public void BuildLightingStream_writes_GRB_byte_order_per_LED()
    {
        // Reference: LightDancing/Hardware/Devices/Components/MiniHubLedStrip.cs:59
        // writes `new byte[] { color.G, color.R, color.B }`. The firmware spec
        // doc (firmware-protocol/MiniHub/main.md:73) claims R G B but is
        // demonstrably wrong — the shipping reference agent emits GRB.
        var leds = new[]
        {
            new RgbColor(R: 0x11, G: 0x22, B: 0x33),
            new RgbColor(R: 0xAA, G: 0xBB, B: 0xCC),
        };
        var buf = MiniHubProtocol.BuildLightingStream(channel: 3, leds);
        Assert.Equal(0x22, buf[7 + 0]); // G
        Assert.Equal(0x11, buf[7 + 1]); // R
        Assert.Equal(0x33, buf[7 + 2]); // B
        Assert.Equal(0xBB, buf[7 + 3]);
        Assert.Equal(0xAA, buf[7 + 4]);
        Assert.Equal(0xCC, buf[7 + 5]);
    }

    [Fact]
    public void BuildLightingStream_always_emits_LedCount_magic_constant_0x0168()
    {
        // IBPMiniHubController.cs:200 hardcodes bytes 4/5 to 0x01 0x68 (= 360)
        // for every frame regardless of the real LED count. Match exactly.
        foreach (var channel in new[] { 1, 2, 3, 4 })
        {
            foreach (var ledCount in new[] { 0, 1, 8, 50, 100 })
            {
                var leds = new RgbColor[ledCount];
                var buf = MiniHubProtocol.BuildLightingStream(channel, leds);
                Assert.Equal(0x01, buf[4]);
                Assert.Equal(0x68, buf[5]);
            }
        }
    }

    [Fact]
    public void BuildLightingStream_clamps_to_max_LED_count_per_channel()
    {
        // Channel 4 ("big" output) caps at 100 LEDs; channels 1-3 cap at 50.
        // Anything past the cap is silently dropped. The buffer length is
        // fixed (157 / 307) and the in-cap LEDs fill it exactly, so there
        // is no trailing byte to inspect — the proof of clamping is that
        // every byte we DID write matches the supplied colour and the
        // buffer stays at the documented length.
        var tooManyCh1 = new RgbColor[200];
        for (var i = 0; i < tooManyCh1.Length; i++) tooManyCh1[i] = new RgbColor(0xFF, 0xFF, 0xFF);
        var ch1 = MiniHubProtocol.BuildLightingStream(1, tooManyCh1);
        Assert.Equal(157, ch1.Length);
        for (var i = 0; i < 50; i++)
        {
            var off = 7 + i * 3;
            Assert.Equal(0xFF, ch1[off + 0]);
            Assert.Equal(0xFF, ch1[off + 1]);
            Assert.Equal(0xFF, ch1[off + 2]);
        }

        var tooManyCh4 = new RgbColor[200];
        for (var i = 0; i < tooManyCh4.Length; i++) tooManyCh4[i] = new RgbColor(0xFF, 0xFF, 0xFF);
        var ch4 = MiniHubProtocol.BuildLightingStream(4, tooManyCh4);
        Assert.Equal(307, ch4.Length);
        Assert.Equal(0xFF, ch4[7 + 99 * 3 + 0]);
    }

    [Fact]
    public void BuildLightingStream_zero_pads_trailing_LEDs_so_disconnected_indices_dark_blank()
    {
        // Whatever the firmware actually does with bytes past `declaredCount`,
        // we want them deterministically zero. New-byte[] gives us that by
        // default — this test guards against any future refactor that
        // accidentally introduces uninitialized rented buffers.
        var leds = new[] { new RgbColor(0x10, 0x20, 0x30) };
        var buf = MiniHubProtocol.BuildLightingStream(channel: 4, leds);
        // First LED present.
        Assert.Equal(0x20, buf[7]);
        Assert.Equal(0x10, buf[8]);
        Assert.Equal(0x30, buf[9]);
        // Every byte from the second LED onwards is zero.
        for (var i = 10; i < buf.Length; i++)
        {
            Assert.Equal(0x00, buf[i]);
        }
    }

    // ── Fan commands ──

    [Theory]
    [InlineData(MiniHubProtocol.FanModeSoftware)]
    [InlineData(MiniHubProtocol.FanModeMotherboard)]
    public void BuildSetFanControlMode_emits_FF_DD_05_mode(byte mode)
    {
        Assert.Equal(new byte[] { 0xFF, 0xDD, 0x05, mode }, MiniHubProtocol.BuildSetFanControlMode(mode));
    }

    [Fact]
    public void BuildSetFanControlMode_rejects_unknown_mode_byte()
    {
        Assert.Throws<ArgumentException>(() => MiniHubProtocol.BuildSetFanControlMode(0x99));
    }

    [Fact]
    public void BuildSetFanSpeed_matches_HYTE_reference_7_byte_frame()
    {
        // HYTE's shipping IBPMiniHubController.SetFanSpeed (line 324) writes
        // only `FF DD 04 00 <p1> 00 <p2>` — 7 bytes total — even though the
        // spec doc shows an 11-byte frame. The firmware ignores the trailing
        // channel-3/4 bytes and the shipping agent never sends them.
        Assert.Equal(
            new byte[] { 0xFF, 0xDD, 0x04, 0x00, 50, 0x00, 80 },
            MiniHubProtocol.BuildSetFanSpeed(50, 80));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-50, 10)]
    [InlineData(5, 10)]
    [InlineData(10, 10)]
    [InlineData(75, 75)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void BuildSetFanSpeed_clamps_inputs_to_10_through_100(int input, byte expected)
    {
        var buf = MiniHubProtocol.BuildSetFanSpeed(input, input);
        Assert.Equal(expected, buf[4]); // port 1
        Assert.Equal(expected, buf[6]); // port 2
    }

    [Fact]
    public void BuildGetFanSpeed_emits_FF_DD_06()
    {
        Assert.Equal(new byte[] { 0xFF, 0xDD, 0x06 }, MiniHubProtocol.BuildGetFanSpeed());
    }

    [Fact]
    public void TryParseFanSpeeds_applies_60000_over_period_times_0_4_formula()
    {
        // Reference: IBPMiniHubController.GetFanRPM (line 328).
        // rpm = 60000 / (periodByte * 0.4). Pick periods that yield clean ints.
        // period = 21 → 60000 / 8.4 ≈ 7142.857… → int = 7142
        // period = 88 → 60000 / 35.2 ≈ 1704.545… → int = 1704
        var response = new byte[]
        {
            0xFF, 0xDD, 0x06,
            0x01, 0x00, 21, // port 1: period byte = 21
            0x02, 0x00, 88, // port 2: period byte = 88
        };
        Assert.True(MiniHubProtocol.TryParseFanSpeeds(response, out var rpm1, out var rpm2));
        Assert.Equal(7142, rpm1);
        Assert.Equal(1704, rpm2);
    }

    [Fact]
    public void TryParseFanSpeeds_returns_zero_rpm_for_period_byte_0()
    {
        // Period 0 = no tach signal (port empty / fan stalled / no tach wire).
        // HYTE's reference also gates this — division by zero would be int.MaxValue.
        var response = new byte[] { 0xFF, 0xDD, 0x06, 0x01, 0x00, 0x00, 0x02, 0x00, 0x00 };
        Assert.True(MiniHubProtocol.TryParseFanSpeeds(response, out var rpm1, out var rpm2));
        Assert.Equal(0, rpm1);
        Assert.Equal(0, rpm2);
    }

    [Fact]
    public void TryParseFanSpeeds_rejects_short_or_wrong_header_responses()
    {
        Assert.False(MiniHubProtocol.TryParseFanSpeeds(
            new byte[] { 0xFF, 0xDD, 0x06, 0x01, 0x00 }, out _, out _));
        Assert.False(MiniHubProtocol.TryParseFanSpeeds(
            new byte[] { 0xFF, 0xCC, 0x06, 0x01, 0x00, 0x10, 0x02, 0x00, 0x10 }, out _, out _));
        Assert.False(MiniHubProtocol.TryParseFanSpeeds(
            new byte[] { 0xFF, 0xDD, 0x02, 0x01, 0x00, 0x10, 0x02, 0x00, 0x10 }, out _, out _));
    }

    // ── Firmware version parser ──

    [Fact]
    public void ParseFirmwareVersion_returns_dotted_version_string()
    {
        var response = new byte[] { 0xFF, 0xDD, 0x02, 1, 0, 1, 1 };
        Assert.Equal("1.0.1.1", MiniHubProtocol.ParseFirmwareVersion(response));
    }

    [Fact]
    public void ParseFirmwareVersion_returns_empty_on_unexpected_header()
    {
        Assert.Equal("", MiniHubProtocol.ParseFirmwareVersion(new byte[] { 0xFF, 0xCC, 0x02, 1, 0, 1, 1 }));
        Assert.Equal("", MiniHubProtocol.ParseFirmwareVersion(new byte[] { 0xFF, 0xDD })); // short
    }
}
