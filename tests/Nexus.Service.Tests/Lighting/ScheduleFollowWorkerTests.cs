using Nexus.Service.Lighting.ScheduleFollow;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

[Collection("SunShift")]
public sealed class ScheduleFollowWorkerTests
{
    private static readonly DateTime Noon = new(2026, 6, 21, 12, 0, 0);

    private static InMemoryConfigStore Store(bool enabled, int percent)
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Lighting.BrightnessSchedule = new()
        {
            Enabled = enabled,
            SunShift = false,
            Points = new() { new() { Hour = 0, Brightness = percent }, new() { Hour = 12, Brightness = percent } },
        });
        return store;
    }

    private sealed class FakeSurface : IScheduleSurface
    {
        public int? SliderValue { get; set; } = 80;
        public int? Hardware { get; set; }
        public bool WriteOk { get; set; } = true;
        public bool Present { get; set; } = true;
        public List<int> Writes { get; } = new();
        public long MinWriteGapMs { get; set; } = 4000;
        public IReadOnlyList<string> PresentKeys() => Present ? new[] { "mon1" } : Array.Empty<string>();
        public int? Slider(string key) => SliderValue;
        public void RememberSlider(string key, int percent) => SliderValue = percent;
        public Task<int?> ReadHardwareAsync(string key, CancellationToken ct) => Task.FromResult(Hardware);
        public Task<bool> WriteAsync(string key, int level, CancellationToken ct)
        {
            Writes.Add(level);
            return Task.FromResult(WriteOk);
        }
    }

    private sealed class FakeY70 : IY70Provider
    {
        public int Reapplied { get; private set; }
        public bool Connected { get; set; } = true;
        public bool IsConnected() => Connected;
        public string GetOrientation() => "Landscape";
        public void SetOrientation(string orientation) { }
        public bool GetForceOrientation() => false;
        public void SetForceOrientation(bool forceOrientation) { }
        public void ApplyEffectiveOrientation() { }
        public int GetBrightness() => 0;
        public void SetBrightness(int brightness) { }
        public void ReapplyBrightness() => Reapplied++;
        public bool GetToggle() => false;
        public void SetToggle(bool toggle) { }
        public void SetGameModeScreenOff(bool screenOff) { }
        public bool IsRotated() => false;
    }

    private static (ScheduleFollowWorker Worker, ScheduleFollowState State, FakeSurface Surface, FakeY70 Y70, InMemoryConfigStore Store) Make(bool enabled, int percent)
    {
        var store = Store(enabled, percent);
        store.Update(s => s.Devices.ScheduleFollowing.AddRange(new[] { ScheduleFollowKeys.Y70, "mon1" }));
        var state = new ScheduleFollowState(store, () => Noon);
        var surface = new FakeSurface();
        var y70 = new FakeY70();
        return (new ScheduleFollowWorker(state, store, y70, new[] { surface }), state, surface, y70, store);
    }

    [Fact]
    public async Task Schedule_off_writes_nothing()
    {
        var (worker, _, surface, y70, _) = Make(false, 50);

        await worker.TickAsync(10_000, false, default);

        Assert.Empty(surface.Writes);
        Assert.Equal(0, y70.Reapplied);
    }

    [Fact]
    public async Task A_surface_above_the_cap_is_written_once_at_the_cap()
    {
        var (worker, _, surface, y70, _) = Make(true, 50);

        await worker.TickAsync(10_000, false, default);
        await worker.TickAsync(11_000, false, default);

        Assert.Equal(new[] { 50 }, surface.Writes);
        Assert.Equal(1, y70.Reapplied);
    }

    [Fact]
    public async Task Turning_the_schedule_on_applies_the_cap_in_one_step_from_the_settings_change()
    {
        var (worker, _, surface, y70, store) = Make(false, 50);
        await worker.TickAsync(1_000, false, default);
        Assert.Empty(surface.Writes);

        store.Update(s => s.Lighting.BrightnessSchedule.Enabled = true);
        await worker.OnSettingsChangedAsync();

        Assert.Equal(new[] { 50 }, surface.Writes);
        Assert.Equal(1, y70.Reapplied);
    }

    [Fact]
    public async Task Opting_a_device_in_and_editing_the_curve_apply_at_once_past_the_write_gap()
    {
        var (worker, _, surface, _, store) = Make(true, 50);
        store.Update(s => s.Devices.ScheduleFollowing.Remove("mon1"));
        await worker.TickAsync(1_000, false, default);
        Assert.Empty(surface.Writes);

        store.Update(s => s.Devices.ScheduleFollowing.Add("mon1"));
        await worker.OnSettingsChangedAsync();
        store.Update(s => s.Lighting.BrightnessSchedule.Points = new() { new() { Hour = 0, Brightness = 30 }, new() { Hour = 12, Brightness = 30 } });
        await worker.OnSettingsChangedAsync();

        Assert.Equal(new[] { 50, 30 }, surface.Writes);
    }

    [Fact]
    public async Task Curve_drift_keeps_the_write_gap_and_a_slider_above_the_cap_changes_nothing()
    {
        var (worker, _, surface, _, store) = Make(true, 50);
        await worker.TickAsync(10_000, false, default);
        store.Update(s => s.Lighting.BrightnessSchedule.Points = new() { new() { Hour = 0, Brightness = 49 }, new() { Hour = 12, Brightness = 49 } });

        await worker.TickAsync(11_000, false, default);
        Assert.Equal(new[] { 50 }, surface.Writes);
        await worker.TickAsync(14_500, false, default);
        Assert.Equal(new[] { 50, 49 }, surface.Writes);

        surface.SliderValue = 100;
        await worker.TickAsync(30_000, false, default);
        Assert.Equal(new[] { 50, 49 }, surface.Writes);
    }

    [Fact]
    public async Task A_slider_below_the_cap_is_never_written_by_the_worker()
    {
        var (worker, _, surface, _, _) = Make(true, 50);
        surface.SliderValue = 30;

        await worker.TickAsync(10_000, false, default);
        await worker.TickAsync(20_000, false, default);

        Assert.Empty(surface.Writes);
    }

    [Fact]
    public async Task Turning_the_schedule_off_restores_the_slider_at_once_from_the_settings_change()
    {
        var (worker, state, surface, y70, store) = Make(true, 50);
        await worker.TickAsync(10_000, false, default);
        Assert.Equal(new[] { 50 }, surface.Writes);
        var reapplied = y70.Reapplied;

        store.Update(s => s.Lighting.BrightnessSchedule.Enabled = false);
        await worker.OnSettingsChangedAsync();

        Assert.Equal(1.0, state.Multiplier("mon1"));
        Assert.Equal(new[] { 50, 80 }, surface.Writes);
        Assert.Equal(reapplied + 1, y70.Reapplied);
    }

    [Fact]
    public async Task Switching_a_device_off_restores_only_that_device_at_once()
    {
        var (worker, state, surface, y70, store) = Make(true, 50);
        await worker.TickAsync(10_000, false, default);
        var reapplied = y70.Reapplied;

        store.Update(s => s.Devices.ScheduleFollowing.Remove("mon1"));
        await worker.OnSettingsChangedAsync();

        Assert.Equal(new[] { 50, 80 }, surface.Writes);
        Assert.Equal(1.0, state.Multiplier("mon1"));
        Assert.Equal(0.5, state.Multiplier(ScheduleFollowKeys.Y70), 6);
        Assert.Equal(reapplied, y70.Reapplied);
    }

    [Fact]
    public async Task A_surface_the_user_never_enabled_is_not_touched()
    {
        var (worker, _, surface, _, store) = Make(true, 50);
        store.Update(s => s.Devices.ScheduleFollowing.Remove("mon1"));
        surface.SliderValue = null;
        surface.Hardware = 60;

        await worker.TickAsync(10_000, false, default);
        await worker.TickAsync(20_000, false, default);

        Assert.Empty(surface.Writes);
        Assert.Null(surface.SliderValue);
    }

    [Fact]
    public async Task A_surface_with_no_slider_and_no_readable_hardware_is_skipped()
    {
        var (worker, _, surface, _, _) = Make(true, 50);
        surface.SliderValue = null;

        await worker.TickAsync(10_000, false, default);

        Assert.Empty(surface.Writes);
    }

    [Fact]
    public async Task An_unknown_slider_is_taken_from_the_hardware_and_remembered()
    {
        var (worker, _, surface, _, _) = Make(true, 50);
        surface.SliderValue = null;
        surface.Hardware = 60;

        await worker.TickAsync(10_000, false, default);

        Assert.Equal(60, surface.SliderValue);
        Assert.Equal(new[] { 50 }, surface.Writes);
    }

    [Fact]
    public async Task The_y70_is_not_reapplied_while_disconnected()
    {
        var (worker, _, _, y70, _) = Make(true, 50);
        y70.Connected = false;

        await worker.TickAsync(10_000, false, default);

        Assert.Equal(0, y70.Reapplied);
    }

    [Fact]
    public async Task A_settle_inside_the_write_gap_is_deferred_not_dropped()
    {
        var (worker, _, surface, _, store) = Make(true, 98);
        surface.SliderValue = 100;
        await worker.TickAsync(10_000, false, default);
        Assert.Equal(new[] { 98 }, surface.Writes);

        store.Update(s => s.Lighting.BrightnessSchedule.Points = new() { new() { Hour = 0, Brightness = 100 }, new() { Hour = 12, Brightness = 100 } });
        await worker.TickAsync(11_000, false, default);
        Assert.Equal(new[] { 98 }, surface.Writes);

        await worker.TickAsync(14_500, false, default);
        Assert.Equal(new[] { 98, 100 }, surface.Writes);
    }

    [Fact]
    public async Task A_failed_restore_write_is_retried_after_the_gap_even_with_the_schedule_off()
    {
        var (worker, _, surface, _, store) = Make(true, 50);
        await worker.TickAsync(10_000, false, default);
        store.Update(s => s.Lighting.BrightnessSchedule.Enabled = false);
        surface.WriteOk = false;
        await worker.TickAsync(11_000, true, default);
        Assert.Equal(new[] { 50, 80 }, surface.Writes);

        await worker.TickAsync(12_000, false, default);
        Assert.Equal(new[] { 50, 80 }, surface.Writes);
        surface.WriteOk = true;
        await worker.TickAsync(15_500, false, default);
        Assert.Equal(new[] { 50, 80, 80 }, surface.Writes);

        await worker.TickAsync(30_000, false, default);
        Assert.Equal(3, surface.Writes.Count);
    }

    [Fact]
    public async Task A_failed_first_cap_write_is_retried()
    {
        var (worker, _, surface, _, _) = Make(true, 50);
        surface.WriteOk = false;
        await worker.TickAsync(10_000, false, default);
        surface.WriteOk = true;
        await worker.TickAsync(14_500, false, default);

        Assert.Equal(new[] { 50, 50 }, surface.Writes);
    }

    [Fact]
    public async Task A_device_that_left_mid_restore_no_longer_keeps_the_tick_alive()
    {
        var (worker, _, surface, _, store) = Make(true, 50);
        await worker.TickAsync(10_000, false, default);
        surface.Present = false;
        store.Update(s => s.Lighting.BrightnessSchedule.Enabled = false);

        await worker.TickAsync(20_000, false, default);
        surface.Present = true;
        await worker.TickAsync(30_000, false, default);

        Assert.Equal(new[] { 50 }, surface.Writes);
    }

    [Fact]
    public async Task Opting_in_takes_the_slider_from_the_hardware_again()
    {
        var (worker, _, surface, _, store) = Make(true, 50);
        store.Update(s => s.Devices.ScheduleFollowing.Remove("mon1"));
        await worker.TickAsync(1_000, false, default);
        surface.SliderValue = 80;
        surface.Hardware = 40;

        store.Update(s => s.Devices.ScheduleFollowing.Add("mon1"));
        await worker.OnSettingsChangedAsync();

        Assert.Equal(40, surface.SliderValue);
        Assert.Empty(surface.Writes);
    }
}
