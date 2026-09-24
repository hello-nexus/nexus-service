using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Tests.Np50;

/// <summary>
/// The typed firmware-animation read over 0xCC 0x0D, driven with the 8-byte
/// reply fw 2.0.5.1 sends (bench, 2026-09-13).
/// </summary>
public class Np50FirmwareAnimationReadTests
{
    private static byte[] GoodHubInfo()
    {
        var reply = new byte[20];
        reply[0] = 0xFF;
        reply[1] = 0xCC;
        return reply;
    }

    [Fact]
    public void Reads_the_8_byte_0x0D_reply()
    {
        var hub = NewHub(out var transport, GoodHubInfo);
        Assert.True(hub.PollHubInfo());
        transport.NextRead = () => new byte[] { 0xFF, 0xCC, 0x0D, Np50Protocol.FwAnimationRainbow, 1, 2, 3, 40 };
        transport.Writes.Clear();

        var a = hub.GetFirmwareAnimation();

        Assert.NotNull(a);
        Assert.Equal(Np50Protocol.FwAnimationRainbow, a!.Value.Animation);
        Assert.Equal((1, 2, 3, 40), (a.Value.R, a.Value.G, a.Value.B, a.Value.Brightness));
        Assert.Contains(transport.Writes, w => w.SequenceEqual(Np50Protocol.BuildGetFirmwareAnimation()));
    }

    [Fact]
    public void Is_null_when_the_hub_does_not_answer()
    {
        var hub = NewHub(out var transport, GoodHubInfo);
        Assert.True(hub.PollHubInfo());
        transport.NextRead = Array.Empty<byte>;

        Assert.Null(hub.GetFirmwareAnimation());
    }

    [Fact]
    public void An_identical_write_is_skipped_when_0x0D_reports_it()
    {
        var hub = NewHub(out var transport, GoodHubInfo);
        Assert.True(hub.PollHubInfo());
        transport.NextRead = () => new byte[] { 0xFF, 0xCC, 0x0D, Np50Protocol.FwAnimationColor, 200, 0, 50, 90 };
        transport.Writes.Clear();

        Assert.True(hub.SetFirmwareAnimation(Np50Protocol.FwAnimationColor, 200, 0, 50, 90));

        // EEPROM endurance: only the 0x0D read went out, no 0x0C write.
        Assert.DoesNotContain(transport.Writes, w => w[2] == 0x0C);
    }

    [Fact]
    public void A_different_write_goes_out()
    {
        var hub = NewHub(out var transport, GoodHubInfo);
        Assert.True(hub.PollHubInfo());
        transport.NextRead = () => new byte[] { 0xFF, 0xCC, 0x0D, Np50Protocol.FwAnimationColor, 200, 0, 50, 90 };
        transport.Writes.Clear();

        Assert.True(hub.SetFirmwareAnimation(Np50Protocol.FwAnimationBreathe, 200, 0, 50, 90));

        Assert.Single(transport.Writes, w => w.SequenceEqual(
            Np50Protocol.BuildWriteFirmwareAnimationToMcu(Np50Protocol.FwAnimationBreathe, 200, 0, 50, 90)));
    }

    private static Np50Hub NewHub(out FakeTransport transport, Func<byte[]> read)
    {
        var t = new FakeTransport { NextRead = read };
        transport = t;
        var hub = new Np50Hub(new FakeDiscovery(), _ => t);
        hub.EnsureConnected();
        return hub;
    }

    private sealed class FakeDiscovery : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover()
            => new[] { new Np50PortInfo { PortName = "COM_TEST", Serial = "NPTEST" } };
    }

    private sealed class FakeTransport : INp50Transport
    {
        public readonly List<byte[]> Writes = new();
        public Func<byte[]> NextRead = Array.Empty<byte>;
        public bool Disposed { get; private set; }
        public bool IsOpen => !Disposed;
        public string Serial => "NPTEST";
        public void DiscardInput() { }
        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
        public int Read(Span<byte> buffer, int timeoutMs)
        {
            var src = NextRead();
            var n = Math.Min(src.Length, buffer.Length);
            src.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
        public void Dispose() => Disposed = true;
    }
}
