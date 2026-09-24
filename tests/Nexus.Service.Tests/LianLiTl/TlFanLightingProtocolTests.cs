using System;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLiCp;
using Nexus.Service.Peripherals.LianLiTl;

namespace Nexus.Service.Tests.LianLiTl;

public class TlFanLightingProtocolTests
{
    private const int PayloadOffset = CommandPacket.PayloadOffset;

    private static byte[] Colors(params byte[] bytes)
    {
        var buf = new byte[TlFanProtocol.MaxLightColors * 3];
        bytes.CopyTo(buf, 0);
        return buf;
    }

    [Fact]
    public void Fan_light_is_command_A3_with_a_20_byte_payload()
    {
        var packet = TlFanProtocol.EncodeSetFanLight(
            0, 0, mode: 3, brightness: 4, speed: 2, direction: 0,
            Colors(), colorCount: 0, disabled: false, motherboardSync: false);

        Assert.Equal(CommandPacket.Length, packet.Length);
        Assert.Equal(0xA3, packet[1]);
        Assert.Equal(TlFanProtocol.LightPayloadLength, packet[5]);
    }

    [Fact]
    public void Fan_light_addresses_port_and_fan()
    {
        var packet = TlFanProtocol.EncodeSetFanLight(
            2, 3, mode: 3, brightness: 4, speed: 0, direction: 0,
            Colors(), colorCount: 0, disabled: false, motherboardSync: false);

        Assert.Equal(0x20, packet[PayloadOffset]);
        Assert.Equal(0x23, packet[PayloadOffset + 1]);
    }

    [Fact]
    public void Fan_light_carries_the_sync_bit_in_the_low_nibble()
    {
        var packet = TlFanProtocol.EncodeSetFanLight(
            1, 0, mode: 3, brightness: 4, speed: 0, direction: 0,
            Colors(), colorCount: 0, disabled: false, motherboardSync: true);

        Assert.Equal(0x11, packet[PayloadOffset]);
    }

    [Fact]
    public void Fan_light_writes_mode_brightness_speed_direction_and_count()
    {
        var packet = TlFanProtocol.EncodeSetFanLight(
            0, 0, mode: 21, brightness: 3, speed: 1, direction: 4,
            Colors(0x11, 0x22, 0x33), colorCount: 1, disabled: false, motherboardSync: false);

        Assert.Equal(21, packet[PayloadOffset + 2]);
        Assert.Equal(3, packet[PayloadOffset + 3]);
        Assert.Equal(1, packet[PayloadOffset + 4]);
        Assert.Equal(4, packet[PayloadOffset + 17]);
        Assert.Equal(0, packet[PayloadOffset + 18]);
        Assert.Equal(1, packet[PayloadOffset + 19]);
    }

    [Fact]
    public void Fan_light_writes_colors_in_rgb_order_from_byte_five()
    {
        var packet = TlFanProtocol.EncodeSetFanLight(
            0, 0, mode: 3, brightness: 4, speed: 0, direction: 0,
            Colors(0xAA, 0xBB, 0xCC, 0x10, 0x20, 0x30), colorCount: 2,
            disabled: false, motherboardSync: false);

        Assert.Equal(0xAA, packet[PayloadOffset + 5]);
        Assert.Equal(0xBB, packet[PayloadOffset + 6]);
        Assert.Equal(0xCC, packet[PayloadOffset + 7]);
        Assert.Equal(0x10, packet[PayloadOffset + 8]);
        Assert.Equal(0x20, packet[PayloadOffset + 9]);
        Assert.Equal(0x30, packet[PayloadOffset + 10]);
        Assert.Equal(2, packet[PayloadOffset + 19]);
    }

    [Fact]
    public void Fan_light_caps_the_colour_count_at_four()
    {
        var packet = TlFanProtocol.EncodeSetFanLight(
            0, 0, mode: 3, brightness: 4, speed: 0, direction: 0,
            Colors(), colorCount: 9, disabled: false, motherboardSync: false);

        Assert.Equal(TlFanProtocol.MaxLightColors, packet[PayloadOffset + 19]);
    }

    [Fact]
    public void Fan_light_off_sets_the_disable_flag()
    {
        var packet = TlFanProtocol.EncodeSetFanLight(
            0, 0, mode: 0, brightness: 0, speed: 0, direction: 0,
            Colors(), colorCount: 0, disabled: true, motherboardSync: false);

        Assert.Equal(1, packet[PayloadOffset + 18]);
    }

    [Fact]
    public void Fan_light_clamps_brightness_speed_and_direction()
    {
        var packet = TlFanProtocol.EncodeSetFanLight(
            0, 0, mode: 3, brightness: 99, speed: 99, direction: 99,
            Colors(), colorCount: 0, disabled: false, motherboardSync: false);

        Assert.Equal(4, packet[PayloadOffset + 3]);
        Assert.Equal(4, packet[PayloadOffset + 4]);
        Assert.Equal(5, packet[PayloadOffset + 17]);
    }

    [Fact]
    public void Group_light_is_command_B0_with_a_zero_first_byte_and_the_group_number()
    {
        var packet = TlFanProtocol.EncodeSetFanGroupLight(
            9, mode: 3, brightness: 4, speed: 0, direction: 0,
            Colors(), colorCount: 0, disabled: false);

        Assert.Equal(0xB0, packet[1]);
        Assert.Equal(0x00, packet[PayloadOffset]);
        Assert.Equal(9, packet[PayloadOffset + 1]);
    }

    [Fact]
    public void Group_numbers_are_two_per_port()
    {
        Assert.Equal(0, TlFanProtocol.TopGroup(0));
        Assert.Equal(1, TlFanProtocol.BottomGroup(0));
        Assert.Equal(8, TlFanProtocol.TopGroup(1));
        Assert.Equal(9, TlFanProtocol.BottomGroup(1));
    }

    [Fact]
    public void Group_declaration_lists_every_fan_with_the_half_bit()
    {
        var top = TlFanProtocol.EncodeSetFanGroup(group: 8, port: 1, fanCount: 3, topHalf: true);

        Assert.Equal(0xAD, top[1]);
        Assert.Equal(5, top[5]);
        Assert.Equal(8, top[PayloadOffset]);
        Assert.Equal(3, top[PayloadOffset + 1]);
        Assert.Equal(0x90, top[PayloadOffset + 2]);
        Assert.Equal(0x91, top[PayloadOffset + 3]);
        Assert.Equal(0x92, top[PayloadOffset + 4]);
    }

    [Fact]
    public void Group_declaration_bottom_half_uses_bit_six()
    {
        var bottom = TlFanProtocol.EncodeSetFanGroup(group: 9, port: 1, fanCount: 1, topHalf: false);

        Assert.Equal(0x50, bottom[PayloadOffset + 2]);
    }

    [Fact]
    public void Mode_catalog_maps_keys_to_firmware_bytes()
    {
        Assert.Equal(3, TlLightingModes.Find("static").ModeByte);
        Assert.Equal(21, TlLightingModes.Find("wave").ModeByte);
        Assert.Equal(28, TlLightingModes.Find("kaleidoscope").ModeByte);
        Assert.True(TlLightingModes.Find(TlLightingModes.OffKey).IsOff);
    }

    [Fact]
    public void Unknown_mode_falls_back_to_off()
    {
        Assert.True(TlLightingModes.Find("nope").IsOff);
    }
}
