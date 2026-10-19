using System;
using System.Security.Cryptography;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexScreenHeaderTests
{
    [Theory]
    [InlineData(0x0D, 1000u, 1, -1, 0, 0, "de80098e5b7b9448232fe2de324fe4c771f0cb33254b5f8370a5e7446c32b042")]
    [InlineData(0x65, 123456u, 0, 10520, 0, 0, "43f1efda72812e24fe0cadc759d6a45c5b3e4029f4b51a22efc3a53cdbd6732f")]
    [InlineData(0x67, 5u, 0, 1145, 140, 140, "6eeca466309c82734753d6c83547c83f19a1e4f9585a7699edc387b47d189d2c")]
    public void Flex_headers_match_an_independent_encryption_of_the_500_byte_plain_header(
        int cmd, uint stamp, int arg, int length, int x, int y, string sha256)
    {
        var header = Slv3LcdProtocol.BuildFlexHeader(
            (Slv3LcdProtocol.CmdType)cmd, stamp, (byte)arg, length < 0 ? null : length, x, y);
        Assert.Equal(512, header.Length);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(header)).ToLowerInvariant());
    }

    [Fact]
    public void Flex_header_ends_with_six_zero_bytes_and_the_frame_marker()
    {
        var header = Slv3LcdProtocol.BuildFlexHeader(Slv3LcdProtocol.CmdType.BrigthSet, 1, 50);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0xA1, 0x1A }, header[504..]);
    }

    [Fact]
    public void Layer_and_check_commands_use_the_vendor_command_numbers()
    {
        Assert.Equal(0x66, (int)Slv3LcdProtocol.CmdType.PushPng);
        Assert.Equal(0x67, (int)Slv3LcdProtocol.CmdType.PushLayer3);
        Assert.Equal(0x80, (int)Slv3LcdProtocol.CmdType.CheckNewLcd);
        Assert.Equal(0x65, (int)Slv3LcdProtocol.CmdType.PushJpg);
    }

    [Fact]
    public void Unpadded_buffer_is_the_header_plus_the_payload_only()
    {
        var header = Slv3LcdProtocol.BuildFlexHeader(Slv3LcdProtocol.CmdType.PushJpg, 1, length: 3);
        var buffer = Slv3LcdProtocol.BuildUnpaddedBuffer(header, new byte[] { 1, 2, 3 });
        Assert.Equal(515, buffer.Length);
        Assert.Equal(3, buffer[^1]);
    }

    [Fact]
    public void The_transparent_overlay_png_is_a_valid_400_square()
    {
        var png = Nexus.Service.Peripherals.LianLiFlex.FlexPng.TransparentSquare(400);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.Equal(400, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(400, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
        Assert.Equal(6, png[25]);
        Assert.InRange(png.Length, 100, 4000);
    }
}
