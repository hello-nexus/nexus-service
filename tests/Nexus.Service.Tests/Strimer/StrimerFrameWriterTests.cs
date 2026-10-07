using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Strimer;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Strimer;

/// <summary>
/// Verifies the TickCustom call sequence: per-zone color data followed immediately
/// by a Direct mode-set, then a single apply-latch after all 12 zones.
/// </summary>
public class StrimerFrameWriterTests
{
    private readonly StrimerTransportSpy _spy = new();
    private readonly StrimerHub _hub = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly StrimerLightingFrameWriter _writer;

    public StrimerFrameWriterTests()
    {
        _hub.Attach(_spy);
        _store.Update(s => s.Devices.StrimerLighting.Mode = "custom");
        var provider = new StrimerLightingDeviceProvider(_hub, _store, new Np50IdentifyTracker());
        _writer = new StrimerLightingFrameWriter(new LightingEngine(), _hub, _store, new Np50IdentifyTracker(), provider);
    }

    [Fact]
    public void TickCustom_emits_25_writes_for_12_zones_plus_latch()
    {
        _writer.TickCustom(_store.Load(), System.Array.Empty<DeviceFrame>(), 1f);
        // 12 zones * 2 (color + effect commit) + 1 apply latch
        Assert.Equal(25, _spy.Calls.Count);
    }

    [Fact]
    public void TickCustom_sends_color_then_direct_mode_commit_per_zone()
    {
        _writer.TickCustom(_store.Load(), System.Array.Empty<DeviceFrame>(), 1f);
        var calls = _spy.Calls;
        for (var zone = 0; zone < 12; zone++)
        {
            var colorIdx  = zone * 2;
            var effectIdx = zone * 2 + 1;

            // color write: report[1] = 0x30 | zone
            Assert.Equal(0xE0, calls[colorIdx].Bytes[0]);
            Assert.Equal((byte)(0x30 | zone), calls[colorIdx].Bytes[1]);

            // effect commit: report[1] = 0x10 | zone, report[2] = ModeDirect
            Assert.Equal(0xE0, calls[effectIdx].Bytes[0]);
            Assert.Equal((byte)(0x10 | zone), calls[effectIdx].Bytes[1]);
            Assert.Equal(StrimerProtocol.ModeDirect, calls[effectIdx].Bytes[2]);
        }
    }

    [Fact]
    public void TickCustom_sends_apply_latch_after_all_zones()
    {
        _writer.TickCustom(_store.Load(), System.Array.Empty<DeviceFrame>(), 1f);
        var last = _spy.Calls[^1].Bytes;
        Assert.Equal(0xE0, last[0]);
        Assert.Equal(0x2C, last[1]);
        Assert.Equal(0x0F, last[2]);
        Assert.Equal(0xFF, last[3]);
    }
}

public class StrimerArgbSyncTests
{
    private readonly StrimerTransportSpy _spy = new();
    private readonly StrimerHub _hub = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly StrimerLightingDeviceProvider _provider;
    private readonly StrimerLightingFrameWriter _writer;

    public StrimerArgbSyncTests()
    {
        _hub.Attach(_spy);
        _provider = new StrimerLightingDeviceProvider(_hub, _store, new Np50IdentifyTracker());
        _writer = new StrimerLightingFrameWriter(new LightingEngine(), _hub, _store, new Np50IdentifyTracker(), _provider);
    }

    private static bool IsSync(StrimerTransportSpy.Call c, byte on) => c.Bytes[1] == 0x10 && c.Bytes[2] == 0x40 && c.Bytes[3] == on;

    [Fact]
    public void Turning_sync_on_switches_the_controller_once_and_hides_the_cable()
    {
        _store.Update(s => s.Devices.StrimerLighting.ArgbSync = true);

        _writer.Tick();
        _writer.Tick();

        Assert.Single(_spy.Calls, c => IsSync(c, 1));
        Assert.Equal(new byte[] { 0xE0, 0x20, 0x00, 0x00 }, _spy.Calls[1].Bytes[0..4]);
        Assert.Equal(2, _spy.Calls.Count);
        Assert.Empty(_provider.GetStructures());
        Assert.Empty(_provider.BuildFrames(0));
    }

    [Fact]
    public void A_cable_never_put_on_sync_gets_no_sync_write()
    {
        _writer.Tick();

        Assert.DoesNotContain(_spy.Calls, c => c.Bytes[2] == 0x40);
    }

    [Fact]
    public void Turning_sync_off_switches_the_controller_back()
    {
        _store.Update(s => s.Devices.StrimerLighting.ArgbSync = true);
        _writer.Tick();
        _store.Update(s => s.Devices.StrimerLighting.ArgbSync = false);

        _writer.Tick();

        Assert.Single(_spy.Calls, c => IsSync(c, 0));
        Assert.NotEmpty(_provider.GetStructures());
    }
}
