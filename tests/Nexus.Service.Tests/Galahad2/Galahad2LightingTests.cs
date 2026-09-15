using System;
using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Peripherals.LianLiCp;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Galahad2;

public class Galahad2LightingTests
{
    // ── Protocol / encoding ──

    private const int PayloadBase = CommandPacket.PayloadOffset; // 6

    // Returns a minimal connected hub backed by a fake that accepts the handshake.
    private static (Galahad2Hub hub, Galahad2DeviceFake fake) ConnectedHub()
    {
        var reply  = BuildHandshakeReply();
        var fake   = new Galahad2DeviceFake(reply);
        var hub    = new Galahad2Hub();
        hub.Attach(fake);
        hub.Connect();
        return (hub, fake);
    }

    private static byte[] BuildHandshakeReply()
    {
        var packet = new byte[CommandPacket.Length];
        packet[0] = CommandPacket.ReportId;
        packet[1] = 0x81;
        packet[5] = 4;
        return packet;
    }

    [Fact]
    public void EncodeLighting_cmd_byte_is_0x83()
    {
        var packet = Galahad2Protocol.EncodeLighting(0, 0x01, 4, 2, 0, ReadOnlySpan<byte>.Empty);
        Assert.Equal(0x83, packet[1]);
    }

    [Fact]
    public void EncodeLighting_payload_length_byte_is_19()
    {
        var packet = Galahad2Protocol.EncodeLighting(0, 0x01, 4, 2, 0, ReadOnlySpan<byte>.Empty);
        Assert.Equal(19, packet[5]);
    }

    [Fact]
    public void EncodeLighting_ring_at_payload_offset_0()
    {
        var packet = Galahad2Protocol.EncodeLighting(ring: 2, mode: 0x01, brightness: 0, speed: 0, direction: 0, colors: ReadOnlySpan<byte>.Empty);
        Assert.Equal(2, packet[PayloadBase + 0]);
    }

    [Fact]
    public void EncodeLighting_mode_at_payload_offset_1()
    {
        var packet = Galahad2Protocol.EncodeLighting(ring: 0, mode: 0x03, brightness: 0, speed: 0, direction: 0, colors: ReadOnlySpan<byte>.Empty);
        Assert.Equal(0x03, packet[PayloadBase + 1]);
    }

    [Fact]
    public void EncodeLighting_brightness_at_payload_offset_2()
    {
        var packet = Galahad2Protocol.EncodeLighting(ring: 0, mode: 0, brightness: 3, speed: 0, direction: 0, colors: ReadOnlySpan<byte>.Empty);
        Assert.Equal(3, packet[PayloadBase + 2]);
    }

    [Fact]
    public void EncodeLighting_speed_at_payload_offset_3()
    {
        var packet = Galahad2Protocol.EncodeLighting(ring: 0, mode: 0, brightness: 0, speed: 4, direction: 0, colors: ReadOnlySpan<byte>.Empty);
        Assert.Equal(4, packet[PayloadBase + 3]);
    }

    [Fact]
    public void EncodeLighting_direction_at_payload_offset_16()
    {
        var packet = Galahad2Protocol.EncodeLighting(ring: 0, mode: 0, brightness: 0, speed: 0, direction: 1, colors: ReadOnlySpan<byte>.Empty);
        Assert.Equal(1, packet[PayloadBase + 16]);
    }

    [Fact]
    public void EncodeLighting_color_R_G_B_at_offsets_4_5_6()
    {
        byte[] colors = { 0xAA, 0xBB, 0xCC };
        var packet = Galahad2Protocol.EncodeLighting(0, 0, 0, 0, 0, colors);
        Assert.Equal(0xAA, packet[PayloadBase + 4]);
        Assert.Equal(0xBB, packet[PayloadBase + 5]);
        Assert.Equal(0xCC, packet[PayloadBase + 6]);
    }

    [Fact]
    public void EncodeLighting_second_color_slot_at_offsets_7_8_9()
    {
        byte[] colors = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        var packet = Galahad2Protocol.EncodeLighting(0, 0, 0, 0, 0, colors);
        Assert.Equal(0x44, packet[PayloadBase + 7]);
        Assert.Equal(0x55, packet[PayloadBase + 8]);
        Assert.Equal(0x66, packet[PayloadBase + 9]);
    }

    [Fact]
    public void EncodeLighting_zero_colors_produces_zero_color_slots()
    {
        var packet = Galahad2Protocol.EncodeLighting(0, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty);
        for (var i = PayloadBase + 4; i <= PayloadBase + 15; i++)
        {
            Assert.Equal(0, packet[i]);
        }
    }

    [Fact]
    public void EncodeLighting_colors_clamped_to_12_bytes()
    {
        // 13 bytes: only first 12 must appear; byte 13 is silently dropped.
        var colors = new byte[13];
        for (var i = 0; i < colors.Length; i++) colors[i] = (byte)(i + 1);
        var packet = Galahad2Protocol.EncodeLighting(0, 0, 0, 0, 0, colors);
        Assert.Equal(12, packet[PayloadBase + 15]); // last allowed byte
        Assert.Equal(0,  packet[PayloadBase + 16]); // direction slot, must stay zero here
    }

    [Fact]
    public void EncodePumpPerLed_uses_the_1024_byte_report_and_pump_header()
    {
        var packet = Galahad2Protocol.EncodePumpPerLed(new byte[36]);

        Assert.Equal(1024, packet.Length);
        Assert.Equal(0x02, packet[0]);
        Assert.Equal(0x14, packet[1]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x3D }, packet[2..6]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00 }, packet[6..9]);
        Assert.Equal(new byte[] { 0x00, 0x3D }, packet[9..11]);
        Assert.Equal(0x00, packet[11]);
        Assert.All(packet[12..36], b => Assert.Equal(0, b));
    }

    [Fact]
    public void EncodePumpPerLed_reverses_the_twelve_ui_positions_on_the_wire()
    {
        var colors = new byte[36];
        for (var i = 0; i < colors.Length; i++)
        {
            colors[i] = (byte)(i + 1);
        }

        var packet = Galahad2Protocol.EncodePumpPerLed(colors);

        // data starts at byte 11; data[25] is the first transmitted RGB triplet.
        Assert.Equal(new byte[] { 34, 35, 36 }, packet[36..39]); // UI LED 12
        Assert.Equal(new byte[] { 1, 2, 3 }, packet[69..72]);    // UI LED 1
        Assert.All(packet[72..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void EncodePumpPerLed_ignores_colors_after_the_twelve_supported_positions()
    {
        var colors = new byte[39];
        colors[36] = 0xAA;
        colors[37] = 0xBB;
        colors[38] = 0xCC;

        var packet = Galahad2Protocol.EncodePumpPerLed(colors);

        Assert.All(packet[36..72], b => Assert.Equal(0, b));
    }

    // ── Hub.SendLighting ──

    [Fact]
    public void SendLighting_returns_false_when_not_connected()
    {
        using var hub = new Galahad2Hub();
        bool ok = hub.SendLighting(0, 0x03, 4, 0, 0, ReadOnlySpan<byte>.Empty);
        Assert.False(ok);
    }

    [Fact]
    public void SendLighting_writes_packet_with_cmd_0x83_when_connected()
    {
        var (hub, fake) = ConnectedHub();
        using (hub)
        {
            fake.Writes.Clear();
            hub.SendLighting(ring: 2, mode: 0x03, brightness: 4, speed: 2, direction: 0, colors: ReadOnlySpan<byte>.Empty);
            Assert.Single(fake.Writes);
            Assert.Equal(0x83, fake.Writes[0][1]);
        }
    }

    // ── Provider structure ──

    private static InMemoryConfigStoreForGalahad2 MakeStore() => new();

    [Fact]
    public void GetAll_returns_empty_when_disconnected()
    {
        using var hub      = new Galahad2Hub();
        var provider = new Galahad2LightingDeviceProvider(hub, MakeStore());
        var result   = provider.GetAll();
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void GetAll_returns_2_zones_when_connected()
    {
        var (hub, _) = ConnectedHub();
        using (hub)
        {
            var provider = new Galahad2LightingDeviceProvider(hub, MakeStore());
            var result   = provider.GetAll();
            Assert.Equal(2, result.Devices.Count);
        }
    }

    [Fact]
    public void GetAll_zone_ids_use_lianli_aio_namespace()
    {
        var (hub, _) = ConnectedHub();
        using (hub)
        {
            var provider = new Galahad2LightingDeviceProvider(hub, MakeStore());
            var devices  = provider.GetAll().Devices;
            foreach (var d in devices)
            {
                Assert.StartsWith("lianli-aio:", d.Id, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void GetStructures_returns_empty_when_disconnected()
    {
        using var hub      = new Galahad2Hub();
        var provider = new Galahad2LightingDeviceProvider(hub, MakeStore());
        Assert.Empty(provider.GetStructures());
    }

    [Fact]
    public void GetStructures_returns_1_structure_when_connected()
    {
        var (hub, _) = ConnectedHub();
        using (hub)
        {
            var provider = new Galahad2LightingDeviceProvider(hub, MakeStore());
            Assert.Single(provider.GetStructures());
        }
    }

    [Fact]
    public void GetStructures_structure_has_2_segments()
    {
        var (hub, _) = ConnectedHub();
        using (hub)
        {
            var provider   = new Galahad2LightingDeviceProvider(hub, MakeStore());
            var structures = provider.GetStructures();
            Assert.Equal(2, structures[0].Segments.Count);
        }
    }

    // ── Mode catalog ──

    [Fact]
    public void Catalog_contains_15_modes()
    {
        Assert.Equal(15, Galahad2LightingModes.Catalog.Length);
    }

    [Fact]
    public void Find_returns_null_for_unknown_key()
    {
        Assert.Null(Galahad2LightingModes.Find("unknown"));
        Assert.Null(Galahad2LightingModes.Find(""));
    }

    [Theory]
    [InlineData("rainbow",     0x01)]
    [InlineData("staticColor", 0x03)]
    [InlineData("colorsMorph", 0x0F)]
    public void Find_returns_correct_wire_byte(string key, byte expected)
    {
        var m = Galahad2LightingModes.Find(key);
        Assert.NotNull(m);
        Assert.Equal(expected, m!.WireByte);
    }

    [Fact]
    public void StaticColor_mode_has_ColorsMax_zero()
    {
        var m = Galahad2LightingModes.Find("staticColor");
        Assert.NotNull(m);
        Assert.Equal(0, m!.ColorsMax);
    }

    // ── Firmware color encoding ──

    [Fact]
    public void BuildColorBytes_staticColor_uses_InnerColor_slot0_OuterColor_slot1()
    {
        var mode = Galahad2LightingModes.Find("staticColor")!;
        var ls   = new Galahad2LightingSettings
        {
            InnerColor = "#FF0000",
            OuterColor = "#0000FF",
        };
        var bytes = Galahad2LightingFrameWriter.BuildColorBytes(ls, mode);
        Assert.Equal(6, bytes.Length);
        // slot0 = inner (red)
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0x00, bytes[1]);
        Assert.Equal(0x00, bytes[2]);
        // slot1 = outer (blue)
        Assert.Equal(0x00, bytes[3]);
        Assert.Equal(0x00, bytes[4]);
        Assert.Equal(0xFF, bytes[5]);
    }

    [Fact]
    public void BuildColorBytes_non_static_mode_uses_Colors_list()
    {
        var mode = Galahad2LightingModes.Find("breathingColor")!;
        var ls   = new Galahad2LightingSettings
        {
            InnerColor = "#FF0000",
            OuterColor = "#0000FF",
            Colors     = new List<string> { "#112233", "#445566" },
        };
        var bytes = Galahad2LightingFrameWriter.BuildColorBytes(ls, mode);
        Assert.Equal(6, bytes.Length);
        Assert.Equal(0x11, bytes[0]);
        Assert.Equal(0x22, bytes[1]);
        Assert.Equal(0x33, bytes[2]);
        Assert.Equal(0x44, bytes[3]);
        Assert.Equal(0x55, bytes[4]);
        Assert.Equal(0x66, bytes[5]);
    }
}

// Minimal in-memory store for Galahad2 provider tests.
internal sealed class InMemoryConfigStoreForGalahad2 : Nexus.Service.Persistence.IConfigStore
{
    private Nexus.Service.Persistence.NexusSettings _s = new();

    public string SettingsPath => ":memory:";

    public Nexus.Service.Persistence.NexusSettings Load() => _s;

    public void Update(Action<Nexus.Service.Persistence.NexusSettings> mutator)
    {
        mutator(_s);
        OnChanged?.Invoke();
    }

    public void Reload() { _s = new(); }
    public void FlushNow() { }
    public event Action? OnChanged;
}
