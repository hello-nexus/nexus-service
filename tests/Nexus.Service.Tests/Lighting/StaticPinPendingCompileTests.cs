using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// A static pin on one device renders its design through a shader. While that
/// shader is still compiling the render is skipped; the pin must not cache that
/// blank canvas as the look. Real engine loop and real ShaderEffect; only the GL
/// render is replaced by ShaderEffect.TestRender, so the live ANGLE compile
/// itself is not exercised.
/// </summary>
public class StaticPinPendingCompileTests
{
    private const string DeviceId = "corsair-link:fan";

    public static IEnumerable<object[]> EveryStaticDesign() =>
        StaticEffectCatalog.Fills.Concat(StaticEffectCatalog.Patterns).Select(k => new object[] { k });

    private sealed class Rig : IDisposable
    {
        public readonly LightingEngine Engine = new();
        public readonly DeviceFrame Device = new(0, DeviceId, 5, x: 100, y: 100, w: 250, h: 40, rotation: 0);
        public readonly DevicePinTracker Tracker = new();
        public readonly GpuContext Gpu = new(16, 9, null, () => { });
        public readonly List<ShaderEffect> Built = new();
        public readonly ManualResetEventSlim Gate = new(false);
        public int FailBuilds; // the first N built effects throw on render

        public Rig(DevicePin pin)
        {
            Tracker.Set(DeviceId, pin);
            Engine.Pins = Tracker;
            Engine.StaticPinEffectFactory = _ =>
            {
                var build = Built.Count;
                var fx = new ShaderEffect("static-test", Gpu, "")
                {
                    TestRender = (canvas, _) =>
                    {
                        if (build < FailBuilds)
                        {
                            throw new InvalidOperationException("compile failed");
                        }
                        if (!Gate.IsSet)
                        {
                            return false;
                        }
                        for (var y = 0; y < canvas.Height; y++)
                        {
                            for (var x = 0; x < canvas.Width; x++)
                            {
                                canvas.SetPixel(x, y, 255, 0, 0);
                            }
                        }
                        return true;
                    },
                };
                Built.Add(fx);
                return fx;
            };
            Engine.UpdateDevices(new[] { Device });
            Engine.FrameIntervalMs = 10;
        }

        public void Start() => Engine.SetEffect(new Fill(0, 255, 0)); // shared canvas = green

        public (byte R, byte G, byte B) Led0()
        {
            var b = Device.LedBytes;
            return (b[0], b[1], b[2]);
        }

        public bool WaitFor(Func<bool> cond, int ms = 4000)
        {
            var end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end)
            {
                if (cond())
                {
                    return true;
                }
                Thread.Sleep(5);
            }
            return cond();
        }

        public void Dispose()
        {
            Engine.Dispose();
            Gpu.Dispose();
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
        using var rig = new Rig(Pin(design));
        var seen = new List<(byte, byte, byte)>();
        rig.Engine.OnFrame += _ =>
        {
            lock (seen)
            {
                seen.Add(rig.Led0());
            }
        };
        rig.Start();

        // Pending: the device holds the shared canvas (green), never black.
        Assert.True(rig.WaitFor(() => rig.Led0() == ((byte)0, (byte)255, (byte)0)), "device should show the canvas while the program compiles");
        Thread.Sleep(100);
        Assert.Single(rig.Built);
        Assert.Equal(0, rig.Built[0].DisposeCount);
        Assert.True(rig.Built[0].IsRenderPending);

        // The program lands: same pin, same version, the real look appears.
        var version = rig.Tracker.Version;
        rig.Gate.Set();
        Assert.True(rig.WaitFor(() => rig.Led0() == ((byte)255, (byte)0, (byte)0)), "device should show the design once the program lands");
        Assert.Equal(version, rig.Tracker.Version);
        lock (seen)
        {
            Assert.DoesNotContain(((byte)0, (byte)0, (byte)0), seen);
        }
        Assert.Equal(1, rig.Built[0].DisposeCount); // disposed once, after the real frame was cached
        Assert.Single(rig.Built);
    }

    [Fact]
    public void A_failed_compile_does_not_cache_black_and_a_later_retry_recovers()
    {
        using var rig = new Rig(Pin("gradientlinear")) { FailBuilds = 1 };
        rig.Gate.Set();
        rig.Start();

        Assert.True(rig.WaitFor(() => rig.Built.Count == 1));
        // The failed build leaves the device on the canvas, not black.
        Thread.Sleep(100);
        Assert.Equal(((byte)0, (byte)255, (byte)0), rig.Led0());
        Assert.True(rig.Built[0].HasFailed);

        // The retry builds a second effect that works; the result is not stuck black.
        Assert.True(rig.WaitFor(() => rig.Led0() == ((byte)255, (byte)0, (byte)0), 6000), "a failed look must be retried, not cached as black");
        Assert.Equal(2, rig.Built.Count);
    }

    [Fact]
    public void A_flat_color_pin_paints_without_building_any_shader()
    {
        using var rig = new Rig(new DevicePin { Kind = DevicePinKinds.Static, Color = "#0000ff" });
        rig.Start();
        Assert.True(rig.WaitFor(() => rig.Led0() == ((byte)0, (byte)0, (byte)255)));
        Assert.Empty(rig.Built);
    }

    [Fact]
    public void Changing_the_pin_while_a_compile_is_pending_disposes_the_abandoned_effect_and_builds_the_new_one()
    {
        using var rig = new Rig(Pin("gradientlinear"));
        rig.Start();
        Assert.True(rig.WaitFor(() => rig.Built.Count == 1));
        rig.Tracker.Set(DeviceId, Pin("stripes"));
        Assert.True(rig.WaitFor(() => rig.Built.Count == 2));
        Assert.True(rig.WaitFor(() => rig.Built[0].DisposeCount == 1));
    }
}
