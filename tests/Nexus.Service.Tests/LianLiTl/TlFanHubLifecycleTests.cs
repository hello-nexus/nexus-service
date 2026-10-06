using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.LianLiCp;
using Nexus.Service.Peripherals.LianLiTl;

namespace Nexus.Service.Tests.LianLiTl;

// Minimal fake: records Write calls and returns a fixed handshake reply on Read.
internal sealed class TlDeviceFake : IHidDevice
{
    private readonly byte[]? _handshakeReply;

    public List<byte[]> Writes { get; } = new();

    public TlDeviceFake(byte[]? handshakeReply = null)
    {
        _handshakeReply = handshakeReply;
    }

    public int VendorId => TlFanProtocol.VendorId;
    public int ProductId => TlFanProtocol.ProductId;
    public string Path => "fake";
    public string? Serial => null;
    public int UsagePage => 0xFF00;
    public int Usage => 0x01;

    public bool Write(ReadOnlySpan<byte> report)
    {
        Writes.Add(report.ToArray());
        return true;
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_handshakeReply == null)
        {
            return 0;
        }
        int n = Math.Min(buffer.Length, _handshakeReply.Length);
        _handshakeReply.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public bool SetFeature(ReadOnlySpan<byte> report) => false;
    public bool GetFeature(Span<byte> buffer) => false;
    public bool GetInputReport(Span<byte> buffer) => false;
    public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
    public void Dispose() { }
}

public class TlFanHubLifecycleTests
{
    // Builds a 64-byte handshake reply matching the wire format from TlFanProtocol.
    // Each fan is (port, fanIdx, rpm). Header byte: bit7=detected, bits[5:4]=port, bits[3:0]=fanIdx.
    private static byte[] BuildHandshakeReply(params (int port, int fanIdx, int rpm)[] fans)
    {
        var packet = new byte[CommandPacket.Length];
        packet[0] = CommandPacket.ReportId;
        packet[1] = 0xA1;
        // PayloadOffset is 6; payload length field sits one byte before it.
        packet[CommandPacket.PayloadOffset - 1] = (byte)(fans.Length * 3);
        for (int i = 0; i < fans.Length; i++)
        {
            int offset = CommandPacket.PayloadOffset + i * 3;
            var (port, fanIdx, rpm) = fans[i];
            packet[offset] = (byte)(0x80 | ((port & 0x03) << 4) | (fanIdx & 0x0F));
            packet[offset + 1] = (byte)(rpm >> 8);
            packet[offset + 2] = (byte)(rpm & 0xFF);
        }
        return packet;
    }

    [Fact]
    public void Detach_resets_channel_count_to_zero_with_no_exception()
    {
        var fake = new TlDeviceFake(BuildHandshakeReply((port: 1, fanIdx: 0, rpm: 1200)));
        using var hub = new TlFanHub();
        hub.Attach(fake);

        bool discovered = hub.DiscoverFans();

        Assert.True(discovered);
        Assert.Equal(1, hub.ChannelCount);
        Assert.True(hub.IsConnected);

        hub.Detach();

        Assert.Equal(0, hub.ChannelCount);
        Assert.False(hub.IsConnected);
    }

    [Fact]
    public void DiscoverFans_sorts_channels_by_port_then_fanIdx()
    {
        // Fans out of sort order to verify sorting.
        var fake = new TlDeviceFake(BuildHandshakeReply(
            (port: 2, fanIdx: 1, rpm: 1100),
            (port: 0, fanIdx: 3, rpm: 900),
            (port: 1, fanIdx: 0, rpm: 1200)));
        using var hub = new TlFanHub();
        hub.Attach(fake);

        bool discovered = hub.DiscoverFans();

        Assert.True(discovered);
        var snap = hub.Snapshot;
        Assert.Equal(3, snap.ChannelCount);

        Assert.Equal(0, snap.Port[0]);
        Assert.Equal(3, snap.FanIndex[0]);

        Assert.Equal(1, snap.Port[1]);
        Assert.Equal(0, snap.FanIndex[1]);

        Assert.Equal(2, snap.Port[2]);
        Assert.Equal(1, snap.FanIndex[2]);
    }

    [Fact]
    public void Snapshot_taken_before_Detach_is_unaffected_by_Detach()
    {
        var fake = new TlDeviceFake(BuildHandshakeReply((port: 0, fanIdx: 0, rpm: 800)));
        using var hub = new TlFanHub();
        hub.Attach(fake);
        hub.DiscoverFans();

        var snapBefore = hub.Snapshot;
        Assert.Equal(1, snapBefore.ChannelCount);

        hub.Detach();

        Assert.Equal(0, hub.ChannelCount);
        Assert.Equal(1, snapBefore.ChannelCount);
        Assert.Equal(0, snapBefore.Port[0]);
        Assert.Equal(0, snapBefore.FanIndex[0]);
    }

    [Fact]
    public void DiscoverFans_sends_mobo_sync_off_for_each_detected_fan()
    {
        var fake = new TlDeviceFake(BuildHandshakeReply(
            (port: 0, fanIdx: 0, rpm: 1000),
            (port: 1, fanIdx: 2, rpm: 1500)));
        using var hub = new TlFanHub();
        hub.Attach(fake);
        hub.DiscoverFans();

        // All 0xB1 writes are mobo-sync commands (byte[1] = command).
        var syncWrites = fake.Writes.FindAll(w => w.Length > 1 && w[1] == 0xB1);
        Assert.Equal(2, syncWrites.Count);

        // Address byte is at PayloadOffset; sync=false means bit7 is clear.
        foreach (var w in syncWrites)
        {
            byte address = w[CommandPacket.PayloadOffset];
            Assert.Equal(0, address & 0x80);
        }

        // The two addresses match the two detected fans.
        byte addrP0F0 = (byte)(((0 & 0x0F) << 4) | (0 & 0x0F));
        byte addrP1F2 = (byte)(((1 & 0x0F) << 4) | (2 & 0x0F));
        var addresses = new HashSet<byte>
        {
            syncWrites[0][CommandPacket.PayloadOffset],
            syncWrites[1][CommandPacket.PayloadOffset],
        };
        Assert.Contains(addrP0F0, addresses);
        Assert.Contains(addrP1F2, addresses);
    }
}

public class TlLightingFrameWriterArgbSyncTests
{
    private static byte[] Handshake(params (int port, int fanIdx)[] fans)
    {
        var packet = new byte[CommandPacket.Length];
        packet[0] = CommandPacket.ReportId;
        packet[1] = 0xA1;
        packet[CommandPacket.PayloadOffset - 1] = (byte)(fans.Length * 3);
        for (int i = 0; i < fans.Length; i++)
        {
            packet[CommandPacket.PayloadOffset + i * 3] = (byte)(0x80 | ((fans[i].port & 0x03) << 4) | (fans[i].fanIdx & 0x0F));
        }
        return packet;
    }

    [Fact]
    public void Argb_sync_sets_the_sync_bit_on_every_fan_whatever_the_scope()
    {
        var fake = new TlDeviceFake(Handshake((0, 0), (1, 2)));
        using var hub = new TlFanHub();
        hub.Attach(fake);
        Assert.True(hub.DiscoverFans());
        var store = new InMemoryConfigStore();
        store.Update(s => { s.Devices.TlLighting.Scope = "top"; s.Devices.TlLighting.ArgbSync = true; });
        var writer = new Nexus.Service.Lighting.TlLightingFrameWriter(hub, store);
        fake.Writes.Clear();

        writer.Tick();

        var fanLights = fake.Writes.FindAll(w => w[1] == 0xA3);
        Assert.Equal(2, fanLights.Count);
        Assert.All(fanLights, w => Assert.Equal(1, w[CommandPacket.PayloadOffset] & 0x01));
    }

    [Fact]
    public void Leaving_sync_on_a_scoped_look_clears_each_fans_sync_bit()
    {
        var fake = new TlDeviceFake(Handshake((0, 0), (1, 1)));
        using var hub = new TlFanHub();
        hub.Attach(fake);
        Assert.True(hub.DiscoverFans());
        var store = new InMemoryConfigStore();
        store.Update(s => { s.Devices.TlLighting.Scope = "top"; s.Devices.TlLighting.ArgbSync = true; });
        var writer = new Nexus.Service.Lighting.TlLightingFrameWriter(hub, store);
        writer.Tick();
        fake.Writes.Clear();

        store.Update(s => s.Devices.TlLighting.ArgbSync = false);
        writer.Tick();

        var fanLights = fake.Writes.FindAll(w => w[1] == 0xA3);
        Assert.Equal(2, fanLights.Count);
        Assert.All(fanLights, w => Assert.Equal(0, w[CommandPacket.PayloadOffset] & 0x01));
        Assert.Contains(fake.Writes, w => w[1] != 0xA3);
    }

    [Fact]
    public void Turning_argb_sync_off_recommits_without_the_sync_bit()
    {
        var fake = new TlDeviceFake(Handshake((0, 0)));
        using var hub = new TlFanHub();
        hub.Attach(fake);
        Assert.True(hub.DiscoverFans());
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.TlLighting.ArgbSync = true);
        var writer = new Nexus.Service.Lighting.TlLightingFrameWriter(hub, store);
        writer.Tick();
        fake.Writes.Clear();

        store.Update(s => s.Devices.TlLighting.ArgbSync = false);
        writer.Tick();

        var fanLight = Assert.Single(fake.Writes.FindAll(w => w[1] == 0xA3));
        Assert.Equal(0, fanLight[CommandPacket.PayloadOffset] & 0x01);
    }
}
