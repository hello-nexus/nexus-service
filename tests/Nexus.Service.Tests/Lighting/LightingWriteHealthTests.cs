using Nexus.Service.Lighting;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Tests.Lighting;

public class LightingWriteHealthTests
{
    private sealed class Clock
    {
        public TimeSpan Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static (LightingWriteHealth Health, Clock Clock, List<int> Flips) Make()
    {
        var clock = new Clock();
        var flips = new List<int>();
        var health = new LightingWriteHealth(() => clock.Now);
        health.Changed += () => flips.Add(1);
        return (health, clock, flips);
    }

    private static void FailEvery(LightingWriteHealth health, Clock clock, string key, int count, TimeSpan step)
    {
        for (var i = 0; i < count; i++)
        {
            health.NoteFailure(key);
            clock.Advance(step);
        }
    }

    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Fact]
    public void A_hotplug_burst_of_five_failures_in_one_second_never_flags()
    {
        var (health, clock, flips) = Make();
        FailEvery(health, clock, "hub", 5, TimeSpan.FromMilliseconds(200));
        health.Sweep();
        clock.Advance(TimeSpan.FromSeconds(60));
        health.Sweep();

        Assert.False(health.IsFailing("hub"));
        Assert.Empty(flips);
    }

    [Fact]
    public void A_resume_burst_followed_by_a_detach_never_flags()
    {
        var (health, clock, _) = Make();
        FailEvery(health, clock, "hub", 5, TimeSpan.FromMilliseconds(100));
        health.Forget("hub");
        clock.Advance(OneSecond);
        health.NoteSuccess("hub");

        Assert.False(health.IsFailing("hub"));
    }

    [Fact]
    public void Failures_still_occurring_after_the_sustain_time_flag()
    {
        var (health, clock, flips) = Make();
        FailEvery(health, clock, "hub", 16, OneSecond);
        Assert.False(health.IsFailing("hub"));

        health.NoteFailure("hub");
        health.Sweep();

        Assert.True(health.IsFailing("hub"));
        Assert.Single(flips);
    }

    [Fact]
    public void A_resume_burst_plus_a_few_stray_failures_never_flags()
    {
        var (health, clock, _) = Make();
        FailEvery(health, clock, "hub", 5, TimeSpan.FromMilliseconds(100));
        clock.Advance(TimeSpan.FromSeconds(8.5));
        health.NoteFailure("hub");
        clock.Advance(TimeSpan.FromSeconds(9));
        health.NoteFailure("hub");
        health.Sweep();

        Assert.False(health.IsFailing("hub"));
    }

    [Fact]
    public void A_new_burst_after_a_quiet_gap_starts_a_fresh_streak()
    {
        var (health, clock, _) = Make();
        FailEvery(health, clock, "hub", 10, OneSecond);
        clock.Advance(LightingWriteHealth.StreakGap + OneSecond);
        FailEvery(health, clock, "hub", 6, TimeSpan.FromMilliseconds(100));

        Assert.False(health.IsFailing("hub"));
    }

    [Fact]
    public void One_bad_report_never_flags_however_many_failures_it_carries()
    {
        var (health, _, _) = Make();

        health.Report("hub", 500, 0);

        Assert.False(health.IsFailing("hub"));
    }

    [Fact]
    public void Failures_outnumbered_by_successes_do_not_flag()
    {
        var (health, clock, _) = Make();
        for (var i = 0; i < 20; i++)
        {
            health.Report("hub", 1, 40);
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.False(health.IsFailing("hub"));
    }

    [Fact]
    public void Successes_before_the_first_failure_count_against_it()
    {
        var (health, clock, _) = Make();
        health.Report("hub", 0, 1000);
        FailEvery(health, clock, "hub", 17, OneSecond);

        Assert.False(health.IsFailing("hub"));
    }

    private static void Flag(LightingWriteHealth health, Clock clock, string key)
    {
        FailEvery(health, clock, key, 17, OneSecond);
        health.Sweep();
    }

    [Fact]
    public void A_flagged_key_clears_after_a_quiet_spell_and_not_before()
    {
        var (health, clock, flips) = Make();
        Flag(health, clock, "hub");
        Assert.True(health.IsFailing("hub"));
        // FailEvery leaves the clock one step past the last failure.
        clock.Advance(LightingWriteHealth.ClearAfter - TimeSpan.FromSeconds(3));
        health.Sweep();
        Assert.True(health.IsFailing("hub"));

        clock.Advance(TimeSpan.FromSeconds(3));
        health.Sweep();

        Assert.False(health.IsFailing("hub"));
        Assert.Equal(2, flips.Count);
    }

    [Fact]
    public void Clearing_does_not_reflag_from_the_burst_still_inside_the_window()
    {
        var (health, clock, flips) = Make();
        Flag(health, clock, "hub");
        clock.Advance(LightingWriteHealth.ClearAfter);
        health.Sweep();
        health.NoteSuccess("hub");
        health.Sweep();

        Assert.False(health.IsFailing("hub"));
        Assert.Equal(2, flips.Count);
    }

    [Fact]
    public void Forget_drops_a_flagged_key_and_announces_it_on_the_next_sweep()
    {
        var (health, clock, flips) = Make();
        Flag(health, clock, "hub");

        health.Forget("hub");
        health.Sweep();

        Assert.False(health.IsFailing("hub"));
        Assert.Equal(2, flips.Count);
    }

    [Fact]
    public async Task Changed_handlers_never_run_on_the_reporting_thread()
    {
        var (health, clock, _) = Make();
        var handlerThreads = new List<int>();
        health.Changed += () => handlerThreads.Add(Environment.CurrentManagedThreadId);
        var reporterThread = 0;

        await Task.Run(() =>
        {
            reporterThread = Environment.CurrentManagedThreadId;
            FailEvery(health, clock, "hub", 17, OneSecond);
        });
        Assert.Empty(handlerThreads);

        health.Sweep();

        Assert.Single(handlerThreads);
        Assert.NotEqual(reporterThread, handlerThreads[0]);
        Assert.Equal(Environment.CurrentManagedThreadId, handlerThreads[0]);
    }

    [Fact]
    public void Keys_are_independent()
    {
        var (health, clock, _) = Make();
        for (var i = 0; i < 17; i++)
        {
            health.NoteFailure("a");
            health.NoteSuccess("b");
            clock.Advance(OneSecond);
        }

        Assert.True(health.IsFailing("a"));
        Assert.False(health.IsFailing("b"));
    }

    [Fact]
    public void Apply_flags_controlled_cards_matched_by_any_owner_key()
    {
        var (health, clock, _) = Make();
        foreach (var key in new[] { "card", "dev", "parent", "handler" })
        {
            FailEvery(health, clock, key, 17, OneSecond);
            clock.Advance(-17 * OneSecond);
        }
        var devices = new List<LightingDevice>
        {
            new() { Id = "card" },
            new() { Id = "x", DeviceId = "dev" },
            new() { Id = "y", ParentDeviceId = "parent" },
            new() { Id = "z", ControlHandlerId = "handler" },
            new() { Id = "healthy", DeviceId = "other" },
            new() { Id = "card2", DeviceId = "dev", Controlled = false },
        };

        health.Apply(devices);

        Assert.Equal(LightingWriteHealth.Failing, devices[0].WriteHealth);
        Assert.Equal(LightingWriteHealth.Failing, devices[1].WriteHealth);
        Assert.Equal(LightingWriteHealth.Failing, devices[2].WriteHealth);
        Assert.Equal(LightingWriteHealth.Failing, devices[3].WriteHealth);
        Assert.Null(devices[4].WriteHealth);
        Assert.Null(devices[5].WriteHealth);
    }

    [Fact]
    public void Handler_match_is_exact_on_the_id_or_the_reported_handler()
    {
        var (health, clock, _) = Make();
        for (var i = 0; i < 17; i++)
        {
            health.NoteFailure("keeb:SN1", "keeb");
            clock.Advance(OneSecond);
        }

        Assert.True(health.IsHandlerFailing("keeb"));
        Assert.False(health.IsHandlerFailing("kee"));
        Assert.False(health.IsHandlerFailing("keeb:SN1x"));
        Assert.False(health.IsHandlerFailing("np50"));
    }

    [Fact]
    public void WriteHealth_is_omitted_from_the_wire_when_healthy_and_present_when_failing()
    {
        var healthy = new GetLightingDevicesResponse { Devices = { new LightingDevice { Id = "a" } } };
        var failing = new GetLightingDevicesResponse { Devices = { new LightingDevice { Id = "a", WriteHealth = LightingWriteHealth.Failing } } };

        var healthyJson = System.Text.Json.JsonSerializer.Serialize(healthy, Nexus.Service.Serialization.AppJsonContext.Default.GetLightingDevicesResponse);
        var failingJson = System.Text.Json.JsonSerializer.Serialize(failing, Nexus.Service.Serialization.AppJsonContext.Default.GetLightingDevicesResponse);

        Assert.DoesNotContain("writeHealth", healthyJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"writeHealth\":\"failing\"", failingJson, StringComparison.OrdinalIgnoreCase);
    }
}
