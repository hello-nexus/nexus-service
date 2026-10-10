using Nexus.Service.Lighting;
using Nexus.Service.Lighting.IdleDim;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public sealed class IdleDimRampTests
{
    [Fact]
    public void Starts_uncapped()
    {
        var ramp = new IdleDimRamp(() => 0);

        Assert.Equal(1f, ramp.Cap());
    }

    [Fact]
    public void Ramps_linearly_between_the_endpoints()
    {
        long now = 1000;
        var ramp = new IdleDimRamp(() => now);
        ramp.RampTo(0.1f, TimeSpan.FromMilliseconds(1500));

        Assert.Equal(1f, ramp.Cap(1000));
        Assert.Equal(0.55f, ramp.Cap(1750), 3);
        Assert.Equal(0.1f, ramp.Cap(2500));
        Assert.Equal(0.1f, ramp.Cap(9000));
    }

    [Fact]
    public void A_retarget_mid_ramp_starts_from_where_the_cap_is()
    {
        long now = 0;
        var ramp = new IdleDimRamp(() => now);
        ramp.RampTo(0f, TimeSpan.FromMilliseconds(1000));
        now = 500;
        ramp.RampTo(1f, TimeSpan.FromMilliseconds(900));

        Assert.Equal(0.5f, ramp.Cap(500), 3);
        Assert.Equal(1f, ramp.Cap(1400));
    }

    [Fact]
    public void Target_is_clamped()
    {
        var ramp = new IdleDimRamp(() => 0);
        ramp.RampTo(-3f, TimeSpan.Zero);

        Assert.Equal(0f, ramp.Cap());
    }
}

public sealed class IdleDimControllerTests : IDisposable
{
    private sealed class FakeWatch : IIdleDimWatch
    {
        public readonly List<int> Input = new();
        public readonly List<bool> Display = new();
        public void SetInputWatch(int thresholdSeconds) => Input.Add(thresholdSeconds);
        public void SetDisplayWatch(bool armed) => Display.Add(armed);
    }

    private readonly TempDir _dir = new();
    private readonly TestableConfigStore _store;
    private long _now;
    private readonly IdleDimRamp _ramp;
    private readonly FakeWatch _watch = new();

    public IdleDimControllerTests()
    {
        _store = new TestableConfigStore(Path.Combine(_dir.Root, "settings.json"));
        _ramp = new IdleDimRamp(() => _now);
    }

    public void Dispose() => _dir.Dispose();

    private IdleDimController Make(bool screenOff = true)
    {
        var c = new IdleDimController(_store, _ramp, screenOff);
        c.Watch = _watch;
        c.Start();
        return c;
    }

    private void Set(bool enabled, int timeout, int level = 10) =>
        _store.Update(s => s.Lighting.IdleDim = new() { Enabled = enabled, TimeoutSeconds = timeout, Level = level });

    private float CapAfterRamps()
    {
        _now += 5000;
        return _ramp.Cap();
    }

    [Fact]
    public void Disabled_arms_nothing()
    {
        Make();

        Assert.Empty(_watch.Input);
        Assert.Empty(_watch.Display);
    }

    [Fact]
    public void Fixed_timeout_arms_the_input_watch_only()
    {
        var c = Make();
        Set(true, 300);

        Assert.Equal(new[] { 300 }, _watch.Input);
        Assert.Empty(_watch.Display);
        c.OnDisplayOff(true);
        Assert.Equal(1f, CapAfterRamps());
    }

    [Fact]
    public void Zero_timeout_arms_the_display_watch_only()
    {
        var c = Make();
        Set(true, 0);

        Assert.Empty(_watch.Input);
        Assert.Equal(new[] { true }, _watch.Display);
        c.OnInputIdle(true);
        Assert.Equal(1f, CapAfterRamps());
    }

    [Fact]
    public void Idle_engages_to_the_level_and_input_releases()
    {
        var c = Make();
        Set(true, 300, level: 25);

        c.OnInputIdle(true);
        Assert.Equal(0.25f, CapAfterRamps());

        c.OnInputIdle(false);
        Assert.Equal(1f, CapAfterRamps());
    }

    [Fact]
    public void Engage_ramps_over_the_lock_fade_and_release_over_the_unlock_fade()
    {
        var c = Make();
        Set(true, 300, level: 0);

        c.OnInputIdle(true);
        _now += 750;
        Assert.Equal(0.5f, _ramp.Cap(), 2);

        _now += 5000;
        c.OnInputIdle(false);
        _now += 450;
        Assert.Equal(0.5f, _ramp.Cap(), 2);
        _now += 450;
        Assert.Equal(1f, _ramp.Cap());
    }

    [Fact]
    public void Display_off_engages_and_on_releases()
    {
        var c = Make();
        Set(true, 0, level: 40);

        c.OnDisplayOff(true);
        Assert.Equal(0.4f, CapAfterRamps());

        c.OnDisplayOff(false);
        Assert.Equal(1f, CapAfterRamps());
    }

    [Fact]
    public void Turning_it_off_while_dimmed_releases_and_disarms()
    {
        var c = Make();
        Set(true, 300);
        c.OnInputIdle(true);
        Assert.Equal(0.1f, CapAfterRamps());

        Set(false, 300);

        Assert.Equal(1f, CapAfterRamps());
        Assert.Equal(new[] { 300, 0 }, _watch.Input);
    }

    [Fact]
    public void Changing_the_level_while_dimmed_retargets()
    {
        var c = Make();
        Set(true, 300, level: 10);
        c.OnInputIdle(true);
        CapAfterRamps();

        Set(true, 300, level: 60);

        Assert.Equal(0.6f, CapAfterRamps());
        Assert.Equal(new[] { 300 }, _watch.Input);
    }

    [Fact]
    public void Changing_the_timeout_rearms_and_clears_the_idle_state()
    {
        var c = Make();
        Set(true, 300);
        c.OnInputIdle(true);

        Set(true, 600);

        Assert.Equal(new[] { 300, 600 }, _watch.Input);
        Assert.Equal(1f, CapAfterRamps());
    }

    [Fact]
    public void Switching_between_modes_swaps_the_watches()
    {
        Make();
        Set(true, 300);
        Set(true, 0);

        Assert.Equal(new[] { 300, 0 }, _watch.Input);
        Assert.Equal(new[] { true }, _watch.Display);
    }

    [Fact]
    public void Where_there_is_no_display_source_a_stored_zero_is_ten_minutes_of_input_idle()
    {
        var c = Make(screenOff: false);
        Set(true, 0, level: 20);

        Assert.Equal(new[] { 600 }, _watch.Input);
        Assert.Empty(_watch.Display);
        c.OnInputIdle(true);
        Assert.Equal(0.2f, CapAfterRamps());
        c.OnDisplayOff(true);
        c.OnInputIdle(false);
        Assert.Equal(1f, CapAfterRamps());
    }

    [Fact]
    public void A_report_with_nothing_armed_is_ignored()
    {
        var c = Make();

        c.OnInputIdle(true);
        c.OnDisplayOff(true);

        Assert.Equal(1f, CapAfterRamps());
    }

    [Fact]
    public void Stop_releases_a_dim_and_disarms()
    {
        var c = Make();
        Set(true, 300);
        c.OnInputIdle(true);

        c.Stop();

        Assert.Equal(1f, CapAfterRamps());
        Assert.Equal(new[] { 300, 0 }, _watch.Input);
    }

    [Fact]
    public void The_frame_path_reads_the_shared_ramp_through_MasterBrightness()
    {
        var lighting = new LightingSettings { GlobalBrightness = 0.8f };
        var ramp = new IdleDimRamp(() => 0);
        ramp.RampTo(0.1f, TimeSpan.Zero);

        Assert.Equal(0.1f, MasterBrightness.Effective(lighting, TimeSpan.FromHours(1), ramp.Cap()));
    }
}

public sealed class IdleTimeSourceSelectorTests
{
    private sealed class FakeSource : IIdleTimeSource
    {
        public string Name { get; init; } = "";
        public Func<long?> Answer { get; set; } = () => null;
        public int Calls;

        public Task<long?> TryGetIdleMsAsync()
        {
            Calls++;
            return Task.FromResult(Answer());
        }
    }

    [Fact]
    public async Task Picks_the_first_source_that_answers_in_order()
    {
        var kde = new FakeSource { Name = "kde" };
        var mutter = new FakeSource { Name = "mutter", Answer = () => 4000 };
        var x11 = new FakeSource { Name = "x11", Answer = () => 9 };
        var sel = new IdleTimeSourceSelector(new[] { kde, mutter, x11 });

        Assert.Equal(4000, await sel.GetIdleMsAsync());
        Assert.Equal("mutter", sel.ActiveName);
        Assert.Equal(0, x11.Calls);
    }

    [Fact]
    public async Task Sticks_with_the_active_source()
    {
        var kde = new FakeSource { Name = "kde", Answer = () => 1 };
        var mutter = new FakeSource { Name = "mutter", Answer = () => 2 };
        var sel = new IdleTimeSourceSelector(new[] { kde, mutter });

        await sel.GetIdleMsAsync();
        await sel.GetIdleMsAsync();

        Assert.Equal(2, kde.Calls);
        Assert.Equal(0, mutter.Calls);
    }

    [Fact]
    public async Task Falls_back_when_the_active_source_stops_answering()
    {
        var kde = new FakeSource { Name = "kde", Answer = () => 1 };
        var x11 = new FakeSource { Name = "x11", Answer = () => 7 };
        var sel = new IdleTimeSourceSelector(new[] { kde, x11 });
        await sel.GetIdleMsAsync();

        kde.Answer = () => null;

        Assert.Equal(7, await sel.GetIdleMsAsync());
        Assert.Equal("x11", sel.ActiveName);
    }

    [Fact]
    public async Task A_throwing_source_counts_as_not_answering()
    {
        var kde = new FakeSource { Name = "kde", Answer = () => throw new InvalidOperationException("no bus") };
        var x11 = new FakeSource { Name = "x11", Answer = () => 5 };
        var sel = new IdleTimeSourceSelector(new[] { kde, x11 });

        Assert.Equal(5, await sel.GetIdleMsAsync());
    }

    [Fact]
    public async Task Reports_null_when_nothing_answers()
    {
        var sel = new IdleTimeSourceSelector(new[] { new FakeSource { Name = "kde" }, new FakeSource { Name = "x11" } });

        Assert.Null(await sel.GetIdleMsAsync());
        Assert.Null(sel.ActiveName);
    }

    [Fact]
    public async Task A_negative_answer_is_not_an_answer()
    {
        var sel = new IdleTimeSourceSelector(new[] { new FakeSource { Name = "kde", Answer = () => -1 } });

        Assert.Null(await sel.GetIdleMsAsync());
    }
}
