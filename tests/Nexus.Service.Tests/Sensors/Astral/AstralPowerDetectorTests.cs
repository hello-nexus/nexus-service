using Nexus.Service.Persistence;
using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralPowerDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static float[] Pins(float p1, float others = 8f)
    {
        var a = new float[6];
        Array.Fill(a, others);
        a[0] = p1;
        return a;
    }

    private static AstralStep Tick(AstralPowerDetector d, float[] amps, AstralSettings s, int second) =>
        d.Update(amps, s, T0.AddSeconds(second));

    [Theory]
    [InlineData(8.0f, false, AstralPinState.Ok)]
    [InlineData(8.5f, false, AstralPinState.Caution)]
    [InlineData(9.2f, false, AstralPinState.Warning)]
    [InlineData(12.5f, false, AstralPinState.Overcurrent)]
    [InlineData(17f, false, AstralPinState.Critical)]
    [InlineData(0f, false, AstralPinState.Dead)]
    [InlineData(12.5f, true, AstralPinState.Ok)]
    [InlineData(16f, true, AstralPinState.Caution)]
    [InlineData(18f, true, AstralPinState.Overcurrent)]
    [InlineData(20f, true, AstralPinState.Critical)]
    public void Classify_follows_the_thresholds(float amps, bool equalizer, AstralPinState expected)
    {
        Assert.Equal(expected, AstralPowerDetector.Classify(amps, 8f, equalizer));
    }

    [Fact]
    public void An_idle_card_has_no_dead_pin()
    {
        Assert.Equal(AstralPinState.Ok, AstralPowerDetector.Classify(0f, 0.2f, false));
    }

    [Fact]
    public void A_hot_pin_alerts_on_the_third_reading_only_and_respects_the_cooldown()
    {
        var d = new AstralPowerDetector();
        var s = new AstralSettings();
        Assert.Equal(AstralAlertKind.None, Tick(d, Pins(9.5f), s, 0).Alert);
        Assert.Equal(AstralAlertKind.None, Tick(d, Pins(9.5f), s, 1).Alert);
        var third = Tick(d, Pins(9.5f), s, 2);
        Assert.Equal(AstralAlertKind.Warning, third.Alert);
        Assert.Equal(0, third.Pin);
        Assert.Equal(AstralAlertKind.None, Tick(d, Pins(9.5f), s, 3).Alert);

        // A new spell inside the cooldown stays quiet, after it alerts again.
        Tick(d, Pins(8f), s, 4);
        for (var i = 5; i < 8; i++) Assert.Equal(AstralAlertKind.None, Tick(d, Pins(9.5f), s, i).Alert);
        Tick(d, Pins(8f), s, 8);
        var late = AstralAlertKind.None;
        for (var i = 0; i < 3; i++) late = Tick(d, Pins(9.5f), s, 400 + i).Alert;
        Assert.Equal(AstralAlertKind.Warning, late);
    }

    [Fact]
    public void A_dead_pin_alerts_after_three_readings()
    {
        var d = new AstralPowerDetector();
        var s = new AstralSettings();
        Tick(d, Pins(0f), s, 0);
        Tick(d, Pins(0f), s, 1);
        var step = Tick(d, Pins(0f), s, 2);
        Assert.Equal(AstralAlertKind.Dead, step.Alert);
        Assert.Equal(0, step.Pin);
    }

    [Fact]
    public void Equalizer_raises_the_limits()
    {
        var d = new AstralPowerDetector();
        var s = new AstralSettings { Equalizer = true };
        Assert.Equal(AstralAlertKind.None, Tick(d, Pins(14f), s, 0).Alert);
        Assert.Equal(AstralAlertKind.Overcurrent, Tick(d, Pins(18f), s, 1).Alert);
    }

    [Fact]
    public void Critical_needs_two_readings()
    {
        var d = new AstralPowerDetector();
        var s = new AstralSettings();
        Assert.Equal(AstralAlertKind.Overcurrent, Tick(d, Pins(17.5f), s, 0).Alert);
        Assert.Equal(AstralAlertKind.Critical, Tick(d, Pins(17.5f), s, 1).Alert);
    }

    [Fact]
    public void Overcurrent_countdown_starts_after_the_set_time_and_expires()
    {
        var d = new AstralPowerDetector();
        var s = new AstralSettings { OvercurrentShutdown = true, OvercurrentSeconds = 10, CountdownSeconds = 10 };
        for (var i = 0; i < 10; i++) Assert.False(Tick(d, Pins(13f), s, i).CountdownStarted);
        Assert.True(Tick(d, Pins(13f), s, 10).CountdownStarted);
        Assert.False(d.Critical);
        Assert.Equal(T0.AddSeconds(20), d.EndsUtc);
        Assert.False(d.TakeExpired(T0.AddSeconds(19)));
        Assert.True(d.TakeExpired(T0.AddSeconds(20)));
        Assert.False(d.TakeExpired(T0.AddSeconds(21)));
    }

    [Fact]
    public void Shutdown_off_never_starts_a_countdown()
    {
        var d = new AstralPowerDetector();
        var s = new AstralSettings();
        for (var i = 0; i < 100; i++) Assert.False(Tick(d, Pins(13f), s, i).CountdownStarted);
        Assert.False(d.Running);
    }

    [Fact]
    public void Cancelling_an_overcurrent_countdown_holds_overcurrent_off_but_critical_still_arms()
    {
        var d = new AstralPowerDetector();
        var s = new AstralSettings
        {
            OvercurrentShutdown = true, CriticalShutdown = true, OvercurrentSeconds = 10, CountdownSeconds = 30,
        };
        for (var i = 0; i <= 10; i++) Tick(d, Pins(13f), s, i);
        Assert.True(d.Running);
        Assert.True(d.Cancel());
        Assert.True(d.HeldOff);
        Assert.False(d.Running);

        // Held off: the pin staying over the limit does not restart the overcurrent countdown.
        for (var i = 11; i < 25; i++) Assert.False(Tick(d, Pins(13f), s, i).CountdownStarted);

        // The same pin reaching the critical current still arms the short countdown.
        Assert.False(Tick(d, Pins(18f), s, 25).CountdownStarted);
        Assert.True(Tick(d, Pins(18f), s, 26).CountdownStarted);
        Assert.True(d.Critical);
        Assert.Equal(T0.AddSeconds(36), d.EndsUtc);
        Assert.True(d.TakeExpired(T0.AddSeconds(36)));
    }

    [Fact]
    public void A_critical_countdown_cannot_be_cancelled_and_keeps_its_end_time()
    {
        var d = new AstralPowerDetector();
        var s = new AstralSettings { CriticalShutdown = true };
        Tick(d, Pins(18f), s, 0);
        Assert.True(Tick(d, Pins(18f), s, 1).CountdownStarted);
        var ends = d.EndsUtc;
        for (var i = 2; i < 8; i++)
        {
            Assert.False(d.Cancel());
            Assert.False(Tick(d, Pins(18f), s, i).CountdownStarted);
        }
        Assert.True(d.Running);
        Assert.False(d.HeldOff);
        Assert.Equal(ends, d.EndsUtc);
    }

    [Fact]
    public void Critical_replaces_a_longer_overcurrent_countdown()
    {
        var d = new AstralPowerDetector();
        var s = new AstralSettings
        {
            OvercurrentShutdown = true, CriticalShutdown = true, OvercurrentSeconds = 10, CountdownSeconds = 300,
        };
        for (var i = 0; i <= 10; i++) Tick(d, Pins(13f), s, i);
        Tick(d, Pins(18f), s, 11);
        Assert.True(Tick(d, Pins(18f), s, 12).CountdownStarted);
        Assert.True(d.Critical);
        Assert.Equal(T0.AddSeconds(22), d.EndsUtc);
    }

    [Fact]
    public void Turning_the_shutdown_off_ends_a_running_countdown()
    {
        var d = new AstralPowerDetector();
        var s = new AstralSettings { OvercurrentShutdown = true, OvercurrentSeconds = 10 };
        for (var i = 0; i <= 10; i++) Tick(d, Pins(13f), s, i);
        s.OvercurrentShutdown = false;
        Assert.True(Tick(d, Pins(13f), s, 11).CountdownCancelled);
        Assert.False(d.Running);
    }

    [Fact]
    public void ShutdownNow_only_shortens_a_running_countdown()
    {
        var d = new AstralPowerDetector();
        Assert.False(d.ShutdownNow());
        var s = new AstralSettings { OvercurrentShutdown = true, OvercurrentSeconds = 10 };
        for (var i = 0; i <= 10; i++) Tick(d, Pins(13f), s, i);
        Assert.True(d.ShutdownNow());
        Assert.False(d.ShutdownNow());
    }

    [Fact]
    public void An_implausible_reading_is_ignored()
    {
        var d = new AstralPowerDetector();
        Assert.Equal(AstralAlertKind.None, Tick(d, Pins(80f), new AstralSettings(), 0).Alert);
    }
}
