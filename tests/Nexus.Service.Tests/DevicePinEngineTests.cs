using Nexus.Service.Activity;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Capture;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

public class DevicePinEngineTests : IDisposable
{
    private sealed class Fill : IEffect
    {
        public byte R, G, B;
        public bool Disposed;
        public bool Throws;
        public Fill(string name, byte r, byte g, byte b) { Name = name; R = r; G = g; B = b; }
        public string Name { get; }
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            if (Throws) throw new InvalidOperationException("boom");
            for (int y = 0; y < canvas.Height; y++)
                for (int x = 0; x < canvas.Width; x++)
                    canvas.SetPixel(x, y, R, G, B);
        }
        public void Dispose() { Disposed = true; }
    }

    private sealed class FakeScreen : IScreenFrameSource
    {
        public int Starts, Stops;
        public string? LastMonitor;
        public bool Running;
        public byte R = 255, G = 0, B = 0;
        public void Start(string monitorId, int width, int height) { Starts++; Running = true; LastMonitor = monitorId; }
        public void Stop() { Stops++; Running = false; }
        public byte[]? TryAcquireFrame(out int width, out int height)
        {
            width = 16;
            height = 9;
            var frame = new byte[16 * 9 * 3];
            for (var i = 0; i < frame.Length; i += 3) { frame[i] = R; frame[i + 1] = G; frame[i + 2] = B; }
            return frame;
        }
    }

    private sealed class FakeBeats : IBeatsProvider
    {
        public bool Running;
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public event Action? OnBeat { add { } remove { } }
        public void Dispose() { }
    }

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-pins-" + Guid.NewGuid().ToString("N")[..8]);

    public DevicePinEngineTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static DeviceFrame Device(string id = "dev-a") =>
        new(0, id, 5, x: 300, y: 100, w: 40, h: 250, rotation: 90);

    private static async Task<byte[]> WaitFor(DeviceFrame dev, Func<byte[], bool> ok)
    {
        for (var i = 0; i < 300; i++)
        {
            var leds = dev.LedBytes.ToArray();
            if (ok(leds)) return leds;
            await Task.Delay(10);
        }
        return dev.LedBytes.ToArray();
    }

    private static async Task<bool> Eventually(Func<bool> cond)
    {
        for (var i = 0; i < 300; i++)
        {
            if (cond()) return true;
            await Task.Delay(10);
        }
        return cond();
    }

    /// <summary>Completes after <paramref name="count"/> more published frames.</summary>
    private static async Task Frames(LightingEngine engine, int count)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = 0;
        void Handler()
        {
            if (Interlocked.Increment(ref seen) >= count) done.TrySetResult();
        }
        engine.FramePublished += Handler;
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { engine.FramePublished -= Handler; }
    }

    private static LightingEngine Engine(DevicePinTracker pins, DeviceFrame dev, Func<string, IEffect?, IEffect?> factory)
    {
        var engine = new LightingEngine
        {
            Pins = pins,
            PinEffectFactory = factory,
            PinLookTag = _ => "t",
            FrameIntervalMs = 10,
        };
        engine.UpdateDevices(new[] { dev });
        return engine;
    }

    [Fact]
    public async Task An_animate_pin_applies_while_the_global_mode_is_static()
    {
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "red" });
        var dev = Device();
        using var engine = Engine(pins, dev, (_, _) => new Fill("red", 255, 0, 0));
        // Static mode holds one catalog shader's frame.
        engine.SetEffect(new Fill("gradientlinear", 0, 255, 0));
        engine.SetFrozen(true);

        var leds = await WaitFor(dev, l => l[0] == 255);
        Assert.Equal(255, leds[0]);
        Assert.Equal(0, leds[1]);
    }

    [Fact]
    public async Task A_pin_cleared_returns_the_device_and_disposes_the_instance()
    {
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "red" });
        var dev = Device();
        Fill? built = null;
        using var engine = Engine(pins, dev, (_, _) => built = new Fill("red", 255, 0, 0));
        engine.SetEffect(new Fill("green", 0, 255, 0));
        await WaitFor(dev, l => l[0] == 255);

        pins.Clear("dev-a");
        var leds = await WaitFor(dev, l => l[1] == 255);

        Assert.Equal(255, leds[1]);
        Assert.True(await Eventually(() => built!.Disposed));
    }

    [Fact]
    public async Task A_pin_for_an_absent_device_builds_nothing()
    {
        var pins = new DevicePinTracker();
        pins.Set("not-here", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "red" });
        var dev = Device();
        var builds = 0;
        using var engine = Engine(pins, dev, (_, _) => { builds++; return new Fill("red", 255, 0, 0); });
        engine.SetEffect(new Fill("green", 0, 255, 0));

        await WaitFor(dev, l => l[1] == 255);
        await Frames(engine, 5);
        Assert.Equal(0, builds);
    }

    [Fact]
    public async Task A_pinned_effect_that_throws_lets_the_device_fall_through()
    {
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "red" });
        var dev = Device();
        using var engine = Engine(pins, dev, (_, _) => new Fill("red", 255, 0, 0) { Throws = true });
        engine.SetEffect(new Fill("green", 0, 255, 0));

        var leds = await WaitFor(dev, l => l[1] == 255);
        Assert.Equal(255, leds[1]);
        Assert.Equal(0, leds[0]);
    }

    [Fact]
    public async Task A_pin_onto_the_running_effect_samples_the_main_canvas_without_a_second_render()
    {
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "wave" });
        var dev = Device();
        var builds = 0;
        using var engine = Engine(pins, dev, (_, _) => { builds++; return new Fill("wave", 255, 0, 0); });
        engine.SetEffect(new Fill("wave", 0, 255, 0));

        var leds = await WaitFor(dev, l => l[1] == 255);
        Assert.Equal(255, leds[1]);
        Assert.Equal(0, builds);
    }

    // ---- mirror pins, through the provider that builds the capture effect ----

    private sealed class Rig : IDisposable
    {
        public readonly JsonConfigStore Store;
        public readonly DevicePinTracker Pins;
        public readonly LightingEngine Engine;
        public readonly GpuContext Gpu = new(160, 90);
        public readonly FakeScreen Screen = new();
        public readonly FakeBeats Beats = new();
        public readonly LightingProvider Provider;
        public readonly MediaLibrary Media;

        public Rig(string dir, Action<JsonConfigStore>? seed = null)
        {
            Store = new JsonConfigStore(Path.Combine(dir, "settings.json"));
            seed?.Invoke(Store);
            Pins = new DevicePinTracker(Store);
            Engine = new LightingEngine { Pins = Pins, FrameIntervalMs = 10 };
            Media = new MediaLibrary(Path.Combine(dir, "media"));
            Provider = new LightingProvider(Store, Engine, new LightingOutputHub(), Gpu, Media,
                new Nexus.Service.Platform.DefaultMonitorEnumerator(), frameSource: Screen, beats: Beats);
        }

        public void Dispose()
        {
            Engine.Dispose();
            Provider.Dispose();
            Gpu.Dispose();
        }
    }

    [Fact]
    public async Task A_mirror_pin_samples_the_capture_while_the_global_mode_is_animate()
    {
        using var rig = new Rig(_tempDir);
        var dev = Device();
        rig.Engine.UpdateDevices(new[] { dev });
        rig.Engine.SetEffect(new Fill("plasma", 0, 255, 0));   // global Animate
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Mirror });

        var leds = await WaitFor(dev, l => l[0] > 200);
        Assert.Equal(255, leds[0]);
        Assert.Equal(0, leds[1]);
    }

    [Fact]
    public async Task Screen_keep_mode_moves_the_monitor_of_mirror_pins_and_leaves_the_global_mode()
    {
        using var rig = new Rig(_tempDir);
        rig.Store.Update(s => { s.Lighting.ScreenMonitor = "1"; s.Lighting.Sync = "static"; });
        var dev = Device();
        rig.Engine.UpdateDevices(new[] { dev });
        var global = new Fill("gradientlinear", 0, 255, 0);
        rig.Engine.SetEffect(global);
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Mirror });
        Assert.True(await Eventually(() => rig.Screen.Running && rig.Screen.LastMonitor == "1"));

        rig.Provider.StartScreen(new Nexus.Service.Models.Lighting.ScreenHeadlessStart { Monitor = "2", KeepMode = true });

        Assert.True(await Eventually(() => rig.Screen.LastMonitor == "2"));
        Assert.Equal("2", rig.Store.Load().Lighting.ScreenMonitor);
        Assert.Equal("static", rig.Store.Load().Lighting.Sync);
        Assert.Same(global, rig.Engine.CurrentEffect);
        Assert.Equal(255, (await WaitFor(dev, l => l[0] > 200))[0]);
    }

    [Fact]
    public void Screen_start_without_keep_mode_switches_the_global_mode()
    {
        using var rig = new Rig(_tempDir);
        rig.Store.Update(s => s.Lighting.Sync = "static");

        rig.Provider.StartScreen(new Nexus.Service.Models.Lighting.ScreenHeadlessStart { Monitor = "2" });

        Assert.Equal("screen", rig.Store.Load().Lighting.Sync);
        Assert.Equal("2", rig.Store.Load().Lighting.ScreenMonitor);
        Assert.Equal("screen", rig.Engine.CurrentEffect?.Name);
    }

    [Fact]
    public async Task The_capture_starts_on_the_first_mirror_pin_runs_once_and_stops_with_the_last()
    {
        using var rig = new Rig(_tempDir);
        var a = Device("dev-a");
        var b = new DeviceFrame(1, "dev-b", 5, x: 100, y: 100, w: 40, h: 250, rotation: 90);
        rig.Engine.UpdateDevices(new[] { a, b });
        rig.Engine.SetEffect(new Fill("plasma", 0, 255, 0));
        await Frames(rig.Engine, 5);
        Assert.Equal(0, rig.Screen.Starts);

        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Mirror });
        Assert.True(await Eventually(() => rig.Screen.Running));
        rig.Pins.Set("dev-b", new DevicePin { Kind = DevicePinKinds.Mirror });
        await WaitFor(b, l => l[0] > 200);
        Assert.Equal(1, rig.Screen.Starts);

        rig.Pins.Clear("dev-a");
        await Frames(rig.Engine, 5);
        Assert.True(rig.Screen.Running);

        rig.Pins.Clear("dev-b");
        Assert.True(await Eventually(() => !rig.Screen.Running));
        Assert.Equal(1, rig.Screen.Stops);
    }

    // ---- media pins: the one shared Media source ----

    private static void SeedMedia(MediaLibrary media, string id, byte r, byte g, byte b)
    {
        media.SaveMeta(new Nexus.Service.Models.Media.MediaItem
        {
            Id = id, Name = id, Type = "static", Frames = 1, Fps = 1, Width = 4, Height = 4,
        });
        var frame = new byte[4 * 4 * 3];
        for (var i = 0; i < frame.Length; i += 3) { frame[i] = r; frame[i + 1] = g; frame[i + 2] = b; }
        File.WriteAllBytes(media.GetFramesBinPath(id), frame);
    }

    [Fact]
    public async Task A_media_pin_plays_the_media_item_while_the_global_mode_is_static()
    {
        using var rig = new Rig(_tempDir);
        SeedMedia(rig.Media, "clip", 0, 0, 255);
        rig.Store.Update(s => s.Lighting.LastMediaId = "clip");
        var dev = Device();
        rig.Engine.UpdateDevices(new[] { dev });
        rig.Engine.SetEffect(new Fill("gradientlinear", 0, 255, 0));
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Media });

        var leds = await WaitFor(dev, l => l[2] > 200);
        Assert.Equal(0, leds[0]);
        Assert.Equal(255, leds[2]);
    }

    [Fact]
    public async Task Picking_another_media_item_changes_what_a_media_pin_shows()
    {
        using var rig = new Rig(_tempDir);
        SeedMedia(rig.Media, "one", 0, 0, 255);
        SeedMedia(rig.Media, "two", 255, 0, 0);
        rig.Store.Update(s => s.Lighting.LastMediaId = "one");
        var dev = Device();
        rig.Engine.UpdateDevices(new[] { dev });
        rig.Engine.SetEffect(new Fill("gradientlinear", 0, 255, 0));
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Media });
        await WaitFor(dev, l => l[2] > 200);

        rig.Store.Update(s => s.Lighting.LastMediaId = "two");

        var leds = await WaitFor(dev, l => l[0] > 200);
        Assert.Equal(255, leds[0]);
        Assert.Equal(0, leds[2]);
    }

    [Fact]
    public async Task Keep_mode_changes_the_shared_item_without_switching_the_global_mode()
    {
        using var rig = new Rig(_tempDir);
        SeedMedia(rig.Media, "one", 0, 0, 255);
        SeedMedia(rig.Media, "two", 255, 0, 0);
        rig.Store.Update(s => { s.Lighting.LastMediaId = "one"; s.Lighting.Sync = "static"; });
        var dev = Device();
        rig.Engine.UpdateDevices(new[] { dev });
        var global = new Fill("gradientlinear", 0, 255, 0);
        rig.Engine.SetEffect(global);
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Media });
        await WaitFor(dev, l => l[2] > 200);

        Assert.True(rig.Provider.StartMedia("two", keepMode: true));

        var leds = await WaitFor(dev, l => l[0] > 200);
        Assert.Equal(255, leds[0]);
        Assert.Equal("static", rig.Store.Load().Lighting.Sync);
        Assert.Equal("two", rig.Store.Load().Lighting.LastMediaId);
        Assert.Same(global, rig.Engine.CurrentEffect);
    }

    [Fact]
    public void Keep_mode_refuses_an_unknown_item_and_leaves_the_selection()
    {
        using var rig = new Rig(_tempDir);
        rig.Store.Update(s => s.Lighting.LastMediaId = "");
        Assert.False(rig.Provider.StartMedia("nope", keepMode: true));
        Assert.Equal("", rig.Store.Load().Lighting.LastMediaId);
    }

    [Fact]
    public void Without_keep_mode_the_play_switches_the_global_mode_to_media()
    {
        using var rig = new Rig(_tempDir);
        SeedMedia(rig.Media, "one", 0, 0, 255);
        rig.Store.Update(s => s.Lighting.Sync = "static");

        Assert.True(rig.Provider.StartMedia("one", keepMode: false));

        Assert.Equal("media", rig.Store.Load().Lighting.Sync);
        Assert.Equal("media", rig.Engine.CurrentEffect?.Name);
    }

    [Fact]
    public void Keep_mode_while_global_media_is_active_switches_the_playing_item()
    {
        using var rig = new Rig(_tempDir);
        SeedMedia(rig.Media, "one", 0, 0, 255);
        SeedMedia(rig.Media, "two", 255, 0, 0);
        Assert.True(rig.Provider.StartMedia("one"));

        Assert.True(rig.Provider.StartMedia("two", keepMode: true));

        Assert.Equal("media", rig.Store.Load().Lighting.Sync);
        Assert.Equal("two", rig.Store.Load().Lighting.LastMediaId);
    }

    [Fact]
    public async Task A_media_pin_whose_load_failed_retries_on_the_next_refresh()
    {
        using var rig = new Rig(_tempDir);
        rig.Store.Update(s => s.Lighting.LastMediaId = "late");
        var dev = Device();
        rig.Engine.UpdateDevices(new[] { dev });
        rig.Engine.SetEffect(new Fill("gradientlinear", 0, 255, 0));
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Media });
        Assert.True(await Eventually(() => dev.LedBytes.ToArray().All(v => v == 0)));

        SeedMedia(rig.Media, "late", 0, 0, 255);
        rig.Store.Update(s => s.Lighting.MusicReactive = !s.Lighting.MusicReactive);

        var leds = await WaitFor(dev, l => l[2] > 200);
        Assert.Equal(255, leds[2]);
    }

    [Fact]
    public async Task A_media_pin_shows_the_item_at_once_after_leaving_global_media()
    {
        using var rig = new Rig(_tempDir);
        SeedMedia(rig.Media, "clip", 0, 0, 255);
        rig.Store.Update(s => { s.Lighting.LastMediaId = "clip"; s.Lighting.Sync = "media"; });
        var dev = Device();
        rig.Engine.UpdateDevices(new[] { dev });
        rig.Engine.SetEffect(new Fill("media", 0, 255, 0));
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Media });
        await WaitFor(dev, l => l[1] == 255);

        rig.Engine.SetEffect(new Fill("gradientlinear", 255, 0, 0));
        rig.Store.Update(s => s.Lighting.Sync = "static");

        var leds = await WaitFor(dev, l => l[2] > 200);
        Assert.Equal(255, leds[2]);
    }

    [Fact]
    public void A_settings_write_with_no_pins_notifies_once_and_construction_writes_nothing()
    {
        var constructionChanges = 0;
        using var rig = new Rig(_tempDir, store => store.OnChanged += () => Interlocked.Increment(ref constructionChanges));
        Assert.Equal(0, constructionChanges);

        var changes = 0;
        rig.Store.OnChanged += () => Interlocked.Increment(ref changes);
        rig.Store.Update(s => s.Lighting.MusicReactive = !s.Lighting.MusicReactive);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void A_settings_write_with_a_media_pin_notifies_once()
    {
        using var rig = new Rig(_tempDir);
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Media });
        var changes = 0;
        rig.Store.OnChanged += () => Interlocked.Increment(ref changes);

        rig.Store.Update(s => s.Lighting.MusicReactive = !s.Lighting.MusicReactive);

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Concurrent_pin_writes_during_refreshes_do_not_break_the_pinned_looks()
    {
        using var rig = new Rig(_tempDir);
        SeedMedia(rig.Media, "clip", 0, 0, 255);
        rig.Store.Update(s => s.Lighting.LastMediaId = "clip");
        var errors = new List<Exception>();
        var writer = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 200; i++)
                {
                    rig.Pins.Set("id-" + i, new DevicePin { Kind = DevicePinKinds.Media });
                    rig.Pins.Clear("id-" + i);
                }
            }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        });
        var refresher = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 200; i++)
                {
                    rig.Store.Update(s => s.Lighting.MusicReactive = !s.Lighting.MusicReactive);
                }
            }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        });
        await Task.WhenAll(writer, refresher);
        Assert.Empty(errors);

        var dev = Device();
        rig.Engine.UpdateDevices(new[] { dev });
        rig.Engine.SetEffect(new Fill("gradientlinear", 0, 255, 0));
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Media });
        Assert.Equal(255, (await WaitFor(dev, l => l[2] > 200))[2]);
    }

    [Fact]
    public async Task A_media_pin_with_no_item_renders_idle_media_black()
    {
        using var rig = new Rig(_tempDir);
        var dev = Device();
        var other = new DeviceFrame(1, "dev-b", 5, x: 100, y: 100, w: 40, h: 250, rotation: 90);
        rig.Engine.UpdateDevices(new[] { dev, other });
        rig.Engine.SetEffect(new Fill("gradientlinear", 0, 255, 0));
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Media });

        // The pin settles a frame or two after the first render; the unpinned
        // device proves frames are being drawn while the pinned one is black.
        Assert.True(await Eventually(() => other.LedBytes.ToArray()[1] == 255 && dev.LedBytes.ToArray().All(v => v == 0)));
    }

    [Fact]
    public async Task A_media_pin_and_a_following_device_match_while_the_global_mode_is_media()
    {
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Media });
        var a = Device("dev-a");
        var b = new DeviceFrame(1, "dev-b", 5, x: 100, y: 100, w: 40, h: 250, rotation: 90);
        var built = 0;
        using var engine = Engine(pins, a, (_, _) => { built++; return new Fill("media", 9, 9, 9); });
        engine.UpdateDevices(new[] { a, b });
        engine.SetEffect(new Fill("media", 0, 0, 255));

        var pinned = await WaitFor(a, l => l[2] == 255);
        var following = await WaitFor(b, l => l[2] == 255);
        Assert.Equal(following, pinned);
        Assert.Equal(0, built);
    }

    [Fact]
    public async Task The_media_source_builds_once_for_many_pins_and_is_released_with_the_last()
    {
        var pins = new DevicePinTracker();
        var a = Device("dev-a");
        var b = new DeviceFrame(1, "dev-b", 5, x: 100, y: 100, w: 40, h: 250, rotation: 90);
        var built = new List<Fill>();
        using var engine = Engine(pins, a, (key, _) =>
        {
            Assert.Equal(LightingEngine.MediaPinKey, key);
            var f = new Fill("media", 0, 0, 255);
            built.Add(f);
            return f;
        });
        engine.UpdateDevices(new[] { a, b });
        engine.SetEffect(new Fill("gradientlinear", 0, 255, 0));
        await WaitFor(b, l => l[1] == 255);
        Assert.Empty(built);

        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Media });
        pins.Set("dev-b", new DevicePin { Kind = DevicePinKinds.Media });
        await WaitFor(b, l => l[2] > 200);
        Assert.Single(built);

        pins.Clear("dev-a");
        await WaitFor(a, l => l[1] == 255);
        Assert.False(built[0].Disposed);

        pins.Clear("dev-b");
        Assert.True(await Eventually(() => built[0].Disposed));
    }

    [Fact]
    public async Task Pins_that_come_and_go_quickly_leave_no_live_media_source()
    {
        var pins = new DevicePinTracker();
        var dev = Device();
        var built = new List<Fill>();
        using var engine = Engine(pins, dev, (_, _) =>
        {
            var f = new Fill("media", 0, 0, 255);
            lock (built) built.Add(f);
            return f;
        });
        engine.SetEffect(new Fill("gradientlinear", 0, 255, 0));

        for (var i = 0; i < 20; i++)
        {
            pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Media });
            await Task.Delay(15);
            pins.Clear("dev-a");
            await Task.Delay(15);
        }

        Assert.True(await Eventually(() =>
        {
            lock (built) return built.All(f => f.Disposed);
        }));
        Assert.NotEmpty(built);
    }

    [Fact]
    public void A_media_pin_survives_a_restart()
    {
        var path = Path.Combine(_tempDir, "persist.json");
        using (var first = new JsonConfigStore(path))
        {
            var tracker = new DevicePinTracker(first);
            tracker.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Media });
            first.FlushNow();
        }

        using var second = new JsonConfigStore(path);
        var restored = new DevicePinTracker(second);
        Assert.True(restored.TryGet("dev-a", out var pin));
        Assert.Equal(DevicePinKinds.Media, pin.Kind);
        Assert.Null(pin.Effect);
    }

    [Fact]
    public async Task Pausing_holds_the_mirror_pin_frame()
    {
        using var rig = new Rig(_tempDir);
        var dev = Device();
        rig.Engine.UpdateDevices(new[] { dev });
        rig.Engine.SetEffect(new Fill("plasma", 0, 255, 0));
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Mirror });
        await WaitFor(dev, l => l[0] > 200);

        rig.Engine.SetPaused(true);
        // A tick already past the paused check may still render; let it publish
        // before the screen changes.
        await Frames(rig.Engine, 1);
        rig.Screen.R = 0;
        rig.Screen.B = 255;
        await Frames(rig.Engine, 5);

        Assert.Equal(255, dev.LedBytes.ToArray()[0]);
        Assert.Equal(0, dev.LedBytes.ToArray()[2]);
    }

    [Fact]
    public void The_shared_capture_closes_only_with_its_last_handle()
    {
        var inner = new FakeScreen();
        var shared = new SharedScreenFrameSource(inner);
        var first = shared.CreateHandle();
        var second = shared.CreateHandle();
        var neverStarted = shared.CreateHandle();

        first.Start("", 160, 90);
        second.Start("", 160, 90);
        Assert.Equal(1, inner.Starts);

        neverStarted.Stop();   // stopping an unstarted handle must not close the capture
        first.Stop();
        Assert.True(inner.Running);
        first.Stop();          // idempotent
        Assert.True(inner.Running);
        second.Stop();
        Assert.False(inner.Running);
        Assert.Equal(1, inner.Stops);
    }

    [Fact]
    public void An_audio_reactive_animate_pin_keeps_capture_on()
    {
        using var rig = new Rig(_tempDir);
        rig.Store.Update(s => s.Lighting.MusicReactive = true);
        Assert.False(rig.Beats.Running);

        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "basspulse" });
        Assert.True(rig.Beats.Running);

        rig.Pins.Clear("dev-a");
        Assert.False(rig.Beats.Running);

        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "plasma" });
        Assert.False(rig.Beats.Running);
    }

    [Fact]
    public void A_template_save_moves_a_pinned_effects_look()
    {
        using var rig = new Rig(_tempDir);
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "plasma" });
        var before = rig.Engine.PinLookTag!("plasma");
        Assert.NotNull(before);

        var slot = AnimateTemplateDefaults.ResolveSelected(rig.Store.Load().Lighting.Animate.Templates, "plasma")!;
        rig.Provider.SaveAnimateTemplates(new Dictionary<string, AnimateEffectTemplates>
        {
            ["plasma"] = new()
            {
                Selected = 0,
                Slots = new List<AnimateEffectState?>
                {
                    new AnimateEffectState
                    {
                        Speed = slot.Speed,
                        Intensity = slot.Intensity,
                        Hue = slot.Hue + 0.31f,
                        Colorize = slot.Colorize,
                        Saturation = slot.Saturation,
                        Contrast = slot.Contrast,
                    },
                },
            },
        });

        var after = rig.Engine.PinLookTag!("plasma");
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task A_mirror_pin_captures_the_persisted_monitor_after_a_restart()
    {
        using var rig = new Rig(_tempDir, store =>
        {
            store.Update(s =>
            {
                s.Lighting.ScreenMonitor = "2";
                s.Lighting.DevicePins["dev-a"] = new DevicePin { Kind = DevicePinKinds.Mirror };
            });
        });
        var dev = Device();
        rig.Engine.UpdateDevices(new[] { dev });
        rig.Engine.SetEffect(new Fill("plasma", 0, 255, 0));

        Assert.True(await Eventually(() => rig.Screen.Running));
        Assert.Equal("2", rig.Screen.LastMonitor);
    }

    [Fact]
    public void Starting_mirror_persists_the_monitor_and_a_start_without_one_keeps_it()
    {
        using var rig = new Rig(_tempDir);
        rig.Provider.StartScreen(new Nexus.Service.Models.Lighting.ScreenHeadlessStart { Monitor = "3" });
        Assert.Equal("3", rig.Store.Load().Lighting.ScreenMonitor);

        rig.Provider.StartScreen(new Nexus.Service.Models.Lighting.ScreenHeadlessStart());
        Assert.Equal("3", rig.Store.Load().Lighting.ScreenMonitor);

        rig.Provider.StartScreen(new Nexus.Service.Models.Lighting.ScreenHeadlessStart { Monitor = "" });
        Assert.Equal("", rig.Store.Load().Lighting.ScreenMonitor);
    }

    private sealed class HalfAndHalf : IEffect
    {
        public string Name => "sweeprainbow";
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (int y = 0; y < canvas.Height; y++)
                for (int x = 0; x < canvas.Width; x++)
                    canvas.SetPixel(x, y, x < canvas.Width / 2 ? (byte)255 : (byte)0, 0, x < canvas.Width / 2 ? (byte)0 : (byte)255);
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task A_device_pinned_to_a_sweep_reads_the_whole_canvas()
    {
        Assert.True(DevicePinTracker.IsAnimateEffect("sweeprainbow"));
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "sweeprainbow" });
        // A small strip parked in the red half of the canvas.
        var dev = new DeviceFrame(0, "dev-a", 10, x: 20, y: 20, w: 100, h: 20);
        using var engine = Engine(pins, dev, (_, _) => new HalfAndHalf());
        engine.SetEffect(new Fill("green", 0, 255, 0));

        var leds = await WaitFor(dev, l => l[2] > 200 || l[27] > 200);
        Assert.Equal(255, leds[0]);        // first LED: red end
        Assert.Equal(255, leds[9 * 3 + 2]); // last LED: blue end
    }

    private sealed class Halves : IEffect
    {
        public Halves(string name) { Name = name; }
        public string Name { get; }
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (int y = 0; y < canvas.Height; y++)
                for (int x = 0; x < canvas.Width; x++)
                    canvas.SetPixel(x, y, x < canvas.Width / 2 ? (byte)255 : (byte)0, 0, x < canvas.Width / 2 ? (byte)0 : (byte)255);
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task A_pinned_device_samples_its_own_rect_while_a_global_sweep_runs()
    {
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "red" });
        // A small strip parked in the red half of the pin canvas.
        var dev = new DeviceFrame(0, "dev-a", 10, x: 20, y: 20, w: 100, h: 20);
        using var engine = Engine(pins, dev, (_, _) => new Halves("red"));
        engine.FullFrameSampling = true;
        engine.SetEffect(new Fill("sweeprainbow", 0, 255, 0));

        var leds = await WaitFor(dev, l => l[0] == 255);
        Assert.Equal(255, leds[0]);
        Assert.Equal(0, leds[9 * 3 + 2]);
    }

    [Fact]
    public async Task A_pin_cleared_while_paused_disposes_its_render_and_keeps_the_others_held()
    {
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "red" });
        pins.Set("dev-b", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "blue" });
        var a = Device("dev-a");
        var b = new DeviceFrame(1, "dev-b", 5, x: 100, y: 100, w: 40, h: 250, rotation: 90);
        var built = new Dictionary<string, Fill>();
        var engine = new LightingEngine
        {
            Pins = pins,
            PinEffectFactory = (key, _) => built[key] = key == "red" ? new Fill("red", 255, 0, 0) : new Fill("blue", 0, 0, 255),
            PinLookTag = _ => "t",
            FrameIntervalMs = 10,
        };
        using var disposeEngine = engine;
        engine.UpdateDevices(new[] { a, b });
        engine.SetEffect(new Fill("green", 0, 255, 0));
        await WaitFor(a, l => l[0] == 255);
        await WaitFor(b, l => l[2] == 255);

        engine.SetPaused(true);
        await Frames(engine, 2);
        pins.Clear("dev-a");

        Assert.True(await Eventually(() => built["red"].Disposed));
        Assert.False(built["blue"].Disposed);
        Assert.Equal(255, a.LedBytes.ToArray()[0]);
        Assert.Equal(255, b.LedBytes.ToArray()[2]);
    }

    [Fact]
    public async Task A_pin_cleared_while_blacked_out_disposes_its_render()
    {
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "red" });
        var dev = Device();
        Fill? built = null;
        using var engine = Engine(pins, dev, (_, _) => built = new Fill("red", 255, 0, 0));
        engine.SetEffect(new Fill("green", 0, 255, 0));
        await WaitFor(dev, l => l[0] == 255);

        engine.SetBlackout(true);
        await Frames(engine, 2);
        pins.Clear("dev-a");

        Assert.True(await Eventually(() => built!.Disposed));
    }

    // ---- global static color, monitors list, mirror post-process outside Mirror ----

    [Fact]
    public async Task A_global_static_color_is_exact_for_unpinned_devices_and_survives_presets_and_a_restart()
    {
        using var rig = new Rig(_tempDir);
        var dev = Device();
        rig.Engine.UpdateDevices(new[] { dev });

        rig.Provider.StartStatic(new Nexus.Service.Models.Lighting.StaticHeadlessStart { Color = "#336699" });

        var leds = await WaitFor(dev, l => l[0] == 0x33);
        Assert.Equal(new byte[] { 0x33, 0x66, 0x99 }, leds[0..3]);
        var lighting = rig.Store.Load().Lighting;
        Assert.Equal("#336699", lighting.Static.Color);
        Assert.Equal((0x33, 0x66, 0x99), (lighting.StaticColor.R, lighting.StaticColor.G, lighting.StaticColor.B));
        Assert.Equal("static", lighting.Sync);

        // A preset carries it and restores it.
        var look = LightingPresetLooks.Capture(lighting);
        Assert.Equal("#336699", look.StaticColor);
        lighting.Static.Color = null;
        LightingPresetLooks.Apply(lighting, look);
        Assert.Equal("#336699", lighting.Static.Color);

        // A pin still wins over the global color.
        rig.Pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Static, Effect = "flat", Color = "#ff0000" });
        var pinned = await WaitFor(dev, l => l[0] == 255);
        Assert.Equal(255, pinned[0]);
        Assert.Equal(0, pinned[1]);
    }

    [Fact]
    public void Picking_an_effect_after_a_color_returns_the_global_static_look_to_the_effect()
    {
        using var rig = new Rig(_tempDir);
        rig.Provider.StartStatic(new Nexus.Service.Models.Lighting.StaticHeadlessStart { Color = "#336699" });
        rig.Provider.StartStatic(new Nexus.Service.Models.Lighting.StaticHeadlessStart { Effect = "simplered" });
        Assert.Null(rig.Store.Load().Lighting.Static.Color);
        Assert.Equal("simplered", rig.Store.Load().Lighting.Static.Effect);
    }

    [Fact]
    public void The_monitor_list_reports_the_persisted_selection()
    {
        using var rig = new Rig(_tempDir, store => store.Update(s => s.Lighting.ScreenMonitor = "2"));
        Assert.Equal("2", rig.Provider.GetScreenSyncOptions().SelectedMonitor);
    }

    [Fact]
    public void Updating_the_mirror_post_process_outside_mirror_changes_no_mode()
    {
        using var rig = new Rig(_tempDir);
        var running = new Fill("plasma", 0, 255, 0);
        rig.Engine.SetEffect(running);
        rig.Store.Update(s => s.Lighting.Sync = "plasma");

        rig.Provider.UpdateScreenEffect(0.3f, 0.2f, 1f, 1f, false, false, persist: true);

        Assert.Same(running, rig.Engine.CurrentEffect);
        Assert.Equal("plasma", rig.Store.Load().Lighting.Sync);
        Assert.Equal(0.3f, rig.Store.Load().Lighting.ScreenEffect.Hue, 3);
        Assert.Equal(0.3f, rig.Provider.ScreenPostProcess.Hue, 3);
    }

    [Fact]
    public void A_start_that_arrives_after_the_handle_stopped_takes_no_reference()
    {
        var inner = new FakeScreen();
        var shared = new SharedScreenFrameSource(inner);
        var handle = shared.CreateHandle();

        handle.Stop();                    // the effect was disposed first
        handle.Start("", 160, 90);        // a render that raced the disposal starts late

        Assert.Equal(0, inner.Starts);
        Assert.Equal(0, shared.ActiveHandles);
        Assert.False(inner.Running);
    }

    [Fact]
    public async Task Disposing_while_another_thread_starts_always_ends_with_the_capture_stopped()
    {
        for (var i = 0; i < 200; i++)
        {
            var inner = new FakeScreen();
            var shared = new SharedScreenFrameSource(inner);
            var handle = shared.CreateHandle();
            var starter = Task.Run(() => handle.Start("", 160, 90));
            var stopper = Task.Run(() => handle.Stop());
            await Task.WhenAll(starter, stopper);
            Assert.Equal(0, shared.ActiveHandles);
            Assert.False(inner.Running);
        }
    }

    [Fact]
    public async Task A_profile_switch_that_changes_the_screen_is_picked_up_by_the_next_mirror_start()
    {
        using var rig = new Rig(_tempDir, store => store.Update(s => s.Lighting.ScreenMonitor = "1"));
        // What a profile apply does: replace the whole Lighting object.
        rig.Store.Update(s => s.Lighting = new LightingSettings { ScreenMonitor = "5" });

        rig.Provider.StartScreen(new Nexus.Service.Models.Lighting.ScreenHeadlessStart());

        Assert.True(await Eventually(() => rig.Screen.Running));
        Assert.Equal("5", rig.Screen.LastMonitor);
    }

    [Fact]
    public void A_malformed_static_color_is_refused()
    {
        using var rig = new Rig(_tempDir);
        Assert.Throws<ArgumentException>(() =>
            rig.Provider.StartStatic(new Nexus.Service.Models.Lighting.StaticHeadlessStart { Color = "zzz" }));
    }
}
