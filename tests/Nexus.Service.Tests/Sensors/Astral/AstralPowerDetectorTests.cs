using System.Linq;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors.Astral;
using Level = Nexus.Service.Sensors.Astral.AstralPowerDetector.Level;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralPowerDetectorTests
{
    // One reading a second of the worst pin (pin 3 here), as the service feeds it.
    private sealed class Harness
    {
        public AstralPowerDetector Detector { get; } = new();
        public AstralPowerDetectorSettings Settings { get; } = new();
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);
        public List<AstralPowerDetector.Step> Steps { get; } = new();

        public AstralPowerDetector.Step Feed(float? amps)
        {
            var reading = amps is { } a ? new AstralPowerDetector.Reading("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", 3, a) : (AstralPowerDetector.Reading?)null;
            var step = Detector.Update(reading, Settings, Now);
            Steps.Add(step);
            Now += TimeSpan.FromSeconds(1);
            return step;
        }

        public void Feed(float? amps, int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                Feed(amps);
            }
        }

        // Pin 4 carries nothing while pin 3 carries `amps`.
        public void FeedDead(float amps, int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                var worst = new AstralPowerDetector.Reading("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", 3, amps);
                var dead = new AstralPowerDetector.Reading("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", 4, 0f);
                Steps.Add(Detector.Update(worst, dead, Settings, Now));
                Now += TimeSpan.FromSeconds(1);
            }
        }

        public List<AstralPowerDetector.Alert> Alerts => Steps.Where(s => s.Alert is not null).Select(s => s.Alert!.Value).ToList();
        public int Shutdowns => Steps.Count(s => s.Shutdown);
        public List<AstralPowerDetector.Countdown> Countdowns => Steps.Where(s => s.CountdownStarted is not null).Select(s => s.CountdownStarted!.Value).ToList();
    }

    [Fact]
    public void Normal_current_raises_nothing()
    {
        var h = new Harness();
        h.Settings.OvercurrentShutdown = true;
        h.Settings.CriticalShutdown = true;

        h.Feed(8.5f, 120);

        Assert.Empty(h.Alerts);
        Assert.Empty(h.Countdowns);
        Assert.Equal(Level.Normal, h.Detector.Current);
        Assert.Equal(8.5f, h.Detector.Latest!.Value.Amps);
    }

    [Fact]
    public void Overcurrent_alerts_once_and_never_counts_down_with_its_shutdown_off()
    {
        var h = new Harness();

        h.Feed(13f, 300);

        var alert = Assert.Single(h.Alerts);
        Assert.Equal(Level.Overcurrent, alert.Level);
        Assert.Equal(3, alert.Reading.Pin);
        Assert.Equal(AstralPowerDetector.OvercurrentAmps, alert.LimitAmps);
        Assert.Empty(h.Countdowns);
        Assert.Equal(0, h.Shutdowns);
    }

    [Fact]
    public void Overcurrent_shutdown_counts_down_after_the_chosen_time_then_shuts_down()
    {
        var h = new Harness();
        h.Settings.OvercurrentShutdown = true;
        h.Settings.OvercurrentSeconds = 60;
        h.Settings.CountdownSeconds = 60;
        var start = h.Now;

        h.Feed(13f, 60);
        Assert.Empty(h.Countdowns);

        h.Feed(13f);
        var countdown = Assert.Single(h.Countdowns);
        Assert.Equal(Level.Overcurrent, countdown.Reason);
        Assert.Equal(start + TimeSpan.FromSeconds(120), countdown.EndsUtc);

        h.Feed(13f, 59);
        Assert.Equal(0, h.Shutdowns);
        h.Feed(13f);
        Assert.Equal(1, h.Shutdowns);
        Assert.True(h.Detector.ShutdownIssued);

        h.Feed(13f, 10);
        Assert.Equal(1, h.Shutdowns);
    }

    [Fact]
    public void A_countdown_runs_on_when_the_current_drops()
    {
        var h = new Harness();
        h.Settings.OvercurrentShutdown = true;
        h.Settings.OvercurrentSeconds = 10;
        h.Settings.CountdownSeconds = 30;

        h.Feed(13f, 11);
        Assert.Single(h.Countdowns);
        h.Feed(5f, 30);

        Assert.Equal(1, h.Shutdowns);
    }

    [Fact]
    public void Cancel_stops_the_countdown_and_holds_off_until_the_pins_are_back_to_normal()
    {
        var h = new Harness();
        h.Settings.OvercurrentShutdown = true;
        h.Settings.OvercurrentSeconds = 10;
        h.Settings.CountdownSeconds = 30;
        h.Feed(13f, 11);

        Assert.True(h.Detector.Cancel());
        Assert.Null(h.Detector.Running);
        Assert.True(h.Detector.HeldOff);

        h.Feed(13f, 200);
        // Under the limit but within the hysteresis: still held off.
        h.Feed(12.2f, 5);
        Assert.Single(h.Countdowns);
        Assert.Equal(0, h.Shutdowns);
        Assert.True(h.Detector.HeldOff);

        h.Feed(11f);
        Assert.False(h.Detector.HeldOff);
        h.Feed(13f, 11);
        Assert.Equal(2, h.Countdowns.Count);
    }

    [Fact]
    public void A_single_critical_reading_is_only_an_overcurrent()
    {
        var h = new Harness();
        h.Settings.CriticalShutdown = true;

        h.Feed(18f);
        h.Feed(9f, 20);

        var alert = Assert.Single(h.Alerts);
        Assert.Equal(Level.Overcurrent, alert.Level);
        Assert.Empty(h.Countdowns);
    }

    [Fact]
    public void Critical_current_shuts_down_after_ten_seconds()
    {
        var h = new Harness();
        h.Settings.CriticalShutdown = true;

        h.Feed(18f);
        var second = h.Feed(18f);

        Assert.Equal(Level.Critical, second.Alert!.Value.Level);
        Assert.Equal(AstralPowerDetector.CriticalAmps, second.Alert!.Value.LimitAmps);
        var countdown = second.CountdownStarted!.Value;
        Assert.Equal(Level.Critical, countdown.Reason);
        Assert.Equal(TimeSpan.FromSeconds(10), countdown.EndsUtc - countdown.StartedUtc);

        h.Feed(9f, 9);
        Assert.Equal(0, h.Shutdowns);
        h.Feed(9f);
        Assert.Equal(1, h.Shutdowns);
    }

    [Fact]
    public void Critical_cuts_short_a_longer_overcurrent_countdown()
    {
        var h = new Harness();
        h.Settings.OvercurrentShutdown = true;
        h.Settings.OvercurrentSeconds = 10;
        h.Settings.CountdownSeconds = 300;
        h.Settings.CriticalShutdown = true;
        h.Feed(13f, 11);
        Assert.Equal(Level.Overcurrent, h.Detector.Running!.Value.Reason);

        h.Feed(18f, 2);

        Assert.Equal(Level.Critical, h.Detector.Running!.Value.Reason);
        h.Feed(18f, 9);
        Assert.Equal(0, h.Shutdowns);
        h.Feed(18f);
        Assert.Equal(1, h.Shutdowns);
    }

    [Fact]
    public void Critical_without_its_shutdown_only_alerts()
    {
        var h = new Harness();

        h.Feed(18f, 60);

        Assert.Equal(new[] { Level.Overcurrent, Level.Critical }, h.Alerts.Select(a => a.Level));
        Assert.Empty(h.Countdowns);
    }

    [Fact]
    public void The_equalizer_raises_every_limit()
    {
        var h = new Harness();
        h.Settings.Equalizer = true;
        h.Settings.CriticalShutdown = true;

        h.Feed(16.5f, 30);
        Assert.Empty(h.Alerts);

        h.Feed(17.5f, AstralPowerDetector.WarningReadings);
        h.Feed(19f);
        h.Feed(20.5f, 2);

        Assert.Equal(new[] { Level.Warning, Level.Overcurrent, Level.Critical }, h.Alerts.Select(a => a.Level));
        Assert.Equal(
            new[] { AstralPowerDetector.EqualizerPinWarnAmps, AstralPowerDetector.EqualizerOvercurrentAmps, AstralPowerDetector.EqualizerCriticalAmps },
            h.Alerts.Select(a => a.LimitAmps));
    }

    [Fact]
    public void A_pin_over_its_rating_alerts_once_it_lasts()
    {
        var h = new Harness();

        h.Feed(9.6f, AstralPowerDetector.WarningReadings - 1);
        Assert.Empty(h.Alerts);
        h.Feed(9.6f, 120);

        var alert = Assert.Single(h.Alerts);
        Assert.Equal(Level.Warning, alert.Level);
        Assert.False(alert.Dead);
        Assert.Equal(AstralPowerDetector.PinWarnAmps, alert.LimitAmps);
        Assert.Equal(Level.Normal, h.Detector.Current);
        Assert.Empty(h.Countdowns);
    }

    [Fact]
    public void A_short_spike_over_the_rating_does_not_alert()
    {
        var h = new Harness();

        for (var i = 0; i < 20; i++)
        {
            h.Feed(9.6f, AstralPowerDetector.WarningReadings - 1);
            h.Feed(7f);
        }

        Assert.Empty(h.Alerts);
    }

    [Fact]
    public void The_caution_step_never_alerts()
    {
        var h = new Harness();

        h.Feed(AstralPowerDetector.PinCautionAmps + 0.4f, 600);

        Assert.Empty(h.Alerts);
    }

    [Fact]
    public void A_dead_pin_alerts_once_it_lasts()
    {
        var h = new Harness();

        h.FeedDead(7f, AstralPowerDetector.WarningReadings - 1);
        Assert.Empty(h.Alerts);
        h.FeedDead(7f, 60);

        var alert = Assert.Single(h.Alerts);
        Assert.Equal(Level.Warning, alert.Level);
        Assert.True(alert.Dead);
        Assert.Equal(4, alert.Reading.Pin);
    }

    [Fact]
    public void A_dead_pin_still_alerts_after_an_overcurrent()
    {
        var h = new Harness();

        h.FeedDead(13f, 30);

        Assert.Equal(new[] { Level.Overcurrent, Level.Warning }, h.Alerts.Select(a => a.Level));
        Assert.True(h.Alerts[1].Dead);
    }

    [Fact]
    public void A_dead_pin_alert_that_meets_another_comes_a_second_later()
    {
        var h = new Harness();

        // The pin dies two readings before the overcurrent starts: its third reading is the overcurrent's first.
        h.FeedDead(7f, AstralPowerDetector.WarningReadings - 1);
        h.FeedDead(13f, 5);

        Assert.Equal(new[] { Level.Overcurrent, Level.Warning }, h.Alerts.Select(a => a.Level));
        Assert.True(h.Alerts[1].Dead);
    }

    [Fact]
    public void Readings_gone_missing_start_the_overcurrent_time_over()
    {
        var h = new Harness();
        h.Settings.OvercurrentShutdown = true;
        h.Settings.OvercurrentSeconds = 60;

        h.Feed(13f, 30);
        h.Feed(null, 5);
        Assert.Equal(Level.Normal, h.Detector.Current);
        Assert.Null(h.Detector.Latest);

        h.Feed(13f, 60);
        Assert.Empty(h.Countdowns);
        h.Feed(13f);
        Assert.Single(h.Countdowns);
    }

    [Fact]
    public void A_missing_reading_breaks_the_critical_streak()
    {
        var h = new Harness();
        h.Settings.CriticalShutdown = true;

        h.Feed(18f);
        h.Feed(null);
        h.Feed(18f);

        Assert.DoesNotContain(h.Alerts, a => a.Level == Level.Critical);
        Assert.Empty(h.Countdowns);
    }

    [Fact]
    public void Turning_a_shutdown_off_stops_its_countdown()
    {
        var h = new Harness();
        h.Settings.OvercurrentShutdown = true;
        h.Settings.OvercurrentSeconds = 10;
        h.Feed(13f, 11);

        h.Settings.OvercurrentShutdown = false;
        var step = h.Feed(13f);

        Assert.True(step.CountdownCancelled);
        Assert.Null(h.Detector.Running);
        h.Feed(13f, 120);
        Assert.Equal(0, h.Shutdowns);
    }

    [Fact]
    public void The_same_level_alerts_again_only_after_the_cooldown()
    {
        var h = new Harness();

        h.Feed(13f);
        h.Feed(9f, 10);
        h.Feed(13f);
        Assert.Single(h.Alerts);

        h.Feed(9f, (int)AstralPowerDetector.AlertCooldown.TotalSeconds);
        h.Feed(13f);
        Assert.Equal(2, h.Alerts.Count);
    }

    [Fact]
    public void Shutdown_now_only_cuts_a_running_countdown_short()
    {
        var h = new Harness();
        h.Settings.CriticalShutdown = true;
        Assert.False(h.Detector.ShutdownNow());

        h.Feed(18f, 2);
        Assert.True(h.Detector.ShutdownNow());
        Assert.True(h.Detector.ShutdownIssued);

        Assert.False(h.Detector.ShutdownNow());
        Assert.False(h.Detector.Cancel());
        h.Feed(18f, 20);
        Assert.Equal(0, h.Shutdowns);
    }

    [Fact]
    public void A_failed_shutdown_ends_the_countdown_and_holds_off()
    {
        var h = new Harness();
        h.Settings.CriticalShutdown = true;
        h.Feed(18f, 12);
        Assert.Equal(1, h.Shutdowns);

        h.Detector.ShutdownFailed();

        Assert.False(h.Detector.ShutdownIssued);
        Assert.Null(h.Detector.Running);
        Assert.True(h.Detector.HeldOff);
        h.Feed(18f, 30);
        Assert.Equal(1, h.Shutdowns);
    }

    [Fact]
    public void Chosen_times_are_kept_within_bounds()
    {
        var settings = new AstralPowerDetectorSettings { OvercurrentSeconds = 0, CountdownSeconds = 1_000_000 };

        Assert.Equal(TimeSpan.FromSeconds(AstralPowerDetector.MinOvercurrentSeconds), AstralPowerDetector.OvercurrentTime(settings));
        Assert.Equal(TimeSpan.FromSeconds(AstralPowerDetector.MaxSeconds), AstralPowerDetector.CountdownTime(settings));
    }
}
