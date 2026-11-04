using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

public class LightingSleepBlackoutTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;

    public LightingSleepBlackoutTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-sleep-blackout-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static DeviceFrame[] MakeLitDevices(int count = 2, int leds = 4)
    {
        var frames = new DeviceFrame[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = new DeviceFrame(i, $"test-{i}", leds);
            frames[i].Fill(255, 128, 64);
            frames[i].Publish();
        }
        return frames;
    }

    private static bool AllBlack(DeviceFrame frame)
    {
        foreach (var b in frame.LedBytes)
        {
            if (b != 0) return false;
        }
        return true;
    }

    [Fact]
    public void SleepBlackout_DefaultsOn()
    {
        Assert.True(_store.Load().Lighting.SleepBlackout);
    }

    [Fact]
    public void SetBlackout_WithNoEffectRunning_BlanksImmediately()
    {
        // The case the feature exists for: nothing is rendering, yet RAM is
        // still showing whatever it was last written.
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        Assert.False(AllBlack(devices[0]));

        engine.SetBlackout(true);

        Assert.True(engine.Blackout);
        Assert.True(engine.WaitForBlackout(TimeSpan.Zero));
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public async Task SetBlackout_WithEffectRunning_LoopPublishesBlack()
    {
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 5;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetEffect(new FillEffect(255, 255, 255));

        // Let the effect paint at least one lit frame first.
        await WaitUntil(() => !AllBlack(devices[0]), TimeSpan.FromSeconds(2));

        engine.SetBlackout(true);

        Assert.True(engine.WaitForBlackout(TimeSpan.FromSeconds(2)));
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public async Task ReleasingBlackout_RepaintsFromTheSameEffect()
    {
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 5;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var effect = new FillEffect(255, 255, 255);
        engine.SetEffect(effect);
        engine.SetBlackout(true);
        Assert.True(engine.WaitForBlackout(TimeSpan.FromSeconds(2)));

        engine.SetBlackout(false);

        Assert.False(engine.Blackout);
        // Same effect instance, never swapped out - the release is a resume,
        // not a restart.
        Assert.Same(effect, engine.CurrentEffect);
        Assert.True(await WaitUntil(() => !AllBlack(devices[0]), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void SetEffect_ClearsBlackout()
    {
        // The escape hatch: if a resume event never lands, the user picking a
        // mode has to give them their lighting back.
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeLitDevices());
        engine.SetBlackout(true);
        Assert.True(engine.Blackout);

        engine.SetEffect(new FillEffect(1, 2, 3));

        Assert.False(engine.Blackout);
    }

    [Fact]
    public async Task Coordinator_WithAnEffectRunning_RampsThenEndsBlack()
    {
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 10;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetEffect(new FillEffect(200, 200, 200));
        Assert.True(await WaitUntil(() => !AllBlack(devices[0]), TimeSpan.FromSeconds(2)));
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        coordinator.OnSuspending();
        sw.Stop();

        if (!SleepBlackoutCoordinator.FadeSupported)
        {
            // Off Windows this blanks in one write by design; the ramp itself is
            // covered platform-neutrally by the engine test.
            Assert.All(devices, d => Assert.True(AllBlack(d)));
            return;
        }

        // The ramp waits on the loop reaching black, and a wait only overshoots,
        // so a run shorter than the ramp never ramped.
        Assert.True(sw.Elapsed >= SleepBlackoutCoordinator.FadeDuration - TimeSpan.FromMilliseconds(60),
            $"suspend returned in {sw.ElapsedMilliseconds}ms, too fast to have ramped");
        Assert.True(engine.Blackout);
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public void Coordinator_WithNoEffectRunning_CutsWithoutRamping()
    {
        // No effect means no render loop, so nothing would paint the ramp. It
        // blanks immediately instead, which is what shipped before the fade.
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        coordinator.OnSuspending();
        sw.Stop();

        Assert.True(sw.Elapsed < SleepBlackoutCoordinator.Budget,
            $"blanked in {sw.ElapsedMilliseconds}ms, past its whole budget");
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public void Coordinator_WhenSettingOff_LeavesLightingAlone()
    {
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        _store.Update(s => s.Lighting.SleepBlackout = false);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSuspending();

        Assert.False(engine.Blackout);
        Assert.False(AllBlack(devices[0]));
    }

    [Fact]
    public void Coordinator_WhenSettingOn_BlanksOnSuspendAndRestoresOnResume()
    {
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSuspending();
        Assert.True(engine.Blackout);
        Assert.All(devices, d => Assert.True(AllBlack(d)));

        coordinator.OnResumed();
        Assert.False(engine.Blackout);
    }

    [Fact]
    public void Coordinator_OnResumed_WithoutASuspend_IsANoOp()
    {
        // Resume fires for wake sources that never suspended lighting (and the
        // route calls it when the setting is switched off); it must not disturb
        // a blackout it did not engage, nor throw.
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeLitDevices());
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnResumed();

        Assert.False(engine.Blackout);
    }

    [Fact]
    public void Coordinator_SuspendTwice_StaysBlacked()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeLitDevices());
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSuspending();
        coordinator.OnSuspending();

        Assert.True(engine.Blackout);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        // The test host disposes the DI scope and then the factory, so the
        // engine sees two Dispose calls; the second reaches Stop(), which
        // touches the blackout signal.
        var engine = new LightingEngine();
        engine.UpdateDevices(MakeLitDevices());
        engine.Dispose();

        engine.Dispose();
        engine.Stop();
    }

    [Fact]
    public void Coordinator_WhenAlreadyHeld_SkipsTheFade()
    {
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetBlackout(true);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        coordinator.OnSuspending();
        sw.Stop();

        Assert.True(sw.Elapsed < SleepBlackoutCoordinator.FadeDuration,
            $"a second suspend re-ran the fade ({sw.ElapsedMilliseconds}ms)");
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public void Coordinator_OnHostShutdown_BlanksEverything()
    {
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnHostShutdown();

        Assert.True(engine.Blackout);
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public void Coordinator_OnHostShutdown_AfterLockBlackout_StaysDark()
    {
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSessionLocked();
        coordinator.OnHostShutdown();

        Assert.True(engine.Blackout);
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public void Coordinator_OnHostShutdown_WhenSettingOff_LeavesLightingAlone()
    {
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        _store.Update(s => s.Lighting.SleepBlackout = false);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnHostShutdown();

        Assert.False(engine.Blackout);
        Assert.False(AllBlack(devices[0]));
    }

    [Fact]
    public void Coordinator_ShutdownBudget_LeavesRoomUnderTheOsShutdownTeardownCap()
    {
        Assert.True(SleepBlackoutCoordinator.Budget <= SleepBlackoutCoordinator.ShutdownBudget);
        Assert.True(
            SleepBlackoutCoordinator.ShutdownBudget + TimeSpan.FromMilliseconds(500)
                <= Nexus.Service.Lifecycle.HostShutdown.OsShutdownTeardownBudget);
        Assert.True(SleepBlackoutCoordinator.FadeDuration < SleepBlackoutCoordinator.Budget);
    }

    [Fact]
    public async Task BeginBlackoutFade_RampsDownWithoutEverBrightening()
    {
        // Sampled from outside, the way the hardware sees it: the ramp has to
        // fall the whole way. It used to be driven from a second thread on its
        // own timer, which republished the same level whenever the two clocks
        // collided, and that reads as the ramp pausing partway down.
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 10;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetEffect(new FillEffect(200, 200, 200));
        Assert.True(await WaitUntil(() => devices[0].LedBytes[0] == 200, TimeSpan.FromSeconds(2)));

        Assert.True(engine.BeginBlackoutFade(TimeSpan.FromMilliseconds(400)));
        var samples = new List<byte>();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var v = devices[0].LedBytes[0];
            if (samples.Count == 0 || samples[^1] != v) samples.Add(v);
            if (v == 0) break;
            await Task.Delay(5);
        }

        Assert.True(samples.Count >= 4, $"expected a ramp, saw {samples.Count} distinct levels");
        Assert.Equal(0, samples[^1]);
        for (var i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i] < samples[i - 1], $"level rose: {string.Join(",", samples)}");
        }
    }

    [Fact]
    public void BeginBlackoutFade_WhenAHoldIsEngaged_IsRefused()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeLitDevices());
        Assert.True(engine.BeginBlackoutFade(TimeSpan.FromMilliseconds(200)));

        Assert.False(engine.BeginBlackoutFade(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Coordinator_OnHostShutdown_StaysInsideItsBudget()
    {
        // The black frame has to land inside the shutdown budget.
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        coordinator.OnHostShutdown();
        sw.Stop();

        Assert.True(sw.Elapsed < SleepBlackoutCoordinator.ShutdownBudget,
            $"shutdown blank overran its budget at {sw.ElapsedMilliseconds}ms");
        Assert.True(engine.Blackout);
        Assert.True(engine.Blackout);
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public void DeviceArrivingMidHold_GoesStraightToBlack()
    {
        // It has no baseline to scale, so it must not be left showing whatever
        // it arrived with for the rest of the suspend.
        using var engine = new LightingEngine();
        var devices = MakeLitDevices(count: 1);
        engine.UpdateDevices(devices);
        engine.BeginBlackoutFade(TimeSpan.FromMilliseconds(400));

        var late = MakeLitDevices(count: 1)[0];
        engine.UpdateDevices(new[] { devices[0], late });
        engine.SetBlackout(true);

        Assert.True(AllBlack(late));
    }

    [Fact]
    public void LoopPublishing_TracksTheRenderLoop()
    {
        // The ramp only runs while this holds: the loop is what paints it.
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 5;
        engine.UpdateDevices(MakeLitDevices());
        Assert.False(engine.LoopPublishing);

        engine.SetEffect(new FillEffect(10, 10, 10));
        Assert.True(engine.LoopPublishing);

        engine.Stop();
        Assert.False(engine.LoopPublishing);
    }

    [Fact]
    public void LockBlackout_DefaultsOn()
    {
        Assert.True(_store.Load().Lighting.LockBlackout);
    }

    [Fact]
    public void Coordinator_OnSessionLocked_WhenSettingOff_LeavesLightingAlone()
    {
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        _store.Update(s => s.Lighting.LockBlackout = false);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSessionLocked();

        Assert.False(engine.Blackout);
        Assert.False(AllBlack(devices[0]));
    }

    [Fact]
    public void Coordinator_OnSessionLocked_WithNoEffectRunning_BlanksImmediately()
    {
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSessionLocked();

        Assert.True(engine.Blackout);
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public async Task Coordinator_OnSessionLocked_ReturnsAtOnceAndRampsToBlack()
    {
        // The opposite of the suspend path: this runs on an OS callback with
        // nothing tearing down, so it must hand the ramp to the render loop and
        // return rather than blocking for the length of it.
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 10;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetEffect(new FillEffect(200, 200, 200));
        Assert.True(await WaitUntil(() => devices[0].LedBytes[0] == 200, TimeSpan.FromSeconds(2)));
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSessionLocked();

        // Returning with the devices still lit is the proof it did not wait out the ramp.
        Assert.True(engine.Blackout);
        Assert.False(AllBlack(devices[0]));
        Assert.True(engine.WaitForBlackout(SleepBlackoutCoordinator.LockFadeDuration + TimeSpan.FromSeconds(1)));
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public async Task Coordinator_OnSessionUnlocked_RampsBackUpToTheLiveEffect()
    {
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 10;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetEffect(new FillEffect(200, 200, 200));
        Assert.True(await WaitUntil(() => devices[0].LedBytes[0] == 200, TimeSpan.FromSeconds(2)));
        var coordinator = new SleepBlackoutCoordinator(engine, _store);
        coordinator.OnSessionLocked();
        Assert.True(engine.WaitForBlackout(SleepBlackoutCoordinator.LockFadeDuration + TimeSpan.FromSeconds(1)));

        coordinator.OnSessionUnlocked();

        // Sampled the way the hardware sees it: it has to climb, and land on
        // the effect's own value rather than stopping short or stepping to it.
        var samples = new List<byte>();
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            var v = devices[0].LedBytes[0];
            if (samples.Count == 0 || samples[^1] != v) samples.Add(v);
            if (v == 200) break;
            await Task.Delay(5);
        }

        Assert.Equal(200, samples[^1]);
        Assert.True(samples.Count >= 4, $"expected a ramp, saw {samples.Count} distinct levels");
        for (var i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i] > samples[i - 1], $"level fell: {string.Join(",", samples)}");
        }
        // The loop publishes the full-level frame first and drops the hold on its next step, so the flag clears just after the last sample.
        Assert.True(await WaitUntil(() => !engine.Blackout, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Coordinator_OnSessionUnlocked_ReleasesEvenWithTheSettingOff()
    {
        // Same reason OnResumed releases unconditionally: someone who turned it
        // off while a hold was engaged must get their lighting back.
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetBlackout(true);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSessionUnlocked();

        Assert.False(engine.Blackout);
    }

    [Fact]
    public void Coordinator_OnSessionUnlocked_WithoutALock_IsANoOp()
    {
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSessionUnlocked();

        Assert.False(engine.Blackout);
        Assert.False(AllBlack(devices[0]));
    }

    [Fact]
    public void BeginBlackoutRelease_WithNoHold_IsRefused()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeLitDevices());

        Assert.False(engine.BeginBlackoutRelease(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void BeginBlackoutRelease_WithNoLoopToPaintIt_CutsBack()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeLitDevices());
        engine.SetBlackout(true);

        Assert.True(engine.BeginBlackoutRelease(TimeSpan.FromMilliseconds(200)));

        Assert.False(engine.Blackout);
        Assert.False(engine.BlackoutReleasing);
    }

    [Fact]
    public async Task BeginBlackoutFade_DuringARelease_FadesBackDown()
    {
        // Locking again while the lights are still coming up: the down ramp has
        // to re-arm from the dimmed frame, not be refused as "already held".
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 10;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetEffect(new FillEffect(200, 200, 200));
        Assert.True(await WaitUntil(() => devices[0].LedBytes[0] == 200, TimeSpan.FromSeconds(2)));
        Assert.True(engine.BeginBlackoutFade(TimeSpan.FromMilliseconds(100)));
        Assert.True(engine.WaitForBlackout(TimeSpan.FromSeconds(2)));
        Assert.True(engine.BeginBlackoutRelease(TimeSpan.FromSeconds(3)));
        Assert.True(await WaitUntil(() => devices[0].LedBytes[0] > 0, TimeSpan.FromSeconds(2)));

        Assert.True(engine.BeginBlackoutFade(TimeSpan.FromMilliseconds(200)));

        Assert.False(engine.BlackoutReleasing);
        Assert.True(engine.WaitForBlackout(TimeSpan.FromSeconds(2)));
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public void PublishScaled_DoesNotCompoundIntoTheNextFrame()
    {
        // Publish seeds the next frame from what it published, so a ramp that
        // scaled in place would re-scale every LED the next paint pass does not
        // rewrite - which walks a static device to black over a second.
        var frame = new DeviceFrame(0, "test", 1);
        frame.Fill(200, 200, 200);
        frame.Publish();

        frame.PublishScaled(0.5f);
        Assert.Equal(100, frame.LedBytes[0]);

        // Nothing repaints; the next publish must carry the ORIGINAL value.
        frame.Publish();
        Assert.Equal(200, frame.LedBytes[0]);
    }

    [Fact]
    public void Coordinator_OnResumed_WhileLocked_KeepsTheBlackout()
    {
        // Sleeping a locked machine wakes it to the lock screen. Releasing there
        // would light an unattended machine, and the later unlock would have
        // nothing left to release.
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSessionLocked();
        Assert.True(engine.Blackout);
        coordinator.OnSuspending();
        coordinator.OnResumed();

        Assert.True(engine.Blackout);
        Assert.All(devices, d => Assert.True(AllBlack(d)));

        coordinator.OnSessionUnlocked();
        Assert.False(engine.Blackout);
    }

    [Fact]
    public async Task BeginBlackoutRelease_MidFade_PicksUpFromTheCurrentLevel()
    {
        // Unlocking while the lights are still going down must not finish the
        // trip to black first.
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 10;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetEffect(new FillEffect(200, 200, 200));
        Assert.True(await WaitUntil(() => devices[0].LedBytes[0] == 200, TimeSpan.FromSeconds(2)));

        Assert.True(engine.BeginBlackoutFade(TimeSpan.FromSeconds(3)));
        Assert.True(await WaitUntil(() => devices[0].LedBytes[0] is > 0 and < 150, TimeSpan.FromSeconds(3)));

        Assert.True(engine.BeginBlackoutRelease(TimeSpan.FromMilliseconds(600)));

        var floor = 255;
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            var v = devices[0].LedBytes[0];
            if (v < floor) floor = v;
            if (v == 200) break;
            await Task.Delay(5);
        }

        Assert.Equal(200, devices[0].LedBytes[0]);
        Assert.True(floor > 0, "the release dropped the lights to black before ramping up");
    }

    [Fact]
    public async Task LockScreenInput_WhileLocked_BringsTheLightingBack()
    {
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 5;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetEffect(new FillEffect(255, 255, 255));
        Assert.True(await WaitUntil(() => !AllBlack(devices[0]), TimeSpan.FromSeconds(2)));

        // Short window so the test does not leave a live timer behind that
        // fires against the disposed engine after it returns.
        var coordinator = new SleepBlackoutCoordinator(engine, _store, lockWakeTimeout: TimeSpan.FromSeconds(5));
        coordinator.OnSessionLocked();
        Assert.True(await WaitUntil(() => AllBlack(devices[0]), TimeSpan.FromSeconds(4)));

        coordinator.OnLockScreenInput();

        Assert.True(await WaitUntil(() => !AllBlack(devices[0]), TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public async Task LockScreenInput_AfterUnlock_IsIgnored()
    {
        // An envelope in flight as the user signs in must not park an idle
        // timer that would darken the desktop they are now sitting at.
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 5;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetEffect(new FillEffect(255, 255, 255));

        var coordinator = new SleepBlackoutCoordinator(engine, _store, lockWakeTimeout: TimeSpan.FromMilliseconds(150));
        coordinator.OnSessionLocked();
        coordinator.OnSessionUnlocked();
        Assert.True(await WaitUntil(() => !AllBlack(devices[0]), TimeSpan.FromSeconds(4)));

        coordinator.OnLockScreenInput();
        await Task.Delay(500);

        Assert.False(engine.Blackout);
        Assert.False(AllBlack(devices[0]));
    }

    [Fact]
    public async Task LockScreenInput_GoesDarkAgainAfterTheIdleWindow()
    {
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 5;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetEffect(new FillEffect(255, 255, 255));

        var coordinator = new SleepBlackoutCoordinator(engine, _store, lockWakeTimeout: TimeSpan.FromMilliseconds(150));
        coordinator.OnSessionLocked();
        Assert.True(await WaitUntil(() => AllBlack(devices[0]), TimeSpan.FromSeconds(4)));

        coordinator.OnLockScreenInput();
        Assert.True(await WaitUntil(() => !AllBlack(devices[0]), TimeSpan.FromSeconds(4)));

        // No further input: the window elapses and the lock ramp runs again.
        Assert.True(await WaitUntil(() => AllBlack(devices[0]), TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void LockInputWatch_ArmsOnLockAndDisarmsOnUnlock()
    {
        using var engine = new LightingEngine();
        var seen = new List<bool>();
        var coordinator = new SleepBlackoutCoordinator(engine, _store) { LockInputWatch = seen.Add };

        coordinator.OnSessionLocked();
        coordinator.OnSessionUnlocked();

        Assert.Equal(new[] { true, false }, seen);
    }

    [Fact]
    public void LockInputWatch_NotArmedWhenLockBlackoutIsOff()
    {
        _store.Update(s => s.Lighting.LockBlackout = false);
        using var engine = new LightingEngine();
        var seen = new List<bool>();
        var coordinator = new SleepBlackoutCoordinator(engine, _store) { LockInputWatch = seen.Add };

        coordinator.OnSessionLocked();

        Assert.Empty(seen);
    }

    private static async Task<bool> WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(10);
        }
        return predicate();
    }

    private sealed class FillEffect : IEffect
    {
        private readonly byte _r, _g, _b;
        public FillEffect(byte r, byte g, byte b) { _r = r; _g = g; _b = b; }
        public string Name => "fill";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) => canvas.Fill(_r, _g, _b);
        public void Dispose() { }
    }
}
