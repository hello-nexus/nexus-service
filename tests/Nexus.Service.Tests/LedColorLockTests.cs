using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Tests;

public class LedColorLockTests
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

    private const string Id = "keeb:keys";

    private static DeviceFrame MakeDevice(int leds = 5) =>
        new(0, Id, leds, x: 300, y: 100, w: 40, h: 250, rotation: 90);

    private static async Task<byte[]> RenderOnce(DeviceFrame device, LedColorLockTracker locks, StaticDeviceEffectTracker? statics = null)
    {
        using var engine = new LightingEngine { LedColorLocks = locks, StaticEffects = statics };
        engine.UpdateDevices(new[] { device });
        engine.FrameIntervalMs = 10;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnFrame += _ => tcs.TrySetResult(device.LedBytes.ToArray());
        engine.SetEffect(new FillEffect(0, 255, 0));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, done);
        return await tcs.Task;
    }

    [Fact]
    public async Task Locked_leds_override_the_canvas_and_the_rest_keep_it()
    {
        var locks = new LedColorLockTracker();
        Assert.True(locks.Set(Id, new[] { 1, 3 }, "#ff0000"));

        var leds = await RenderOnce(MakeDevice(), locks);

        Assert.Equal(new byte[] { 0, 255, 0 }, leds[0..3]);
        Assert.Equal(new byte[] { 255, 0, 0 }, leds[3..6]);
        Assert.Equal(new byte[] { 0, 255, 0 }, leds[6..9]);
        Assert.Equal(new byte[] { 255, 0, 0 }, leds[9..12]);
    }

    [Fact]
    public async Task Locked_leds_override_a_locked_static_look()
    {
        var statics = new StaticDeviceEffectTracker();
        statics.Set(Id, new StaticDeviceAssignment { Effect = "flat", Color = "#0000ff" });
        statics.SetLocked(Id, true);
        var locks = new LedColorLockTracker();
        locks.Set(Id, new[] { 0 }, "#ffffff");

        var leds = await RenderOnce(MakeDevice(), locks, statics);

        Assert.Equal(new byte[] { 255, 255, 255 }, leds[0..3]);
        Assert.Equal(new byte[] { 0, 0, 255 }, leds[3..6]);
    }

    [Fact]
    public async Task A_test_pattern_shows_the_raw_mapping_without_locks()
    {
        var locks = new LedColorLockTracker();
        locks.Set(Id, new[] { 0 }, "#ff0000");
        var device = MakeDevice();
        device.TestPattern = "none";

        var leds = await RenderOnce(device, locks);

        Assert.Equal(new byte[] { 0, 0, 0 }, leds[0..3]);
    }

    [Fact]
    public async Task Under_an_editor_highlight_only_highlighted_leds_show_their_lock()
    {
        var locks = new LedColorLockTracker();
        locks.Set(Id, new[] { 0, 1 }, "#ff0000");
        var device = MakeDevice();
        device.HighlightLeds = new HashSet<int> { 0 };

        var leds = await RenderOnce(device, locks);

        Assert.Equal(new byte[] { 255, 0, 0 }, leds[0..3]);
        Assert.Equal(new byte[] { 0, 0, 0 }, leds[3..6]);
    }

    [Fact]
    public void An_empty_color_unlocks_and_a_bad_one_is_refused()
    {
        var locks = new LedColorLockTracker();
        locks.Set(Id, new[] { 0, 1 }, "#00ff00");
        Assert.True(locks.Set(Id, new[] { 0 }, ""));
        Assert.True(locks.TryGet(Id, out var leds));
        Assert.Equal(new[] { 1 }, leds.Select(l => l.Index));

        Assert.False(locks.Set(Id, new[] { 1 }, "green"));
        Assert.True(locks.Set(Id, new[] { 1 }, null));
        Assert.False(locks.TryGet(Id, out _));
    }

    [Fact]
    public void Locks_are_persisted_rehydrated_and_cleared()
    {
        var store = new InMemoryConfigStore();
        var first = new LedColorLockTracker(store);
        first.Set(Id, new[] { 2, 4 }, "#AABBCC");
        Assert.Equal("#aabbcc", store.Load().Lighting.LedColorLocks[Id][2]);

        var reborn = new LedColorLockTracker(store);
        Assert.True(reborn.TryGet(Id, out var leds));
        Assert.Equal(new LockedLed(2, 0xaa, 0xbb, 0xcc), leds[0]);
        Assert.Equal(4, leds[1].Index);

        reborn.ClearDevice(Id);
        Assert.Empty(store.Load().Lighting.LedColorLocks);
        Assert.False(new LedColorLockTracker(store).TryGet(Id, out _));
    }

    [Fact]
    public void A_profile_switch_rehydrates_the_tracker()
    {
        var store = new InMemoryConfigStore();
        var locks = new LedColorLockTracker(store);
        locks.Set(Id, new[] { 0 }, "#ff0000");

        store.Update(s =>
        {
            s.Lighting = new Nexus.Service.Persistence.LightingSettings();
            s.Lighting.LedColorLocks[Id] = new Dictionary<int, string> { [3] = "#0000ff" };
        });

        Assert.True(locks.TryGet(Id, out var leds));
        Assert.Equal(new LockedLed(3, 0, 0, 255), Assert.Single(leds));
    }
}
