using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Nexus.Service.Tests.Lighting.CustomEffects;

namespace Nexus.Service.Tests.Lighting;

/// <summary>Studio live is an engine overlay: real engine and provider, an uninitialised GPU, fake effects where a distinct colour is needed.</summary>
public sealed class StudioLiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-studio-live-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly JsonConfigStore _config;
    private readonly LightingEngine _engine;
    private readonly GpuContext _gpu;
    private readonly LightingProvider _provider;
    private long _now = 1_000;

    public StudioLiveTests()
    {
        Directory.CreateDirectory(_dir);
        _config = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        _engine = new LightingEngine { FrameIntervalMs = 10 };
        _gpu = new GpuContext(160, 90);
        _provider = new LightingProvider(_config, _engine, new LightingOutputHub(), _gpu, new MediaLibrary(),
            new Nexus.Service.Platform.DefaultMonitorEnumerator());
        _provider.StudioClockMs = () => _now;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _gpu.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeEffect(string name, byte r = 0, byte g = 0, byte b = 0, bool ramp = false) : IEffect
    {
        private int _renders;
        public string Name { get; } = name;
        public int Renders => Volatile.Read(ref _renders);
        public bool Disposed { get; private set; }

        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (var y = 0; y < canvas.Height; y++)
            {
                for (var x = 0; x < canvas.Width; x++)
                {
                    // A ramp reads the position: red is x, so a device's own UVs decide what each LED shows.
                    canvas.SetPixel(x, y, ramp ? (byte)(255 * x / (canvas.Width - 1)) : r, g, b);
                }
            }
            Interlocked.Increment(ref _renders);
        }

        public void Dispose() => Disposed = true;
    }

    // Records the tick of each render, for asserting which frame a repaint shows.
    private sealed class ClockEffect : IEffect
    {
        private long _lastTick;
        private int _renders;
        public string Name => "clock";
        public int Renders => Volatile.Read(ref _renders);
        public long LastTick => Interlocked.Read(ref _lastTick);

        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            Interlocked.Exchange(ref _lastTick, (long)tickMs);
            Interlocked.Increment(ref _renders);
        }

        public void Dispose() { }
    }

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
        }
    }

    private static StudioLiveBody Body(string? frag = null, int speed = 50, float hue = 0f, string? sessionId = null) => new()
    {
        SessionId = sessionId,
        Frag = frag ?? CustomFrags.Frag(),
        Speed = speed,
        Hue = hue,
    };

    private string SettingsJson()
    {
        _config.FlushNow();
        return File.ReadAllText(_config.SettingsPath);
    }

    private static AnimateHeadlessStart Animate(string effect) => new()
    {
        Effect = effect,
        Speed = 50,
        Intensity = 1f,
        Saturation = 1f,
        Contrast = 1f,
        Persist = true,
    };

    private static DeviceFrame Device(string id, int leds, float[]? u = null, float[]? v = null) =>
        new(0, id, leds) { LedU = u, LedV = v };

    // Waits for a published frame whose first LED has the given bytes.
    private static void WaitForLed(DeviceFrame dev, int led, byte r, byte g, byte b)
    {
        WaitFor(() =>
        {
            var bytes = dev.LedBytes;
            return bytes.Length >= (led + 1) * 3 && bytes[led * 3] == r && bytes[led * 3 + 1] == g && bytes[led * 3 + 2] == b;
        });
    }

    // Engine-level overlay tests -------------------------------------------------

    [Fact]
    public void The_overlay_outranks_the_global_effect()
    {
        var dev = Device("d", 1);
        _engine.UpdateDevices([dev]);
        _engine.SetEffect(new FakeEffect("global", r: 200));
        WaitForLed(dev, 0, 200, 0, 0);

        _engine.SetStudioOverlay(new FakeEffect("studio", g: 90));

        WaitForLed(dev, 0, 0, 90, 0);
        Assert.Equal(90, dev.LedBytes[1]);
        Assert.Equal(0, dev.LedBytes[0]);
    }

    [Fact]
    public void The_overlay_outranks_a_static_pin_and_an_animate_pin()
    {
        var tracker = new DevicePinTracker();
        tracker.Set("static-dev", new DevicePin { Kind = DevicePinKinds.Static, Effect = "fill" });
        tracker.Set("animate-dev", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "rainbow" });
        _engine.Pins = tracker;
        _engine.StaticPinEffectFactory = _ => new FakeEffect("pinned-static", r: 255);
        _engine.PinEffectFactory = (_, existing) => existing ?? new FakeEffect("pinned-animate", b: 255);
        var staticDev = Device("static-dev", 1);
        var animateDev = new DeviceFrame(1, "animate-dev", 1);
        _engine.UpdateDevices([staticDev, animateDev]);
        _engine.SetEffect(new FakeEffect("global", g: 255));
        WaitForLed(staticDev, 0, 255, 0, 0);
        WaitForLed(animateDev, 0, 0, 0, 255);

        _engine.SetStudioOverlay(new FakeEffect("studio", r: 10, g: 20, b: 30));

        WaitForLed(staticDev, 0, 10, 20, 30);
        WaitForLed(animateDev, 0, 10, 20, 30);
        Assert.Equal(new byte[] { 10, 20, 30 }, staticDev.LedBytes[..3].ToArray());
        Assert.Equal(new byte[] { 10, 20, 30 }, animateDev.LedBytes[..3].ToArray());
    }

    [Fact]
    public void Each_device_shows_the_overlay_at_its_own_uvs()
    {
        var spread = Device("spread", 2, u: [0f, 1f], v: [0.5f, 0.5f]);
        var middle = new DeviceFrame(1, "middle", 2) { LedU = [0.5f, 0.5f], LedV = [0.5f, 0.5f] };
        var noUvs = new DeviceFrame(2, "none", 3);
        _engine.UpdateDevices([spread, middle, noUvs]);
        _engine.SetEffect(new FakeEffect("global", b: 255));

        _engine.SetStudioOverlay(new FakeEffect("studio", ramp: true));

        WaitForLed(spread, 1, 255, 0, 0);
        Assert.Equal(0, spread.LedBytes[0]);
        Assert.Equal(255, spread.LedBytes[3]);
        // 255 * 80 / 159 for the middle column of the 160 wide canvas.
        Assert.InRange(middle.LedBytes[0], 125, 130);
        // Without UVs the LEDs spread along the canvas in index order.
        Assert.Equal(0, noUvs.LedBytes[0]);
        Assert.Equal(255, noUvs.LedBytes[6]);
    }

    [Fact]
    public void The_overlay_keeps_animating_while_the_effect_is_frozen_and_while_it_is_paused()
    {
        var dev = Device("d", 1);
        _engine.UpdateDevices([dev]);
        var overlay = new FakeEffect("studio", g: 50);
        _engine.SetEffect(new FakeEffect("global", r: 200));
        _engine.SetStudioOverlay(overlay);

        _engine.SetFrozen(true);
        var frozenFrom = overlay.Renders;
        WaitFor(() => overlay.Renders > frozenFrom + 3);
        Assert.True(overlay.Renders > frozenFrom + 3);

        _engine.SetPaused(true);
        var pausedFrom = overlay.Renders;
        WaitFor(() => overlay.Renders > pausedFrom + 3);
        Assert.True(overlay.Renders > pausedFrom + 3);
        WaitForLed(dev, 0, 0, 50, 0);
        Assert.Equal(50, dev.LedBytes[1]);
    }

    [Fact]
    public void A_blackout_hold_wins_over_the_overlay()
    {
        var dev = Device("d", 1);
        _engine.UpdateDevices([dev]);
        _engine.SetEffect(new FakeEffect("global", r: 200));
        _engine.SetStudioOverlay(new FakeEffect("studio", g: 90));
        WaitForLed(dev, 0, 0, 90, 0);

        _engine.SetBlackout(true);

        WaitForLed(dev, 0, 0, 0, 0);
        Assert.Equal(new byte[] { 0, 0, 0 }, dev.LedBytes[..3].ToArray());
    }

    [Fact]
    public void The_mappers_test_pattern_and_highlight_stay_above_the_overlay()
    {
        var patterned = Device("patterned", 1);
        patterned.TestPattern = "none";
        var highlighted = new DeviceFrame(1, "highlighted", 2) { HighlightLeds = [0] };
        var plain = new DeviceFrame(2, "plain", 1);
        _engine.UpdateDevices([patterned, highlighted, plain]);
        _engine.SetEffect(new FakeEffect("global", r: 200));

        _engine.SetStudioOverlay(new FakeEffect("studio", g: 90));

        WaitForLed(plain, 0, 0, 90, 0);
        Assert.Equal(new byte[] { 0, 0, 0 }, patterned.LedBytes[..3].ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 0, 0, 0 }, highlighted.LedBytes[..6].ToArray());
    }

    [Fact]
    public void Clearing_the_overlay_returns_the_devices_to_the_global_effect()
    {
        var dev = Device("d", 1);
        _engine.UpdateDevices([dev]);
        _engine.SetEffect(new FakeEffect("global", r: 200));
        _engine.SetStudioOverlay(new FakeEffect("studio", g: 90));
        WaitForLed(dev, 0, 0, 90, 0);

        _engine.SetStudioOverlay(null);

        WaitForLed(dev, 0, 200, 0, 0);
        Assert.Equal(200, dev.LedBytes[0]);
    }

    // Session tests -------------------------------------------------------------

    [Fact]
    public void A_session_sets_the_overlay_and_leaves_the_running_effect_and_settings_alone()
    {
        _provider.StartAnimate(Animate("plasma"));
        var before = _engine.CurrentEffect;
        var json = SettingsJson();

        var open = _provider.StartStudioLive(Body());
        _provider.StartStudioLive(Body(CustomFrags.Frag(0.9f), sessionId: open.SessionId));
        var during = SettingsJson();
        Assert.True(_engine.StudioOverlayActive);
        Assert.Same(before, _engine.CurrentEffect);
        Assert.Equal("plasma", _provider.GetSync());
        Assert.False(_engine.FullFrameSampling);
        _provider.StopStudioLive();

        Assert.False(_engine.StudioOverlayActive);
        Assert.Same(before, _engine.CurrentEffect);
        Assert.Equal(json, during);
        Assert.Equal(json, SettingsJson());
    }

    [Fact]
    public void Static_mode_and_pins_are_left_untouched()
    {
        _provider.StartStatic(new StaticHeadlessStart { Effect = StaticEffectCatalog.DefaultEffect, Persist = true, Saturation = 1f, Contrast = 1f });
        var before = _engine.CurrentEffect;
        Assert.True(_engine.Frozen);

        _provider.StartStudioLive(Body());
        Assert.True(_engine.Frozen);
        _provider.StopStudioLive();

        Assert.Same(before, _engine.CurrentEffect);
        Assert.True(_engine.Frozen);
    }

    [Fact]
    public void The_opening_post_returns_an_id_and_a_renewal_with_it_keeps_the_session()
    {
        var open = _provider.StartStudioLive(Body());
        Assert.False(string.IsNullOrEmpty(open.SessionId));

        var renew = _provider.StartStudioLive(Body(sessionId: open.SessionId));

        Assert.True(renew.Ok);
        Assert.Equal(open.SessionId, renew.SessionId);
    }

    [Fact]
    public void A_renewal_with_an_unknown_or_ended_id_is_session_ended_and_starts_nothing()
    {
        Assert.Equal(StudioLiveOutcome.Ended, _provider.StartStudioLive(Body(sessionId: "nope")).Outcome);
        var open = _provider.StartStudioLive(Body());
        _provider.StopStudioLive();

        var late = _provider.StartStudioLive(Body(sessionId: open.SessionId));

        Assert.Equal(StudioLiveOutcome.Ended, late.Outcome);
        Assert.Equal("session_ended", late.Message);
        Assert.False(_provider.StudioLiveActive);
        Assert.False(_engine.StudioOverlayActive);
    }

    [Fact]
    public void A_second_post_without_an_id_takes_over_and_retires_the_old_id()
    {
        var first = _provider.StartStudioLive(Body(CustomFrags.Frag(0.2f)));

        var second = _provider.StartStudioLive(Body(CustomFrags.Frag(0.9f)));

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(StudioLiveOutcome.Ended, _provider.StartStudioLive(Body(sessionId: first.SessionId)).Outcome);
        Assert.True(_provider.StudioLiveActive);
    }

    [Fact]
    public void Stop_with_a_stale_id_is_a_no_op_without_an_id_it_ends_whatever_runs_and_it_is_idempotent()
    {
        Assert.False(_provider.StopStudioLive());
        var open = _provider.StartStudioLive(Body());

        Assert.False(_provider.StopStudioLive("stale"));
        Assert.True(_provider.StudioLiveActive);
        Assert.True(_provider.StopStudioLive(open.SessionId));
        Assert.False(_provider.StopStudioLive());
        Assert.False(_engine.StudioOverlayActive);
    }

    [Fact]
    public void The_lease_lapsing_clears_the_overlay_and_every_post_renews_it()
    {
        var open = _provider.StartStudioLive(Body());
        _now += 10_000;
        _provider.StartStudioLive(Body(sessionId: open.SessionId));
        _now += 10_000;
        _provider.CheckStudioLease();
        Assert.True(_engine.StudioOverlayActive);

        _now += 5_001;
        _provider.CheckStudioLease();

        Assert.False(_provider.StudioLiveActive);
        Assert.False(_engine.StudioOverlayActive);
    }

    [Fact]
    public void The_same_fragment_updates_uniforms_in_place_and_a_different_one_swaps_the_shader()
    {
        var open = _provider.StartStudioLive(Body(speed: 50, hue: 0f));
        var first = _provider.StudioLiveEffect;

        _provider.StartStudioLive(Body(speed: 100, hue: 0.25f, sessionId: open.SessionId));
        Assert.Same(first, _provider.StudioLiveEffect);
        Assert.Equal(2f, first!.Speed);
        Assert.Equal(0.25f, first.Hue);

        _provider.StartStudioLive(Body(CustomFrags.Frag(0.9f), sessionId: open.SessionId));
        Assert.NotSame(first, _provider.StudioLiveEffect);
        Assert.True(_engine.StudioOverlayActive);
    }

    [Fact]
    public void Lighting_off_refuses_a_session()
    {
        _config.Update(c => c.Features.Lighting = false);
        using var provider = new LightingProvider(_config, new LightingEngine(), new LightingOutputHub(), _gpu, new MediaLibrary(),
            new Nexus.Service.Platform.DefaultMonitorEnumerator(), gates: new Nexus.Service.Lifecycle.FeatureGates(_config));

        Assert.False(provider.StartStudioLive(Body()).Ok);
    }

    private sealed class FakeBeats : Nexus.Service.Activity.IBeatsProvider
    {
        public bool Running;
        public event Action? OnBeat { add { } remove { } }
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Dispose() { }
    }

    [Fact]
    public void A_studio_fragment_that_reads_audio_holds_audio_capture_until_the_session_ends()
    {
        var beats = new FakeBeats();
        using var provider = new LightingProvider(_config, new LightingEngine(), new LightingOutputHub(), _gpu, new MediaLibrary(),
            new Nexus.Service.Platform.DefaultMonitorEnumerator(), beats: beats);

        var open = provider.StartStudioLive(Body(CustomFrags.Frag(audio: true)));
        Assert.True(beats.Running);

        provider.StartStudioLive(Body(CustomFrags.Frag(), sessionId: open.SessionId));
        Assert.False(beats.Running);

        provider.StartStudioLive(Body(CustomFrags.Frag(audio: true), sessionId: open.SessionId));
        Assert.True(beats.Running);
        provider.StopStudioLive();
        Assert.False(beats.Running);
    }

    // Waits for n more published engine ticks.
    private void WaitTicks(int n)
    {
        var seen = 0;
        void Handler() => Interlocked.Increment(ref seen);
        _engine.FramePublished += Handler;
        try
        {
            WaitFor(() => Volatile.Read(ref seen) >= n);
        }
        finally
        {
            _engine.FramePublished -= Handler;
        }
        Assert.True(Volatile.Read(ref seen) >= n);
    }

    // A paused red effect with a green overlay over it, one device.
    private (DeviceFrame Dev, FakeEffect Global) PausedUnderOverlay()
    {
        var dev = Device("d", 1);
        _engine.UpdateDevices([dev]);
        var global = new FakeEffect("global", r: 200);
        _engine.SetEffect(global);
        WaitFor(() => global.Renders > 0);
        _engine.SetPaused(true);
        _engine.SetStudioOverlay(new FakeEffect("studio", g: 90));
        WaitForLed(dev, 0, 0, 90, 0);
        return (dev, global);
    }

    private static void AssertRed(DeviceFrame dev)
    {
        WaitForLed(dev, 0, 200, 0, 0);
        Assert.Equal(new byte[] { 200, 0, 0 }, dev.LedBytes[..3].ToArray());
    }

    [Fact]
    public void Clearing_the_overlay_on_a_paused_effect_shows_the_effects_own_frame()
    {
        var (dev, _) = PausedUnderOverlay();

        _engine.SetStudioOverlay(null);

        AssertRed(dev);
        Assert.True(_engine.Paused);
    }

    [Fact]
    public void A_paused_repaint_renders_the_frame_the_pause_held()
    {
        var dev = Device("d", 1);
        _engine.UpdateDevices([dev]);
        var clock = new ClockEffect();
        _engine.SetEffect(clock);
        WaitFor(() => clock.Renders > 2);
        _engine.SetPaused(true);
        WaitTicks(3);
        var held = clock.LastTick;
        var rendersAtPause = clock.Renders;
        _engine.SetStudioOverlay(new FakeEffect("studio", g: 90));
        WaitForLed(dev, 0, 0, 90, 0);
        WaitTicks(3);

        _engine.SetStudioOverlay(null);
        WaitFor(() => clock.Renders > rendersAtPause);
        WaitTicks(3);

        Assert.Equal(rendersAtPause + 1, clock.Renders);
        Assert.Equal(held, clock.LastTick);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Ending_during_a_fade_then_releasing_shows_the_effects_own_frame(int releaseMs)
    {
        var (dev, _) = PausedUnderOverlay();
        Assert.True(_engine.BeginBlackoutFade(TimeSpan.FromMilliseconds(400)));

        _engine.SetStudioOverlay(null);
        _engine.SetBlackout(true);
        WaitForLed(dev, 0, 0, 0, 0);
        _engine.BeginBlackoutRelease(TimeSpan.FromMilliseconds(releaseMs));

        AssertRed(dev);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Ending_during_a_hold_then_releasing_shows_the_effects_own_frame(int releaseMs)
    {
        var (dev, _) = PausedUnderOverlay();
        _engine.SetBlackout(true);
        WaitForLed(dev, 0, 0, 0, 0);

        _engine.SetStudioOverlay(null);
        WaitTicks(3);
        _engine.BeginBlackoutRelease(TimeSpan.FromMilliseconds(releaseMs));

        AssertRed(dev);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Ending_before_a_hold_then_releasing_shows_the_effects_own_frame(int releaseMs)
    {
        var (dev, _) = PausedUnderOverlay();

        _engine.SetStudioOverlay(null);
        AssertRed(dev);
        WaitTicks(2);
        _engine.SetBlackout(true);
        WaitForLed(dev, 0, 0, 0, 0);
        _engine.BeginBlackoutRelease(TimeSpan.FromMilliseconds(releaseMs));

        AssertRed(dev);
    }

    [Fact]
    public void Ending_across_a_whole_fade_hold_and_release_never_leaves_the_studio_frame()
    {
        var (dev, _) = PausedUnderOverlay();
        _engine.BeginBlackoutFade(TimeSpan.FromMilliseconds(100));
        WaitForLed(dev, 0, 0, 0, 0);
        _engine.BeginBlackoutRelease(TimeSpan.FromMilliseconds(100));
        _engine.SetStudioOverlay(null);

        AssertRed(dev);
    }

    [Fact]
    public void A_post_from_off_succeeds_reports_lighting_off_and_shows_once_a_mode_starts()
    {
        var open = _provider.StartStudioLive(Body());

        Assert.True(open.Ok);
        Assert.True(open.LightingOff);
        Assert.Null(_engine.CurrentEffect);
        Assert.True(_engine.StudioOverlayActive);

        _provider.StartAnimate(Animate("plasma"));

        Assert.False(_provider.StartStudioLive(Body(sessionId: open.SessionId)).LightingOff);
        Assert.True(_engine.StudioOverlayActive);
    }

    [Fact]
    public void Lighting_off_is_recomputed_on_every_renewal()
    {
        _provider.StartAnimate(Animate("plasma"));
        var open = _provider.StartStudioLive(Body());
        Assert.False(open.LightingOff);

        _provider.StopAll();

        Assert.True(_provider.StartStudioLive(Body(sessionId: open.SessionId)).LightingOff);
    }
}
