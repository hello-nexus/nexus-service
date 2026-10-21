using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

/// <summary>
/// Byte sequences are those iCUE sent to a Nautilus LCD Cap in a USB capture.
/// </summary>
public class NautilusLcdHandshakeTests
{
    private const int ReportLength = 1024;

    private static byte[] Feature(params byte[] head)
    {
        var report = new byte[32];
        head.CopyTo(report, 0);
        return report;
    }

    [Fact]
    public void Attach_replays_the_icue_open_order()
    {
        var handshake = new NautilusLcdHandshake();
        var device = new FeatureDevice();
        device.Replies[0x05] = Feature(0x05, 0x0C, 0x62, 0x38, 0xE6, 0xE4, 0x30, 0x2E, 0x33, 0x2E, 0x30, 0x2E, 0x33);
        device.Replies[0x0F] = Feature(0x0F, 0xEE, 0x03, 0x03, 0xC4);

        Assert.True(handshake.OnAttach(device, ReportLength));

        Assert.Equal(new[] { "G13", "G05", "G11", "G0F" }, device.Gets);
        Assert.Equal(4, device.Sets.Count);
        Assert.Equal(Feature(0x03, 0x1D, 0x01), device.Sets[0]);
        Assert.Equal(Feature(0x03, 0x19, 0x00, 0x00), device.Sets[1]);
        // The firmware string is echoed back from the byte after the reply's header.
        Assert.Equal(
            Feature(0x03, 0x0B, 0x64, 0x01, 0xE6, 0xE4, 0x30, 0x2E, 0x33, 0x2E, 0x30, 0x2E, 0x33),
            device.Sets[2]);
        Assert.Equal(Feature(0x03, 0x1B, 0x00, 0x03, 0xC4), device.Sets[3]);
    }

    [Fact]
    public void Attach_falls_back_to_the_captured_claim_when_the_limits_read_fails()
    {
        var handshake = new NautilusLcdHandshake();
        var device = new FeatureDevice { GetsSucceed = false };

        Assert.True(handshake.OnAttach(device, ReportLength));

        Assert.Equal(Feature(0x03, 0x1B, 0x00, 0x03, 0xC4), device.Sets[^1]);
    }

    [Fact]
    public void Attach_fails_when_the_session_open_is_rejected()
    {
        var handshake = new NautilusLcdHandshake();
        var device = new FeatureDevice { SetsSucceed = false };

        Assert.False(handshake.OnAttach(device, ReportLength));
    }

    [Theory]
    [InlineData(50818, 51, 50, 18)]
    [InlineData(111518, 110, 109, 774)]
    [InlineData(108641, 107, 106, 945)]
    public void Keepalive_carries_the_last_frame_size_as_icue_encodes_it(int jpegBytes, int chunks, int blocks, int remainder)
    {
        var handshake = new NautilusLcdHandshake();
        var device = new FeatureDevice();
        handshake.OnFrameSent(jpegBytes, chunks);

        Assert.True(handshake.Tick(device, ReportLength, 0));

        var sent = device.Sets[0];
        Assert.Equal(new byte[] { 0x03, 0x19, 0x00, 0x01 }, sent[..4]);
        Assert.Equal(blocks, sent[4] | (sent[5] << 8));
        Assert.Equal(remainder, sent[6] | (sent[7] << 8));
        Assert.All(sent[8..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Keepalive_before_any_frame_is_not_marked_streaming()
    {
        var handshake = new NautilusLcdHandshake();
        var device = new FeatureDevice();

        handshake.Tick(device, ReportLength, 0);

        Assert.Equal(Feature(0x03, 0x19, 0x00, 0x00), device.Sets[0]);
    }

    [Fact]
    public void Detach_sends_the_streaming_close_then_the_session_close()
    {
        var handshake = new NautilusLcdHandshake();
        var device = new FeatureDevice();
        handshake.OnFrameSent(50818, 51);

        handshake.OnDetach(device, ReportLength);

        Assert.Equal(new byte[] { 0x03, 0x1E, 0x00, 0x01, 50, 0, 18, 0 }, device.Sets[0][..8]);
        Assert.Equal(new byte[] { 0x03, 0x1D, 0x00, 0x01, 50, 0, 18, 0 }, device.Sets[1][..8]);
    }

    [Fact]
    public void Cancel_during_the_claim_pause_throws_and_returns_the_gate_held()
    {
        var handshake = new NautilusLcdHandshake();
        var device = new FeatureDevice();
        var gate = new object();
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        lock (gate)
        {
            Assert.Throws<OperationCanceledException>(() => handshake.Attach(device, ReportLength, gate, cts.Token));
            Assert.True(System.Threading.Monitor.IsEntered(gate));
        }
    }

    [Fact]
    public void A_failed_keepalive_asks_the_hub_to_drop_the_handle()
    {
        var handshake = new NautilusLcdHandshake();
        var device = new FeatureDevice { SetsSucceed = false };

        Assert.False(handshake.Tick(device, ReportLength, 0));
    }

    [Fact]
    public void The_hub_reports_each_frame_to_the_handshake_and_ticks_on_the_icue_cadence()
    {
        var model = JpegPanelModel.CorsairNautilusCap with { Handshake = new NautilusLcdHandshake() };
        using var hub = new JpegPanelHub(model);
        var device = new FeatureDevice();
        Assert.True(hub.Attach(device));

        Assert.True(hub.SendFrame(new byte[2032 + 100]));
        hub.Tick();

        // Three chunks: the keepalive size is the JPEG plus two chunk headers.
        int expected = 2132 + 16;
        var keepalive = device.Sets[^1];
        Assert.Equal(expected, ((keepalive[4] | (keepalive[5] << 8)) << 10) + (keepalive[6] | (keepalive[7] << 8)));
        Assert.Equal(4500, hub.TickIntervalMs);
    }

    [Fact]
    public void Frames_use_selector_zero_and_split_a_large_jpeg_into_1016_byte_chunks()
    {
        var model = JpegPanelModel.CorsairNautilusCap;
        Assert.Equal(0x1B1C, model.VendorId);
        Assert.Equal(new[] { 0x0C55 }, model.ProductIds);

        var report = new byte[model.ReportLength];
        var jpeg = new byte[91_000];
        int written = JpegPanelProtocol.FillChunk(report, model.HeaderStyle, model.Selector, jpeg, 0, 0);

        Assert.Equal(1016, written);
        Assert.Equal(new byte[] { 0x02, 0x05, 0x00, 0x00, 0x00, 0x00, 0xF8, 0x03 }, report[..8]);
        Assert.Equal(90, (jpeg.Length + 1015) / 1016);

        int lastOffset = 89 * 1016;
        JpegPanelProtocol.FillChunk(report, model.HeaderStyle, model.Selector, jpeg, lastOffset, 89);
        Assert.Equal(0x01, report[3]);
        Assert.Equal(89, report[4]);
    }

    private sealed class FeatureDevice : IHidDevice
    {
        public List<byte[]> Sets { get; } = new();
        public List<string> Gets { get; } = new();
        public Dictionary<byte, byte[]> Replies { get; } = new();
        public bool SetsSucceed { get; set; } = true;
        public bool GetsSucceed { get; set; } = true;

        public int VendorId => 0x1B1C;
        public int ProductId => 0x0C55;
        public string Path => "/dev/fake-nautilus";
        public string? Serial => "FAKE";
        public int UsagePage => 0x000C;
        public int Usage => 0x01;

        public bool SetFeature(ReadOnlySpan<byte> report)
        {
            Sets.Add(report.ToArray());
            return SetsSucceed;
        }

        public bool GetFeature(Span<byte> buffer)
        {
            Gets.Add($"G{buffer[0]:X2}");
            if (!GetsSucceed)
            {
                return false;
            }
            if (Replies.TryGetValue(buffer[0], out var reply))
            {
                reply.AsSpan(0, Math.Min(reply.Length, buffer.Length)).CopyTo(buffer);
            }
            return true;
        }

        public bool Write(ReadOnlySpan<byte> report) => true;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public void Dispose() { }
    }
}
