using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Nexus.Service.Peripherals.BulkPanels;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

/// <summary>
/// Pins the wire formats for the three bulk-pipe cooler LCDs. All three were reconstructed
/// from third-party documentation and none has hardware behind it, so these guard the
/// reconstruction rather than the panel's acceptance of it.
/// </summary>
public class BulkPanelProtocolTests
{
    // ── Thermalright ──

    [Fact]
    public void Thermalright_init_request_carries_the_magic_and_the_init_kind()
    {
        var packet = ThermalrightProtocol.EncodeInitRequest();

        Assert.Equal(64, packet.Length);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, packet[0..4]);
        Assert.Equal(0x00, packet[4]);
        Assert.Equal(0x01, packet[56]);
    }

    [Fact]
    public void Thermalright_frame_header_writes_size_little_endian_and_the_frame_kind()
    {
        var header = ThermalrightProtocol.EncodeFrameHeader(1920, 462, payloadLength: 0x00012345, rgb565: false);

        Assert.Equal(0x02, header[4]); // JPEG
        Assert.Equal(new byte[] { 0x80, 0x07 }, header[8..10]);   // 1920
        Assert.Equal(new byte[] { 0xCE, 0x01 }, header[12..14]);  // 462
        Assert.Equal(0x02, header[56]);
        Assert.Equal(new byte[] { 0x45, 0x23, 0x01, 0x00 }, header[60..64]);
    }

    [Fact]
    public void Thermalright_rgb565_panels_take_a_different_command_byte()
    {
        Assert.Equal(0x03, ThermalrightProtocol.EncodeFrameHeader(320, 320, 64, rgb565: true)[4]);
    }

    [Fact]
    public void Thermalright_reply_while_booting_yields_no_model()
    {
        var booting = new byte[64];
        ThermalrightProtocol.Magic.CopyTo(booting);
        booting[4] = 0xA1; booting[5] = 0xA2; booting[6] = 0xA3; booting[7] = 0xA4;

        Assert.True(ThermalrightProtocol.IsBooting(booting));
        Assert.Null(ThermalrightProtocol.DecodeModelId(booting));
    }

    [Fact]
    public void Thermalright_model_id_comes_from_byte_24()
    {
        var reply = new byte[64];
        ThermalrightProtocol.Magic.CopyTo(reply);
        reply[24] = 0x41;

        Assert.Equal((byte)0x41, ThermalrightProtocol.DecodeModelId(reply));
        var panel = ThermalrightProtocol.PanelFor(0x41);
        Assert.Equal("TL-M10 Vision", panel!.Value.Name);
        Assert.Equal(1920, panel.Value.Width);
        Assert.Equal(462, panel.Value.Height);
    }

    [Fact]
    public void Thermalright_reply_without_the_magic_is_refused()
    {
        Assert.Null(ThermalrightProtocol.DecodeModelId(new byte[64]));
    }

    [Theory]
    [InlineData(0x01, 480, 480, false)]
    [InlineData(0x05, 640, 480, false)]
    [InlineData(0x0B, 854, 480, false)]
    [InlineData(0x40, 1600, 720, false)]
    [InlineData(0x20, 320, 320, true)]
    public void Thermalright_panel_table_matches_the_documented_models(byte id, int w, int h, bool rgb565)
    {
        var panel = ThermalrightProtocol.PanelFor(id);

        Assert.NotNull(panel);
        Assert.Equal(w, panel!.Value.Width);
        Assert.Equal(h, panel.Value.Height);
        Assert.Equal(rgb565, panel.Value.Rgb565);
    }

    [Theory]
    // Square and 4:3 glass keeps the 2x2 tile; anything 3:2 or wider takes the 4x2.
    [InlineData(0x01, false)] // Grand Vision 480x480
    [InlineData(0x05, false)] // Mjolnir Vision 640x480
    [InlineData(0x20, false)] // Frozen Warframe Pro 320x320
    [InlineData(0x0B, true)]  // Vision Max 854x480
    [InlineData(0x40, true)]  // Wonder Vision 1600x720
    [InlineData(0x41, true)]  // TL-M10 Vision 1920x462
    public void Thermalright_wide_glass_is_the_side_the_4x2_tile_belongs_on(byte id, bool wide)
    {
        Assert.Equal(wide, ThermalrightProtocol.PanelFor(id)!.Value.IsWide);
    }

    [Fact]
    public void Thermalright_surface_follows_the_negotiated_panel_not_a_constant()
    {
        var wide = new ThermalrightPanel("Wonder Vision", 1600, 720, false);
        var square = new ThermalrightPanel("Grand Vision", 480, 480, false);

        Assert.True(wide.IsWide);
        Assert.False(square.IsWide);
        // A driver that has not connected yet must not claim the wide surface.
        Assert.False(default(ThermalrightPanel).IsWide);
    }

    [Fact]
    public void Thermalright_unknown_model_has_no_panel_and_the_fallback_is_the_no_init_one()
    {
        Assert.Null(ThermalrightProtocol.PanelFor(0xFE));
        // The Frozen Warframe Pro is the one model that answers no init at all.
        Assert.Equal(0x20, ThermalrightProtocol.FallbackModelId);
        Assert.NotNull(ThermalrightProtocol.PanelFor(ThermalrightProtocol.FallbackModelId));
    }

    // ── ASUS Ryujin ──

    [Fact]
    public void Ryujin_frame_drops_alpha_and_keeps_the_capture_channel_order()
    {
        var bgra = new byte[RyujinProtocol.Width * RyujinProtocol.Height * 4];
        bgra[0] = 0x11; bgra[1] = 0x22; bgra[2] = 0x33; bgra[3] = 0xFF;
        var dest = new byte[RyujinProtocol.FrameBytes];

        var written = RyujinProtocol.EncodeFrame(bgra, dest);

        Assert.Equal(RyujinProtocol.FrameBytes, written);
        Assert.Equal(320 * 240 * 3, written);
        // The panel wants BGR and the capture is already blue-first, so nothing swaps.
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, dest[0..3]);
    }

    [Fact]
    public void Ryujin_rejects_a_frame_shorter_than_the_panel()
    {
        Assert.Throws<ArgumentException>(() =>
            RyujinProtocol.EncodeFrame(new byte[16], new byte[RyujinProtocol.FrameBytes]));
    }

    [Fact]
    public void Ryujin_commit_is_the_documented_hid_report()
    {
        var report = RyujinProtocol.EncodeCommit();

        Assert.Equal(65, report.Length);
        Assert.Equal(new byte[] { 0xEC, 0x7F, 0x03, 0x00, 0x84, 0x03, 0x00, 0x00 }, report[0..8]);
    }

    // ── Lian Li Universal Screen 8.8 ──

    [Fact]
    public void Us88_packet_is_512_bytes_and_ends_with_the_magic_tail()
    {
        var packet = UniversalScreen88Protocol.EncodeCommand(
            UniversalScreen88Protocol.CommandGetVersion, ReadOnlySpan<byte>.Empty, 1_700_000_000);

        Assert.Equal(512, packet.Length);
        Assert.Equal(new byte[] { 0xA1, 0x1A }, packet[^2..]);
        // Six zeros between the 504-byte ciphertext and the tail.
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0 }, packet[504..510]);
    }

    /// <summary>
    /// The packet is opaque on the wire, so the only way to check the layout is to decrypt
    /// it back. This also proves the key, IV, mode and padding agree end to end.
    /// </summary>
    [Fact]
    public void Us88_plaintext_layout_survives_a_decrypt()
    {
        const uint timestamp = 0x11223344;
        var packet = UniversalScreen88Protocol.EncodeCommand(
            UniversalScreen88Protocol.CommandBrightness, new byte[] { 0x32, 0x00, 0x00, 0x00 }, timestamp);

        var plain = Decrypt(packet.AsSpan(0, 504));

        Assert.Equal(UniversalScreen88Protocol.PlainLength, plain.Length);
        Assert.Equal(UniversalScreen88Protocol.CommandBrightness, plain[0]);
        Assert.Equal(0x00, plain[1]);
        Assert.Equal(0x1A, plain[2]);
        Assert.Equal(0x6D, plain[3]);
        // Timestamp is little-endian.
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, plain[4..8]);
        Assert.Equal(new byte[] { 0x32, 0x00, 0x00, 0x00 }, plain[8..12]);
    }

    [Fact]
    public void Us88_frame_header_carries_the_jpeg_length_big_endian()
    {
        var header = UniversalScreen88Protocol.EncodeFrameHeader(0x00ABCDEF, timestampSeconds: 42);

        var plain = Decrypt(header.AsSpan(0, 504));

        Assert.Equal(UniversalScreen88Protocol.CommandPushJpeg, plain[0]);
        // Big-endian here, unlike the little-endian timestamp four bytes earlier.
        Assert.Equal(new byte[] { 0x00, 0xAB, 0xCD, 0xEF }, plain[8..12]);
    }

    [Fact]
    public void Us88_encryption_pads_500_bytes_to_504()
    {
        Assert.Equal(504, UniversalScreen88Protocol.Encrypt(new byte[500]).Length);
    }

    [Fact]
    public void Us88_init_sequence_is_the_five_documented_commands_with_rising_timestamps()
    {
        uint t = 100;
        var packets = UniversalScreen88Protocol.EncodeInitSequence(() => t++);

        Assert.Equal(5, packets.Length);
        var commands = packets.Select(p => Decrypt(p.AsSpan(0, 504))[0]).ToArray();
        Assert.Equal(
            new byte[]
            {
                UniversalScreen88Protocol.CommandGetVersion,
                UniversalScreen88Protocol.CommandBrightness,
                UniversalScreen88Protocol.CommandStopPlay,
                UniversalScreen88Protocol.CommandStopClock,
                UniversalScreen88Protocol.CommandFrameRate,
            },
            commands);

        var timestamps = packets
            .Select(p => BitConverter.ToUInt32(Decrypt(p.AsSpan(0, 504)), 4))
            .ToArray();
        for (int i = 1; i < timestamps.Length; i++)
        {
            Assert.True(timestamps[i] > timestamps[i - 1], "the panel rejects a repeated timestamp");
        }
    }

    [Fact]
    public void Us88_refuses_more_parameters_than_the_block_holds()
    {
        Assert.Throws<ArgumentException>(() => UniversalScreen88Protocol.EncodeCommand(
            0x0A, new byte[UniversalScreen88Protocol.MaxParams + 1], 1));
    }

    /// <summary>
    /// The panel uses standard DES (standard IP/FP tables and S-boxes). This is a published
    /// single-block DES-ECB test vector: if .NET's DES matches it, our implementation agrees
    /// with the algorithm and only the key, IV and padding remain to be checked - which the
    /// round-trip tests above cover.
    /// </summary>
    [Fact]
    public void Dotnet_des_matches_a_published_test_vector()
    {
#pragma warning disable CA5351
        using var des = DES.Create();
#pragma warning restore CA5351
        des.Key = Convert.FromHexString("133457799BBCDFF1");
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        using var encryptor = des.CreateEncryptor();

        var cipher = encryptor.TransformFinalBlock(Convert.FromHexString("0123456789ABCDEF"), 0, 8);

        Assert.Equal("85E813540F0AB405", Convert.ToHexString(cipher));
    }

    [Fact]
    public void Us88_panel_is_the_documented_tall_strip()
    {
        Assert.Equal(480, UniversalScreen88Protocol.Width);
        Assert.Equal(1920, UniversalScreen88Protocol.Height);
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> cipher)
    {
        var key = Encoding.ASCII.GetBytes("slv3tuzx");
#pragma warning disable CA5351
        using var des = DES.Create();
#pragma warning restore CA5351
        des.Key = key;
        des.IV = key;
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.PKCS7;
        using var decryptor = des.CreateDecryptor();
        var input = cipher.ToArray();
        return decryptor.TransformFinalBlock(input, 0, input.Length);
    }

    // ── ZMatrices ──

    [Fact]
    public void ZMatrices_picture_mode_command_closes_with_a_sum16_of_the_first_41_bytes()
    {
        var command = ZMatricesProtocol.EncodePictureModeCommand();

        Assert.Equal(43, command.Length);
        Assert.Equal(new byte[] { 0xAA, 0x2E, 0x05, 0x01 }, command[0..4]);
        Assert.All(command[4..41], b => Assert.Equal(0, b));
        Assert.Equal(new byte[] { 0xDE, 0x00 }, command[41..43]); // 0xAA + 0x2E + 0x05 + 0x01
    }

    [Fact]
    public void ZMatrices_start_frame_declares_length_checksum_and_packet_count()
    {
        var jpeg = Enumerable.Range(0, 1011).Select(i => (byte)i).ToArray();

        var start = ZMatricesProtocol.EncodeStartFrame(jpeg);

        Assert.Equal(16, start.Length);
        Assert.Equal("Start"u8.ToArray(), start[0..5]);
        Assert.Equal(1, start[5]);
        Assert.Equal(1011, BitConverter.ToInt32(start, 6));
        Assert.Equal(ZMatricesProtocol.Sum16(jpeg), BitConverter.ToUInt16(start, 10));
        Assert.Equal(3, BitConverter.ToUInt16(start, 12));
        Assert.Equal(0, BitConverter.ToUInt16(start, 14));
    }

    [Fact]
    public void ZMatrices_trans_packets_are_full_512_except_a_short_last_one()
    {
        var jpeg = Enumerable.Range(0, 1011).Select(i => (byte)i).ToArray();
        var buffer = new byte[3 * ZMatricesProtocol.PacketSize];

        int written = ZMatricesProtocol.EncodeTransPackets(jpeg, buffer);

        Assert.Equal(512 + 512 + 7 + 1, written);
        for (int packet = 0; packet < 3; packet++)
        {
            var header = buffer.AsSpan(packet * 512, 7).ToArray();
            Assert.Equal("Trans"u8.ToArray(), header[0..5]);
            Assert.Equal(packet + 1, BitConverter.ToUInt16(header, 5));
        }
        Assert.Equal(jpeg[0..505], buffer[7..512]);
        Assert.Equal(jpeg[505..1010], buffer[519..1024]);
        Assert.Equal(jpeg[1010], buffer[1031]);
    }

    [Fact]
    public void ZMatrices_jpeg_filling_the_last_packet_exactly_leaves_it_full()
    {
        var jpeg = new byte[505 * 2];
        var buffer = new byte[2 * ZMatricesProtocol.PacketSize];

        Assert.Equal(1024, ZMatricesProtocol.EncodeTransPackets(jpeg, buffer));
    }
}
