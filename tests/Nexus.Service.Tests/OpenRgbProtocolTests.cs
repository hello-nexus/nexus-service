using System.Buffers.Binary;
using System.Text;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests;

public class OpenRgbProtocolTests
{
    [Fact]
    public void ForceModeBrightnessMax_RaisesLiveBrightnessToMax()
    {
        // Synthetic v3+ mode "Direct": brightness_max=200, live brightness=5.
        // After forcing, the replayed brightness must equal brightness_max so the
        // hardware runs full (software sliders dim on top).
        var nameBytes = Encoding.ASCII.GetBytes("Direct");
        var mode = new byte[2 + nameBytes.Length + 1 + 48 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(mode.AsSpan(0, 2), (ushort)(nameBytes.Length + 1));
        nameBytes.CopyTo(mode.AsSpan(2));
        var fixedStart = 2 + nameBytes.Length + 1;
        BinaryPrimitives.WriteUInt32LittleEndian(mode.AsSpan(fixedStart + 20, 4), 200); // brightness_max
        BinaryPrimitives.WriteUInt32LittleEndian(mode.AsSpan(fixedStart + 36, 4), 5);   // brightness

        OpenRgbProtocol.ForceModeBrightnessMax(mode, protocolVersion: 5);

        Assert.Equal(200u, BinaryPrimitives.ReadUInt32LittleEndian(mode.AsSpan(fixedStart + 36, 4)));
    }

    [Fact]
    public void ForceModeBrightnessMax_UsesTheV6OffsetsWithoutModeValue()
    {
        // v6 drops the leading mode value, so every field sits 4 bytes earlier.
        var nameBytes = Encoding.ASCII.GetBytes("Direct");
        var mode = new byte[2 + nameBytes.Length + 1 + 44 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(mode.AsSpan(0, 2), (ushort)(nameBytes.Length + 1));
        nameBytes.CopyTo(mode.AsSpan(2));
        var fixedStart = 2 + nameBytes.Length + 1;
        BinaryPrimitives.WriteUInt32LittleEndian(mode.AsSpan(fixedStart + 16, 4), 100); // brightness_max
        BinaryPrimitives.WriteUInt32LittleEndian(mode.AsSpan(fixedStart + 32, 4), 0);   // brightness

        OpenRgbProtocol.ForceModeBrightnessMax(mode, protocolVersion: 6);

        Assert.Equal(100u, BinaryPrimitives.ReadUInt32LittleEndian(mode.AsSpan(fixedStart + 32, 4)));
    }

    [Fact]
    public void ForceModeBrightnessMax_IgnoresTruncatedModeBytes()
    {
        // A short buffer (malformed mode) must be left untouched, not throw.
        var mode = new byte[] { 0x02, 0x00, 0x41 };
        var before = (byte[])mode.Clone();
        OpenRgbProtocol.ForceModeBrightnessMax(mode, protocolVersion: 6);
        Assert.Equal(before, mode);
    }

    [Fact]
    public void ParseControllerData_RaisesSubMaxModeBrightnessToMax()
    {
        // v4 controller with one "Direct" mode reporting brightness=7 but
        // brightness_max=255. The parse path must raise the replayed brightness to
        // 255 so the hardware runs full; without the forcing it stays 7 (dim), and a
        // mode reporting 0 would drive the device dark (Corsair K100).
        var body = new List<byte>();
        void U16(int v) { body.Add((byte)(v & 0xFF)); body.Add((byte)((v >> 8) & 0xFF)); }
        void U32(uint v) { for (int s = 0; s < 32; s += 8) body.Add((byte)((v >> s) & 0xFF)); }
        void BStr(string s) { U16(s.Length + 1); body.AddRange(Encoding.ASCII.GetBytes(s)); body.Add(0); }

        U32(0);        // data_size (unused by the parser)
        U32(5);        // device_type
        BStr("K");     // name
        BStr("");      // vendor (v>=1)
        BStr("");      // description
        BStr("");      // version
        BStr("");      // serial
        BStr("");      // location
        U16(1);        // num_modes
        U32(0);        // active_mode
        BStr("Direct");
        U32(0);        // value
        U32(0);        // flags
        U32(0);        // speed_min
        U32(0);        // speed_max
        U32(0);        // brightness_min
        U32(255);      // brightness_max
        U32(0);        // colors_min
        U32(0);        // colors_max
        U32(0);        // speed
        U32(7);        // brightness (sub-max)
        U32(0);        // direction
        U32(1);        // color_mode
        U16(0);        // mode num_colors
        U16(0);        // num_zones
        U16(0);        // num_leds

        var dev = OpenRgbProtocol.ParseControllerData(0, body.ToArray(), protocolVersion: 4);

        var mode = Assert.Single(dev.Modes);
        var fixedStart = 2 + BinaryPrimitives.ReadUInt16LittleEndian(mode.Bytes.AsSpan(0, 2));
        Assert.Equal(255u, BinaryPrimitives.ReadUInt32LittleEndian(mode.Bytes.AsSpan(fixedStart + 36, 4)));
    }

    [Fact]
    public void WriteHeader_ProducesCorrectMagicAndFields()
    {
        var buf = new byte[OpenRgbProtocol.HeaderSize];
        OpenRgbProtocol.WriteHeader(buf, deviceIndex: 5, OpenRgbProtocol.PacketId.RgbControllerUpdateLeds, dataSize: 100);

        // Magic "ORGB"
        Assert.Equal((byte)'O', buf[0]);
        Assert.Equal((byte)'R', buf[1]);
        Assert.Equal((byte)'G', buf[2]);
        Assert.Equal((byte)'B', buf[3]);

        // device_index = 5 (little-endian)
        Assert.Equal(5, buf[4]);
        Assert.Equal(0, buf[5]);

        // packet_id = 1050 (little-endian) → 0x0000041A
        Assert.Equal(0x1A, buf[8]);
        Assert.Equal(0x04, buf[9]);

        // data_size = 100 (little-endian)
        Assert.Equal(100, buf[12]);
    }

    [Fact]
    public void ReadHeader_ParsesWriteHeaderOutput()
    {
        var buf = new byte[OpenRgbProtocol.HeaderSize];
        OpenRgbProtocol.WriteHeader(buf, deviceIndex: 42, OpenRgbProtocol.PacketId.RequestControllerData, dataSize: 4);

        var (idx, id, size) = OpenRgbProtocol.ReadHeader(buf);
        Assert.Equal(42u, idx);
        Assert.Equal(OpenRgbProtocol.PacketId.RequestControllerData, id);
        Assert.Equal(4u, size);
    }

    [Fact]
    public void ReadHeader_RejectsBadMagic()
    {
        var buf = new byte[OpenRgbProtocol.HeaderSize];
        buf[0] = (byte)'X';
        buf[1] = (byte)'X';
        buf[2] = (byte)'X';
        buf[3] = (byte)'X';

        Assert.Throws<InvalidOperationException>(() => OpenRgbProtocol.ReadHeader(buf));
    }

    [Fact]
    public void BuildSetClientNameBody_AppendsNul()
    {
        var body = OpenRgbProtocol.BuildSetClientNameBody("nexus");
        Assert.Equal(6, body.Length);
        Assert.Equal((byte)'n', body[0]);
        Assert.Equal((byte)'e', body[1]);
        Assert.Equal((byte)'x', body[2]);
        Assert.Equal((byte)'u', body[3]);
        Assert.Equal((byte)'s', body[4]);
        Assert.Equal(0, body[5]);
    }

    [Fact]
    public void BuildProtocolVersionBody_WritesUInt32LittleEndian()
    {
        var body = OpenRgbProtocol.BuildProtocolVersionBody(4);
        Assert.Equal(4, body.Length);
        Assert.Equal(4, body[0]);
        Assert.Equal(0, body[1]);
        Assert.Equal(0, body[2]);
        Assert.Equal(0, body[3]);
    }

    [Fact]
    public void BuildUpdateLedsBody_LayoutMatchesProtocol()
    {
        var colors = new[]
        {
            new RgbColor(255, 0, 0),
            new RgbColor(0, 255, 0),
            new RgbColor(0, 0, 255),
        };

        var body = OpenRgbProtocol.BuildUpdateLedsBody(colors);

        // 4 (data_size) + 2 (led_count) + 3 LEDs * 4 bytes = 18
        Assert.Equal(18, body.Length);

        // data_size = 18
        Assert.Equal(18, body[0]);
        Assert.Equal(0, body[1]);

        // led_count = 3
        Assert.Equal(3, body[4]);
        Assert.Equal(0, body[5]);

        // LED 0: R=255, G=0, B=0, padding=0
        Assert.Equal(255, body[6]);
        Assert.Equal(0, body[7]);
        Assert.Equal(0, body[8]);
        Assert.Equal(0, body[9]);

        // LED 1: R=0, G=255, B=0, padding=0
        Assert.Equal(0, body[10]);
        Assert.Equal(255, body[11]);
        Assert.Equal(0, body[12]);
        Assert.Equal(0, body[13]);

        // LED 2: R=0, G=0, B=255, padding=0
        Assert.Equal(0, body[14]);
        Assert.Equal(0, body[15]);
        Assert.Equal(255, body[16]);
        Assert.Equal(0, body[17]);
    }

    [Fact]
    public void RgbColor_ScaleClampsBrightness()
    {
        var c = new RgbColor(200, 100, 50);
        Assert.Equal(RgbColor.Black.R, c.Scale(0).R);
        Assert.Equal(c.R, c.Scale(1).R);
        Assert.Equal(c.R, c.Scale(2).R); // Above 1 clamps to 1
    }

    [Fact]
    public void RgbColor_ScaleHalfHalvesChannels()
    {
        var c = new RgbColor(200, 100, 50);
        var dim = c.Scale(0.5);
        Assert.Equal(100, dim.R);
        Assert.Equal(50, dim.G);
        Assert.Equal(25, dim.B);
    }
}
