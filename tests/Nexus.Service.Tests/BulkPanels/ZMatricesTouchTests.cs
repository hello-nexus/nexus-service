using System.Collections.Generic;
using Nexus.Service.Peripherals.BulkPanels;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class ZMatricesTouchTests
{
    internal static byte[] V1(byte track, TouchPhase phase, ushort x, ushort y) => new byte[]
    {
        0xAE, track, (byte)phase, 0x02, 0x1C, 0x04, 0x60, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y,
    };

    private static byte[] V2(byte track, TouchPhase phase, ushort x, ushort y, bool corrupt = false)
    {
        var p = new byte[] { 0xAE, 0x5A, 0x02, (byte)phase, track, 0, 0x02, 0x1C, 0x04, 0x60, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, 0, 0 };
        byte xor = 0;
        for (int i = 0; i < 15; i++)
        {
            xor ^= p[i];
        }
        p[15] = corrupt ? (byte)(xor ^ 1) : xor;
        return p;
    }

    [Fact]
    public void Parses_both_layouts_between_status_tokens()
    {
        var parser = new ZMatricesTouchParser();
        var reports = new List<ZMatricesTouchReport>();

        parser.Feed("IDL"u8, reports);
        parser.Feed(V1(0, TouchPhase.Down, 100, 200), reports);
        parser.Feed(V2(1, TouchPhase.Move, 300, 400), reports);

        Assert.Equal(new[]
        {
            new ZMatricesTouchReport(0, TouchPhase.Down, 100, 200),
            new ZMatricesTouchReport(1, TouchPhase.Move, 300, 400),
        }, reports);
    }

    [Fact]
    public void A_packet_split_across_reads_is_reassembled()
    {
        var parser = new ZMatricesTouchParser();
        var reports = new List<ZMatricesTouchReport>();
        var packet = V2(0, TouchPhase.Up, 5, 6);

        parser.Feed(packet.AsSpan(0, 7), reports);
        Assert.Empty(reports);
        parser.Feed(packet.AsSpan(7), reports);

        Assert.Equal(new ZMatricesTouchReport(0, TouchPhase.Up, 5, 6), Assert.Single(reports));
    }

    [Fact]
    public void A_v2_packet_with_a_bad_checksum_is_dropped()
    {
        var parser = new ZMatricesTouchParser();
        var reports = new List<ZMatricesTouchReport>();

        parser.Feed(V2(0, TouchPhase.Down, 5, 6, corrupt: true), reports);

        Assert.Empty(reports);
    }

    [Fact]
    public void Portrait_glass_coordinates_turn_onto_the_landscape_frame()
    {
        Assert.True(ZMatricesTouchParser.TryMapToFrame(new ZMatricesTouchReport(0, TouchPhase.Down, 0, 0), 1120, 540, out int x, out int y));
        Assert.Equal((0, 539), (x, y));
        Assert.True(ZMatricesTouchParser.TryMapToFrame(new ZMatricesTouchReport(0, TouchPhase.Down, 539, 1119), 1120, 540, out x, out y));
        Assert.Equal((1119, 0), (x, y));
        Assert.False(ZMatricesTouchParser.TryMapToFrame(new ZMatricesTouchReport(0, TouchPhase.Down, 540, 10), 1120, 540, out _, out _));
    }

    [Fact]
    public void Contacts_follow_down_move_up()
    {
        var tracker = new TouchContactTracker();
        var output = new List<TouchContactTracker.Injection>();

        tracker.Apply(0, TouchPhase.Move, 10, 10, mapped: true, output);
        tracker.Apply(0, TouchPhase.Move, 10, 10, mapped: true, output);
        tracker.Apply(0, TouchPhase.Move, 12, 10, mapped: true, output);
        tracker.Apply(0, TouchPhase.Up, 0, 0, mapped: false, output);
        tracker.Apply(0, TouchPhase.Up, 0, 0, mapped: false, output);

        Assert.Equal(new[]
        {
            new TouchContactTracker.Injection(1, TouchPhase.Down, 10, 10),
            new TouchContactTracker.Injection(1, TouchPhase.Move, 12, 10),
            new TouchContactTracker.Injection(1, TouchPhase.Up, 12, 10),
        }, output);
    }

    [Fact]
    public void A_second_down_releases_the_stale_contact_first()
    {
        var tracker = new TouchContactTracker();
        var output = new List<TouchContactTracker.Injection>();

        tracker.Apply(2, TouchPhase.Down, 1, 1, mapped: true, output);
        tracker.Apply(2, TouchPhase.Down, 5, 5, mapped: true, output);
        tracker.ReleaseAll(output);

        Assert.Equal(new[]
        {
            new TouchContactTracker.Injection(3, TouchPhase.Down, 1, 1),
            new TouchContactTracker.Injection(3, TouchPhase.Up, 1, 1),
            new TouchContactTracker.Injection(3, TouchPhase.Down, 5, 5),
            new TouchContactTracker.Injection(3, TouchPhase.Up, 5, 5),
        }, output);
    }
}
