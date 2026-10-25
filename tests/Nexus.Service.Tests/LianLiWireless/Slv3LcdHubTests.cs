using System;
using System.Linq;
using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LcdHubTests
{
    [Fact]
    public void Screens_starts_empty_and_reflects_the_last_UpdateScreens_call()
    {
        var (hub, _, _) = CreateHub();
        Assert.Empty(hub.Screens);

        hub.UpdateScreens(new[] { new Slv3LcdScreenInfo { Serial = "SER1", Position = 1 } });

        var screen = Assert.Single(hub.Screens);
        Assert.Equal("SER1", screen.Serial);
        Assert.Equal(1, screen.Position);
    }

    [Fact]
    public void Discover_returns_the_discovery_ports()
    {
        var (hub, _, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "AAAA111122223333" });

        var ports = hub.Discover();

        var port = Assert.Single(ports);
        Assert.Equal("AAAA111122223333", port.Serial);
    }

    [Fact]
    public void PushImageToSerial_opens_and_pushes()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });

        var ok = hub.PushImageToSerial("SER1", new byte[] { 1, 2, 3 });

        Assert.True(ok);
        var transport = Assert.Single(factory.Opened.Values);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(transport.PushedImages));
    }

    [Theory]
    [InlineData(Slv3LcdProtocol.ProductIdTl, 60)]
    [InlineData(Slv3LcdProtocol.ProductIdSl, 30)]
    public void MaxFps_follows_the_opened_screens_model(int productId, int fps)
    {
        var (hub, _, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1", ProductId = productId });

        // Not opened yet: the model is unknown, so the slower rate.
        Assert.Equal(Slv3LcdHub.DefaultMaxFps, hub.MaxFps("SER1"));
        hub.PushImageToSerial("SER1", new byte[] { 1, 2, 3 });
        Assert.Equal(fps, hub.MaxFps("SER1"));
    }

    [Fact]
    public void PushImageToSerial_hands_a_jpeg_without_a_jfif_header_to_the_wireless_screen_unchanged()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xFE, 0x00, 0x04, 0x41, 0x42, 0xFF, 0xD9 };

        Assert.True(hub.PushImageToSerial("SER1", jpeg));

        var transport = Assert.Single(factory.Opened.Values);
        Assert.Equal(jpeg, Assert.Single(transport.PushedImages));
    }

    [Fact]
    public void PushImageToSerial_reuses_the_cached_transport()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });

        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 1 }));
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 2 }));

        Assert.Equal(1, factory.OpenCallCount);
        var transport = Assert.Single(factory.Opened.Values);
        Assert.Equal(2, transport.PushedImages.Count);
    }

    [Fact]
    public void PushImageToSerial_reopens_after_the_transport_goes_stale()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });

        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 1 }));
        factory.Opened["SER1"].SimulateDisconnect();
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 2 }));

        Assert.Equal(2, factory.OpenCallCount);
    }

    [Fact]
    public void A_failed_write_drops_the_handle_and_the_screen_reopens_after_the_backoff()
    {
        long now = 0;
        var discovery = new FakeDiscovery();
        var factory = new FakeTransportFactory();
        var hub = new Slv3LcdHub(discovery, factory.Create, nowMs: () => now);
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 1 }));
        var unplugged = factory.Opened["SER1"];
        unplugged.FailWrites = true;

        Assert.False(hub.PushImageToSerial("SER1", new byte[] { 2 }));
        Assert.True(unplugged.Disposed);

        // Inside the backoff the screen is not reopened.
        Assert.False(hub.PushImageToSerial("SER1", new byte[] { 3 }));
        Assert.Equal(1, factory.OpenCallCount);

        now += 1000;
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 4 }));
        Assert.Equal(2, factory.OpenCallCount);
        Assert.Equal(new byte[] { 4 }, Assert.Single(factory.Opened["SER1"].PushedImages));
    }

    [Fact]
    public void Forget_closes_a_departed_screen_so_its_return_opens_fresh()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 1 }));
        var first = factory.Opened["SER1"];

        hub.Forget("SER1", "left the bus");

        Assert.True(first.Disposed);
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 2 }));
        Assert.Equal(2, factory.OpenCallCount);
    }

    [Fact]
    public void A_reopened_screen_gets_its_last_brightness_and_rotation_back()
    {
        long now = 0;
        var discovery = new FakeDiscovery();
        var factory = new FakeTransportFactory();
        var hub = new Slv3LcdHub(discovery, factory.Create, nowMs: () => now);
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        Assert.True(hub.SetBrightness("SER1", 40));
        Assert.True(hub.SetRotation("SER1", 2));
        factory.Opened["SER1"].FailWrites = true;
        Assert.False(hub.PushImageToSerial("SER1", new byte[] { 1 }));

        now += 1000;
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 2 }));

        var reopened = factory.Opened["SER1"];
        Assert.Equal(new byte[] { 40 }, reopened.Brightness);
        Assert.Equal(new byte[] { 2 }, reopened.Rotation);
    }

    [Fact]
    public void A_screen_missing_from_discovery_is_not_looked_up_again_inside_the_backoff()
    {
        long now = 0;
        var discovery = new FakeDiscovery();
        var hub = new Slv3LcdHub(discovery, new FakeTransportFactory().Create, nowMs: () => now);

        Assert.False(hub.PushImageToSerial("SER1", new byte[] { 1 }));
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        Assert.False(hub.PushImageToSerial("SER1", new byte[] { 2 }));

        now += 1000;
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 3 }));
    }

    [Fact]
    public void OpenSerials_lists_every_open_connection_and_Forget_closes_it()
    {
        var (hub, _, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 1 }));

        Assert.Equal(new[] { "SER1" }, hub.OpenSerials());
        hub.Forget("SER1", "Nexus Control off");
        Assert.Empty(hub.OpenSerials());
    }

    [Fact]
    public void An_oversized_frame_does_not_drop_a_working_handle()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 1 }));
        factory.Opened["SER1"].ThrowOnPush = true;

        Assert.False(hub.PushImageToSerial("SER1", new byte[] { 2 }));

        Assert.False(factory.Opened["SER1"].Disposed);
        Assert.Equal(1, factory.OpenCallCount);
    }

    [Fact]
    public void PushImageToSerial_fails_for_unknown_serial()
    {
        var (hub, _, _) = CreateHub();
        Assert.False(hub.PushImageToSerial("NOPE", new byte[] { 1 }));
    }

    [Fact]
    public void SetBrightness_and_SetRotation_reach_the_transport()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });

        Assert.True(hub.SetBrightness("SER1", 80));
        Assert.True(hub.SetRotation("SER1", 2));

        var transport = factory.Opened["SER1"];
        Assert.Equal(80, Assert.Single(transport.Brightness));
        Assert.Equal(2, Assert.Single(transport.Rotation));
    }

    [Fact]
    public void TryGetPosition_returns_the_transport_groupIndex()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        factory.PendingPosition = 2;

        var ok = hub.TryGetPosition("SER1", out var position);

        Assert.True(ok);
        Assert.Equal(2, position);
    }

    [Fact]
    public void TryGetPosition_fails_when_the_transport_does_not_ack()
    {
        var (hub, _, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });

        var ok = hub.TryGetPosition("SER1", out var position);

        Assert.False(ok);
        Assert.Equal(-1, position);
    }

    [Fact]
    public void PushImageToSerial_returns_false_instead_of_throwing_on_an_oversized_frame()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 1 }));
        factory.Opened["SER1"].ThrowOnPush = true;

        var ok = hub.PushImageToSerial("SER1", new byte[] { 2 });

        Assert.False(ok);
    }

    [Fact]
    public void Dispose_closes_every_open_transport()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 1 }));

        hub.Dispose();

        Assert.True(factory.Opened["SER1"].Disposed);
    }

    // ---- Sleep when locked -------------------------------------------------

    private static (Slv3LcdHub Hub, FakeTransportFactory Factory, LcdLockState Lock) LockHub()
    {
        var discovery = new FakeDiscovery();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        var factory = new FakeTransportFactory();
        var state = new LcdLockState();
        return (new Slv3LcdHub(discovery, factory.Create, state), factory, state);
    }

    private sealed class LoopState
    {
        public int Applied = -1;
        public bool Slept;
    }

    private static void Pass(Slv3LcdHub hub, LianLiWirelessScreenSettings settings, LoopState loop) =>
        hub.SyncBrightness("SER1", settings, ref loop.Applied, ref loop.Slept);

    [Fact]
    public void Lock_sets_brightness_zero_and_unlock_restores_the_stored_value()
    {
        var (hub, factory, state) = LockHub();
        var settings = new LianLiWirelessScreenSettings { Brightness = 65 };
        var loop = new LoopState();
        Pass(hub, settings, loop);

        state.Set(true);
        Pass(hub, settings, loop);
        state.Set(false);
        Pass(hub, settings, loop);

        Assert.Equal(new byte[] { 65, 0, 65 }, factory.Opened["SER1"].Brightness);
        Assert.Equal(65, settings.Brightness);
    }

    [Fact]
    public void A_screen_that_attaches_while_locked_gets_the_dark_value_first()
    {
        var (hub, factory, state) = LockHub();
        state.Set(true);

        Pass(hub, new LianLiWirelessScreenSettings { Brightness = 65 }, new LoopState());

        Assert.Equal(new byte[] { 0 }, factory.Opened["SER1"].Brightness);
    }

    [Fact]
    public void A_reattach_while_locked_applies_dark_not_the_stored_value()
    {
        var (hub, factory, state) = LockHub();
        var settings = new LianLiWirelessScreenSettings { Brightness = 65 };
        Pass(hub, settings, new LoopState());
        state.Set(true);

        // A new screen loop starts with the sentinel, as after a detach and attach.
        Pass(hub, settings, new LoopState());

        Assert.Equal(0, factory.Opened["SER1"].Brightness[^1]);
        Assert.DoesNotContain(factory.Opened["SER1"].Brightness.Skip(1), b => b == 65);
    }

    [Fact]
    public void The_setting_off_keeps_the_screen_lit_and_unlock_wakes_a_screen_whose_setting_was_turned_off_mid_lock()
    {
        var (hub, factory, state) = LockHub();
        var off = new LianLiWirelessScreenSettings { Brightness = 65, SleepWhenLocked = false };
        var offLoop = new LoopState();
        state.Set(true);
        Pass(hub, off, offLoop);
        Assert.Equal(new byte[] { 65 }, factory.Opened["SER1"].Brightness);
        state.Set(false);

        var (hub2, factory2, state2) = LockHub();
        var settings = new LianLiWirelessScreenSettings { Brightness = 65 };
        var loop = new LoopState();
        state2.Set(true);
        Pass(hub2, settings, loop);
        settings.SleepWhenLocked = false;
        Pass(hub2, settings, loop);
        state2.Set(false);
        Pass(hub2, settings, loop);

        Assert.Equal(new byte[] { 0, 65 }, factory2.Opened["SER1"].Brightness);
    }

    private static (Slv3LcdHub Hub, FakeTransportFactory Factory, FakeDiscovery Discovery) CreateHub()
    {
        var discovery = new FakeDiscovery();
        var factory = new FakeTransportFactory();
        var hub = new Slv3LcdHub(discovery, factory.Create);
        return (hub, factory, discovery);
    }

    private sealed class FakeDiscovery : ISlv3LcdDiscovery
    {
        public List<Slv3LcdPortInfo> Ports { get; } = new();
        public IReadOnlyList<Slv3LcdPortInfo> Discover() => Ports;
    }

    private sealed class FakeTransportFactory
    {
        public Dictionary<string, FakeTransport> Opened { get; } = new(StringComparer.Ordinal);
        public int OpenCallCount { get; private set; }
        public byte? PendingPosition { get; set; }

        public ISlv3LcdTransport Create(Slv3LcdPortInfo port)
        {
            OpenCallCount++;
            var transport = new FakeTransport(port.PortName) { PositionToReturn = PendingPosition };
            Opened[port.Serial] = transport;
            return transport;
        }
    }

    private sealed class FakeTransport : ISlv3LcdTransport
    {
        private bool _open = true;

        public FakeTransport(string portName)
        {
            PortName = portName;
        }

        public List<byte[]> PushedImages { get; } = new();
        public List<byte> Brightness { get; } = new();
        public List<byte> Rotation { get; } = new();
        public bool Disposed { get; private set; }
        public byte? PositionToReturn { get; set; }
        public bool ThrowOnPush { get; set; }

        public bool IsOpen => _open;
        public string PortName { get; }

        public void SimulateDisconnect() => _open = false;

        /// <summary>Every write fails while the handle still reads as open: an unplugged screen's handle.</summary>
        public bool FailWrites { get; set; }

        public bool PushImage(byte[] jpeg)
        {
            if (ThrowOnPush)
            {
                throw new ArgumentException("jpeg exceeds the payload capacity");
            }
            if (FailWrites)
            {
                return false;
            }
            PushedImages.Add(jpeg);
            return true;
        }

        public bool SetBrightness(byte value)
        {
            Brightness.Add(value);
            return true;
        }

        public bool SetRotation(byte value)
        {
            Rotation.Add(value);
            return true;
        }

        public bool TryGetPosition(out byte groupIndex)
        {
            groupIndex = PositionToReturn ?? 0;
            return PositionToReturn.HasValue;
        }

        public void Dispose() => Disposed = true;
    }
}
