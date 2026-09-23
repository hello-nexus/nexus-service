using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class BulkPanelDriverTests
{
    private static byte[] Frame(int width, int height, byte blue = 0x40)
    {
        var frame = new byte[width * height * 4];
        for (int i = 0; i < frame.Length; i += 4)
        {
            frame[i] = blue;
            frame[i + 1] = 0x80; // G
            frame[i + 2] = 0xC0; // R
            frame[i + 3] = 0xFF;
        }
        return frame;
    }

    // ── Thermalright: geometry is negotiated, not declared ──

    [Fact]
    public void Thermalright_adopts_the_size_the_panel_reports()
    {
        var driver = new ThermalrightPanelDriver();
        var reply = new byte[64];
        ThermalrightProtocol.Magic.CopyTo(reply);
        reply[24] = 0x41; // TL-M10 Vision, 1920x462
        var pipe = new FakeBulkPipe { Replies = { reply } };

        var geometry = driver.Connect(pipe, null);

        Assert.Equal((1920, 462), geometry);
        Assert.Contains("TL-M10", driver.Name);
    }

    [Theory]
    [InlineData(0x40, "lcd-wide")]   // Wonder Vision 1600x720
    [InlineData(0x01, "lcd-square")] // Grand Vision 480x480
    public void Thermalright_surface_matches_the_shape_of_the_glass(byte model, string surface)
    {
        var driver = new ThermalrightPanelDriver();
        var reply = new byte[64];
        ThermalrightProtocol.Magic.CopyTo(reply);
        reply[24] = model;
        var pipe = new FakeBulkPipe { Replies = { reply } };

        driver.Connect(pipe, null);

        Assert.Equal(surface, driver.Surface);
    }

    [Fact]
    public void Thermalright_retries_while_the_panel_reports_booting()
    {
        var driver = new ThermalrightPanelDriver();
        var booting = new byte[64];
        ThermalrightProtocol.Magic.CopyTo(booting);
        booting[4] = 0xA1; booting[5] = 0xA2; booting[6] = 0xA3; booting[7] = 0xA4;
        var ready = new byte[64];
        ThermalrightProtocol.Magic.CopyTo(ready);
        ready[24] = 0x01; // Grand Vision, 480x480
        var pipe = new FakeBulkPipe { Replies = { booting, booting, ready } };

        Assert.Equal((480, 480), driver.Connect(pipe, null));
        Assert.Equal(3, pipe.Writes.Count);
    }

    /// <summary>
    /// Silence is a positive result for exactly one model - the Frozen Warframe Pro answers
    /// no init at all - so a timeout must not be read as absence.
    /// </summary>
    [Fact]
    public void Thermalright_falls_back_to_the_model_that_never_answers()
    {
        var driver = new ThermalrightPanelDriver();

        var geometry = driver.Connect(new FakeBulkPipe(), null);

        Assert.Equal((320, 320), geometry);
    }

    [Fact]
    public void Thermalright_unknown_model_is_refused_rather_than_guessed()
    {
        var driver = new ThermalrightPanelDriver();
        var reply = new byte[64];
        ThermalrightProtocol.Magic.CopyTo(reply);
        reply[24] = 0xFE;

        Assert.Null(driver.Connect(new FakeBulkPipe { Replies = { reply } }, null));
    }

    [Fact]
    public void Thermalright_sends_header_and_payload_as_one_transfer()
    {
        var driver = new ThermalrightPanelDriver();
        var reply = new byte[64];
        ThermalrightProtocol.Magic.CopyTo(reply);
        reply[24] = 0x01; // 480x480 JPEG
        var pipe = new FakeBulkPipe { Replies = { reply } };
        driver.Connect(pipe, null);
        pipe.Writes.Clear();

        Assert.True(driver.SendFrame(pipe, null, Frame(480, 480)));

        // One write, not two: this panel wants them concatenated, unlike the Kraken.
        var packet = Assert.Single(pipe.Writes);
        Assert.True(packet.Length > ThermalrightProtocol.HeaderLength);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, packet[0..4]);
        var declared = BitConverter.ToInt32(packet, 60);
        Assert.Equal(packet.Length - ThermalrightProtocol.HeaderLength, declared);
        // A real JPEG follows the header.
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, packet[64..66]);
    }

    [Fact]
    public void Thermalright_rgb565_panel_sends_raw_pixels_not_jpeg()
    {
        var driver = new ThermalrightPanelDriver();
        // No reply -> the RGB565 fallback model.
        var pipe = new FakeBulkPipe();
        driver.Connect(pipe, null);
        pipe.Writes.Clear();

        Assert.True(driver.SendFrame(pipe, null, Frame(320, 320)));

        var packet = Assert.Single(pipe.Writes);
        Assert.Equal(0x03, packet[4]); // RGB565 command
        Assert.Equal(ThermalrightProtocol.HeaderLength + (320 * 320 * 2), packet.Length);
    }

    // ── Ryujin: bulk pixels, HID commit ──

    [Fact]
    public void Ryujin_needs_its_hid_channel_to_connect_at_all()
    {
        var driver = new RyujinPanelDriver();

        Assert.Null(driver.Connect(new FakeBulkPipe(), null));
        Assert.Equal((320, 240), driver.Connect(new FakeBulkPipe(), new FakeHid()));
    }

    [Fact]
    public void Ryujin_chunks_the_frame_then_commits_over_hid()
    {
        var driver = new RyujinPanelDriver();
        var pipe = new FakeBulkPipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        Assert.True(driver.SendFrame(pipe, hid, Frame(320, 240)));

        Assert.Equal(RyujinProtocol.FrameBytes, pipe.Writes.Sum(w => w.Length));
        Assert.All(pipe.Writes, w => Assert.True(w.Length <= RyujinProtocol.ChunkBytes));
        // Nothing shows until the commit lands.
        var commit = Assert.Single(hid.Writes);
        Assert.Equal(new byte[] { 0xEC, 0x7F }, commit[0..2]);
    }

    [Fact]
    public void Ryujin_reports_failure_when_the_commit_is_refused()
    {
        var driver = new RyujinPanelDriver();
        var pipe = new FakeBulkPipe();
        var hid = new FakeHid { FailWrites = true };
        driver.Connect(pipe, hid);

        Assert.False(driver.SendFrame(pipe, hid, Frame(320, 240)));
    }

    // ── Universal Screen 8.8 ──

    [Fact]
    public void Screen88_runs_the_five_command_init_and_reports_its_strip_size()
    {
        var driver = new UniversalScreen88Driver();
        var pipe = new FakeBulkPipe { DefaultReadBytes = 512 };

        var geometry = driver.Connect(pipe, null);

        Assert.Equal((480, 1920), geometry);
        Assert.Equal(5, pipe.Writes.Count);
        Assert.All(pipe.Writes, w => Assert.Equal(512, w.Length));
        Assert.All(pipe.Writes, w => Assert.Equal(new byte[] { 0xA1, 0x1A }, w[^2..]));
    }

    [Fact]
    public void Screen88_frame_is_an_encrypted_header_then_the_jpeg_in_one_transfer()
    {
        var driver = new UniversalScreen88Driver();
        var pipe = new FakeBulkPipe { DefaultReadBytes = 512 };
        driver.Connect(pipe, null);
        pipe.Writes.Clear();

        Assert.True(driver.SendFrame(pipe, null, Frame(480, 1920)));

        var packet = Assert.Single(pipe.Writes);
        Assert.True(packet.Length > UniversalScreen88Protocol.PacketLength);
        Assert.Equal(new byte[] { 0xA1, 0x1A }, packet[510..512]);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, packet[512..514]);
    }

    // ── ZMatrices: mode command on its own pipe, then framed JPEGs ──

    [Fact]
    public void ZMatrices_connect_sends_picture_mode_on_the_command_pipe()
    {
        var driver = new ZMatricesPanelDriver();
        var pipe = new FakeBulkPipe();

        Assert.Equal((ZMatricesPanelDriver.Width, ZMatricesPanelDriver.Height), driver.Connect(pipe, null));

        var (pipeId, command) = Assert.Single(pipe.PipeWrites);
        Assert.Equal(ZMatricesProtocol.CommandPipe, pipeId);
        Assert.Equal(new byte[] { 0xAA, 0x2E, 0x05, 0x01 }, command[0..4]);
        Assert.Empty(pipe.Writes);
    }

    [Fact]
    public void ZMatrices_frame_is_start_then_trans_packets_then_trailer()
    {
        var driver = new ZMatricesPanelDriver();
        var pipe = new FakeBulkPipe();
        driver.Connect(pipe, null);

        Assert.True(driver.SendFrame(pipe, null, Frame(ZMatricesPanelDriver.Width, ZMatricesPanelDriver.Height)));

        var (start, trans, finish) = (pipe.Writes[^3], pipe.Writes[^2], pipe.Writes[^1]);
        Assert.Equal("Start"u8.ToArray(), start[0..5]);
        int jpegLength = BitConverter.ToInt32(start, 6);
        Assert.Equal(ZMatricesProtocol.TransPacketCount(jpegLength), BitConverter.ToUInt16(start, 12));
        Assert.Equal("Trans"u8.ToArray(), trans[0..5]);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, trans[7..9]);
        Assert.Equal("DCLdfinish"u8.ToArray(), finish);
    }

    [Fact]
    public void ZMatrices_resend_repeats_the_last_frame_byte_for_byte()
    {
        var driver = new ZMatricesPanelDriver();
        var pipe = new FakeBulkPipe();
        driver.Connect(pipe, null);
        driver.SendFrame(pipe, null, Frame(ZMatricesPanelDriver.Width, ZMatricesPanelDriver.Height));
        var sent = pipe.Writes.TakeLast(3).ToList();
        pipe.Writes.Clear();

        Assert.True(driver.Resend(pipe, null));

        Assert.Equal(sent, pipe.Writes);
    }

    [Fact]
    public void ZMatrices_every_seventh_picture_after_picture_mode_is_a_copy()
    {
        var driver = new ZMatricesPanelDriver();
        var pipe = new FakeBulkPipe();
        driver.Connect(pipe, null);

        for (int i = 0; i < 12; i++)
        {
            driver.SendFrame(pipe, null, Frame(ZMatricesPanelDriver.Width, ZMatricesPanelDriver.Height, (byte)(i * 20)));
        }

        var pictures = pipe.Writes.Chunk(3).ToList();
        Assert.Equal(14, pictures.Count);
        Assert.Equal(pictures[1], pictures[0]);
        Assert.Equal(pictures[8], pictures[7]);
        Assert.NotEqual(pictures[2][0], pictures[1][0]);
    }

    [Fact]
    public void ZMatrices_resend_before_any_frame_writes_nothing()
    {
        var driver = new ZMatricesPanelDriver();
        var pipe = new FakeBulkPipe();
        driver.Connect(pipe, null);

        Assert.True(driver.Resend(pipe, null));
        Assert.Empty(pipe.Writes);
    }

    [Theory]
    [InlineData(55, 55)]
    [InlineData(150, 100)]
    [InlineData(-5, 0)]
    public void ZMatrices_brightness_goes_to_the_command_pipe_clamped(int requested, byte expected)
    {
        var driver = new ZMatricesPanelDriver();
        var pipe = new FakeBulkPipe();
        driver.Connect(pipe, null);
        pipe.PipeWrites.Clear();

        Assert.True(driver.SetBrightness(pipe, null, requested));

        var (pipeId, command) = Assert.Single(pipe.PipeWrites);
        Assert.Equal(ZMatricesProtocol.CommandPipe, pipeId);
        Assert.Equal(new byte[] { 0xAA, 0x2E, 0x04, 0x00, expected, 0x00 }, command);
    }

    // ── policy ──

    [Fact]
    public void Every_bulk_driver_defaults_to_nexus_control_off_and_reads_as_experimental()
    {
        IBulkPanelDriver[] drivers =
        {
            new ThermalrightPanelDriver(), new RyujinPanelDriver(), new UniversalScreen88Driver(),
            new ZMatricesPanelDriver(),
        };

        Assert.All(drivers, d =>
        {
            Assert.False(DeviceControlPolicy.DefaultOn(d.HandlerId), $"{d.HandlerId} must default off");
            Assert.True(DeviceControlPolicy.IsExperimental(d.HandlerId), $"{d.HandlerId} must be experimental");
            Assert.NotEmpty(d.ProductIds);
            Assert.NotEqual(0, d.WritePipeId);
        });
    }

    private sealed class FakeBulkPipe : IBulkUsbPipe
    {
        public List<byte[]> Writes { get; } = new();
        public List<byte[]> Replies { get; } = new();
        private int _replyIndex;

        /// <summary>Bytes a read returns when no scripted reply is queued; 0 means timeout.</summary>
        public int DefaultReadBytes { get; set; }

        /// <summary>Writes that named their pipe.</summary>
        public List<(byte Pipe, byte[] Data)> PipeWrites { get; } = new();

        public bool Write(ReadOnlySpan<byte> data)
        {
            Writes.Add(data.ToArray());
            return true;
        }

        public bool Write(byte pipeId, ReadOnlySpan<byte> data)
        {
            PipeWrites.Add((pipeId, data.ToArray()));
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_replyIndex < Replies.Count)
            {
                var reply = Replies[_replyIndex++];
                var length = Math.Min(reply.Length, buffer.Length);
                reply.AsSpan(0, length).CopyTo(buffer);
                return length;
            }
            return Math.Min(DefaultReadBytes, buffer.Length);
        }

        public void Dispose() { }
    }

    private sealed class FakeHid : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public bool FailWrites { get; set; }

        public int VendorId => 0x0B05;
        public int ProductId => RyujinProtocol.ProductIdWithLcd;
        public string Path => "/dev/fake-ryujin";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF01;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            Writes.Add(report.ToArray());
            return !FailWrites;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }
}
