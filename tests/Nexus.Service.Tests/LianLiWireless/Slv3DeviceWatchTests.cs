using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3DeviceWatchTests
{
    private const string Mac = "AABBCCDDEEFF";

    [Fact]
    public void Echo_confirms_and_never_flags_stuck()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020304", 1_000);
        watch.NoteReported(Mac, "01020304", 1_400, log.Add);
        watch.Check(9_000, log.Add);
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
        watch.Check(3_999, log.Add);
        Assert.Empty(log);
        watch.Check(4_000, log.Add);
        watch.Check(5_000, log.Add);
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
        watch.Check(4_000, log.Add);
        Assert.Single(log);
    }

    [Fact]
    public void Late_echo_recovers_with_duration_and_counts_overtaken_uploads()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020301", 1_000);
        watch.NoteSent(Mac, "01020302", 2_000);
        watch.Check(4_500, log.Add);
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
        watch.Check(4_100, log.Add);
        Assert.Single(log);
        Assert.Equal(0, watch.Take(Mac, 4_100)!.Value.Confirmed);
    }

    [Fact]
    public void Dropped_stuck_chain_logs_end()
    {
        var watch = new Slv3DeviceWatch();
        var log = new List<string>();
        watch.NoteSent(Mac, "01020304", 1_000);
        watch.Check(4_100, log.Add);
        watch.Remove(Mac, 40_000, log.Add);
        Assert.Contains("dropped", log[^1]);
        Assert.Null(watch.Take(Mac, 40_000));
    }
}
