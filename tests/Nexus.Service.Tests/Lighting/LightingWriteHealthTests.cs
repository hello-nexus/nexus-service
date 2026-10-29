using Nexus.Service.Lighting;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Tests.Lighting;

public class LightingWriteHealthTests
{
    private sealed class Clock
    {
        public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
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

    [Fact]
    public void Four_failures_stay_healthy_and_the_fifth_flags()
    {
        var (health, clock, flips) = Make();
        for (var i = 0; i < LightingWriteHealth.MinFailures - 1; i++)
        {
            health.NoteFailure("hub");
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.False(health.IsFailing("hub"));

        health.NoteFailure("hub");

        Assert.True(health.IsFailing("hub"));
        Assert.Single(flips);
    }

    [Fact]
    public void One_bad_report_never_flags_however_many_failures_it_carries()
    {
        var (health, _, _) = Make();

        health.Report("hub", 500, 0);

        Assert.False(health.IsFailing("hub"));
    }

    [Fact]
    public void Failures_in_two_reports_flag_when_they_dominate()
    {
        var (health, clock, _) = Make();
        health.Report("hub", 3, 0);
        clock.Advance(TimeSpan.FromSeconds(5));
        health.Report("hub", 3, 1);

        Assert.True(health.IsFailing("hub"));
    }

    [Fact]
    public void Failures_outnumbered_by_successes_do_not_flag()
    {
        var (health, clock, _) = Make();
        for (var i = 0; i < 10; i++)
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
        for (var i = 0; i < 6; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            health.NoteFailure("hub");
        }

        Assert.False(health.IsFailing("hub"));
    }

    [Fact]
    public void Failures_older_than_the_window_do_not_add_up()
    {
        var (health, clock, _) = Make();
        for (var i = 0; i < LightingWriteHealth.MinFailures - 1; i++)
        {
            health.NoteFailure("hub");
        }
        clock.Advance(LightingWriteHealth.Window + TimeSpan.FromSeconds(1));

        health.NoteFailure("hub");

        Assert.False(health.IsFailing("hub"));
    }

    [Fact]
    public void A_flagged_key_clears_after_a_quiet_spell_and_not_before()
    {
        var (health, clock, flips) = Make();
        for (var i = 0; i < LightingWriteHealth.MinFailures; i++)
        {
            health.NoteFailure("hub");
        }
        Assert.True(health.IsFailing("hub"));

        clock.Advance(LightingWriteHealth.ClearAfter - TimeSpan.FromSeconds(1));
        health.Sweep();
        Assert.True(health.IsFailing("hub"));

        clock.Advance(TimeSpan.FromSeconds(1));
        health.Sweep();

        Assert.False(health.IsFailing("hub"));
        Assert.Equal(2, flips.Count);
    }

    [Fact]
    public void Clearing_does_not_reflag_from_the_burst_still_inside_the_window()
    {
        var (health, clock, flips) = Make();
        for (var i = 0; i < LightingWriteHealth.MinFailures; i++)
        {
            health.NoteFailure("hub");
        }
        clock.Advance(LightingWriteHealth.ClearAfter);
        health.Sweep();
        health.NoteSuccess("hub");
        health.Sweep();

        Assert.False(health.IsFailing("hub"));
        Assert.Equal(2, flips.Count);
    }

    [Fact]
    public void A_new_failure_keeps_a_flagged_key_flagged()
    {
        var (health, clock, flips) = Make();
        for (var i = 0; i < LightingWriteHealth.MinFailures; i++)
        {
            health.NoteFailure("hub");
        }
        clock.Advance(LightingWriteHealth.ClearAfter - TimeSpan.FromSeconds(1));
        health.NoteFailure("hub");
        clock.Advance(LightingWriteHealth.ClearAfter - TimeSpan.FromSeconds(1));
        health.Sweep();

        Assert.True(health.IsFailing("hub"));
        Assert.Single(flips);
    }

    [Fact]
    public void Forget_drops_a_flagged_key_and_announces_it()
    {
        var (health, _, flips) = Make();
        for (var i = 0; i < LightingWriteHealth.MinFailures; i++)
        {
            health.NoteFailure("hub");
        }

        health.Forget("hub");

        Assert.False(health.IsFailing("hub"));
        Assert.Equal(2, flips.Count);
    }

    [Fact]
    public void Keys_are_independent()
    {
        var (health, _, _) = Make();
        for (var i = 0; i < LightingWriteHealth.MinFailures; i++)
        {
            health.NoteFailure("a");
            health.NoteSuccess("b");
        }

        Assert.True(health.IsFailing("a"));
        Assert.False(health.IsFailing("b"));
    }

    [Fact]
    public void Apply_flags_controlled_cards_matched_by_any_owner_key()
    {
        var (health, _, _) = Make();
        foreach (var key in new[] { "card", "dev", "parent", "handler" })
        {
            for (var i = 0; i < LightingWriteHealth.MinFailures; i++)
            {
                health.NoteFailure(key);
            }
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
    public void Handler_match_takes_the_id_or_a_serial_suffix_only()
    {
        var (health, _, _) = Make();
        for (var i = 0; i < LightingWriteHealth.MinFailures; i++)
        {
            health.NoteFailure("keeb:SN1");
        }

        Assert.True(health.IsHandlerFailing("keeb"));
        Assert.False(health.IsHandlerFailing("kee"));
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
