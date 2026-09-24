using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;

namespace Nexus.Service.Tests;

/// <summary>
/// Per-device Static assignments have to survive the canvas sample, which is the
/// step that previously overwrote them, and they must render the ASSIGNED
/// EFFECT - a flat colour cannot represent a gradient or carry the tint
/// controls, which is how the first cut of this shipped wrong.
/// </summary>
public class StaticDeviceEffectTests
{
    private sealed class FillEffect : IEffect
    {
        private readonly byte _r, _g, _b;
        public FillEffect(byte r, byte g, byte b) { _r = r; _g = g; _b = b; }
        public string Name => "fill";
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (int y = 0; y < canvas.Height; y++)
                for (int x = 0; x < canvas.Width; x++)
                    canvas.SetPixel(x, y, _r, _g, _b);
        }
        public void Dispose() { }
    }

    /// <summary>Red on the left edge, blue on the right - a horizontal ramp.</summary>
    private sealed class RampEffect : IEffect
    {
        public string Name => "ramp";
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (int y = 0; y < canvas.Height; y++)
                for (int x = 0; x < canvas.Width; x++)
                {
                    var t = canvas.Width > 1 ? (float)x / (canvas.Width - 1) : 0f;
                    canvas.SetPixel(x, y, (byte)(255 * (1 - t)), 0, (byte)(255 * t));
                }
        }
        public void Dispose() { }
    }

    private static async Task<byte[]> RenderOnce(
        DeviceFrame device,
        StaticDeviceEffectTracker? tracker,
        Func<StaticDeviceAssignment, IEffect?>? factory)
    {
        using var engine = new LightingEngine { StaticEffects = tracker, StaticEffectFactory = factory };
        engine.UpdateDevices(new[] { device });
        engine.FrameIntervalMs = 10;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnFrame += _ => tcs.TrySetResult(device.LedBytes.ToArray());
        engine.SetEffect(new FillEffect(0, 255, 0));   // shared canvas = green
        var done = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, done);
        return await tcs.Task;
    }

    private static DeviceFrame MakeDevice(int leds = 5) =>
        new(0, "keeb:keys", leds, x: 300, y: 100, w: 40, h: 250, rotation: 90);

    private static StaticDeviceAssignment Assign(string effect) => new() { Effect = effect };

    [Fact]
    public async Task Assigned_effect_overrides_the_shared_canvas()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("keeb:keys", Assign("red"));
        var leds = await RenderOnce(MakeDevice(), tracker, _ => new FillEffect(255, 0, 0));
        Assert.Equal(255, leds[0]);
        Assert.Equal(0, leds[1]);
    }

    [Fact]
    public async Task A_gradient_assignment_paints_DIFFERENT_leds_across_the_device()
    {
        // The flat-colour implementation this replaces could only ever produce
        // one colour here; a ramp must read end to end (fullscreen evaluation).
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("keeb:keys", Assign("ramp"));
        // Wider than tall, so its LEDs run ACROSS the canvas and meet the sweep.
        var wide = new DeviceFrame(0, "keeb:keys", 5, x: 100, y: 100, w: 250, h: 40, rotation: 0);
        var leds = await RenderOnce(wide, tracker, _ => new RampEffect());
        var first = (leds[0], leds[1], leds[2]);
        var last = (leds[12], leds[13], leds[14]);
        Assert.NotEqual(first, last);
        Assert.True(leds[0] > leds[12], "left end should be redder than the right");
        Assert.True(leds[14] > leds[2], "right end should be bluer than the left");
    }

    [Fact]
    public async Task Assignment_is_ignored_outside_static_mode()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = false };
        tracker.Set("keeb:keys", Assign("red"));
        var leds = await RenderOnce(MakeDevice(), tracker, _ => new FillEffect(255, 0, 0));
        Assert.Equal(0, leds[0]);
        Assert.Equal(255, leds[1]);   // shared canvas green
    }

    [Fact]
    public async Task Unassigned_device_still_samples_the_canvas()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("someone-else", Assign("red"));
        var leds = await RenderOnce(MakeDevice(), tracker, _ => new FillEffect(255, 0, 0));
        Assert.Equal(255, leds[1]);
    }

    [Fact]
    public async Task A_factory_that_throws_falls_back_to_the_canvas()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("keeb:keys", Assign("boom"));
        var leds = await RenderOnce(MakeDevice(), tracker, _ => throw new InvalidOperationException("no gpu"));
        Assert.Equal(255, leds[1]);
    }

    /// <summary>Blue at the top, red at the bottom - a VERTICAL sweep.</summary>
    private sealed class VerticalRampEffect : IEffect
    {
        public string Name => "vramp";
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (int y = 0; y < canvas.Height; y++)
                for (int x = 0; x < canvas.Width; x++)
                {
                    var t = canvas.Height > 1 ? (float)y / (canvas.Height - 1) : 0f;
                    canvas.SetPixel(x, y, (byte)(255 * t), 0, (byte)(255 * (1 - t)));
                }
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task A_vertically_swept_pattern_reads_along_a_tall_device()
    {
        // Sampling a fixed horizontal midline collapsed this to one colour on
        // the bench: every LED of a tall strip read the same pixel.
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("keeb:keys", Assign("vramp"));
        // Taller than wide, so its LEDs run down the canvas.
        var tall = new DeviceFrame(0, "keeb:keys", 5, x: 300, y: 100, w: 40, h: 250, rotation: 0);
        var leds = await RenderOnce(tall, tracker, _ => new VerticalRampEffect());
        Assert.NotEqual((leds[0], leds[1], leds[2]), (leds[12], leds[13], leds[14]));
        Assert.True(leds[14] < leds[2], "bottom end should be less blue than the top");
    }

    [Fact]
    public void Key_changes_when_any_control_moves()
    {
        var baseline = new StaticDeviceAssignment { Effect = "gradientlinear", Hue = 0.5f };
        Assert.Equal(baseline.Key(), new StaticDeviceAssignment { Effect = "gradientlinear", Hue = 0.5f }.Key());
        Assert.NotEqual(baseline.Key(), new StaticDeviceAssignment { Effect = "gradientlinear", Hue = 0.6f }.Key());
        Assert.NotEqual(baseline.Key(), new StaticDeviceAssignment { Effect = "gradientlinear", Hue = 0.5f, Colorize = 0.3f }.Key());
        Assert.NotEqual(baseline.Key(), new StaticDeviceAssignment
        {
            Effect = "gradientlinear", Hue = 0.5f,
            Params = new Dictionary<string, float> { ["u_angle"] = 90f },
        }.Key());
    }

    [Fact]
    public void Version_bumps_so_the_engine_drops_stale_renders()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        var v0 = tracker.Version;
        tracker.Set("a", Assign("x"));
        Assert.NotEqual(v0, tracker.Version);
        var v1 = tracker.Version;
        tracker.Clear("a");
        Assert.NotEqual(v1, tracker.Version);
    }

    /// <summary>
    /// An assignment is what the hardware is meant to show, so it must survive a
    /// restart. Without this the UI still listed every pick (it keeps its own
    /// copy) while the devices fell back to the shared canvas on entering
    /// Static - which is exactly how this surfaced on the bench.
    /// </summary>
    [Fact]
    public void Assignments_are_persisted_and_rehydrated()
    {
        var store = new InMemoryConfigStore();
        var first = new StaticDeviceEffectTracker(store) { Enabled = true };
        first.Set("keeb:keys", new StaticDeviceAssignment
        {
            Effect = "gradientlinear",
            Colorize = 0.25f,
            Params = new Dictionary<string, float> { ["u_angle"] = 90f },
        });

        // A fresh tracker over the same store is the restart.
        var reborn = new StaticDeviceEffectTracker(store) { Enabled = true };
        Assert.True(reborn.TryGet("keeb:keys", out var back));
        Assert.Equal("gradientlinear", back.Effect);
        Assert.Equal(0.25f, back.Colorize, 3);
        Assert.NotNull(back.Params);
        Assert.Equal(90f, back.Params!["u_angle"], 3);
        Assert.Equal(first.TryGet("keeb:keys", out var orig) ? orig.Key() : "", back.Key());
    }

    [Fact]
    public void Clearing_an_assignment_removes_it_from_the_store_too()
    {
        var store = new InMemoryConfigStore();
        var tracker = new StaticDeviceEffectTracker(store) { Enabled = true };
        tracker.Set("keeb:keys", new StaticDeviceAssignment { Effect = "simplered" });
        tracker.Clear("keeb:keys");
        Assert.Empty(store.Load().Lighting.StaticDeviceLooks);
        Assert.False(new StaticDeviceEffectTracker(store) { Enabled = true }.TryGet("keeb:keys", out _));
    }

    /// <summary>
    /// A profile switch replaces the whole LightingSettings object. Hydrating
    /// only in the ctor left the engine applying the PREVIOUS profile's
    /// assignments while settings said otherwise.
    /// </summary>
    [Fact]
    public void A_profile_switch_rehydrates_the_tracker()
    {
        var store = new InMemoryConfigStore();
        var tracker = new StaticDeviceEffectTracker(store) { Enabled = true };
        tracker.Set("keeb:keys", new StaticDeviceAssignment { Effect = "simplered" });

        // What a profile activate does: swap the Lighting object wholesale.
        store.Update(s =>
        {
            s.Lighting = new Nexus.Service.Persistence.LightingSettings();
            s.Lighting.StaticDeviceLooks["keeb:keys"] = new Nexus.Service.Persistence.StaticDeviceLook
            {
                Effect = "simplecyan",
            };
        });

        Assert.True(tracker.TryGet("keeb:keys", out var now));
        Assert.Equal("simplecyan", now.Effect);
    }

    [Fact]
    public void A_profile_without_assignments_drops_them()
    {
        var store = new InMemoryConfigStore();
        var tracker = new StaticDeviceEffectTracker(store) { Enabled = true };
        tracker.Set("keeb:keys", new StaticDeviceAssignment { Effect = "simplered" });
        store.Update(s => s.Lighting = new Nexus.Service.Persistence.LightingSettings());
        Assert.False(tracker.TryGet("keeb:keys", out _));
    }

    /// <summary>
    /// Set/Clear write through the store and re-enter the change handler. A
    /// blind rebuild would bump Version every time and throw away the engine's
    /// render cache on every assignment.
    /// </summary>
    [Fact]
    public void Rewriting_the_same_look_does_not_bump_the_version()
    {
        var store = new InMemoryConfigStore();
        var tracker = new StaticDeviceEffectTracker(store) { Enabled = true };
        tracker.Set("keeb:keys", new StaticDeviceAssignment { Effect = "simplered" });
        var settled = tracker.Version;
        tracker.Set("keeb:keys", new StaticDeviceAssignment { Effect = "simplered" });
        // One bump for the write itself, and no extra from the re-entrant hydrate.
        Assert.Equal(settled + 1, tracker.Version);
    }

    /// <summary>
    /// A palette pick is a colour, not an effect: it must reach the LEDs with
    /// no factory involved at all, so a box with no GPU still wears it.
    /// </summary>
    [Fact]
    public async Task A_palette_colour_paints_without_rendering_an_effect()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("keeb:keys", new StaticDeviceAssignment { Effect = "flat", Color = "#ff8000" });
        var leds = await RenderOnce(MakeDevice(), tracker, _ => throw new InvalidOperationException("must not render"));
        Assert.Equal(255, leds[0]);
        Assert.Equal(0x80, leds[1]);
        Assert.Equal(0, leds[2]);
        // Every LED, not just the first: a colour has no gradient to sample.
        Assert.Equal(255, leds[12]);
        Assert.Equal(0x80, leds[13]);
    }

    [Fact]
    public async Task An_unparseable_colour_falls_back_to_the_effect_path()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("keeb:keys", new StaticDeviceAssignment { Effect = "red", Color = "not-a-colour" });
        var leds = await RenderOnce(MakeDevice(), tracker, _ => new FillEffect(255, 0, 0));
        Assert.Equal(255, leds[0]);
        Assert.Equal(0, leds[1]);
    }

    [Fact]
    public void A_palette_colour_survives_a_restart()
    {
        var store = new InMemoryConfigStore();
        var first = new StaticDeviceEffectTracker(store) { Enabled = true };
        first.Set("keeb:keys", new StaticDeviceAssignment { Effect = "flat", Color = "#00ff7f" });

        var reborn = new StaticDeviceEffectTracker(store) { Enabled = true };
        Assert.True(reborn.TryGet("keeb:keys", out var back));
        Assert.Equal("#00ff7f", back.Color);
        Assert.Equal(first.TryGet("keeb:keys", out var orig) ? orig.Key() : "", back.Key());
    }

    [Fact]
    public void Two_colours_of_the_same_effect_are_different_looks()
    {
        var a = new StaticDeviceAssignment { Effect = "flat", Color = "#ff0000" };
        var b = new StaticDeviceAssignment { Effect = "flat", Color = "#00ff00" };
        Assert.NotEqual(a.Key(), b.Key());
    }

    [Theory]
    [InlineData("#ff8000", 255, 128, 0)]
    [InlineData("ff8000", 255, 128, 0)]
    [InlineData("#000000", 0, 0, 0)]
    public void Hex_parses(string hex, int r, int g, int b)
    {
        Assert.True(StaticColorHex.TryParse(hex, out var pr, out var pg, out var pb));
        Assert.Equal(r, pr);
        Assert.Equal(g, pg);
        Assert.Equal(b, pb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#fff")]
    [InlineData("#gggggg")]
    [InlineData("#ff80000")]
    public void Bad_hex_is_rejected(string hex)
    {
        Assert.False(StaticColorHex.TryParse(hex, out _, out _, out _));
    }

    /// <summary>
    /// The hub writers (keeb, NP50, strimer, ...) each read
    /// <see cref="LightingEngine.Devices"/> from their own timer with no lock,
    /// so a frame published in pieces is a frame they can catch half-written.
    /// This drives the engine with a canvas colour that cannot be confused with
    /// the assignment and reads the frame concurrently: every snapshot has to be
    /// wholly one or the other, never a mix.
    /// </summary>
    [Fact]
    public async Task An_assigned_device_is_never_observed_holding_the_canvas_colour()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("keeb:keys", new StaticDeviceAssignment { Effect = "flat", Color = "#ff0000" });
        var device = MakeDevice(32);

        using var engine = new LightingEngine { StaticEffects = tracker };
        engine.UpdateDevices(new[] { device });
        engine.FrameIntervalMs = 5;
        engine.SetEffect(new FillEffect(0, 255, 0));   // canvas = green, assignment = red

        var mixed = 0;
        var sawAssignment = 0;
        var reads = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var reader = Task.Run(() =>
        {
            var buf = new byte[device.LedBytes.Length];
            while (!cts.IsCancellationRequested)
            {
                device.LedBytes.CopyTo(buf);
                reads++;
                for (var i = 0; i + 2 < buf.Length; i += 3)
                {
                    // Any green at all means a canvas write reached a reader on
                    // a frame the assignment owns.
                    if (buf[i + 1] != 0) { Interlocked.Increment(ref mixed); break; }
                }
                if (buf[0] == 255 && buf[1] == 0) Interlocked.Increment(ref sawAssignment);
            }
        });
        await reader;

        Assert.True(reads > 1000, $"reader only sampled {reads} times - too few to be meaningful");
        // Without this the test passes vacuously when the engine never ticks:
        // an all-zero buffer carries no green either.
        Assert.True(sawAssignment > 0, "reader never observed the assignment - the engine did not run");
        Assert.Equal(0, mixed);
    }

    /// <summary>
    /// The publish contract, asserted directly rather than raced for: a pass
    /// that paints only some LEDs must not be visible until the tick ends, and
    /// the LEDs it did not touch must keep what they were last published with.
    /// This is what makes a half-written frame unobservable no matter how many
    /// passes paint, which the "write each LED once" convention could not.
    /// </summary>
    [Fact]
    public void A_frame_is_invisible_until_it_is_published()
    {
        var frame = new DeviceFrame(0, "keeb:keys", 3);
        frame.Fill(255, 255, 255);
        frame.Publish();
        Assert.Equal(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255 }, frame.LedBytes.ToArray());

        // Mid-tick: a second pass repaints one LED. Readers still see the last
        // published frame, not a mix of the two.
        frame.SetLed(1, 0, 0, 0);
        Assert.Equal(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255 }, frame.LedBytes.ToArray());

        frame.Publish();
        Assert.Equal(new byte[] { 255, 255, 255, 0, 0, 0, 255, 255, 255 }, frame.LedBytes.ToArray());

        // The untouched LEDs must not fall back to two frames ago, which is what
        // a plain ping-pong swap would do.
        frame.SetLed(0, 1, 2, 3);
        frame.Publish();
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 0, 255, 255, 255 }, frame.LedBytes.ToArray());
    }

    /// <summary>
    /// Game Sync repaints the keyboard per-LED AFTER the canvas sample, so the
    /// published frame has to be the grid, never the canvas fill underneath it.
    /// </summary>
    [Fact]
    public async Task A_game_sync_keyboard_publishes_the_grid_not_the_canvas_underneath()
    {
        const int cols = 4, rows = 4, leds = cols * rows;
        var device = new DeviceFrame(0, "keeb:keys", leds, x: 100, y: 100, w: 250, h: 90)
        {
            Archetype = "keyboard",
            LedU = Enumerable.Range(0, leds).Select(i => (i % cols) / (float)(cols - 1)).ToArray(),
            LedV = Enumerable.Range(0, leds).Select(i => (i / cols) / (float)(rows - 1)).ToArray(),
        };

        var gs = new GameSyncEffect();
        // Canvas fills white; the grid is all black. Every LED disagrees.
        gs.IngestAuthoredFill(255, 255, 255, "test");
        gs.IngestFrame("keyboard", "CHROMA_CUSTOM", rows, cols, new int[leds]);

        using var engine = new LightingEngine();
        engine.UpdateDevices(new[] { device });
        engine.FrameIntervalMs = 5;
        var painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnFrame += _ => painted.TrySetResult();
        engine.SetEffect(gs);
        Assert.Same(painted.Task, await Task.WhenAny(painted.Task, Task.Delay(2000)));

        var leds8 = device.LedBytes.ToArray();
        Assert.All(leds8, b => Assert.Equal(0, b));
    }

    // --- Locks: a look the device keeps in every mode until the user unlocks it ---

    [Fact]
    public async Task A_locked_assignment_paints_outside_static_mode()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = false };
        tracker.Set("keeb:keys", Assign("red"));
        Assert.True(tracker.SetLocked("keeb:keys", true));
        var leds = await RenderOnce(MakeDevice(), tracker, _ => new FillEffect(255, 0, 0));
        Assert.Equal(255, leds[0]);
        Assert.Equal(0, leds[1]);   // not the shared canvas green
    }

    [Fact]
    public async Task Unlocking_returns_the_device_to_the_running_mode()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = false };
        tracker.Set("keeb:keys", Assign("red"));
        tracker.SetLocked("keeb:keys", true);
        tracker.SetLocked("keeb:keys", false);
        var leds = await RenderOnce(MakeDevice(), tracker, _ => new FillEffect(255, 0, 0));
        Assert.Equal(0, leds[0]);
        Assert.Equal(255, leds[1]);
    }

    [Fact]
    public void A_locked_device_refuses_a_new_pick_and_a_clear()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("keeb:keys", Assign("red"));
        tracker.SetLocked("keeb:keys", true);
        Assert.False(tracker.Set("keeb:keys", Assign("blue")));
        Assert.False(tracker.Clear("keeb:keys"));
        Assert.True(tracker.TryGet("keeb:keys", out var held));
        Assert.Equal("red", held.Effect);
        Assert.True(held.Locked);
        // Unlocked, the same pick lands.
        tracker.SetLocked("keeb:keys", false);
        Assert.True(tracker.Set("keeb:keys", Assign("blue")));
        Assert.True(tracker.TryGet("keeb:keys", out var now));
        Assert.Equal("blue", now.Effect);
    }

    [Fact]
    public void Locking_needs_a_look_to_hold()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        Assert.False(tracker.SetLocked("keeb:keys", true));
        Assert.False(tracker.IsLocked("keeb:keys"));
    }

    [Fact]
    public void A_lock_survives_a_restart_and_a_lock_toggle_does_not_bump_the_version()
    {
        var store = new InMemoryConfigStore();
        var first = new StaticDeviceEffectTracker(store) { Enabled = true };
        first.Set("keeb:keys", Assign("simplered"));
        var version = first.Version;
        first.SetLocked("keeb:keys", true);
        // The render is unchanged, so the engine's cache must not be dropped.
        Assert.Equal(version, first.Version);

        var reborn = new StaticDeviceEffectTracker(store) { Enabled = false };
        Assert.True(reborn.IsLocked("keeb:keys"));
        Assert.True(reborn.TryGet("keeb:keys", out var back));
        Assert.True(back.Locked);
    }

    /// <summary>
    /// A preset restore swaps the looks wholesale. The same colours with a
    /// different lock still has to land, or a preset could never unlock.
    /// </summary>
    [Fact]
    public void A_profile_switch_that_only_changes_the_lock_still_lands()
    {
        var store = new InMemoryConfigStore();
        var tracker = new StaticDeviceEffectTracker(store) { Enabled = false };
        tracker.Set("keeb:keys", Assign("simplered"));
        tracker.SetLocked("keeb:keys", true);
        store.Update(s =>
        {
            s.Lighting = new Nexus.Service.Persistence.LightingSettings();
            s.Lighting.StaticDeviceLooks["keeb:keys"] = new Nexus.Service.Persistence.StaticDeviceLook { Effect = "simplered" };
        });
        Assert.False(tracker.IsLocked("keeb:keys"));
        Assert.False(tracker.TryGet("keeb:keys", out _));
    }

    /// <summary>
    /// Game Sync repaints archetype devices after the sample; a locked look has
    /// to survive that pass the way it survives the canvas sample.
    /// </summary>
    [Fact]
    public async Task A_locked_keyboard_keeps_its_look_under_game_sync()
    {
        const int cols = 4, rows = 4, leds = cols * rows;
        var device = new DeviceFrame(0, "keeb:keys", leds, x: 100, y: 100, w: 250, h: 90)
        {
            Archetype = "keyboard",
            LedU = Enumerable.Range(0, leds).Select(i => (i % cols) / (float)(cols - 1)).ToArray(),
            LedV = Enumerable.Range(0, leds).Select(i => (i / cols) / (float)(rows - 1)).ToArray(),
        };
        var gs = new GameSyncEffect();
        gs.IngestAuthoredFill(255, 255, 255, "test");
        gs.IngestFrame("keyboard", "CHROMA_CUSTOM", rows, cols, new int[leds]);   // all black grid

        var tracker = new StaticDeviceEffectTracker { Enabled = false };
        tracker.Set("keeb:keys", new StaticDeviceAssignment { Effect = "flat", Color = "#ff0000" });
        tracker.SetLocked("keeb:keys", true);

        using var engine = new LightingEngine { StaticEffects = tracker };
        engine.UpdateDevices(new[] { device });
        engine.FrameIntervalMs = 5;
        var painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnFrame += _ => painted.TrySetResult();
        engine.SetEffect(gs);
        Assert.Same(painted.Task, await Task.WhenAny(painted.Task, Task.Delay(2000)));

        var bytes = device.LedBytes.ToArray();
        Assert.Equal(255, bytes[0]);
        Assert.Equal(0, bytes[1]);
        Assert.Equal(0, bytes[2]);
    }
}
