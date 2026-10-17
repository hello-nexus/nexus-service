using System;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Xunit;

namespace Nexus.Service.Tests.AsiahorseUmbra;

public class UmbraLightingFrameWriterTests
{
    private const string Port10 = "asiahorse-umbra:port:10";

    private readonly InMemoryConfigStore _store = new();
    private readonly UmbraHub _hub = new();
    private readonly UmbraDeviceFake _fake = new();
    private readonly LightingEngine _engine = new();
    private readonly DeviceFrame _frame = new(0, Port10, 6);

    private AsiahorseUmbraLightingFrameWriter Writer()
    {
        _fake.Ports[9][0] = 6;
        _hub.Attach(_fake);
        Assert.True(_hub.Connect());
        _engine.UpdateDevices(new[] { _frame });
        _fake.Writes.Clear();
        return new AsiahorseUmbraLightingFrameWriter(_engine, _hub, _store);
    }

    private static bool IsStream(byte[] w) => w[0] == 0x88;

    private static void Set(DeviceFrame frame, int led, byte r, byte g, byte b)
    {
        var paint = frame.PaintBuffer;
        paint[led * 3] = r;
        paint[led * 3 + 1] = g;
        paint[led * 3 + 2] = b;
        frame.Publish();
    }

    [Fact]
    public void Nexus_source_takes_authority_then_streams_the_port_leds()
    {
        var writer = Writer();
        for (var i = 0; i < 6; i++)
        {
            Set(_frame, i, 0xFF, 0x00, 0xB5);
        }

        writer.Tick();

        Assert.Equal(2, _fake.Writes.Count);
        Assert.Equal(UmbraProtocol.EncodeAuthority(true), _fake.Writes[0]);
        Assert.True(IsStream(_fake.Writes[1]));
        Assert.Equal(new byte[] { 0xFF, 0x00, 0xB5, 0xFF, 0x00, 0xB5 }, _fake.Writes[1][3..9]);
    }

    [Fact]
    public void Nothing_streams_until_the_engine_has_a_frame_for_every_port()
    {
        var writer = Writer();
        _engine.UpdateDevices(System.Array.Empty<DeviceFrame>());

        writer.Tick();

        Assert.DoesNotContain(_fake.Writes, IsStream);

        _engine.UpdateDevices(new[] { _frame });
        for (var i = 0; i < 6; i++)
        {
            Set(_frame, i, 0x10, 0x20, 0x30);
        }
        writer.Tick();

        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, _fake.Writes.Single(IsStream)[3..6]);
    }

    [Fact]
    public void An_unchanged_frame_is_not_resent_within_the_keepalive_window()
    {
        var writer = Writer();
        writer.Tick();
        _fake.Writes.Clear();

        writer.Tick();

        Assert.Empty(_fake.Writes);
    }

    [Fact]
    public void Streams_are_ordered_port_one_to_ten_by_reported_counts()
    {
        _fake.Ports[0][0] = 2;
        var first = new DeviceFrame(0, "asiahorse-umbra:port:1", 2);
        var writer = Writer();
        _engine.UpdateDevices(new[] { first, _frame });
        Set(first, 0, 1, 0, 0);
        Set(_frame, 0, 9, 0, 0);
        _hub.RefreshPorts();

        writer.Tick();

        var stream = _fake.Writes.Single(IsStream);
        Assert.Equal(1, stream[3]);
        Assert.Equal(9, stream[3 + 2 * 3]);
    }

    [Fact]
    public void An_uncontrolled_zone_streams_black()
    {
        var writer = Writer();
        Set(_frame, 0, 0xFF, 0, 0);
        _store.Update(s => s.Devices.UncontrolledLightingDevices = new() { Port10 });

        writer.Tick();

        Assert.Equal(new byte[] { 0, 0, 0 }, _fake.Writes.Single(IsStream)[3..6]);
    }

    [Fact]
    public void A_disabled_zone_streams_black()
    {
        var writer = Writer();
        Set(_frame, 0, 0xFF, 0, 0);
        _store.Update(s => s.Devices.DisabledLightingDevices = new() { Port10 });

        writer.Tick();

        Assert.Equal(new byte[] { 0, 0, 0 }, _fake.Writes.Single(IsStream)[3..6]);
    }

    [Fact]
    public void Firmware_source_commits_the_effect_and_never_streams()
    {
        _store.Update(s => s.Devices.AsiahorseUmbra.Source = "firmware");
        var writer = Writer();
        Set(_frame, 0, 0xFF, 0, 0);

        writer.Tick();

        Assert.Equal(new byte[] { 0xFD, 0x03 }, _fake.Writes.Select(w => w[4]).ToArray());
        Assert.DoesNotContain(_fake.Writes, IsStream);
    }

    [Fact]
    public void ArgbInput_source_only_hands_over_authority()
    {
        _store.Update(s => s.Devices.AsiahorseUmbra.Source = "argbInput");
        var writer = Writer();

        writer.Tick();

        Assert.Single(_fake.Writes);
        Assert.Equal(UmbraProtocol.EncodeAuthority(false), _fake.Writes[0]);
    }

    [Fact]
    public void A_reconnect_reasserts_the_persisted_source()
    {
        _store.Update(s => s.Devices.AsiahorseUmbra.Source = "firmware");
        var writer = Writer();
        writer.Tick();

        _hub.Detach();
        var replacement = new UmbraDeviceFake();
        _hub.Attach(replacement);
        Assert.True(_hub.Connect());
        replacement.Writes.Clear();
        writer.Tick();

        Assert.Equal(new byte[] { 0xFD, 0x03 }, replacement.Writes.Select(w => w[4]).ToArray());
    }

    [Fact]
    public void Nothing_is_written_while_disconnected()
    {
        var writer = Writer();
        _hub.Detach();
        _fake.Writes.Clear();

        writer.Tick();

        Assert.Empty(_fake.Writes);
    }

    [Fact]
    public void A_failed_commit_is_retried_on_the_next_tick()
    {
        var writer = Writer();
        _fake.FailWrites = true;
        writer.Tick();
        _fake.FailWrites = false;
        _fake.Writes.Clear();

        writer.Tick();

        Assert.Equal(UmbraProtocol.EncodeAuthority(true), _fake.Writes[0]);
    }

    [Fact]
    public void A_port_without_an_engine_frame_streams_black_without_freezing_the_others()
    {
        _fake.Ports[0][0] = 2;
        var writer = Writer();
        Set(_frame, 0, 9, 0, 0);
        _hub.RefreshPorts();

        writer.Tick();

        var stream = _fake.Writes.Single(IsStream);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0 }, stream[3..9]);
        Assert.Equal(9, stream[3 + 2 * 3]);
    }

    [Fact]
    public void Nothing_streams_while_the_engine_has_no_port_frame()
    {
        var writer = Writer();
        _engine.UpdateDevices(Array.Empty<DeviceFrame>());
        _fake.Writes.Clear();

        writer.Tick();

        Assert.DoesNotContain(_fake.Writes, IsStream);
    }

    [Fact]
    public void A_one_hertz_cap_cannot_hold_a_changed_frame_past_the_keepalive_window()
    {
        long now = 1000;
        var cap = new LightingFrameCap(_store, () => now);
        _store.Update(s => s.Devices.LightingMaxRates[AsiahorseUmbraLightingDeviceProvider.DeviceId] = 1);
        _fake.Ports[9][0] = 6;
        _hub.Attach(_fake);
        Assert.True(_hub.Connect());
        _engine.UpdateDevices(new[] { _frame });
        var writer = new AsiahorseUmbraLightingFrameWriter(_engine, _hub, _store, cap: cap);
        for (var i = 0; i < 6; i++)
        {
            Set(_frame, i, 0x10, 0x20, 0x30);
        }
        writer.Tick();
        _fake.Writes.Clear();

        // The cap clock never advances, so the gate keeps refusing; a changed frame is held at first.
        for (var i = 0; i < 6; i++)
        {
            Set(_frame, i, 0x40, 0x50, 0x60);
        }
        writer.Tick();
        Assert.DoesNotContain(_fake.Writes, IsStream);

        // Real time passes the keepalive window: the frame goes out although the gate still refuses.
        System.Threading.Thread.Sleep(1100);
        writer.Tick();
        Assert.Equal(new byte[] { 0x40, 0x50, 0x60 }, _fake.Writes.Single(IsStream)[3..6]);
    }
}
