using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

public class PercentToBrightnessByteTests
{
    [Fact]
    public void Zero_maps_to_zero()
    {
        Assert.Equal(0, QSeriesPortWatcher.PercentToBrightnessByte(0));
    }

    [Fact]
    public void Full_maps_to_255()
    {
        Assert.Equal(255, QSeriesPortWatcher.PercentToBrightnessByte(100));
    }

    [Fact]
    public void Rounds_half_away_from_zero()
    {
        // 30 / 100 * 255 = 76.5, and 76 is even: this only passes under
        // AwayFromZero (77), not the default ToEven (76).
        Assert.Equal(77, QSeriesPortWatcher.PercentToBrightnessByte(30));
    }

    [Fact]
    public void Rounds_down_below_midpoint()
    {
        // 99 / 100 * 255 = 252.45
        Assert.Equal(252, QSeriesPortWatcher.PercentToBrightnessByte(99));
    }

    [Fact]
    public void Rounds_up_above_midpoint()
    {
        // 1 / 100 * 255 = 2.55
        Assert.Equal(3, QSeriesPortWatcher.PercentToBrightnessByte(1));
    }

    [Fact]
    public void Out_of_range_input_is_clamped()
    {
        Assert.Equal(0, QSeriesPortWatcher.PercentToBrightnessByte(-10));
        Assert.Equal(255, QSeriesPortWatcher.PercentToBrightnessByte(150));
    }
}

public class SessionLockKeycodeTests
{
    private const int Sleep = 223;
    private const int Wakeup = 224;

    [Fact]
    public void Lock_sleeps_the_panel()
    {
        Assert.Equal(Sleep, QSeriesPortWatcher.SessionLockKeycode(locked: true, screenOff: false));
    }

    [Fact]
    public void Unlock_wakes_the_panel()
    {
        Assert.Equal(Wakeup, QSeriesPortWatcher.SessionLockKeycode(locked: false, screenOff: false));
    }

    [Fact]
    public void Unlock_leaves_a_hand_turned_off_screen_off()
    {
        // The user turned the screen off before locking; coming back must not
        // undo that, same rule the resume path follows.
        Assert.Equal(Sleep, QSeriesPortWatcher.SessionLockKeycode(locked: false, screenOff: true));
    }
}

public class PanelBrightnessTookTests
{
    [Fact]
    public void Exact_match_took()
    {
        Assert.True(QSeriesPortWatcher.PanelBrightnessTook("132", 132));
    }

    [Fact]
    public void Trims_whitespace_and_trailing_newline()
    {
        Assert.True(QSeriesPortWatcher.PanelBrightnessTook("132\n", 132));
        Assert.True(QSeriesPortWatcher.PanelBrightnessTook("  132  ", 132));
    }

    [Fact]
    public void Mismatch_did_not_take()
    {
        Assert.False(QSeriesPortWatcher.PanelBrightnessTook("100", 132));
    }

    [Fact]
    public void Empty_output_did_not_take()
    {
        Assert.False(QSeriesPortWatcher.PanelBrightnessTook("", 132));
    }
}

public class PanelScreenPowerTookTests
{
    [Fact]
    public void Awake_output_matches_awake_expectation()
    {
        Assert.True(QSeriesPortWatcher.PanelScreenPowerTook("  mWakefulness=Awake", true));
        Assert.False(QSeriesPortWatcher.PanelScreenPowerTook("  mWakefulness=Asleep", true));
    }

    [Fact]
    public void Asleep_output_matches_asleep_expectation()
    {
        Assert.True(QSeriesPortWatcher.PanelScreenPowerTook("  mWakefulness=Asleep", false));
        Assert.False(QSeriesPortWatcher.PanelScreenPowerTook("  mWakefulness=Awake", false));
    }

    [Fact]
    public void Empty_output_did_not_take()
    {
        Assert.False(QSeriesPortWatcher.PanelScreenPowerTook("", true));
        Assert.False(QSeriesPortWatcher.PanelScreenPowerTook("", false));
    }
}

public class TickChangeSignalTests
{
    [Fact]
    public void Nothing_to_take_without_an_announcement()
    {
        Assert.False(new TickChangeSignal().Take());
    }

    [Fact]
    public void An_announcement_is_taken_once()
    {
        var signal = new TickChangeSignal();
        signal.Announce();

        Assert.True(signal.Take());
        Assert.False(signal.Take());
    }

    [Fact]
    public void An_announcement_made_mid_apply_is_taken_by_the_next_tick()
    {
        // The reported bug: screen off, then screen on tapped inside the ~1.7s
        // the first apply spends in `input keyevent`. The tick has already
        // taken the first signal, so the second must survive to the next one or
        // the panel stays dark until a re-attach.
        var signal = new TickChangeSignal();
        signal.Announce();
        Assert.True(signal.Take());

        signal.Announce();

        Assert.True(signal.Take());
    }

    [Fact]
    public void Repeated_announcements_collapse_into_one_take()
    {
        var signal = new TickChangeSignal();
        signal.Announce();
        signal.Announce();
        signal.Announce();

        Assert.True(signal.Take());
        Assert.False(signal.Take());
    }
}

public class EffectiveScreenOffTests
{
    [Fact]
    public void The_setting_drives_the_screen_when_no_lock_holds_it()
    {
        Assert.True(QSeriesPortWatcher.EffectiveScreenOff(settingScreenOff: true, sleepingForSessionLock: false));
        Assert.False(QSeriesPortWatcher.EffectiveScreenOff(settingScreenOff: false, sleepingForSessionLock: false));
    }

    [Fact]
    public void A_session_lock_holds_the_panel_asleep_whatever_the_setting_says()
    {
        Assert.True(QSeriesPortWatcher.EffectiveScreenOff(settingScreenOff: false, sleepingForSessionLock: true));
        Assert.True(QSeriesPortWatcher.EffectiveScreenOff(settingScreenOff: true, sleepingForSessionLock: true));
    }
}
