using System.Collections.Concurrent;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// A static pin on one device renders its design through a shader. While that
/// shader has not drawn its frame the pin must not cache the blank canvas, must
/// keep the compile alive, and must never show black. Real engine loop, real
/// LightingProvider shader builder and real ShaderEffect; only the GL render is
/// replaced by ShaderEffect.TestRender (except the context-unavailable test,
/// which runs the real no-draw path), so a live ANGLE compile is not exercised.
/// </summary>
public class StaticPinPendingCompileTests
{
    private const string DeviceId = "corsair-link:fan";
    private const string OtherId = "corsair-link:other";

    private static readonly (byte, byte, byte) Green = (0, 255, 0);
    private static readonly (byte, byte, byte) Red = (255, 0, 0);
    private static readonly (byte, byte, byte) Blue = (0, 0, 255);
    private static readonly (byte, byte, byte) Black = (0, 0, 0);

    public static IEnumerable<object[]> EveryStaticDesign() =>
        StaticEffectCatalog.Fills.Concat(StaticEffectCatalog.Patterns).Select(k => new object[] { k });

    private sealed class Rig : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-staticpin-" + Guid.NewGuid().ToString("N")[..8]);
        private readonly object _sync = new();
        private readonly List<ShaderEffect> _built = new();
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _gates = new();
        private readonly List<(byte, byte, byte)> _seenA = new();
        private int _frames;
        private readonly ConcurrentQueue<string> _mismatches = new();

        public readonly LightingEngine Engine = new();
        public readonly DeviceFrame Device = new(0, DeviceId, 5, x: 100, y: 100, w: 250, h: 40, rotation: 0);
        public readonly DeviceFrame Other = new(1, OtherId, 5, x: 100, y: 200, w: 250, h: 40, rotation: 0);
        public readonly DevicePinTracker Tracker = new();
        public readonly GpuContext Gpu = new(160, 90);
        private readonly LightingProvider _provider;
        private readonly JsonConfigStore _store;

        /// <summary>The first N builds throw when rendered.</summary>
        public int FailBuilds;
        /// <summary>False runs the real ShaderEffect render, which on a gated context draws nothing.</summary>
        public bool UseTestRender = true;
        /// <summary>Colors a landed design draws; red unless set.</summary>
        public readonly ConcurrentDictionary<string, (byte, byte, byte)> Colors = new();

        public Rig()
        {
            Directory.CreateDirectory(_dir);
            _store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
            _provider = new LightingProvider(_store, Engine, new LightingOutputHub(), Gpu, new MediaLibrary(), new Nexus.Service.Platform.DefaultMonitorEnumerator());
            var real = Engine.StaticPinEffectFactory!;
            Engine.StaticPinEffectFactory = pin =>
            {
                var fx = (ShaderEffect)real(pin)!;
                if (pin.Effect != fx.Name)
                {
                    _mismatches.Enqueue($"{pin.Effect} built as {fx.Name}");
                }
                if (UseTestRender)
                {
                    var build = Built.Count;
                    fx.TestRender = (canvas, _) =>
                    {
                        if (build < FailBuilds)
                        {
                            throw new InvalidOperationException("compile failed");
                        }
                        if (!Gate(fx.Name).IsSet)
                        {
                            return false;
                        }
                        var (r, g, b) = Colors.TryGetValue(fx.Name, out var c) ? c : Red;
                        canvas.Fill(r, g, b);
                        return true;
                    };
                }
                lock (_sync)
                {
                    _built.Add(fx);
                }
                return fx;
            };
            Engine.Pins = Tracker;
            Engine.UpdateDevices(new[] { Device, Other });
            Engine.FrameIntervalMs = 10;
            Engine.OnFrame += _ =>
            {
                lock (_sync)
                {
                    _frames++;
                    _seenA.Add(Led(Device));
                    Monitor.PulseAll(_sync);
                }
            };
        }

        public ManualResetEventSlim Gate(string design) => _gates.GetOrAdd(design, _ => new ManualResetEventSlim(false));

        public List<ShaderEffect> Built
        {
            get
            {
                lock (_sync)
                {
                    return new List<ShaderEffect>(_built);
                }
            }
        }

        /// <summary>Every state device A showed on a published frame.</summary>
        public List<(byte, byte, byte)> Seen()
        {
            lock (_sync)
            {
                return new List<(byte, byte, byte)>(_seenA);
            }
        }

        public void Start() => Engine.SetEffect(new Fill(0, 255, 0)); // shared canvas = green

        public static (byte, byte, byte) Led(DeviceFrame d)
        {
            var b = d.LedBytes;
            return (b[0], b[1], b[2]);
        }

        /// <summary>Waits on engine frames, not the clock, until the condition holds.</summary>
        public bool WaitFor(Func<bool> cond, int ms = 6000)
        {
            var end = Environment.TickCount64 + ms;
            lock (_sync)
            {
                while (!cond())
                {
                    var left = end - Environment.TickCount64;
                    if (left <= 0)
                    {
                        return false;
                    }
                    Monitor.Wait(_sync, (int)Math.Min(left, 50));
                }
            }
            return true;
        }

        public void WaitFrames(int n)
        {
            int target;
            lock (_sync)
            {
                target = _frames + n;
            }
            Assert.True(WaitFor(() => _frames >= target));
        }

        public void Dispose()
        {
            _provider.Dispose();
            Gpu.Dispose();
            Assert.Empty(_mismatches);
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    private sealed class Fill : IEffect
    {
        private readonly byte _r, _g, _b;
        public Fill(byte r, byte g, byte b) { _r = r; _g = g; _b = b; }
        public string Name => "fill";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) => canvas.Fill(_r, _g, _b);
        public void Dispose() { }
    }

    private static DevicePin Pin(string effect) => new() { Kind = DevicePinKinds.Static, Effect = effect };

    [Theory]
    [MemberData(nameof(EveryStaticDesign))]
    public void A_static_pin_never_caches_a_frame_from_a_shader_that_is_still_compiling(string design)
    {
        using var rig = new Rig();
        rig.Tracker.Set(DeviceId, Pin(design));
        rig.Start();

        // Pending: the device holds the shared canvas (green), never black.
        Assert.True(rig.WaitFor(() => Rig.Led(rig.Device) == Green), "device should show the canvas while the program compiles");
        rig.WaitFrames(5);
        var built = rig.Built;
        Assert.Single(built);
        Assert.Equal(design, built[0].Name);
        Assert.Equal(0, built[0].DisposeCount);

        // The program lands: same pin, same version, the real look appears.
        var version = rig.Tracker.Version;
        rig.Gate(design).Set();
        Assert.True(rig.WaitFor(() => Rig.Led(rig.Device) == Red), "device should show the design once the program lands");
        Assert.Equal(version, rig.Tracker.Version);
        Assert.DoesNotContain(Black, rig.Seen());
        Assert.True(rig.WaitFor(() => built[0].DisposeCount == 1));
        Assert.Single(rig.Built);
    }

    [Fact]
    public void A_render_that_draws_nothing_without_pending_or_failed_is_not_cached_and_is_given_up_on()
    {
        // Context unavailable: the real ShaderEffect skips the frame with neither flag set.
        using var rig = new Rig { UseTestRender = false };
        rig.Engine.AssignPendingBudgetMs = 200;
        rig.Gpu.OpenUpgradeGateForTest();
        rig.Tracker.Set(DeviceId, Pin("gradientlinear"));
        rig.Start();

        Assert.True(rig.WaitFor(() => Rig.Led(rig.Device) == Green));
        var first = rig.Built[0];
        Assert.False(first.IsRenderPending);
        Assert.False(first.HasFailed);
        Assert.False(first.DrewFrame);

        // Bounded: the never-drawing effect is dropped after its budget and rebuilt on the backoff, not polled forever.
        Assert.True(rig.WaitFor(() => first.DisposeCount == 1));
        Assert.True(rig.WaitFor(() => rig.Built.Count >= 2));
        Assert.Equal(Green, Rig.Led(rig.Device));
        Assert.DoesNotContain(Black, rig.Seen());
    }

    [Fact]
    public void DrewFrame_is_set_only_by_a_render_that_wrote_the_canvas()
    {
        using var gpu = new GpuContext(16, 9);
        using var fx = new ShaderEffect("x", gpu, "");
        var canvas = new CanvasBuffer(16, 9);
        var lands = false;
        fx.TestRender = (c, _) =>
        {
            if (lands)
            {
                c.Fill(1, 2, 3);
            }
            return lands;
        };
        fx.RenderFrame(canvas, 0);
        Assert.False(fx.DrewFrame);
        lands = true;
        fx.RenderFrame(canvas, 0);
        Assert.True(fx.DrewFrame);
        lands = false;
        fx.RenderFrame(canvas, 0);
        Assert.False(fx.DrewFrame);
    }

    [Fact]
    public void A_failed_compile_keeps_the_canvas_not_black_and_a_later_retry_recovers()
    {
        using var rig = new Rig { FailBuilds = 1 };
        rig.Gate("gradientlinear").Set();
        rig.Tracker.Set(DeviceId, Pin("gradientlinear"));
        rig.Start();

        Assert.True(rig.WaitFor(() => rig.Built.Count == 1 && rig.Built[0].HasFailed));
        rig.WaitFrames(5);
        Assert.Equal(Green, Rig.Led(rig.Device));

        Assert.True(rig.WaitFor(() => Rig.Led(rig.Device) == Red), "a failed look must be retried, not cached as black");
        Assert.Equal(2, rig.Built.Count);
        Assert.DoesNotContain(Black, rig.Seen());
    }

    [Fact]
    public void A_shader_that_always_fails_backs_off_instead_of_rebuilding_every_second()
    {
        using var rig = new Rig { FailBuilds = int.MaxValue };
        rig.Tracker.Set(DeviceId, Pin("gradientlinear"));
        rig.Start();

        // Exponential builds land at 0, base and 3x base; a fixed-interval retry would make a fourth within the window.
        Assert.False(rig.WaitFor(() => rig.Built.Count > 3, (int)(LightingEngine.AssignRetryBaseMs * 3 + 500)));
        Assert.InRange(rig.Built.Count, 1, 3);
        Assert.DoesNotContain(Black, rig.Seen());
        Assert.Equal(Green, Rig.Led(rig.Device));
    }

    [Fact]
    public void A_flat_color_pin_paints_without_building_any_shader()
    {
        using var rig = new Rig();
        rig.Tracker.Set(DeviceId, new DevicePin { Kind = DevicePinKinds.Static, Color = "#0000ff" });
        rig.Start();
        Assert.True(rig.WaitFor(() => Rig.Led(rig.Device) == Blue));
        Assert.Empty(rig.Built);
    }

    [Fact]
    public void Changing_the_pin_while_a_compile_is_pending_frees_the_abandoned_effect_and_keeps_the_new_one_alive()
    {
        using var rig = new Rig();
        rig.Tracker.Set(DeviceId, Pin("gradientlinear"));
        rig.Start();
        Assert.True(rig.WaitFor(() => rig.Built.Count == 1));
        rig.WaitFrames(5);
        Assert.Equal(0, rig.Built[0].DisposeCount);
        Assert.True(rig.Built[0].IsRenderPending);

        rig.Tracker.Set(DeviceId, Pin("stripes"));
        Assert.True(rig.WaitFor(() => rig.Built.Count == 2));
        Assert.True(rig.WaitFor(() => rig.Built[0].DisposeCount == 1));
        rig.WaitFrames(5);
        Assert.Equal(0, rig.Built[1].DisposeCount);
        Assert.True(rig.Built[1].IsRenderPending);
    }

    [Fact]
    public void A_pin_change_on_another_device_leaves_a_cached_look_alone()
    {
        using var rig = new Rig();
        rig.Gate("gradientlinear").Set();
        rig.Tracker.Set(DeviceId, Pin("gradientlinear"));
        rig.Start();
        Assert.True(rig.WaitFor(() => Rig.Led(rig.Device) == Red));

        // Another device's pin is pending, then edited repeatedly: each edit bumps the tracker version.
        rig.Tracker.Set(OtherId, Pin("stripes"));
        for (var hue = 1; hue <= 5; hue++)
        {
            rig.Tracker.Set(OtherId, new DevicePin { Kind = DevicePinKinds.Static, Effect = "stripes", Hue = hue / 10f });
            rig.WaitFrames(3);
        }
        rig.WaitFrames(5);

        // Device A never left its look and its effect was built exactly once.
        var seen = rig.Seen();
        var firstRed = seen.IndexOf(Red);
        Assert.DoesNotContain(seen.Skip(firstRed), c => c != Red);
        Assert.Single(rig.Built, b => b.Name == "gradientlinear");
    }

    [Fact]
    public void A_key_change_holds_the_last_finished_look_until_the_new_compile_lands()
    {
        using var rig = new Rig();
        rig.Colors["stripes"] = Blue;
        rig.Gate("gradientlinear").Set();
        rig.Tracker.Set(DeviceId, Pin("gradientlinear"));
        rig.Start();
        Assert.True(rig.WaitFor(() => Rig.Led(rig.Device) == Red));

        rig.Tracker.Set(DeviceId, Pin("stripes")); // gate closed: pending
        Assert.True(rig.WaitFor(() => rig.Built.Any(b => b.Name == "stripes")));
        rig.WaitFrames(10);
        Assert.Equal(Red, Rig.Led(rig.Device)); // old look, not the global green and not black

        rig.Gate("stripes").Set();
        Assert.True(rig.WaitFor(() => Rig.Led(rig.Device) == Blue));
        var seen = rig.Seen();
        Assert.DoesNotContain(Black, seen);
        Assert.DoesNotContain(seen.Skip(seen.IndexOf(Red)), c => c == Green);
    }

    [Fact]
    public void A_device_removed_while_its_compile_is_pending_frees_the_effect()
    {
        using var rig = new Rig();
        rig.Tracker.Set(DeviceId, Pin("gradientlinear"));
        rig.Start();
        Assert.True(rig.WaitFor(() => rig.Built.Count == 1));
        rig.WaitFrames(3);
        Assert.Equal(0, rig.Built[0].DisposeCount);

        rig.Engine.UpdateDevices(new[] { rig.Other });
        Assert.True(rig.WaitFor(() => rig.Built[0].DisposeCount == 1));
    }

    [Fact]
    public void A_paused_engine_converges_without_rendering_the_main_effect_again()
    {
        using var rig = new Rig();
        rig.Tracker.Set(DeviceId, Pin("gradientlinear"));
        var main = new CountingFill();
        rig.Engine.SetEffect(main);
        Assert.True(rig.WaitFor(() => rig.Built.Count == 1));
        rig.Engine.SetPaused(true);
        Assert.True(rig.WaitFor(() => rig.Engine.Paused));
        rig.WaitFrames(3);
        var rendersAtPause = main.Renders;
        rig.WaitFrames(10);
        Assert.Equal(Green, Rig.Led(rig.Device));

        rig.Gate("gradientlinear").Set();
        Assert.True(rig.WaitFor(() => Rig.Led(rig.Device) == Red), "a pending look must finish while paused");
        Assert.Equal(rendersAtPause, main.Renders);
    }

    private sealed class CountingFill : IEffect
    {
        private int _renders;
        public int Renders => Volatile.Read(ref _renders);
        public string Name => "counting";
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            Interlocked.Increment(ref _renders);
            canvas.Fill(0, 255, 0);
        }
        public void Dispose() { }
    }
}
