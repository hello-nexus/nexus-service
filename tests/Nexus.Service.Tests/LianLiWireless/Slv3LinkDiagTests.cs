using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LinkDiagTests
{
    private const string Key = "811085E566E1";
    private static readonly byte[] Record = new byte[Slv3Protocol.RecordLength];
    private static readonly byte[] Reply = new byte[64];

    private static List<string> Drain(Slv3LinkDiag d)
    {
        var lines = new List<string>();
        d.Drain(lines.Add);
        return lines;
    }

    private static void Poll(Slv3LinkDiag d, long t, bool present)
    {
        d.OnReply(t, Reply, 3, Slv3ReplyKind.Ok, present ? 1 : 0);
        if (present)
        {
            d.OnSeen(Key, t, Record, 4, 8);
        }
        d.EndPoll(t, gotList: true);
    }

    [Fact]
    public void Steady_state_writes_only_one_summary_line_per_chain_per_interval()
    {
        var d = new Slv3LinkDiag();
        for (long t = 0; t <= 95_000; t += 300)
        {
            Poll(d, t, present: true);
        }

        var lines = Drain(d);
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Contains(" SUM " + Key + " ", l));
    }

    [Fact]
    public void Summary_counts_polls_longest_gap_and_gap_thresholds()
    {
        var d = new Slv3LinkDiag();
        Poll(d, 0, true);
        Poll(d, 1_000, true);
        Poll(d, 2_000, false);
        Poll(d, 4_500, true);   // gap 3500: over 2 s
        Poll(d, 5_000, true);
        Poll(d, 5_500, false);
        Poll(d, 16_000, true);  // gap 11000: over 2, 5 and 10 s
        Poll(d, 30_001, true);  // gap 14001 and crosses the summary boundary

        var sum = Assert.Single(Drain(d), l => l.Contains(" SUM "));
        Assert.Contains("polls=6/8", sum);
        Assert.Contains("maxgap=14001ms", sum);
        Assert.Contains("g2=3", sum);
        Assert.Contains("g5=2", sum);
        Assert.Contains("g10=2", sum);
        Assert.Contains("slot=4 ch=8", sum);
    }

    [Fact]
    public void Open_gap_counts_toward_the_summary_maximum()
    {
        var d = new Slv3LinkDiag();
        Poll(d, 0, true);
        for (long t = 1_000; t <= 30_000; t += 1_000)
        {
            Poll(d, t, false);
        }

        var sum = Assert.Single(Drain(d), l => l.Contains(" SUM "));
        Assert.Contains("maxgap=30000ms", sum);
    }

    [Fact]
    public void Dump_fires_after_five_seconds_unseen_and_is_rate_limited_per_chain()
    {
        var d = new Slv3LinkDiag();
        Poll(d, 0, true);
        for (long t = 500; t <= 5_000; t += 500)
        {
            Poll(d, t, false);
        }
        Assert.DoesNotContain(Drain(d), l => l.Contains(" DUMP "));

        Poll(d, 5_500, false);
        var lines = Drain(d);
        var header = Assert.Single(lines, l => l.Contains(" DUMP "));
        Assert.Contains("reason=unseen5s", header);
        Assert.Contains(lines, l => l.Contains(" CH ") && l.Contains("absent"));
        Assert.Contains(lines, l => l.Contains(" RX ") && l.Contains(" ok "));
        Assert.Contains(lines, l => l.Contains(" CHRAW "));
    }

    [Fact]
    public void Dump_fires_when_polls_return_no_list_at_all()
    {
        var d = new Slv3LinkDiag();
        Poll(d, 0, true);
        for (long t = 500; t <= 6_000; t += 500)
        {
            d.EndPoll(t, gotList: false);
        }

        Assert.Single(Drain(d), l => l.Contains(" DUMP ") && l.Contains("reason=unseen5s"));
    }

    [Fact]
    public void Early_dump_repeats_only_after_the_rate_limit_window()
    {
        var d = new Slv3LinkDiag();
        Poll(d, 0, true);
        var dumps = 0;
        for (long t = 500; t <= 70_000; t += 500)
        {
            Poll(d, t, false);
            dumps += Drain(d).Count(l => l.Contains(" DUMP ") && l.Contains("reason=unseen5s"));
        }

        Assert.Equal(2, dumps); // first at 5.5 s, next 60 s later
    }

    [Fact]
    public void Drop_always_dumps_and_reappearance_dumps_once_with_uploads_and_raw_replies()
    {
        var d = new Slv3LinkDiag();
        Poll(d, 0, true);
        Poll(d, 5_500, false); // early dump uses the limiter
        d.OnUpload(Key, 5_000, Slv3UploadKind.Window, false, 480, 17, Slv3UploadReason.TxTimeout);
        Drain(d);

        d.OnDropped(Key, 30_000);
        var dropLines = Drain(d);
        Assert.Contains(dropLines, l => l.Contains(" DUMP ") && l.Contains("reason=drop"));
        Assert.Contains(dropLines, l => l.Contains(" UP ") && l.Contains("FAIL") && l.Contains("window=17") && l.Contains("reason=tx-timeout"));
        Assert.Contains(dropLines, l => l.Contains(" RXRAW "));

        Poll(d, 34_000, true);
        var back = Drain(d);
        Assert.Single(back, l => l.Contains(" DUMP ") && l.Contains("reason=reappeared"));
    }

    [Fact]
    public void Chain_ring_wraps_and_keeps_the_newest_entries()
    {
        var d = new Slv3LinkDiag();
        var total = Slv3LinkDiag.ChainRingSize * 2 + 5;
        for (var i = 0; i < total; i++)
        {
            Poll(d, i * 10L, true);
        }
        d.OnDropped(Key, total * 10L);

        var ch = Drain(d).Single(l => l.Contains(" CH t="));
        Assert.Contains("x" + Slv3LinkDiag.ChainRingSize, ch);
        Assert.Contains($"t={(total - Slv3LinkDiag.ChainRingSize) * 10L - total * 10L}..", ch);
    }

    [Fact]
    public void Reply_ring_wraps_and_upload_ring_keeps_the_newest_entries()
    {
        var d = new Slv3LinkDiag();
        Poll(d, 0, true);
        var total = Slv3LinkDiag.UploadRingSize + 7;
        for (var i = 0; i < total; i++)
        {
            d.OnUpload(Key, i, Slv3UploadKind.Frame, true, 1, -1, Slv3UploadReason.Ok);
        }
        for (var i = 0; i < Slv3LinkDiag.ReplyRingSize + 10; i++)
        {
            d.OnReply(1 + i, Reply, 2, Slv3ReplyKind.Ok, 1);
        }
        d.OnDropped(Key, 300);

        var lines = Drain(d);
        Assert.Equal(Slv3LinkDiag.UploadRingSize, lines.Count(l => l.Contains(" UP ")));
        Assert.Contains(lines, l => l.Contains(" RX ") && l.Contains($"x{Slv3LinkDiag.ReplyRingSize}"));
    }

    [Theory]
    [InlineData(@"USB\VID_05E3&PID_0610\5&2E9D8F&0&2", @"USB\VID_05E3&PID_0610\5&2E9D8F&0&2")]
    public void Generated_instance_ids_are_kept(string id, string expected) =>
        Assert.Equal(expected, Slv3LinkDiag.RedactInstanceId(id));

    [Fact]
    public void Serial_style_instance_ids_are_hashed()
    {
        var redacted = Slv3LinkDiag.RedactInstanceId(@"USB\VID_05E3&PID_0610\ABC123");
        Assert.DoesNotContain("ABC123", redacted);
        Assert.StartsWith(@"USB\VID_05E3&PID_0610\ser#", redacted);
    }

    [Fact]
    public void Usb_changes_log_once_and_resolve_the_parent_on_the_drain()
    {
        var d = new Slv3LinkDiag();
        d.StartWriter(System.IO.Path.GetTempPath() + "nexus-diag-unused-" + Guid.NewGuid().ToString("N"), p => @"USB\VID_1&PID_2\5&1&0&3");
        d.Dispose();
        var tx = new Slv3PortInfo { PortName = "tx-path", Role = Slv3DongleRole.Tx, Serial = "SECRET" };
        var rx = new Slv3PortInfo { PortName = "rx-path", Role = Slv3DongleRole.Rx };
        d.NoteUsb(tx, rx);
        d.NoteUsb(tx, rx);

        var lines = Drain(d);
        var usb = Assert.Single(lines, l => l.Contains(" EVT usb tx="));
        Assert.DoesNotContain("SECRET", usb);
        Assert.Contains(lines, l => l.EndsWith(@"EVT usb-parent USB\VID_1&PID_2\5&1&0&3"));
    }
}
