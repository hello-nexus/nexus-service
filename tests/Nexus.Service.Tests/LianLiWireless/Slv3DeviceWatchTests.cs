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
        Assert.Empty(log);
        var s = watch.Take(Mac, 9_000)!.Value;
        Assert.Equal(1, s.Sent);
        Assert.Equal(1, s.Confirmed);
        Assert.Equal(400, s.LatencyMaxMs);
        Assert.Equal(0, s.PendingMs);
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
        Assert.Equal(2, log.Count);
        Assert.Contains("RECOVERED", log[1]);
        Assert.Contains("5000 ms", log[1]);
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
        Assert.Equal(6, log.Count);
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
}
