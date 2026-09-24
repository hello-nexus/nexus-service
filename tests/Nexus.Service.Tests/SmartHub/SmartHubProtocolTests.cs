using System;
using Nexus.Service.Peripherals.Hyte.SmartHub;

namespace Nexus.Service.Tests.SmartHub;

/// <summary>
/// Wire-protocol coverage for the HYTE Smart Hub (legacy enum
/// <c>USBDevices.ControlHub</c>). Reference for every byte pinned below is
/// HYTE's shipping nexus-control-service - the working production agent
/// against the same firmware:
///   • <c>LightDancing/Hardware/Devices/HYTE/Hub/ControlHubController.cs</c>
///   • <c>LightDancing/Common/SmartDeviceCommon/Command/ControlHubCommand.cs</c>
///   • <c>LightDancing/Common/SmartDeviceCommon/SmartDeviceMethods.cs</c> (RPM)
/// </summary>
public class SmartHubProtocolTests
{
    // ── Control / query commands ──

    [Fact]
    public void BuildGetFirmwareVersion_emits_FF_DD_02()
    {
        Assert.Equal(new byte[] { 0xFF, 0xDD, 0x02 }, SmartHubProtocol.BuildGetFirmwareVersion());
    }

    [Fact]
    public void BuildGetInfo_emits_FF_CC_01_00()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x01, 0x00 }, SmartHubProtocol.BuildGetInfo());
    }

    // ── Fan speed ──

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BuildSetFanSpeed_emits_FF_CC_02_with_1_based_wire_port(int channel)
    {
        // Reference: ControlHubCommand.SetFanSpeedByChannel sends `channel + 1`
        // (SetInitialFanSpeed likewise writes ports 1..4). A 0-based wire port
        // targets the wrong fan: hardware-confirmed (fan on physical port 2
        // ignored every FF CC 02 with port byte 1).
        Assert.Equal(
            new byte[] { 0xFF, 0xCC, 0x02, (byte)(channel + 1), 60, 0x01 },
            SmartHubProtocol.BuildSetFanSpeed(channel, 60, enabled: true));
    }

    [Fact]
    public void BuildSetFanSpeed_writes_enabled_byte_zero_when_disabled()
    {
        Assert.Equal(0x00, SmartHubProtocol.BuildSetFanSpeed(0, 50, enabled: false)[5]);
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(0, 0)]
    [InlineData(55, 55)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void BuildSetFanSpeed_clamps_duty_to_0_through_100(int input, byte expected)
    {
        Assert.Equal(expected, SmartHubProtocol.BuildSetFanSpeed(1, input, true)[4]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void BuildSetFanSpeed_rejects_out_of_range_channel(int channel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartHubProtocol.BuildSetFanSpeed(channel, 50, true));
    }

    // ── Firmware animation on/off ──

    [Fact]
    public void BuildSetFirmwareAnimation_on_emits_FF_CC_07_00()
    {
        // ControlHubCommand.SetFwAnimationOnOff: on => 0x00, off => 0x01 (inverted).
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x07, 0x00 }, SmartHubProtocol.BuildSetFirmwareAnimation(on: true));
    }

    [Fact]
    public void BuildSetFirmwareAnimation_off_emits_FF_CC_07_01()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x07, 0x01 }, SmartHubProtocol.BuildSetFirmwareAnimation(on: false));
    }

    [Fact]
    public void BuildGetFirmwareAnimation_emits_FF_CC_08()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x08 }, SmartHubProtocol.BuildGetFirmwareAnimation());
    }

    [Theory]
    [InlineData(0x00, true)]
    [InlineData(0x01, false)]
    public void TryParseFirmwareAnimation_reads_the_inverted_flag_from_byte_4(byte fwAnimOff, bool expectedOn)
    {
        // Y50 firmware main.c Get_Default_Animation: FF CC 08 <startAnimOff> <fwAnimOff> 00 00.
        var response = new byte[] { 0xFF, 0xCC, 0x08, 0x00, fwAnimOff, 0x00, 0x00 };
        Assert.True(SmartHubProtocol.TryParseFirmwareAnimation(response, out var on));
        Assert.Equal(expectedOn, on);
    }

    [Fact]
    public void TryParseFirmwareAnimation_rejects_short_or_wrong_header_responses()
    {
        Assert.False(SmartHubProtocol.TryParseFirmwareAnimation(new byte[] { 0xFF, 0xCC, 0x08, 0x00, 0x00 }, out _));
        Assert.False(SmartHubProtocol.TryParseFirmwareAnimation(new byte[] { 0xFF, 0xCC, 0x0D, 0x00, 0x00, 0x00, 0x00 }, out _));
    }

    // ── MCU setting (firmware FW_Animation, FF CC 0C / FF CC 0D) ──

    [Fact]
    public void BuildSetMcuSetting_emits_FF_CC_0C_frame_with_trailing_01_gate()
    {
        // Y50 firmware usbd_cdc_if.c: the 0x0C handler only matches when
        // buffer[9]==0x01 - without the trailing gate byte the write is ignored.
        Assert.Equal(
            new byte[] { 0xFF, 0xCC, 0x0C, 0x02, 0x11, 0x22, 0x33, 80, 45, 0x01 },
            SmartHubProtocol.BuildSetMcuSetting(SmartHubProtocol.McuAnimationRainbow, 0x11, 0x22, 0x33, 80, 45));
        Assert.Equal(SmartHubProtocol.McuSettingLength,
            SmartHubProtocol.BuildSetMcuSetting(SmartHubProtocol.McuAnimationColor, 0, 0, 0, 0, 0).Length);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(55, 55)]
    [InlineData(100, 100)]
    [InlineData(140, 100)]
    public void BuildSetMcuSetting_clamps_brightness_and_fan_percent_to_0_through_100(int input, byte expected)
    {
        var buf = SmartHubProtocol.BuildSetMcuSetting(SmartHubProtocol.McuAnimationColor, 0, 0, 0, input, input);
        Assert.Equal(expected, buf[7]); // brightness
        Assert.Equal(expected, buf[8]); // fan%
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void BuildSetMcuSetting_rejects_out_of_range_animation(int animation)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SmartHubProtocol.BuildSetMcuSetting(animation, 0, 0, 0, 50, 50));
    }

    [Fact]
    public void BuildGetMcuSetting_emits_FF_CC_0D()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x0D }, SmartHubProtocol.BuildGetMcuSetting());
    }

    [Fact]
    public void TryParseMcuSetting_decodes_animation_color_brightness_and_fan_percent()
    {
        // Y50 firmware main.c Get_FW_Animation transmit:
        //   FF CC 0D <anim> <R> <G> <B> <brightness%> <fan%>.
        var response = new byte[] { 0xFF, 0xCC, 0x0D, 0x03, 0xAA, 0xBB, 0xCC, 70, 35 };
        Assert.True(SmartHubProtocol.TryParseMcuSetting(response, out var setting));
        Assert.NotNull(setting);
        Assert.Equal(
            new SmartHubProtocol.SmartHubMcuSetting(
                Animation: SmartHubProtocol.McuAnimationBreathe,
                R: 0xAA, G: 0xBB, B: 0xCC, Brightness: 70, FanPercent: 35),
            setting!.Value);
    }

    [Fact]
    public void TryParseMcuSetting_rejects_short_or_wrong_header_responses()
    {
        Assert.False(SmartHubProtocol.TryParseMcuSetting(new byte[] { 0xFF, 0xCC, 0x0D, 1, 2 }, out _)); // short
        Assert.False(SmartHubProtocol.TryParseMcuSetting(
            new byte[] { 0xFF, 0xCC, 0x0C, 1, 0, 0, 0, 50, 50 }, out var setting)); // 0x0C, not the 0x0D echo
        Assert.Null(setting);
    }

    // ── LED streaming framing ──

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void BuildLightingStream_emits_fixed_607_byte_frame_with_FF_EE_01_port_header(int port)
    {
        var buf = SmartHubProtocol.BuildLightingStream(port, new[] { new RgbColor(1, 2, 3) });
        Assert.Equal(SmartHubProtocol.LightingFrameLength, buf.Length);
        Assert.Equal(607, buf.Length);
        Assert.Equal(0xFF, buf[0]);
        Assert.Equal(0xEE, buf[1]);
        Assert.Equal(0x01, buf[2]);
        Assert.Equal((byte)port, buf[3]);
        // Reserved header bytes are zero (FF EE 01 <port> 00 00 00).
        Assert.Equal(0x00, buf[4]);
        Assert.Equal(0x00, buf[5]);
        Assert.Equal(0x00, buf[6]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void BuildLightingStream_rejects_out_of_range_ports(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SmartHubProtocol.BuildLightingStream(port, ReadOnlySpan<RgbColor>.Empty));
    }

    [Fact]
    public void BuildLightingStream_writes_GRB_byte_order_per_LED()
    {
        // Reference: LedStrip.ProcessColor emits `new byte[] { color.G, color.R, color.B }`.
        var leds = new[]
        {
            new RgbColor(R: 0x11, G: 0x22, B: 0x33),
            new RgbColor(R: 0xAA, G: 0xBB, B: 0xCC),
        };
        var buf = SmartHubProtocol.BuildLightingStream(port: 2, leds);
        Assert.Equal(0x22, buf[7 + 0]); // G
        Assert.Equal(0x11, buf[7 + 1]); // R
        Assert.Equal(0x33, buf[7 + 2]); // B
        Assert.Equal(0xBB, buf[7 + 3]);
        Assert.Equal(0xAA, buf[7 + 4]);
        Assert.Equal(0xCC, buf[7 + 5]);
    }

    [Fact]
    public void BuildLightingStream_clamps_to_max_200_LEDs_per_port()
    {
        var tooMany = new RgbColor[300];
        for (var i = 0; i < tooMany.Length; i++) tooMany[i] = new RgbColor(0xFF, 0xFF, 0xFF);
        var buf = SmartHubProtocol.BuildLightingStream(1, tooMany);
        Assert.Equal(607, buf.Length);
        // Last in-cap LED (#199) is written…
        Assert.Equal(0xFF, buf[7 + 199 * 3 + 0]);
        // …and that's the final colour byte; there is no LED #200 slot.
        Assert.Equal(7 + SmartHubProtocol.MaxLedsPerPort * 3, buf.Length);
    }

    [Fact]
    public void BuildLightingStream_zero_pads_trailing_LEDs_so_unaddressed_indices_go_dark()
    {
        var leds = new[] { new RgbColor(0x10, 0x20, 0x30) };
        var buf = SmartHubProtocol.BuildLightingStream(port: 3, leds);
        Assert.Equal(0x20, buf[7]); // G
        Assert.Equal(0x10, buf[8]); // R
        Assert.Equal(0x30, buf[9]); // B
        for (var i = 10; i < buf.Length; i++) Assert.Equal(0x00, buf[i]);
    }

    // ── Firmware version parser ──

    [Fact]
    public void ParseFirmwareVersion_returns_dotted_version_string()
    {
        var response = new byte[] { 0xFF, 0xDD, 0x02, 1, 0, 2, 1 };
        Assert.Equal("1.0.2.1", SmartHubProtocol.ParseFirmwareVersion(response));
    }

    [Fact]
    public void ParseFirmwareVersion_returns_empty_on_unexpected_header()
    {
        Assert.Equal("", SmartHubProtocol.ParseFirmwareVersion(new byte[] { 0xFF, 0xCC, 0x02, 1, 0, 2, 1 }));
        Assert.Equal("", SmartHubProtocol.ParseFirmwareVersion(new byte[] { 0xFF, 0xDD })); // short
    }

    // ── Hub-info / channel parser ──

    [Fact]
    public void DecodeFanRpm_applies_60000_over_speed_div_4_formula()
    {
        // rpm = 60000 / (speedH + speedL/100) / 4.
        Assert.Equal(1500, SmartHubProtocol.DecodeFanRpm(10, 0)); // 60000/10/4
        Assert.Equal(750, SmartHubProtocol.DecodeFanRpm(20, 0));  // 60000/20/4
        Assert.Equal(0, SmartHubProtocol.DecodeFanRpm(0, 0));     // no tach signal
    }

    [Fact]
    public void TryParseChannelInfo_decodes_all_four_channels_rpm_and_enabled()
    {
        // Tach layout (ControlHubDeviceBase.CheckAndUpdateChannelInfo):
        //   ch0 speedH=[3] speedL=[4], ch1=[5,6], ch2=[7,8], ch3=[9,10].
        // Enabled flags read back REVERSED - bench-probed on fw 1.0.0.1:
        //   FF CC 02 N … en flips readback [15-N] for every N 1..4,
        //   so ch0 enabled=[14], ch1=[13], ch2=[12], ch3=[11].
        var response = new byte[SmartHubProtocol.GetInfoResponseLength];
        response[0] = 0xFF; response[1] = 0xCC; response[2] = 0x01;
        response[3] = 10; response[4] = 0;   // ch0 → 1500 rpm
        response[5] = 20; response[6] = 0;   // ch1 → 750 rpm
        response[7] = 0; response[8] = 0;    // ch2 → 0 rpm
        response[9] = 30; response[10] = 0;  // ch3 → 500 rpm
        response[11] = 0x00;                  // ch3 disabled
        response[12] = 0x01;                  // ch2 enabled
        response[13] = 0x00;                  // ch1 disabled
        response[14] = 0x01;                  // ch0 enabled

        Assert.True(SmartHubProtocol.TryParseChannelInfo(response, out var channels));
        Assert.NotNull(channels);
        Assert.Equal(4, channels!.Length);
        Assert.Equal((1500, true), (channels[0].Rpm, channels[0].Enabled));
        Assert.Equal((750, false), (channels[1].Rpm, channels[1].Enabled));
        Assert.Equal((0, true), (channels[2].Rpm, channels[2].Enabled));
        Assert.Equal((500, false), (channels[3].Rpm, channels[3].Enabled));
    }

    [Fact]
    public void TryParseChannelInfo_rejects_short_or_wrong_header_responses()
    {
        Assert.False(SmartHubProtocol.TryParseChannelInfo(new byte[] { 0xFF, 0xCC, 0x01 }, out _)); // short
        var wrongHeader = new byte[SmartHubProtocol.GetInfoResponseLength];
        wrongHeader[0] = 0xFF; wrongHeader[1] = 0xDD; // not CC
        Assert.False(SmartHubProtocol.TryParseChannelInfo(wrongHeader, out var ch));
        Assert.Null(ch);
    }
}
