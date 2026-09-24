using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Service.Deck;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;
using static Nexus.Service.Tests.StreamDeck.DeckTestHelpers;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Covers StreamDeckConnectionWorker's session-lock sleep: OnSessionLockChanged
/// (lock ramps to black, unlock ramps back), OnLockScreenInput (input at the
/// lock screen fades in and buys an idle window that Tick re-darkens after),
/// and the brightness ramp itself, stepped by AnimateBrightnessRamps under a
/// manual clock the same way the hold-ring tests step AnimateHolds.
/// </summary>
public sealed class StreamDeckSleepWhenLockedTests : IDisposable
{
    private static readonly StreamDeckModel Mini = StreamDeckModels.ByProductId(0x0063)!;
    private const string Serial = "sim-0001";

    private readonly InMemoryConfigStore _store = new();
    private readonly FakeDeckActionExecutor _executor = new();
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly SimulatedStreamDeckSurface _simulated;
    private readonly StreamDeckConnectionWorker _worker;
    private readonly List<bool> _armCalls = new();

    public StreamDeckSleepWhenLockedTests()
    {
        _simulated = new SimulatedStreamDeckSurface(Mini, Serial);
        var presence = new HardwarePresence(new FixedUsbEnumerator());
        var gate = new DeviceControlGate(_store);
        gate.SetEnabled("streamdeck", true);
        _worker = new StreamDeckConnectionWorker(
            new FakeWorkerHidEnumerator(), presence, gate, _store, _executor, NewTestKeyRenderer(), new MultiplexHub(), new FakeSensorProvider(),
            _simulated, _clock);
        _worker.LockInputWatch = enabled => _armCalls.Add(enabled);
    }

    public void Dispose()
    {
        _worker.Dispose();
    }

    private void ConnectAt(int brightness, bool sleepWhenLocked = true, int sleepAfterSeconds = 0)
    {
        _store.Update(s => s.StreamDeck.Decks[Serial] = new PhysicalDeckSettings
        {
            Brightness = brightness,
            SleepWhenLocked = sleepWhenLocked,
            SleepAfterSeconds = sleepAfterSeconds,
        });
        _worker.Tick();
        Assert.Equal(brightness, _simulated.Brightness);
    }

    /// <summary>Runs a ramp to completion at the animation loop's frame interval, returning every level the surface saw.</summary>
    private List<int> DrainRamp(TimeSpan over)
    {
        var seen = new List<int>();
        var steps = (int)(over.TotalMilliseconds / 50) + 1;
        for (var i = 0; i < steps; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(50));
            _worker.AnimateBrightnessRamps();
            if (seen.Count == 0 || seen[^1] != _simulated.Brightness) seen.Add(_simulated.Brightness);
        }
        return seen;
    }

    [Fact]
    public void Lock_RampsToBlackAndMarksAsleep()
    {
        ConnectAt(80);

        _worker.OnSessionLockChanged(true);

        // Marked asleep at the start of the ramp (so routes and sleep-after
        // leave it alone), while the surface is still at full brightness.
        Assert.True(_worker.IsAsleep(Serial));
        Assert.Equal(80, _simulated.Brightness);
        Assert.Equal(new[] { true }, _armCalls);

        var seen = DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);
        Assert.Equal(0, _simulated.Brightness);
        // A ramp, not a cut: intermediate levels between the endpoints.
        Assert.True(seen.Count > 5, $"expected a multi-step ramp, saw {seen.Count} levels");
        Assert.True(seen[0] < 80 && seen[0] > 0);
    }

    [Fact]
    public void Unlock_RampsBackToPersistedBrightness()
    {
        ConnectAt(80);
        _worker.OnSessionLockChanged(true);
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);
        Assert.Equal(0, _simulated.Brightness);

        // Brightness changed while locked (editor) must be what comes back.
        _store.Update(s => s.StreamDeck.Decks[Serial].Brightness = 55);
        _worker.OnSessionLockChanged(false);

        Assert.False(_worker.IsAsleep(Serial));
        Assert.Equal(new[] { true, false }, _armCalls);
        var seen = DrainRamp(SleepBlackoutCoordinator.UnlockFadeDuration);
        Assert.Equal(55, _simulated.Brightness);
        Assert.True(seen.Count > 5);
    }

    [Fact]
    public void Lock_SettingOff_LeavesDeckLitAndNeverArmsThePoll()
    {
        ConnectAt(80, sleepWhenLocked: false);

        _worker.OnSessionLockChanged(true);
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);

        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep(Serial));
        Assert.Empty(_armCalls);

        // Unlock always disarms; only an arm would be wrong here.
        _worker.OnSessionLockChanged(false);
        Assert.Equal(80, _simulated.Brightness);
        Assert.DoesNotContain(true, _armCalls);
    }

    [Fact]
    public void Unlock_RestoresEvenIfSettingWasTurnedOffMidLock()
    {
        ConnectAt(80);
        _worker.OnSessionLockChanged(true);
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);
        Assert.Equal(0, _simulated.Brightness);

        _store.Update(s => s.StreamDeck.Decks[Serial].SleepWhenLocked = false);
        _worker.OnSessionLockChanged(false);
        DrainRamp(SleepBlackoutCoordinator.UnlockFadeDuration);

        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep(Serial));
    }

    [Fact]
    public void LockScreenInput_FadesInThenTickReDarkensAfterTheIdleWindow()
    {
        ConnectAt(80);
        _worker.OnSessionLockChanged(true);
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);
        Assert.Equal(0, _simulated.Brightness);

        _worker.OnLockScreenInput();
        Assert.False(_worker.IsAsleep(Serial));
        var up = DrainRamp(SleepBlackoutCoordinator.UnlockFadeDuration);
        Assert.Equal(80, _simulated.Brightness);
        Assert.True(up.Count > 5);

        // Inside the window: still up.
        _clock.Advance(SleepBlackoutCoordinator.DefaultLockWakeTimeout - TimeSpan.FromSeconds(5));
        _worker.Tick();
        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep(Serial));

        // Window elapsed with no further input: back to black on the lock ramp.
        _clock.Advance(TimeSpan.FromSeconds(6));
        _worker.Tick();
        Assert.True(_worker.IsAsleep(Serial));
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);
        Assert.Equal(0, _simulated.Brightness);
    }

    [Fact]
    public void LockScreenInput_EveryInputRefreshesTheWindow()
    {
        ConnectAt(80);
        _worker.OnSessionLockChanged(true);
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);

        _worker.OnLockScreenInput();
        DrainRamp(SleepBlackoutCoordinator.UnlockFadeDuration);
        _clock.Advance(TimeSpan.FromSeconds(20));
        _worker.OnLockScreenInput();
        _clock.Advance(TimeSpan.FromSeconds(20));
        _worker.Tick();

        // The window counts from the LAST input, not the first: still up.
        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep(Serial));
    }

    [Fact]
    public void LockScreenInput_WhileUnlocked_IsIgnored()
    {
        ConnectAt(80);
        _worker.OnLockScreenInput();
        _clock.Advance(TimeSpan.FromMinutes(1));
        _worker.Tick();
        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep(Serial));
    }

    [Fact]
    public void Unlock_DuringTheWakeWindow_CancelsTheReDarkenWithoutDipping()
    {
        ConnectAt(80);
        _worker.OnSessionLockChanged(true);
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);
        _worker.OnLockScreenInput();
        DrainRamp(SleepBlackoutCoordinator.UnlockFadeDuration);
        Assert.Equal(80, _simulated.Brightness);

        // A deck input already lit must not be restarted from black.
        _worker.OnSessionLockChanged(false);
        var seen = DrainRamp(SleepBlackoutCoordinator.UnlockFadeDuration);
        Assert.Equal(new[] { 80 }, seen);

        _clock.Advance(TimeSpan.FromMinutes(1));
        _worker.Tick();
        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep(Serial));
    }

    [Fact]
    public void LockScreenInput_WithSleepAfterConfigured_RestartsTheIdleClock()
    {
        ConnectAt(80, sleepAfterSeconds: 30);
        _clock.Advance(TimeSpan.FromSeconds(20));
        _worker.OnSessionLockChanged(true);
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);
        // Well past the idle threshold by now, which lock sleep masks.
        _clock.Advance(TimeSpan.FromSeconds(15));
        _worker.Tick();
        Assert.Equal(0, _simulated.Brightness);

        _worker.OnLockScreenInput();
        // The tick right after the wake must not cut the fade-in.
        _worker.Tick();
        DrainRamp(SleepBlackoutCoordinator.UnlockFadeDuration);
        _worker.Tick();

        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep(Serial));
    }

    [Fact]
    public void Connect_MidLock_JoinsDarkAndArmsThePoll()
    {
        _store.Update(s => s.StreamDeck.Decks[Serial] = new PhysicalDeckSettings { Brightness = 80 });
        // Locked before any deck is present: nothing to blank, nothing armed.
        _worker.OnSessionLockChanged(true);
        Assert.Empty(_armCalls);

        _worker.Tick(); // the simulated deck registers here
        Assert.Equal(0, _simulated.Brightness);
        Assert.True(_worker.IsAsleep(Serial));
        Assert.Equal(new[] { true }, _armCalls);

        _worker.OnSessionLockChanged(false);
        DrainRamp(SleepBlackoutCoordinator.UnlockFadeDuration);
        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep(Serial));
    }

    [Fact]
    public void Unlock_AfterTheDeckLeftMidLock_StillDisarmsThePoll()
    {
        ConnectAt(80);
        _worker.OnSessionLockChanged(true);
        Assert.Equal(new[] { true }, _armCalls);

        _worker.ClearSimulatedModel();
        Assert.False(_worker.HasSerialState(Serial));

        _worker.OnSessionLockChanged(false);
        Assert.Equal(new[] { true, false }, _armCalls);
    }

    [Fact]
    public void LiveBrightnessWrite_DuringFadeIn_WinsOverTheRamp()
    {
        ConnectAt(80);
        _worker.OnSessionLockChanged(true);
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);
        _worker.OnLockScreenInput();
        DrainRamp(TimeSpan.FromMilliseconds(SleepBlackoutCoordinator.UnlockFadeDuration.TotalMilliseconds / 2));

        // What the POST route does on an awake deck.
        _store.Update(s => s.StreamDeck.Decks[Serial].Brightness = 30);
        _worker.SetBrightness(Serial, 30);
        DrainRamp(SleepBlackoutCoordinator.UnlockFadeDuration);

        Assert.Equal(30, _simulated.Brightness);
    }

    [Fact]
    public async Task DeckKeyPress_WhileLockSlept_FadesInInsteadOfCutting()
    {
        var action = new DeckAction { Type = "openUrl", Url = "https://example.com" };
        _store.Update(s => s.StreamDeck.Decks[Serial] = new PhysicalDeckSettings
        {
            Brightness = 80,
            LegacyDeck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        _store.Update(s => ActivateLegacyDeck(s, Serial));
        _worker.Tick();
        _worker.OnSessionLockChanged(true);
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);
        Assert.Equal(0, _simulated.Brightness);

        _simulated.Poke(0, true);
        _worker.Tick();
        if (_worker.LastDispatchTask is not null) await _worker.LastDispatchTask;

        // The press is input at the lock screen: no immediate jump to full,
        // the ramp brings it up; the action still dispatches.
        Assert.False(_worker.IsAsleep(Serial));
        Assert.Equal(0, _simulated.Brightness);
        DrainRamp(SleepBlackoutCoordinator.UnlockFadeDuration);
        Assert.Equal(80, _simulated.Brightness);
        Assert.Single(_executor.Calls);

        // And it bought the same idle window.
        _clock.Advance(SleepBlackoutCoordinator.DefaultLockWakeTimeout + TimeSpan.FromSeconds(1));
        _worker.Tick();
        Assert.True(_worker.IsAsleep(Serial));
    }

    [Fact]
    public void WakeWindowExpiry_MidFadeIn_RampsDownFromTheCurrentLevel()
    {
        ConnectAt(80);
        _worker.OnSessionLockChanged(true);
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);
        _worker.OnLockScreenInput();
        // Half way back up.
        DrainRamp(TimeSpan.FromMilliseconds(SleepBlackoutCoordinator.UnlockFadeDuration.TotalMilliseconds / 2));
        var midway = _simulated.Brightness;
        Assert.InRange(midway, 20, 60);

        // The window expiring re-engages the fade from wherever the deck is.
        _clock.Advance(SleepBlackoutCoordinator.DefaultLockWakeTimeout + TimeSpan.FromSeconds(1));
        _worker.Tick();
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        _worker.AnimateBrightnessRamps();
        Assert.True(_simulated.Brightness <= midway, $"expected a step down from {midway}, got {_simulated.Brightness}");
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);
        Assert.Equal(0, _simulated.Brightness);
    }

    [Fact]
    public void Lock_DeckAlreadyIdleAsleep_IsRestoredOnUnlock()
    {
        ConnectAt(80, sleepAfterSeconds: 30);
        _clock.Advance(TimeSpan.FromSeconds(31));
        _worker.Tick();
        Assert.Equal(0, _simulated.Brightness);
        Assert.True(_worker.IsAsleep(Serial));

        _worker.OnSessionLockChanged(true);
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);
        Assert.Equal(0, _simulated.Brightness);

        _worker.OnSessionLockChanged(false);
        DrainRamp(SleepBlackoutCoordinator.UnlockFadeDuration);
        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep(Serial));

        // The user is back: the idle clock restarts rather than re-blanking
        // on the next tick because the pre-lock idle time is still elapsed.
        _worker.Tick();
        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep(Serial));
    }

    [Fact]
    public void Lock_BrightnessRouteIsSkippedWhileAsleep_ThenAppliesOnUnlock()
    {
        ConnectAt(80);
        _worker.OnSessionLockChanged(true);
        DrainRamp(SleepBlackoutCoordinator.LockFadeDuration);

        // What the POST route does: persist always, push only when awake.
        _store.Update(s => s.StreamDeck.Decks[Serial].Brightness = 30);
        Assert.True(_worker.IsAsleep(Serial));

        _worker.OnSessionLockChanged(false);
        DrainRamp(SleepBlackoutCoordinator.UnlockFadeDuration);
        Assert.Equal(30, _simulated.Brightness);
    }
}
