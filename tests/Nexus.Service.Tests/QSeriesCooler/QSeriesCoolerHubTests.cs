using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Np50;          // INp50Transport, Np50PortInfo
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using RgbColor = Nexus.Service.Peripherals.Hyte.MiniHub.RgbColor;

namespace Nexus.Service.Tests.QSeriesCooler;

/// <summary>
/// End-to-end-on-the-wire coverage for <see cref="QSeriesCoolerHub.WriteLighting"/> and
/// <see cref="QSeriesCoolerHub.WriteLinkLighting"/> using a fake transport - proves the
/// lighting paths actually emit serial bytes (software-control once per connect, then
/// the port frames), not a stub.
/// </summary>
public class QSeriesCoolerHubTests
{
    private static readonly byte[] SetSoftwareControl = { 0xFF, 0xDD, 0x03, 0x00 };

    private static QSeriesCoolerHub NewHub(out FakeTransport transport)
    {
        var t = new FakeTransport();
        transport = t;
        var discovery = new FakeDiscovery(new QSeriesCoolerPort
        {
            PortName = "COM_TEST", Serial = "QTEST123", Variant = QSeriesCoolerProtocol.VariantQ60,
        });
        return new QSeriesCoolerHub(discovery, _ => t);
    }

    [Fact]
    public void WriteLighting_first_frame_sends_software_control_then_the_panel_and_logo_ports()
    {
        var hub = NewHub(out var t);
        hub.WriteLighting(new[] { new RgbColor(0x10, 0x20, 0x30) });

        // 1 control frame + ports 3 and 4 only - 1 and 2 belong to WriteLinkLighting.
        Assert.Equal(3, t.Writes.Count);
        Assert.Equal(SetSoftwareControl, t.Writes[0]);
        var ports = new[] { t.Writes[1][3], t.Writes[2][3] };
        Assert.Equal(new byte[] { QSeriesCoolerProtocol.BacklightPort, QSeriesCoolerProtocol.LogoPort }, ports);
        foreach (var frame in new[] { t.Writes[1], t.Writes[2] })
        {
            Assert.Equal(90, frame.Length);
            Assert.Equal(0xFF, frame[0]);
            Assert.Equal(0xEE, frame[1]);
            Assert.Equal(0x01, frame[2]);
        }
    }

    [Fact]
    public void WriteLighting_does_not_re_send_software_control_on_subsequent_frames()
    {
        var hub = NewHub(out var t);
        hub.WriteLighting(new[] { new RgbColor(1, 2, 3) });
        hub.WriteLighting(new[] { new RgbColor(4, 5, 6) });

        // First call: 1 control + 2 ports. Second call: 2 ports only.
        Assert.Equal(5, t.Writes.Count);
        Assert.Equal(SetSoftwareControl, t.Writes[0]);
        // The 4th write (index 3) is the panel port again, NOT another control frame.
        Assert.Equal(0xEE, t.Writes[3][1]);
        Assert.DoesNotContain(t.Writes.Skip(1), w => w.AsSpan().SequenceEqual(SetSoftwareControl));
    }

    [Fact]
    public void WriteLighting_routes_the_backlight_and_logo_to_their_own_ports()
    {
        var hub = NewHub(out var t);
        var leds = new RgbColor[QSeriesCoolerHub.LedCount];
        for (var i = 0; i < QSeriesCoolerProtocol.BacklightLedCount; i++) leds[i] = new RgbColor(R: 0xAA, G: 0xBB, B: 0xCC);
        for (var i = QSeriesCoolerProtocol.BacklightLedCount; i < leds.Length; i++) leds[i] = new RgbColor(R: 0x11, G: 0x22, B: 0x33);

        hub.WriteLighting(leds);

        var byPort = new byte[QSeriesCoolerProtocol.LedPortCount + 1][];
        foreach (var w in t.Writes)
        {
            if (w.Length > 3 && w[1] == 0xEE) byPort[w[3]] = w;
        }

        var backlight = byPort[QSeriesCoolerProtocol.BacklightPort];
        Assert.Equal(7 + QSeriesCoolerProtocol.BacklightLedCount * 3, backlight.Length);
        for (var i = 0; i < QSeriesCoolerProtocol.BacklightLedCount; i++)
        {
            Assert.Equal(0xBB, backlight[7 + i * 3 + 0]); // G
            Assert.Equal(0xAA, backlight[7 + i * 3 + 1]); // R
            Assert.Equal(0xCC, backlight[7 + i * 3 + 2]); // B
        }

        var logo = byPort[QSeriesCoolerProtocol.LogoPort];
        for (var i = 0; i < QSeriesCoolerProtocol.LogoLedCount; i++)
        {
            Assert.Equal(0x22, logo[7 + i * 3 + 0]);
            Assert.Equal(0x11, logo[7 + i * 3 + 1]);
            Assert.Equal(0x33, logo[7 + i * 3 + 2]);
        }
        // Past the logo's own LEDs the frame is pad, not more panel colours.
        for (var i = 7 + QSeriesCoolerProtocol.LogoLedCount * 3; i < logo.Length; i++) Assert.Equal(0x00, logo[i]);

        // WriteLighting no longer touches ports 1/2 at all.
        Assert.Null(byPort[1]);
        Assert.Null(byPort[2]);
    }

    [Fact]
    public void WriteLinkLighting_streams_to_port_1_or_2_and_shares_the_software_control_assertion()
    {
        var hub = NewHub(out var t);
        hub.WriteLinkLighting(QSeriesCoolerProtocol.LinkChannel1, new[] { new RgbColor(0x01, 0x02, 0x03) });
        hub.WriteLinkLighting(QSeriesCoolerProtocol.FanChannel, new[] { new RgbColor(0x04, 0x05, 0x06) });

        // Software control asserted once, then one frame per call - no duplicate control frame.
        Assert.Equal(3, t.Writes.Count);
        Assert.Equal(SetSoftwareControl, t.Writes[0]);
        Assert.Equal(0xEE, t.Writes[1][1]);
        Assert.Equal(QSeriesCoolerProtocol.LinkChannel1, t.Writes[1][3]);
        Assert.Equal(0xEE, t.Writes[2][1]);
        Assert.Equal(QSeriesCoolerProtocol.FanChannel, t.Writes[2][3]);
    }

    [Fact]
    public void WriteLinkLighting_rejects_ports_outside_1_and_2()
    {
        var hub = NewHub(out _);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            hub.WriteLinkLighting(3, ReadOnlySpan<RgbColor>.Empty));
    }

    [Fact]
    public void WriteLighting_re_asserts_software_control_after_disconnect()
    {
        var hub = NewHub(out var t);
        hub.WriteLighting(new[] { new RgbColor(1, 2, 3) });
        hub.Disconnect();
        hub.WriteLighting(new[] { new RgbColor(1, 2, 3) });

        // After disconnect, _rgbInSwControl resets, so a fresh control frame is sent.
        var controlFrames = t.Writes.Count(w => w.AsSpan().SequenceEqual(SetSoftwareControl));
        Assert.Equal(2, controlFrames);
    }

    private sealed class FakeDiscovery : IQSeriesCoolerPortDiscovery
    {
        private readonly QSeriesCoolerPort[] _ports;
        public FakeDiscovery(params QSeriesCoolerPort[] ports) => _ports = ports;
        public IReadOnlyList<QSeriesCoolerPort> Discover() => _ports;
    }

    private sealed class FakeTransport : INp50Transport
    {
        public readonly List<byte[]> Writes = new();
        public bool IsOpen => true;
        public string Serial => "QTEST123";
        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
        public void DiscardInput() { }
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }
}
