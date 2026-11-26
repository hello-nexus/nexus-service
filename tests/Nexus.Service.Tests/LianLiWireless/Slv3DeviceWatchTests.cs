using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3DeviceWatchTests
{
    private const string Mac = "AABBCCDDEEFF";

    private static bool Bound(string mac) => true;

    [Fact]
    public void Echo_confirms_and_never_flags_stuck()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020304", 1_000);
        watch.NoteReported(Mac, "01020304", 1_400, log.Add);
        watch.Check(9_000, Bound, log.Add);
        Assert.DoesNotContain(log, l => l.Contains("STUCK") || l.StartsWith("[slv3-gap]"));
        var s = watch.Take(Mac, 9_000)!.Value;
        Assert.Equal(1, s.Sent);
        Assert.Equal(1, s.Confirmed);
        Assert.Equal(400, s.LatencyMaxMs);
        Assert.Equal(0, s.PendingMs);
    }

    [Fact]
    public void Revert_to_bare_idx_is_logged_once_with_upload_and_echo_ages()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "02080801", 1_000);
        watch.NoteReported(Mac, "02080801", 1_400, log.Add);
        watch.NoteReported(Mac, "00000000", 6_000, log.Add);
        watch.NoteReported(Mac, "00000000", 6_400, log.Add);
        watch.NoteReported(Mac, "00000001", 7_000, log.Add);
        var revert = Assert.Single(log, l => l.StartsWith("[slv3-revert]"));
        Assert.Contains("02080801 -> 00000000", revert);
        Assert.Contains("last upload 5000 ms ago", revert);
        Assert.Contains("last echo 4600 ms ago", revert);
    }

    [Fact]
    public void Missing_echo_is_stuck_after_threshold_and_logged_once()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020304", 1_000);
        watch.Check(3_999, Bound, log.Add);
        Assert.Empty(log);
        watch.Check(4_000, Bound, log.Add);
        watch.Check(5_000, Bound, log.Add);
        Assert.Single(log);
        Assert.Contains("STUCK", log[0]);
    }

    [Fact]
    public void Superseded_uploads_do_not_reset_the_stuck_clock()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020301", 1_000);
        watch.NoteSent(Mac, "01020302", 2_500);
        watch.Check(4_000, Bound, log.Add);
        Assert.Single(log);
    }

    [Fact]
    public void Late_echo_recovers_with_duration_and_counts_overtaken_uploads()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020301", 1_000);
        watch.NoteSent(Mac, "01020302", 2_000);
        watch.Check(4_500, Bound, log.Add);
        watch.NoteReported(Mac, "01020302", 6_000, log.Add);
        var events = log.FindAll(l => l.Contains("STUCK") || l.Contains("RECOVERED"));
        Assert.Equal(2, events.Count);
        Assert.Contains("RECOVERED", events[1]);
        Assert.Contains("5000 ms", events[1]);
        var s = watch.Take(Mac, 6_000)!.Value;
        Assert.Equal(1, s.Confirmed);
        Assert.Equal(1, s.Unconfirmed);
        Assert.Equal(0, s.PendingMs);
    }

    [Fact]
    public void Unrelated_device_index_is_not_a_confirmation()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020304", 1_000);
        watch.NoteReported(Mac, "FFFFFFFF", 1_200, log.Add);
        watch.Check(4_100, Bound, log.Add);
        Assert.Single(log);
        Assert.Equal(0, watch.Take(Mac, 4_100)!.Value.Confirmed);
    }

    [Fact]
    public void Dropped_stuck_chain_logs_end()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020304", 1_000);
        watch.Check(4_100, Bound, log.Add);
        watch.Remove(Mac, 40_000, log.Add);
        Assert.Contains("dropped", log[^1]);
        Assert.Null(watch.Take(Mac, 40_000));
    }

    [Fact]
    public void Clear_forgets_pending_uploads_so_a_reconnect_is_not_stuck()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020304", 1_000);
        watch.Clear();
        watch.Check(60_000, Bound, log.Add);
        Assert.Empty(log);
        Assert.Null(watch.Take(Mac, 60_000));
    }

    [Fact]
    public void Unbound_chain_is_never_flagged()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020304", 1_000);
        watch.Check(9_000, _ => false, log.Add);
        Assert.Empty(log);
    }

    [Fact]
    public void Flapping_chain_logs_three_episodes_then_suppresses_and_counts()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var t = 1_000 + i * 8_000;
            watch.NoteSent(Mac, $"0102030{i}", t);
            watch.Check(t + 3_100, Bound, log.Add);
            watch.NoteReported(Mac, $"0102030{i}", t + 3_500, log.Add);
        }
        Assert.Equal(6, log.FindAll(l => l.Contains("STUCK") || l.Contains("RECOVERED")).Count);
        Assert.Equal(3, log.FindAll(l => l.Contains("STUCK at")).Count);
        Assert.Equal(3, log.FindAll(l => l.Contains("RECOVERED")).Count);
        Assert.Equal(3, watch.Take(Mac, 60_000)!.Value.Suppressed);
    }

    [Fact]
    public void Suppression_ends_after_the_window()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var t = 1_000 + i * 8_000;
            watch.NoteSent(Mac, $"0102030{i}", t);
            watch.Check(t + 3_100, Bound, log.Add);
            watch.NoteReported(Mac, $"0102030{i}", t + 3_500, log.Add);
        }
        log.Clear();
        watch.NoteSent(Mac, "010203AA", 200_000);
        watch.Check(203_100, Bound, log.Add);
        Assert.Single(log);
    }

    [Fact]
    public void Pending_age_follows_the_oldest_unconfirmed_upload()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020301", 1_000);
        watch.NoteSent(Mac, "01020302", 2_000);
        watch.NoteSent(Mac, "01020303", 2_500);
        watch.NoteReported(Mac, "01020302", 3_000, log.Add);
        Assert.Equal(500, watch.Take(Mac, 3_000)!.Value.PendingMs);
    }

    [Fact]
    public void Failed_writes_are_counted_and_not_treated_as_sent()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSendFailed(Mac);
        watch.Check(60_000, Bound, log.Add);
        Assert.Empty(log);
        var s = watch.Take(Mac, 60_000)!.Value;
        Assert.Equal(1, s.SendFailed);
        Assert.Equal(0, s.Sent);
    }

    [Fact]
    public void Gap_of_one_second_logs_start_end_duration_and_both_indexes()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "0A0B0C0D", 10_000);
        watch.Check(10_999, Bound, log.Add);
        watch.Check(11_000, Bound, log.Add);
        Assert.Empty(log);
        watch.NoteSent(Mac, "0A0B0C0E", 11_100);
        watch.NoteReported(Mac, "0A0B0C0E", 11_400, log.Add);
        var gap = Assert.Single(log, l => l.StartsWith("[slv3-gap]"));
        Assert.Contains("durMs=1400", gap);
        Assert.Contains("sentIdx=0A0B0C0D", gap);
        Assert.Contains("confirmedIdx=0A0B0C0E", gap);
        Assert.Contains("endedBy=echo", gap);
        Assert.Contains("sentInGap=1", gap);
        Assert.DoesNotContain(log, l => l.Contains("STUCK"));
    }

    [Fact]
    public void Echo_line_pairs_send_time_with_landing_time()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020304", 1_000);
        watch.NoteReported(Mac, "01020304", 2_250, log.Add);
        var echo = Assert.Single(log, l => l.StartsWith("[slv3-echo]"));
        Assert.Contains("ok idx=01020304", echo);
        Assert.Contains("latMs=1250", echo);
    }

    [Fact]
    public void Prompt_echo_is_counted_but_not_logged()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020304", 1_000);
        watch.NoteReported(Mac, "01020304", 1_250, log.Add);
        Assert.Empty(log);
        var summary = watch.Take(Mac, 2_000);
        Assert.Equal(1, summary!.Value.Confirmed);
        Assert.Equal(250, summary.Value.LatencyMaxMs);
    }

    [Fact]
    public void Overtaken_upload_is_logged_as_superseded()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020301", 1_000);
        watch.NoteSent(Mac, "01020302", 1_500);
        watch.NoteReported(Mac, "01020302", 1_800, log.Add);
        Assert.Contains(log, l => l.Contains("superseded idx=01020301"));
        Assert.DoesNotContain(log, l => l.Contains("ok idx=01020302"));
    }

    [Fact]
    public void Overtaken_live_pushes_are_counted_but_not_logged()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020301", 1_000, live: true);
        watch.NoteSent(Mac, "01020302", 1_500);
        watch.NoteReported(Mac, "01020302", 1_800, log.Add);
        Assert.DoesNotContain(log, l => l.Contains("superseded"));
        Assert.Equal(1, watch.Take(Mac, 2_000)!.Value.Unconfirmed);
    }

    [Fact]
    public void Gap_lines_are_rate_limited_per_chain()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        long t = 1_000;
        for (var i = 0; i < 40; i++)
        {
            var idx = $"0000{i:X4}";
            watch.NoteSent(Mac, idx, t);
            t += 1_000;
            watch.Check(t, Bound, log.Add);
            t += 100;
            watch.NoteReported(Mac, idx, t, log.Add);
        }
        var gaps = log.FindAll(l => l.StartsWith("[slv3-gap]"));
        Assert.Equal(20, gaps.Count);
    }

    [Fact]
    public void Open_gap_closes_when_the_chain_drops()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020304", 1_000);
        watch.Check(2_500, Bound, log.Add);
        watch.Remove(Mac, 3_000, log.Add);
        Assert.Contains(log, l => l.StartsWith("[slv3-gap]") && l.Contains("endedBy=drop") && l.Contains("durMs=2000"));
    }
}
