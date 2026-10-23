using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Corsair.Galleon;
using Nexus.Service.Peripherals.Hyte.Np50;
using Xunit;

namespace Nexus.Service.Tests.Corsair;

/// <summary>Dial hub sequencing on a scripted LampArray; covers the fake HID layer only, not a real device.</summary>
public class GalleonSdDialsHubTests
{
    private long _clock = 1_000_000_000;

    private GalleonSdDialsHub NewHub(FakeLampArrayHid hid) => new(hid, () => _clock);

    private void Advance(double seconds) => _clock += (long)(seconds * System.Diagnostics.Stopwatch.Frequency);

    private static RgbColor[] Solid(byte v, int n = 4) => Enumerable.Repeat(new RgbColor(v, v, v), n).ToArray();

    private static IEnumerable<byte[]> Sent(FakeLampArrayDevice d, int report) => d.Sets.Where(s => s[0] == report);

    [Fact]
    public void Reconcile_opens_only_the_lamp_array_collection_and_reads_every_lamp()
    {
        var hid = new FakeLampArrayHid();
        using var hub = NewHub(hid);

        Assert.True(hub.Reconcile(true));

        Assert.Equal(1, hid.Opens);
        var unit = hub.Attached!;
        Assert.Equal(4, unit.LampCount);
        Assert.Equal(new[] { 0, 1, 2, 3 }, unit.Lamps.Select(l => l.LampId));
        Assert.Equal(new[] { 0, 10, 20, 30 }, unit.Lamps.Select(l => l.XUm));
        Assert.Equal(4, Sent(hid.Device, FakeLampArrayDevice.Req).Count());
        Assert.Empty(Sent(hid.Device, FakeLampArrayDevice.Multi));
        Assert.Empty(Sent(hid.Device, FakeLampArrayDevice.Ctrl));
    }

    [Fact]
    public void Nothing_opens_without_a_lamp_array_collection()
    {
        var hid = new FakeLampArrayHid { UsagePage = 0xFF42 };
        using var hub = NewHub(hid);
        Assert.False(hub.Reconcile(true));
        Assert.Equal(0, hid.Opens);
    }

    [Fact]
    public void Unreadable_caps_fail_closed_and_close_the_handle()
    {
        var hid = new FakeLampArrayHid();
        hid.Device.CapsOverride = new List<Nexus.Service.Peripherals.Hid.HidValueCap>();
        using var hub = NewHub(hid);
        Assert.False(hub.Reconcile(true));
        Assert.True(hid.Device.Disposed);
        Assert.Null(hub.Attached);
    }

    [Fact]
    public void Unreadable_lamp_attributes_fail_closed()
    {
        var hid = new FakeLampArrayHid();
        hid.Device.FailGets = true;
        using var hub = NewHub(hid);
        Assert.False(hub.Reconcile(true));
        Assert.True(hid.Device.Disposed);
    }

    [Fact]
    public void An_out_of_range_lamp_count_fails_closed()
    {
        var hid = new FakeLampArrayHid();
        hid.Device.LampCount = 0;
        using var hub = NewHub(hid);
        Assert.False(hub.Reconcile(true));
    }

    [Fact]
    public void First_frame_turns_autonomous_mode_off_then_sends_a_uniform_frame_as_one_range_update()
    {
        var hid = new FakeLampArrayHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.Sets.Clear();

        Assert.True(hub.WriteFrame(Solid(40)));

        Assert.Equal(new byte[] { FakeLampArrayDevice.Ctrl, 0 }, hid.Device.Sets[0]);
        var range = Assert.Single(Sent(hid.Device, FakeLampArrayDevice.Range));
        Assert.Equal(new byte[] { 5, 1, 0, 0, 3, 0, 40, 40, 40, 255 }, range);
        Assert.Empty(Sent(hid.Device, FakeLampArrayDevice.Multi));
    }

    [Fact]
    public void A_mixed_frame_is_chunked_into_multi_updates_with_only_the_last_one_complete()
    {
        var hid = new FakeLampArrayHid();
        hid.Device.LampCount = 10;
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.Sets.Clear();
        var frame = Enumerable.Range(0, 10).Select(i => new RgbColor((byte)i, 0, 0)).ToArray();

        hub.WriteFrame(frame);

        var multi = Sent(hid.Device, FakeLampArrayDevice.Multi).ToList();
        Assert.Equal(2, multi.Count);
        Assert.Equal((8, 0), (multi[0][1], multi[0][2]));
        Assert.Equal((2, 1), (multi[1][1], multi[1][2]));
        Assert.Equal(8, multi[1][3]);
    }

    [Fact]
    public void Unchanged_frames_and_frames_inside_the_gap_are_not_resent()
    {
        var hid = new FakeLampArrayHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hub.WriteFrame(Solid(10));
        var count = hid.Device.Sets.Count;

        Advance(0.01);
        hub.WriteFrame(Solid(20));
        Assert.Equal(count, hid.Device.Sets.Count);

        Advance(0.1);
        hub.WriteFrame(Solid(20));
        Assert.Equal(count + 1, hid.Device.Sets.Count);

        Advance(1);
        hub.WriteFrame(Solid(20));
        Assert.Equal(count + 1, hid.Device.Sets.Count);

        Advance(11);
        hub.WriteFrame(Solid(20));
        Assert.Equal(count + 2, hid.Device.Sets.Count);
    }

    [Fact]
    public void Release_restores_autonomous_mode_once_and_only_after_taking_the_dials()
    {
        var hid = new FakeLampArrayHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hub.ReleaseToHardware();
        Assert.Empty(Sent(hid.Device, FakeLampArrayDevice.Ctrl));

        hub.WriteFrame(Solid(5));
        hub.ReleaseToHardware();
        hub.ReleaseToHardware();

        var ctrl = Sent(hid.Device, FakeLampArrayDevice.Ctrl).ToList();
        Assert.Equal(new byte[] { 6, 0 }, ctrl[0]);
        Assert.Equal(new byte[] { 6, 1 }, ctrl[1]);
        Assert.Equal(2, ctrl.Count);
    }

    [Fact]
    public void Gate_off_hands_the_dials_back_before_closing()
    {
        var hid = new FakeLampArrayHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hub.WriteFrame(Solid(5));

        Assert.True(hub.Reconcile(false));

        Assert.Equal(new byte[] { 6, 1 }, hid.Device.Sets[^1]);
        Assert.True(hid.Device.Disposed);
        Assert.False(hub.IsConnected);
    }

    [Fact]
    public void Five_consecutive_write_failures_drop_the_interface()
    {
        var hid = new FakeLampArrayHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.FailSets = true;
        for (var i = 0; i < 5; i++)
        {
            Advance(1);
            Assert.False(hub.WriteFrame(Solid((byte)(i + 1))));
        }
        Assert.False(hub.IsConnected);
        Assert.True(hid.Device.Disposed);
    }

    [Fact]
    public void Per_usage_count_one_caps_still_connect()
    {
        var hid = new FakeLampArrayHid();
        var multi = new List<Nexus.Service.Peripherals.Hid.HidValueCap>
        {
            FakeLampArrayDevice.Cap(4, 0x50, 0x03, 8, 1), FakeLampArrayDevice.Cap(4, 0x50, 0x55, 8, 1),
        };
        for (var i = 0; i < 8; i++) multi.Add(FakeLampArrayDevice.Cap(4, 0x50, 0x21, 16, 1));
        foreach (var u in new[] { 0x51, 0x52, 0x53, 0x54 })
        {
            for (var i = 0; i < 8; i++) multi.Add(FakeLampArrayDevice.Cap(4, 0x50, u, 8, 1));
        }
        hid.Device.CapsOverride = hid.Device.ReadFeatureValueCaps()!.Where(c => c.ReportId != 4).Concat(multi).ToList();
        using var hub = NewHub(hid);
        Assert.True(hub.Reconcile(true));
    }
}
