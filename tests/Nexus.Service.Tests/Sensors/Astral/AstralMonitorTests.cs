using Nexus.Service.Diagnostics;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Power;
using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralMonitorTests
{
    private const string Card = "/gpu-nvidia/0";

    private sealed class Store : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    private sealed class FakePower : ISystemPowerProvider
    {
        public List<string> ForcedFor { get; } = new();
        public bool Lock() => true;
        public bool Sleep() => true;
        public bool Shutdown() => true;
        public bool Restart() => true;
        public bool Logout() => true;
        public bool ForceShutdown(string reason) { ForcedFor.Add(reason); return true; }
    }

    // Setting Now moves the wall and monotonic clocks together; JumpWall moves the wall clock alone.
    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _wall = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public DateTimeOffset Now
        {
            get => _wall;
            set { _ticks += (value - _wall).Ticks; _wall = value; }
        }
        public Timer? Last { get; private set; }
        public void JumpWall(TimeSpan by) => _wall += by;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _wall;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => Last = new Timer();

        public sealed class Timer : ITimer
        {
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        }
    }

    private sealed class Rig
    {
        public Store Settings { get; } = new();
        public FakePower Power { get; } = new();
        public FakeTime Time { get; } = new();
        public List<DiagnosticsAlertNotice> Notices { get; } = new();
        public int Shutdowns { get; set; }
        public AstralMonitor Monitor { get; }

        public Rig()
        {
            Monitor = new AstralMonitor(Settings, Power, Time) { AlertSink = Notices.Add };
            Monitor.ShutdownSink = _ => { Shutdowns++; return true; };
        }

        public void Advance(int seconds) => Time.Now = Time.Now.AddSeconds(seconds);

        public void Feed(float pin1, bool simulated = false)
        {
            var amps = new float[] { pin1, 8f, 8f, 8f, 8f, 8f };
            Monitor.Feed(Card, "ROG Astral RTX 5090", simulated ? null : "gpu0:1", amps, simulated);
            Time.Now = Time.Now.AddSeconds(1);
        }
    }

    [Fact]
    public void State_reports_pins_worst_state_and_the_device_name()
    {
        var rig = new Rig();
        Assert.Null(rig.Monitor.CardName());
        rig.Feed(13f);
        var card = Assert.Single(rig.Monitor.GetState().Cards);
        Assert.Equal(Card, card.GpuId);
        Assert.Equal("overcurrent", card.State);
        Assert.Equal("overcurrent", card.Pins[0].State);
        Assert.Equal("ok", card.Pins[1].State);
        Assert.False(card.Level.Available);
        Assert.Equal("ROG Astral RTX 5090", rig.Monitor.CardName());
    }

    [Fact]
    public void A_card_not_read_for_a_while_drops_out()
    {
        var rig = new Rig();
        rig.Feed(8f);
        rig.Time.Now = rig.Time.Now.AddSeconds(30);
        Assert.Empty(rig.Monitor.GetState().Cards);
        Assert.Null(rig.Monitor.CardName());
    }

    [Fact]
    public void Settings_patch_changes_only_the_given_fields_and_clamps()
    {
        var rig = new Rig();
        var state = rig.Monitor.Patch(new AstralSettingsPatch { Equalizer = true, OvercurrentSeconds = 1, CountdownSeconds = 99999 });
        Assert.True(state.Settings.Equalizer);
        Assert.True(state.Settings.Alerts);
        Assert.Equal(AstralPowerDetector.MinOvercurrentSeconds, state.Settings.OvercurrentSeconds);
        Assert.Equal(AstralPowerDetector.MaxSeconds, state.Settings.CountdownSeconds);
    }

    [Fact]
    public void Alerts_switch_silences_alerts_but_not_the_countdown_notice()
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch { Alerts = false, CriticalShutdown = true });
        rig.Feed(18f);
        rig.Feed(18f);
        var notice = Assert.Single(rig.Notices);
        Assert.Equal(AstralMonitor.CountdownKind, notice.Kind);
    }

    [Fact]
    public void An_overcurrent_alert_goes_through_the_sink_once()
    {
        var rig = new Rig();
        rig.Feed(13f);
        rig.Feed(13f);
        var notice = Assert.Single(rig.Notices);
        Assert.Equal(AstralMonitor.AlertKind, notice.Kind);
    }

    [Fact]
    public void Critical_still_shuts_down_after_an_overcurrent_countdown_was_cancelled()
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch
        {
            OvercurrentShutdown = true, CriticalShutdown = true, OvercurrentSeconds = 10, CountdownSeconds = 60,
        });
        for (var i = 0; i < 12; i++) rig.Feed(13f);
        Assert.Equal("overcurrent", rig.Monitor.GetState().Countdown!.Reason);

        var cancelled = rig.Monitor.Cancel();
        Assert.Null(cancelled.Countdown);
        Assert.True(cancelled.HeldOff);
        rig.Feed(13f);
        Assert.Null(rig.Monitor.GetState().Countdown);

        rig.Feed(18f);
        rig.Feed(18f);
        var armed = rig.Monitor.GetState();
        Assert.Equal("critical", armed.Countdown!.Reason);

        rig.Time.Now = rig.Time.Now.AddSeconds(11);
        rig.Monitor.ExpireIfDue();
        Assert.Equal(1, rig.Shutdowns);
    }

    [Fact]
    public void Cancel_leaves_a_critical_countdown_running()
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch { CriticalShutdown = true });
        rig.Feed(18f);
        rig.Feed(18f);
        var before = rig.Monitor.GetState().Countdown!;
        var after = rig.Monitor.Cancel();
        Assert.Equal("critical", after.Countdown!.Reason);
        Assert.Equal(before.EndsAtMs, after.Countdown.EndsAtMs);
    }

    [Fact]
    public void A_cancel_from_an_earlier_countdown_notice_is_ignored()
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch { OvercurrentShutdown = true, OvercurrentSeconds = 10, CountdownSeconds = 60 });
        for (var i = 0; i < 13; i++) rig.Feed(13f);
        var notice = rig.Notices.Single(n => n.Kind == AstralMonitor.CountdownKind);
        Assert.NotNull(notice.ActionId);

        rig.Monitor.CancelCountdown(notice.ActionId!.Value + 1);
        Assert.NotNull(rig.Monitor.GetState().Countdown);
        Assert.Equal(notice.ActionId, rig.Monitor.CurrentCountdownNotice()!.ActionId);

        rig.Monitor.CancelCountdown(notice.ActionId.Value);
        Assert.Null(rig.Monitor.GetState().Countdown);
        Assert.Null(rig.Monitor.CurrentCountdownNotice());
    }

    [Fact]
    public void A_critical_countdown_notice_carries_no_cancel_action()
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch { CriticalShutdown = true });
        rig.Feed(18f);
        rig.Feed(18f);
        Assert.Null(Assert.Single(rig.Notices, n => n.Kind == AstralMonitor.CountdownKind).ActionId);
    }

    [Fact]
    public void ShutdownNow_shortens_a_running_countdown_and_never_starts_one()
    {
        var rig = new Rig();
        rig.Feed(13f);
        rig.Monitor.ShutdownNow();
        Assert.Equal(0, rig.Shutdowns);

        rig.Monitor.Patch(new AstralSettingsPatch { OvercurrentShutdown = true, OvercurrentSeconds = 10 });
        for (var i = 0; i < 13; i++) rig.Feed(13f);
        Assert.NotNull(rig.Monitor.GetState().Countdown);
        rig.Monitor.ShutdownNow();
        Assert.Equal(1, rig.Shutdowns);
    }

    [Fact]
    public void A_simulated_card_shutdown_is_a_no_op_and_the_countdown_resets()
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch { CriticalShutdown = true });
        rig.Feed(18f, simulated: true);
        rig.Feed(18f, simulated: true);
        Assert.True(rig.Monitor.GetState().Simulated);
        Assert.True(rig.Monitor.GetState().Supported);

        rig.Time.Now = rig.Time.Now.AddSeconds(11);
        rig.Monitor.ExpireIfDue();
        Assert.Equal(0, rig.Shutdowns);
        Assert.Empty(rig.Power.ForcedFor);
        Assert.Null(rig.Monitor.GetState().Countdown);
    }

    private static Rig OvercurrentRig(int overSeconds = 10, int countdownSeconds = 60)
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch
        {
            OvercurrentShutdown = true, OvercurrentSeconds = overSeconds, CountdownSeconds = countdownSeconds,
        });
        for (var i = 0; i <= overSeconds + 1; i++) rig.Feed(13f);
        Assert.NotNull(rig.Monitor.GetState().Countdown);
        return rig;
    }

    [Fact]
    public void Two_critical_spikes_across_a_gap_in_readings_never_arm_a_shutdown()
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch { CriticalShutdown = true });
        rig.Feed(18f);
        rig.Advance(30);
        rig.Feed(18f);
        Assert.Null(rig.Monitor.GetState().Countdown);
        Assert.DoesNotContain(rig.Notices, n => n.Kind == AstralMonitor.CountdownKind);

        rig.Feed(18f);
        Assert.NotNull(rig.Monitor.GetState().Countdown);
    }

    // The gap is five probe intervals: the fallback hand-off window plus slack.
    [Theory]
    [InlineData(3.5, true)]
    [InlineData(3.9, true)]
    [InlineData(4.1, false)]
    [InlineData(29.0, false)]
    public void Critical_readings_keep_their_streak_across_a_gap_only_up_to_the_hand_off_window(double extraSeconds, bool streakKept)
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch { CriticalShutdown = true });
        rig.Feed(18f);
        rig.Time.Now = rig.Time.Now.AddSeconds(extraSeconds);
        rig.Feed(18f);
        Assert.Equal(streakKept, rig.Monitor.GetState().Countdown is not null);
    }

    [Fact]
    public void Turning_the_equalizer_off_keeps_a_running_countdown()
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch
        {
            Equalizer = true, OvercurrentShutdown = true, OvercurrentSeconds = 10, CountdownSeconds = 60,
        });
        for (var i = 0; i < 13; i++) rig.Feed(19f);
        Assert.NotNull(rig.Monitor.GetState().Countdown);
        rig.Monitor.Patch(new AstralSettingsPatch { Equalizer = false });
        Assert.NotNull(rig.Monitor.GetState().Countdown);
    }

    [Fact]
    public void A_gap_in_readings_restarts_the_time_a_pin_has_been_over_the_limit()
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch { OvercurrentShutdown = true, OvercurrentSeconds = 10, CountdownSeconds = 60 });
        for (var i = 0; i < 8; i++) rig.Feed(13f);
        rig.Advance(30);
        for (var i = 0; i < 8; i++) rig.Feed(13f);
        Assert.Null(rig.Monitor.GetState().Countdown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Turning_the_shutdown_off_or_the_equalizer_on_ends_a_running_countdown(bool viaEqualizer)
    {
        var rig = OvercurrentRig();
        rig.Monitor.Patch(viaEqualizer
            ? new AstralSettingsPatch { Equalizer = true }
            : new AstralSettingsPatch { OvercurrentShutdown = false });
        Assert.Null(rig.Monitor.GetState().Countdown);

        rig.Advance(120);
        rig.Monitor.ExpireIfDue();
        Assert.Equal(0, rig.Shutdowns);
    }

    [Fact]
    public void An_unrelated_settings_patch_leaves_a_critical_countdown_running()
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch { CriticalShutdown = true });
        rig.Feed(18f);
        rig.Feed(18f);
        rig.Feed(8f);
        rig.Monitor.Patch(new AstralSettingsPatch { Alerts = false });
        Assert.Equal("critical", rig.Monitor.GetState().Countdown!.Reason);
    }

    [Fact]
    public void A_countdown_whose_shutdown_was_switched_off_does_not_fire_when_it_runs_out()
    {
        var rig = OvercurrentRig(countdownSeconds: 30);
        rig.Settings.Update(s => s.Astral.OvercurrentShutdown = false);
        rig.Advance(31);
        rig.Monitor.ExpireIfDue();
        Assert.Equal(0, rig.Shutdowns);
    }

    [Fact]
    public void A_stale_cards_running_countdown_is_still_reported_and_still_fires()
    {
        var rig = OvercurrentRig(countdownSeconds: 60);
        rig.Advance(20);
        var state = rig.Monitor.GetState();
        Assert.Empty(state.Cards);
        Assert.Equal("overcurrent", state.Countdown!.Reason);
        Assert.Equal(Card, state.Countdown.GpuId);

        rig.Advance(60);
        rig.Monitor.ExpireIfDue();
        Assert.Equal(1, rig.Shutdowns);
    }

    [Fact]
    public void The_countdown_reports_the_card_and_the_time_left_and_never_a_negative_time()
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch { CriticalShutdown = true });
        rig.Feed(18f);
        rig.Feed(18f);
        var wall = rig.Time.Now;
        var c = rig.Monitor.GetState().Countdown!;
        Assert.Equal(Card, c.GpuId);
        Assert.Equal(9000, c.RemainingMs);
        Assert.Equal(wall.ToUnixTimeMilliseconds() + c.RemainingMs, c.EndsAtMs);

        rig.Advance(60);
        Assert.Equal(0, rig.Monitor.GetState().Countdown!.RemainingMs);
    }

    [Fact]
    public void A_wall_clock_jump_neither_ends_a_countdown_early_nor_stretches_it()
    {
        var rig = OvercurrentRig(countdownSeconds: 60);
        rig.Time.JumpWall(TimeSpan.FromHours(1));
        rig.Monitor.ExpireIfDue();
        Assert.Equal(0, rig.Shutdowns);
        var c = rig.Monitor.GetState().Countdown!;
        Assert.InRange(c.RemainingMs, 1, 60_000);
        Assert.Equal(rig.Time.Now.ToUnixTimeMilliseconds() + c.RemainingMs, c.EndsAtMs);

        rig.Time.JumpWall(TimeSpan.FromHours(-2));
        Assert.Equal(c.RemainingMs, rig.Monitor.GetState().Countdown!.RemainingMs);

        rig.Advance(61);
        rig.Monitor.ExpireIfDue();
        Assert.Equal(1, rig.Shutdowns);
    }

    [Fact]
    public void A_wall_clock_jump_does_not_shorten_the_time_a_pin_must_stay_over_the_limit()
    {
        var rig = new Rig();
        rig.Monitor.Patch(new AstralSettingsPatch { OvercurrentShutdown = true, OvercurrentSeconds = 30, CountdownSeconds = 60 });
        for (var i = 0; i < 5; i++) rig.Feed(13f);
        rig.Time.JumpWall(TimeSpan.FromHours(1));
        rig.Feed(13f);
        Assert.Null(rig.Monitor.GetState().Countdown);
    }

    [Fact]
    public void A_shutdown_that_never_takes_effect_is_reported_and_detection_resumes()
    {
        var rig = OvercurrentRig(countdownSeconds: 30);
        rig.Advance(31);
        rig.Monitor.ExpireIfDue();
        Assert.Equal(1, rig.Shutdowns);
        Assert.Null(rig.Monitor.GetState().Countdown);

        rig.Advance(70);
        rig.Feed(13f);
        Assert.Contains(rig.Notices, n => n.Title == "ROG Astral shutdown failed");
        Assert.True(rig.Monitor.GetState().HeldOff);
    }

    [Fact]
    public void Smoothed_posture_stops_publishing_when_the_g_sensor_stops_answering()
    {
        var rig = new Rig();
        rig.Feed(8f);
        var rest = new AstralAngles(90f, 1.2f, 90f);
        for (var i = 0; i < AstralLevelSense.CalibrationSamples; i++) rig.Monitor.FeedAngles(Card, rest);
        var sensors = new List<Nexus.Service.Models.Sensors.HardwareSensor>();
        rig.Monitor.AppendLevelSensors(Card, "GPU", sensors);
        Assert.NotEmpty(sensors);

        rig.Advance(60);
        sensors.Clear();
        rig.Monitor.AppendLevelSensors(Card, "GPU", sensors);
        Assert.Empty(sensors);

        rig.Monitor.FeedAngles(Card, rest);
        rig.Monitor.AppendLevelSensors(Card, "GPU", sensors);
        Assert.NotEmpty(sensors);
    }

    [Fact]
    public void Disposing_the_monitor_disposes_its_countdown_timer_for_good()
    {
        var rig = OvercurrentRig();
        var timer = rig.Time.Last!;
        rig.Monitor.Dispose();
        Assert.True(timer.Disposed);

        var created = rig.Time.Last;
        rig.Monitor.Cancel();
        Assert.Same(created, rig.Time.Last);
    }

    [Fact]
    public void Level_calibrates_itself_persists_and_reloads_a_changed_saved_calibration()
    {
        var rig = new Rig();
        rig.Feed(8f);
        var rest = new AstralAngles(90f, 1.2f, 90f);
        for (var i = 0; i < AstralLevelSense.CalibrationSamples; i++)
        {
            Assert.True(rig.Monitor.GetState().Cards[0].Level.Calibrating || i == 0);
            rig.Monitor.FeedAngles(Card, rest);
        }
        var level = rig.Monitor.GetState().Cards[0].Level;
        Assert.True(level.Available);
        Assert.False(level.Calibrating);
        Assert.NotNull(level.CalibratedAtMs);
        Assert.Equal(rest.Y, rig.Settings.Load().AstralLevelSense["gpu0:1"].Y, 3);

        // An import replaces the saved calibration: the next check picks it up.
        rig.Settings.Update(s => s.AstralLevelSense["gpu0:1"] = new AstralCalibration { X = 80, Y = 5, Z = 90, CalibratedAtMs = 1 });
        rig.Time.Now = rig.Time.Now.AddSeconds(10);
        rig.Feed(8f);
        rig.Monitor.FeedAngles(Card, rest);
        Assert.Equal(1, rig.Monitor.GetState().Cards[0].Level.CalibratedAtMs);
    }

    [Fact]
    public void Recalibrate_marks_the_card_calibrating()
    {
        var rig = new Rig();
        rig.Feed(8f);
        rig.Monitor.FeedAngles(Card, new AstralAngles(90f, 1.2f, 90f));
        rig.Monitor.Recalibrate(null);
        Assert.True(rig.Monitor.GetState().Cards[0].Level.Calibrating);
    }

    [Fact]
    public void Level_sensors_use_the_contract_ids()
    {
        var rig = new Rig();
        rig.Feed(8f);
        var rest = new AstralAngles(90f, 1.2f, 90f);
        for (var i = 0; i < AstralLevelSense.CalibrationSamples; i++) rig.Monitor.FeedAngles(Card, rest);
        var sensors = new List<Nexus.Service.Models.Sensors.HardwareSensor>();
        rig.Monitor.AppendLevelSensors(Card, "GPU", sensors);
        Assert.Equal(
            new[] { $"{Card}/astral/level/x", $"{Card}/astral/level/y", $"{Card}/astral/level/sag", $"{Card}/astral/level/lean" },
            sensors.Select(s => s.Id));
        Assert.All(sensors, s => { Assert.Equal("Angle", s.Type); Assert.Equal("°", s.Units); });
    }
}
