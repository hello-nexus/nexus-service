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
        public bool Present { get; set; } = true;
        public List<int> Writes { get; } = new();
        public double RatePerSecond => 0.05;
        public long MinWriteGapMs { get; set; } = 4000;
        public IReadOnlyList<string> PresentKeys() => Present ? new[] { "mon1" } : Array.Empty<string>();
        public int? Slider(string key) => SliderValue;
        public void RememberSlider(string key, int percent) => SliderValue = percent;
        public Task<int?> ReadHardwareAsync(string key, CancellationToken ct) => Task.FromResult(Hardware);
        public Task<bool> WriteAsync(string key, int level, CancellationToken ct)
        {
            Writes.Add(level);
            return Task.FromResult(true);
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
        var state = new ScheduleFollowState(store, () => Noon);
        var surface = new FakeSurface();
        var y70 = new FakeY70();
        return (new ScheduleFollowWorker(state, store, y70, new[] { surface }), state, surface, y70, store);
    }

    [Fact]
    public async Task Schedule_off_writes_nothing()
    {
        var (worker, _, surface, y70, _) = Make(false, 50);

        await worker.TickAsync(1, 10_000, default);

        Assert.Empty(surface.Writes);
        Assert.Equal(0, y70.Reapplied);
    }

    [Fact]
    public async Task A_surface_that_is_already_dimmed_at_start_is_written_once_at_the_scaled_slider()
    {
        var (worker, _, surface, y70, _) = Make(true, 50);

        await worker.TickAsync(1, 10_000, default);
        await worker.TickAsync(1, 11_000, default);

        Assert.Equal(new[] { 40 }, surface.Writes);
        Assert.Equal(1, y70.Reapplied);
    }

    [Fact]
    public async Task Writes_are_spaced_and_each_one_is_a_new_whole_percent()
    {
        var (worker, state, surface, _, store) = Make(false, 50);
        await worker.TickAsync(1, 0, default);
        state.Advance(0);
        store.Update(s => s.Lighting.BrightnessSchedule.Enabled = true);

        long now = 10_000;
        for (var i = 0; i < 12; i++)
        {
            await worker.TickAsync(1, now, default);
            now += 1000;
        }

        Assert.True(surface.Writes.Count >= 2);
        Assert.Equal(surface.Writes.Distinct().Count(), surface.Writes.Count);
        Assert.True(surface.Writes.Count <= 12 / 4 + 1, "no more than one write per gap");
        Assert.True(surface.Writes.SequenceEqual(surface.Writes.OrderByDescending(x => x)), "steps down monotonically");
    }

    [Fact]
    public async Task Turning_the_schedule_off_restores_the_slider_and_stops_writing()
    {
        var (worker, _, surface, _, store) = Make(true, 50);
        await worker.TickAsync(1, 10_000, default);
        await worker.TickAsync(1, 11_000, default);
        store.Update(s => s.Lighting.BrightnessSchedule.Enabled = false);

        long now = 20_000;
        for (var i = 0; i < 20; i++)
        {
            await worker.TickAsync(1, now, default);
            now += 4000;
        }
        var writes = surface.Writes.Count;
        for (var i = 0; i < 5; i++)
        {
            await worker.TickAsync(1, now, default);
            now += 4000;
        }

        Assert.Equal(80, surface.Writes[^1]);
        Assert.Equal(writes, surface.Writes.Count);
        Assert.Equal(80, surface.SliderValue);
    }

    [Fact]
    public async Task An_unfollowed_surface_is_left_alone()
    {
        var (worker, _, surface, _, store) = Make(true, 50);
        store.Update(s => s.Devices.ScheduleUnfollowed.Add("mon1"));

        await worker.TickAsync(1, 10_000, default);
        await worker.TickAsync(1, 20_000, default);

        Assert.Empty(surface.Writes);
    }

    [Fact]
    public async Task A_surface_with_no_slider_and_no_readable_hardware_is_skipped()
    {
        var (worker, _, surface, _, _) = Make(true, 50);
        surface.SliderValue = null;

        await worker.TickAsync(1, 10_000, default);

        Assert.Empty(surface.Writes);
    }

    [Fact]
    public async Task An_unknown_slider_is_taken_from_the_hardware_and_remembered()
    {
        var (worker, _, surface, _, _) = Make(true, 50);
        surface.SliderValue = null;
        surface.Hardware = 60;

        await worker.TickAsync(1, 10_000, default);
        await worker.TickAsync(1, 11_000, default);

        Assert.Equal(60, surface.SliderValue);
        Assert.Equal(new[] { 30 }, surface.Writes);
    }

    [Fact]
    public async Task The_y70_is_not_reapplied_while_disconnected()
    {
        var (worker, _, _, y70, _) = Make(true, 50);
        y70.Connected = false;

        await worker.TickAsync(1, 10_000, default);

        Assert.Equal(0, y70.Reapplied);
    }
}
