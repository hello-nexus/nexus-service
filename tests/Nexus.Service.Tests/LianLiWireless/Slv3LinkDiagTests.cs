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

        Assert.Single(Drain(d), l => l.Contains(" DUMP ") && l.Contains("reason=stall"));
    }

    [Fact]
    public void Chain_episode_dumps_once_however_long_it_lasts_then_writes_one_end_line()
    {
        var d = new Slv3LinkDiag();
        Poll(d, 0, true);
        var lines = new List<string>();
        for (long t = 500; t <= 300_000; t += 500)
        {
            Poll(d, t, false);
            lines.AddRange(Drain(d).Where(l => !l.Contains(" SUM ")));
        }
        Assert.Single(lines, l => l.Contains(" DUMP "));

        Poll(d, 301_000, true);
        var end = Assert.Single(Drain(d), l => l.Contains(" EPISODE "));
        Assert.Contains("ended=recovered", end);
        Assert.Contains("duration=", end);
    }

    [Fact]
    public void Whole_dongle_stall_is_one_dump_for_all_chains_and_one_end_line()
    {
        var d = new Slv3LinkDiag();
        const string other = "AABBCCDDEEFF";
        d.OnReply(0, Reply, 3, Slv3ReplyKind.Ok, 2);
        d.OnSeen(Key, 0, Record, 4, 8);
        d.OnSeen(other, 0, Record, 5, 9);
        d.EndPoll(0, gotList: true);

        var lines = new List<string>();
        var blindPolls = 0;
        for (long t = 1_000; t <= 40 * 60_000; t += 1_000)
        {
            d.OnReply(t, Reply, 2, Slv3ReplyKind.NoData, 0);
            d.EndPoll(t, gotList: true);
            blindPolls++;
            lines.AddRange(Drain(d).Where(l => !l.Contains(" SUM ")));
        }
        Assert.Single(lines, l => l.Contains(" DUMP "));
        Assert.Contains(lines, l => l.Contains("reason=stall") && l.Contains("chains=2"));
        Assert.Contains(lines, l => l.Contains("chain=" + Key) && l.Contains("unseen="));
        Assert.Contains(lines, l => l.Contains("chain=" + other) && l.Contains("unseen="));

        d.OnReply(2_500_000, Reply, 3, Slv3ReplyKind.Ok, 2);
        d.OnSeen(Key, 2_500_000, Record, 4, 8);
        d.OnSeen(other, 2_500_000, Record, 5, 9);
        d.EndPoll(2_500_000, gotList: true);
        var after = Drain(d).Where(l => !l.Contains(" SUM ")).ToList();
        var end = Assert.Single(after);
        Assert.Contains(" STALL ", end);
        Assert.Contains("ended=recovered", end);
        Assert.Contains("kind=nodata", end);
        Assert.Contains("polls=" + blindPolls, end);
    }

    [Fact]
    public void A_second_stall_after_recovery_gets_its_own_dump()
    {
        var d = new Slv3LinkDiag();
        Poll(d, 0, true);
        var dumps = 0;
        for (var round = 0; round < 2; round++)
        {
            var start = 100_000L * (round + 1);
            for (long t = start; t <= start + 20_000; t += 1_000)
            {
                d.OnReply(t, Reply, 2, Slv3ReplyKind.Timeout, 0);
                d.EndPoll(t, gotList: true);
                dumps += Drain(d).Count(l => l.Contains(" DUMP "));
            }
            Poll(d, start + 30_000, true);
            Drain(d);
        }

        Assert.Equal(2, dumps);
    }

    [Fact]
    public void Drop_inside_a_chain_episode_still_dumps_in_full()
    {
        var d = new Slv3LinkDiag();
        Poll(d, 0, true);
        Poll(d, 6_000, false);
        Drain(d);

        d.OnDropped(Key, 30_000);
        var lines = Drain(d);
        Assert.Single(lines, l => l.Contains(" DUMP ") && l.Contains("reason=drop"));
        Assert.Contains(lines, l => l.Contains(" CHRAW "));
        Assert.Contains(lines, l => l.Contains(" EPISODE ") && l.Contains("ended=drop"));
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

    [Fact]
    public void Rotation_keeps_the_configured_number_of_older_files_and_drops_the_rest()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-diag-rot-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            for (var i = 1; i <= Slv3LinkDiag.RotatedFiles + 3; i++)
            {
                Slv3LinkDiag.AppendRotating(dir, "gen" + i + new string('x', 20) + "\n", 30);
            }

            var files = System.IO.Directory.GetFiles(dir).Select(System.IO.Path.GetFileName).OrderBy(n => n).ToArray();
            Assert.Equal(Slv3LinkDiag.RotatedFiles + 1, files.Length);
            Assert.All(files, n => Assert.True(Slv3LinkDiag.IsLogFileName(n!)));
            Assert.Contains("gen6", System.IO.File.ReadAllText(System.IO.Path.Combine(dir, Slv3LinkDiag.FileName)));
            Assert.Contains("gen5", System.IO.File.ReadAllText(System.IO.Path.Combine(dir, Slv3LinkDiag.RotatedFileName(1))));
            Assert.Contains("gen3", System.IO.File.ReadAllText(System.IO.Path.Combine(dir, Slv3LinkDiag.RotatedFileName(Slv3LinkDiag.RotatedFiles))));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Retained_history_stays_a_few_megabytes()
    {
        Assert.InRange(Slv3LinkDiag.MaxFileBytes * (Slv3LinkDiag.RotatedFiles + 1), 1, 6L * 1024 * 1024);
    }
}
