using Nexus.Service.Lighting;
using Nexus.Service.Lighting.ScheduleFollow;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

[Collection("SunShift")]
public sealed class ScheduleFollowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-test-follow-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static List<BrightnessSchedulePoint> Flat(int percent) => new()
    {
        new() { Hour = 0, Brightness = percent },
        new() { Hour = 12, Brightness = percent },
    };

    private static readonly string[] AllKeys = { ScheduleFollowKeys.Y70, ScheduleFollowKeys.CorsairLcd, ScheduleFollowKeys.KrakenLcd, "display1" };

    private static InMemoryConfigStore StoreWithSchedule(bool enabled, int percent, bool optIn = true)
    {
        var store = new InMemoryConfigStore();
        if (optIn)
        {
            store.Update(s => s.Devices.ScheduleFollowing.AddRange(AllKeys));
        }
        store.Update(s => s.Lighting.BrightnessSchedule = new() { Enabled = enabled, SunShift = false, Points = Flat(percent) });
        return store;
    }

    private static ScheduleFollowState State(InMemoryConfigStore store) => new(store, () => new DateTime(2026, 6, 21, 12, 0, 0));

    [Theory]
    [InlineData(100, 1.0, 100)]
    [InlineData(80, 0.5, 50)]
    [InlineData(30, 0.5, 30)]
    [InlineData(50, 0.1, 10)]
    [InlineData(70, 0.0, 0)]
    [InlineData(100, 2.0, 100)]
    [InlineData(150, 0.5, 50)]
    public void Scale_is_the_slider_capped_by_the_level(int slider, double multiplier, int expected) =>
        Assert.Equal(expected, ScheduleFollowMath.Scale(slider, multiplier));

    [Theory]
    [InlineData(1.0, 0.2, 0.1, 0.9)]
    [InlineData(0.2, 1.0, 0.1, 0.3)]
    [InlineData(0.5, 0.52, 0.1, 0.52)]
    [InlineData(0.5, 0.5, 0.1, 0.5)]
    public void Ramp_moves_by_at_most_the_step(double current, double target, double step, double expected) =>
        Assert.Equal(expected, ScheduleFollowMath.Ramp(current, target, step), 9);

    [Theory]
    [InlineData(false, true, 0.3, 1.0)]
    [InlineData(true, false, 0.3, 1.0)]
    [InlineData(true, true, 0.3, 0.3)]
    public void Desired_is_the_plain_slider_unless_the_schedule_is_on_and_the_device_follows(bool enabled, bool follows, double level, double expected) =>
        Assert.Equal(expected, ScheduleFollowMath.Desired(enabled, follows, level));

    [Fact]
    public void A_device_does_not_follow_until_the_user_enables_it()
    {
        var store = StoreWithSchedule(true, 50, optIn: false);
        var state = State(store);

        Assert.False(state.Follows(store.Load(), "any-display"));
        Assert.False(state.Follows(store.Load(), ScheduleFollowKeys.Y70));
        state.Advance(1);
        Assert.Equal(1.0, state.Multiplier(ScheduleFollowKeys.Y70));

        store.Update(s => s.Devices.ScheduleFollowing.Add(ScheduleFollowKeys.Y70));
        state.Advance(1);
        Assert.True(state.Follows(store.Load(), ScheduleFollowKeys.Y70));
        Assert.Equal(1.0 - ScheduleFollowMath.FastRatePerSecond, state.Multiplier(ScheduleFollowKeys.Y70), 6);
    }

    [Fact]
    public void Master_off_leaves_every_device_at_its_slider()
    {
        var store = StoreWithSchedule(false, 20);
        var state = State(store);

        state.Advance(1);

        Assert.Equal(1.0, state.Multiplier(ScheduleFollowKeys.Y70));
        Assert.Equal(80, state.Scale(ScheduleFollowKeys.Y70, 80));
    }

    [Fact]
    public void First_advance_snaps_to_the_cap_and_the_slider_stays_the_ceiling()
    {
        var store = StoreWithSchedule(true, 50);
        store.Update(s => s.Y70.Brightness = 80);
        var state = State(store);

        state.Advance(1);

        Assert.Equal(0.5, state.Multiplier(ScheduleFollowKeys.Y70));
        Assert.Equal(50, state.Scale(ScheduleFollowKeys.Y70, store.Load().Y70.Brightness));
        Assert.Equal(30, state.Scale(ScheduleFollowKeys.Y70, 30));
        Assert.Equal(80, store.Load().Y70.Brightness);
    }

    [Fact]
    public void Turning_the_schedule_on_ramps_instead_of_stepping_and_turning_it_off_restores_the_slider()
    {
        var store = StoreWithSchedule(false, 50);
        var state = State(store);
        state.Advance(1);

        store.Update(s => s.Lighting.BrightnessSchedule.Enabled = true);
        state.Advance(1);
        Assert.Equal(1.0 - ScheduleFollowMath.FastRatePerSecond, state.Multiplier(ScheduleFollowKeys.Y70), 9);

        for (var i = 0; i < 20; i++)
        {
            state.Advance(1);
        }
        Assert.Equal(0.5, state.Multiplier(ScheduleFollowKeys.Y70), 9);

        store.Update(s => s.Lighting.BrightnessSchedule.Enabled = false);
        for (var i = 0; i < 20; i++)
        {
            state.Advance(1);
        }
        Assert.Equal(1.0, state.Multiplier(ScheduleFollowKeys.Y70), 9);
        Assert.Equal(80, state.Scale(ScheduleFollowKeys.Y70, 80));
    }

    [Fact]
    public void A_device_that_stops_following_returns_to_its_slider()
    {
        var store = StoreWithSchedule(true, 50);
        var state = State(store);
        state.Advance(1);
        Assert.Equal(0.5, state.Multiplier(ScheduleFollowKeys.CorsairLcd));

        store.Update(s => s.Devices.ScheduleFollowing.Remove(ScheduleFollowKeys.CorsairLcd));
        for (var i = 0; i < 20; i++)
        {
            state.Advance(1);
        }

        Assert.Equal(1.0, state.Multiplier(ScheduleFollowKeys.CorsairLcd), 9);
        Assert.Equal(0.5, state.Multiplier(ScheduleFollowKeys.Y70), 9);
    }

    [Fact]
    public void Display_slots_ramp_slower_than_fast_surfaces()
    {
        var store = StoreWithSchedule(false, 50);
        var state = State(store);
        state.Track("display1", ScheduleFollowMath.SlowRatePerSecond);
        state.Advance(1);
        store.Update(s => s.Lighting.BrightnessSchedule.Enabled = true);

        state.Advance(1);

        Assert.Equal(1.0 - ScheduleFollowMath.SlowRatePerSecond, state.Multiplier("display1"), 9);
        Assert.Equal(1.0 - ScheduleFollowMath.FastRatePerSecond, state.Multiplier(ScheduleFollowKeys.Y70), 9);
    }

    [Fact]
    public void Lighting_follows_gates_only_the_lighting_cap()
    {
        var lighting = new LightingSettings
        {
            GlobalBrightness = 1f,
            BrightnessSchedule = new() { Enabled = true, SunShift = false, Points = Flat(30), LightingFollows = false },
        };
        var noon = new DateTime(2026, 6, 21, 12, 0, 0);

        Assert.Equal(1f, MasterBrightness.Effective(lighting, noon));

        lighting.BrightnessSchedule.LightingFollows = true;
        Assert.Equal(0.3f, MasterBrightness.Effective(lighting, noon));

        Assert.Equal(0.3f, MasterBrightness.ScheduleLevel(lighting.BrightnessSchedule, noon));
        lighting.BrightnessSchedule.Enabled = false;
        Assert.Equal(1f, MasterBrightness.ScheduleLevel(lighting.BrightnessSchedule, noon));
    }

    [Fact]
    public void A_settings_file_without_lightingFollows_keeps_lighting_following()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 5,
          "lighting": { "globalBrightness": 1, "brightnessSchedule": { "enabled": true, "sunShift": false, "points": [ { "hour": 0, "brightness": 30 }, { "hour": 12, "brightness": 30 } ] } }
        }
        """);

        var store = new JsonConfigStore(path);
        var lighting = store.Load().Lighting;

        Assert.Null(lighting.BrightnessSchedule.LightingFollows);
        Assert.True(lighting.BrightnessSchedule.LightingFollowsOn);
        Assert.Equal(0.3f, MasterBrightness.Effective(lighting, new DateTime(2026, 6, 21, 12, 0, 0)));
        Assert.Empty(store.Load().Devices.ScheduleFollowing);
    }

    [Fact]
    public void Lightingfollows_false_survives_a_save()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        var store = new JsonConfigStore(path);
        store.Update(s =>
        {
            s.Lighting.BrightnessSchedule.LightingFollows = false;
            s.Devices.ScheduleFollowing.Add("display1");
            s.Devices.DisplaySliderBrightness["display1"] = 70;
        });
        store.FlushNow();

        var reloaded = new JsonConfigStore(path).Load();

        Assert.False(reloaded.Lighting.BrightnessSchedule.LightingFollowsOn);
        Assert.Contains("display1", reloaded.Devices.ScheduleFollowing);
        Assert.Equal(70, reloaded.Devices.DisplaySliderBrightness["display1"]);
    }

    [Fact]
    public void Restoring_to_the_plain_slider_never_ramps()
    {
        var store = StoreWithSchedule(true, 20);
        var state = State(store);
        state.Advance(1);
        Assert.Equal(0.2, state.Multiplier(ScheduleFollowKeys.Y70), 6);

        store.Update(s => s.Lighting.BrightnessSchedule.Enabled = false);
        state.Advance(0, out var restored);

        Assert.Equal(1.0, state.Multiplier(ScheduleFollowKeys.Y70));
        Assert.Contains(ScheduleFollowKeys.Y70, restored);

        store.Update(s => s.Lighting.BrightnessSchedule.Enabled = true);
        store.Update(s => s.Devices.ScheduleFollowing.Remove(ScheduleFollowKeys.Y70));
        state.Advance(1, out restored);
        Assert.Equal(1.0, state.Multiplier(ScheduleFollowKeys.Y70));
        Assert.DoesNotContain(ScheduleFollowKeys.Y70, restored);
    }

    [Fact]
    public void Lighting_cap_drops_the_moment_the_schedule_is_turned_off_or_lighting_stops_following()
    {
        var lighting = new LightingSettings { GlobalBrightness = 1f, BrightnessSchedule = new() { Enabled = true, SunShift = false, Points = Flat(30) } };
        var noon = new DateTime(2026, 6, 21, 12, 0, 0);
        Assert.Equal(0.3f, MasterBrightness.Effective(lighting, noon));

        lighting.BrightnessSchedule.Enabled = false;
        Assert.Equal(1f, MasterBrightness.Effective(lighting, noon));

        lighting.BrightnessSchedule.Enabled = true;
        lighting.BrightnessSchedule.LightingFollows = false;
        Assert.Equal(1f, MasterBrightness.Effective(lighting, noon));
    }
}
