using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.PixelFormats;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

/// <summary>
/// A non-square pushed-frame glass rendered turned a quarter: the page draws the swapped size, the real
/// transport turns each frame back to the glass's own layout, and touch is read back through the inverse.
/// </summary>
public class BulkPanelQuarterTurnTests
{
    // Native glass: wider than tall and not a multiple of anything the turn could hide behind.
    private const int NativeW = 24;
    private const int NativeH = 16;
    // What the page renders when turned: the sides swapped.
    private const int RenderW = NativeH;
    private const int RenderH = NativeW;

    public static IEnumerable<object[]> Turns() =>
        from rotation in new[] { 90, 270 }
        from mirror in new[] { false, true }
        select new object[] { rotation, mirror };

    private static byte[] Source(int width, int height)
    {
        var frame = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var o = ((y * width) + x) * 4;
                frame[o] = (byte)x;
                frame[o + 1] = (byte)y;
                frame[o + 2] = (byte)(x ^ (y * 3));
                frame[o + 3] = 255;
            }
        }
        return frame;
    }

    private static (byte, byte, byte, byte) Px(byte[] frame, int width, int x, int y)
    {
        var o = ((y * width) + x) * 4;
        return (frame[o], frame[o + 1], frame[o + 2], frame[o + 3]);
    }

    /// <summary>The clockwise turn of the mirrored source, written out from the geometry rather than from the code under test.</summary>
    private static byte[] Expected(byte[] source, int rotation, bool mirror)
    {
        var expected = new byte[source.Length];
        for (int y = 0; y < NativeH; y++)
        {
            for (int x = 0; x < NativeW; x++)
            {
                // Clockwise: the native column x is the source row from the bottom; counter-clockwise reads it from the top.
                var (sx, sy) = rotation == 90 ? (y, RenderH - 1 - x) : (RenderW - 1 - y, x);
                if (mirror)
                {
                    sx = RenderW - 1 - sx;
                }
                var (b, g, r, a) = Px(source, RenderW, sx, sy);
                var o = ((y * NativeW) + x) * 4;
                expected[o] = b;
                expected[o + 1] = g;
                expected[o + 2] = r;
                expected[o + 3] = a;
            }
        }
        return expected;
    }

    private sealed class TallGlass : IBulkPanelDriver
    {
        public ConcurrentQueue<byte[]> Frames { get; } = new();
        public string HandlerId => "test-quarter";
        public string Name => "Quarter";
        public int VendorId => 0x1234;
        public IReadOnlyList<int> ProductIds { get; } = new[] { 0x5678 };
        public string Surface => PanelSurfaces.LcdSquare;
        public int Fps => 30;
        public byte WritePipeId => 0x01;
        public byte ReadPipeId => 0x81;
        public bool NeedsHidChannel => false;
        public bool HasTouch => true;
        public bool SupportsSecondaryMonitor => true;
        public IPanelTouchDecoder? CreateTouchDecoder() => new Decoder();
        public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid) => (NativeW, NativeH);
        public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
        {
            Frames.Enqueue(bgra.ToArray());
            return true;
        }
        public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid) { }

        // [phase, track, x, y] on the glass's own pixels.
        private sealed class Decoder : IPanelTouchDecoder
        {
            public void Decode(ReadOnlySpan<byte> data, int width, int height, List<PanelTouchReport> output)
            {
                Assert.Equal((NativeW, NativeH), (width, height));
                output.Add(new PanelTouchReport(data[1], (TouchPhase)data[0], data[2], data[3], true));
            }
        }
    }

    private sealed class TouchPipe : IBulkUsbPipe
    {
        public ConcurrentQueue<byte[]> Input { get; } = new();
        public bool Write(ReadOnlySpan<byte> data) => true;
        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => true;
        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (Input.TryDequeue(out var packet))
            {
                packet.CopyTo(buffer);
                return packet.Length;
            }
            Thread.Sleep(Math.Min(timeoutMs, 5));
            return 0;
        }
        public void Dispose() { }
    }

    private static (BulkPanelHub Hub, TallGlass Glass, TouchPipe Pipe) Attach()
    {
        var glass = new TallGlass();
        var pipe = new TouchPipe();
        var hub = new BulkPanelHub(glass);
        Assert.True(hub.Attach(pipe, null));
        return (hub, glass, pipe);
    }

    [Theory]
    [MemberData(nameof(Turns))]
    public void The_glass_gets_its_native_layout_pixel_for_pixel(int rotation, bool mirror)
    {
        var (hub, glass, _) = Attach();
        using var _h = hub;
        using var transport = new BulkPanelStreamTransport(hub, "serial", renderTurned: true);
        transport.BindOrientation(() => (rotation, mirror));
        var source = Source(RenderW, RenderH);

        transport.Write(source);

        var sent = Assert.Single(glass.Frames);
        Assert.Equal(NativeW * NativeH * 4, sent.Length);
        Assert.Equal(Expected(source, rotation, mirror), sent);
    }

    [Theory]
    [MemberData(nameof(Turns))]
    public void Every_glass_pixel_maps_back_to_the_source_pixel_it_shows(int rotation, bool mirror)
    {
        var (hub, glass, _) = Attach();
        using var _h = hub;
        using var transport = new BulkPanelStreamTransport(hub, "serial", renderTurned: true);
        transport.BindOrientation(() => (rotation, mirror));
        var source = Source(RenderW, RenderH);
        transport.Write(source);
        var sent = Assert.Single(glass.Frames);

        for (int y = 0; y < NativeH; y++)
        {
            for (int x = 0; x < NativeW; x++)
            {
                var (sx, sy) = BgraOrientation.MapToSource(x, y, RenderW, RenderH, rotation, mirror, quarterOnRectangle: true);
                Assert.InRange(sx, 0, RenderW - 1);
                Assert.InRange(sy, 0, RenderH - 1);
                Assert.Equal(Px(sent, NativeW, x, y), Px(source, RenderW, sx, sy));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Turns))]
    public void A_touch_on_every_glass_pixel_reaches_the_page_where_that_pixel_was_drawn(int rotation, bool mirror)
    {
        var (hub, glass, pipe) = Attach();
        using var _h = hub;
        using var transport = new BulkPanelStreamTransport(hub, "serial", renderTurned: true);
        transport.BindOrientation(() => (rotation, mirror));
        var contacts = new ConcurrentQueue<PanelTouchContact>();
        transport.BindTouch(contacts.Enqueue);
        transport.Open();
        // The source pixel (x, y) is the colour (x, y, ..), so the glass shows where each touch must land.
        var source = Source(RenderW, RenderH);
        transport.Write(source);
        var sent = Assert.Single(glass.Frames);

        for (int y = 0; y < NativeH; y++)
        {
            for (int x = 0; x < NativeW; x++)
            {
                pipe.Input.Enqueue(new byte[] { (byte)TouchPhase.Down, 0, (byte)x, (byte)y });
                pipe.Input.Enqueue(new byte[] { (byte)TouchPhase.Up, 0, (byte)x, (byte)y });
            }
        }
        Assert.True(SpinWait.SpinUntil(() => contacts.Count >= NativeW * NativeH * 2, 10000));

        var downs = contacts.Where(c => c.Phase == TouchPhase.Down).ToArray();
        Assert.Equal(NativeW * NativeH, downs.Length);
        for (int i = 0; i < downs.Length; i++)
        {
            int x = i % NativeW;
            int y = i / NativeW;
            var (b, g, _, _) = Px(sent, NativeW, x, y);
            // The blue and green channels carry the source column and row of the pixel the glass shows there.
            Assert.Equal((b + 0.5) / RenderW, downs[i].X, 9);
            Assert.Equal((g + 0.5) / RenderH, downs[i].Y, 9);
        }
    }

    [Fact]
    public void A_record_that_has_just_turned_back_keeps_this_sessions_quarter_until_it_is_remade()
    {
        var (hub, glass, _) = Attach();
        using var _h = hub;
        using var transport = new BulkPanelStreamTransport(hub, "serial", renderTurned: true);
        transport.BindOrientation(() => (0, false));
        var source = Source(RenderW, RenderH);

        transport.Write(source);

        Assert.Equal(Expected(source, 90, false), Assert.Single(glass.Frames));
    }

    [Fact]
    public void A_failing_orientation_source_on_a_turned_session_keeps_the_pinned_quarter()
    {
        var (hub, glass, _) = Attach();
        using var _h = hub;
        using var transport = new BulkPanelStreamTransport(hub, "serial", renderTurned: true);
        var fail = false;
        transport.BindOrientation(() => fail ? throw new InvalidOperationException("record gone") : (270, false));
        var source = Source(RenderW, RenderH);

        transport.Write(source);
        fail = true;
        Thread.Sleep(600);
        transport.Write(source);

        var frames = glass.Frames.ToArray();
        Assert.Equal(2, frames.Length);
        Assert.Equal(Expected(source, 270, false), frames[0]);
        Assert.Equal(frames[0], frames[1]);
    }

    [Fact]
    public void Without_a_turned_render_a_quarter_on_rectangular_glass_stays_a_half_turn()
    {
        var (hub, glass, _) = Attach();
        using var _h = hub;
        using var transport = new BulkPanelStreamTransport(hub, "serial");
        transport.BindOrientation(() => (90, false));
        var source = Source(NativeW, NativeH);

        transport.Write(source);

        Assert.Equal(source, Assert.Single(glass.Frames));
    }

    [Theory]
    [MemberData(nameof(Turns))]
    public void A_secondary_monitor_on_turned_glass_is_the_swapped_size_and_its_touch_lands_in_that_space(int rotation, bool mirror)
    {
        var (hub, glass, pipe) = Attach();
        using var _h = hub;
        var host = new SourceMonitorHost(Source(RenderW, RenderH));
        using var transport = new BulkPanelStreamTransport(hub, "serial", host, renderTurned: true);
        transport.BindOrientation(() => (rotation, mirror));
        transport.BindSecondaryMonitor(() => true);
        transport.ApplySecondaryMonitor();

        Assert.True(SpinWait.SpinUntil(() => transport.SecondaryMonitorState == SecondaryMonitorStates.Active, 5000));
        Assert.Equal((RenderW, RenderH), host.Created);
        Assert.Equal(Expected(Source(RenderW, RenderH), rotation, mirror), glass.Frames.First());

        // Glass pixel (3, 5) as the page frame would map it.
        pipe.Input.Enqueue(new byte[] { (byte)TouchPhase.Down, 0, 3, 5 });
        Assert.True(SpinWait.SpinUntil(() => host.Touches.Count >= 1, 5000));
        var (sx, sy) = BgraOrientation.MapToSource(3, 5, RenderW, RenderH, rotation, mirror, quarterOnRectangle: true);
        Assert.True(host.Touches.TryPeek(out var touch));
        Assert.Equal((sx, sy), touch);
    }

    private sealed class SourceMonitorHost(byte[] frame) : IVirtualMonitorHost
    {
        public (int, int) Created { get; private set; }
        public ConcurrentQueue<(int, int)> Touches { get; } = new();
        public bool IsAvailable => true;
        public double? OwnedDisplayDpi(string adapterName) => null;
        public event Action? OwnedDisplayClaimed { add { } remove { } }
        public IVirtualMonitor? Create(int width, int height, double? dpi, CancellationToken ct, out string failureState)
        {
            Created = (width, height);
            failureState = SecondaryMonitorStates.Failed;
            return new Monitor(frame, Touches);
        }

        private sealed class Monitor(byte[] frame, ConcurrentQueue<(int, int)> touches) : IVirtualMonitor
        {
            private int _reads;
            public bool IsAlive => true;
            public bool TryReadFrame(byte[] destination, int timeoutMs, CancellationToken ct)
            {
                if (Interlocked.Increment(ref _reads) == 1)
                {
                    frame.CopyTo(destination, 0);
                    return true;
                }
                ct.WaitHandle.WaitOne(timeoutMs);
                return false;
            }
            public bool InjectTouch(uint pointerId, TouchPhase phase, int x, int y)
            {
                touches.Enqueue((x, y));
                return true;
            }
            public void Dispose() { }
        }
    }

    [Fact]
    public void Square_glass_ignores_a_turned_render()
    {
        var glass = new SquareGlass();
        using var hub = new BulkPanelHub(glass);
        Assert.True(hub.Attach(new TouchPipe(), null));
        using var transport = new BulkPanelStreamTransport(hub, "serial", renderTurned: true);
        transport.BindOrientation(() => (90, false));
        var source = Source(16, 16);

        transport.Write(source);

        var expected = new byte[source.Length];
        BgraQuarterTurn.RotateCw(source, 16, 16, expected);
        Assert.Equal(expected, Assert.Single(glass.Frames));
    }

    private sealed class SquareGlass : IBulkPanelDriver
    {
        public ConcurrentQueue<byte[]> Frames { get; } = new();
        public string HandlerId => "test-square";
        public string Name => "Square";
        public int VendorId => 0x1234;
        public IReadOnlyList<int> ProductIds { get; } = new[] { 0x5678 };
        public string Surface => PanelSurfaces.LcdSquare;
        public int Fps => 30;
        public byte WritePipeId => 0x01;
        public byte ReadPipeId => 0;
        public bool NeedsHidChannel => false;
        public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid) => (16, 16);
        public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
        {
            Frames.Enqueue(bgra.ToArray());
            return true;
        }
        public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid) { }
    }

    [Fact]
    public void Discovery_offers_the_turn_to_every_driver_but_the_one_with_a_portrait_mode()
    {
        var (hub, _, _) = Attach();
        using var _h = hub;
        using var strip = new BulkPanelHub(new UniversalScreen88Driver());
        Assert.True(strip.Attach(new UniversalScreen88Tests.GlassPipe(), null));

        Assert.True(new BulkPanelDiscovery(hub).Discover().Single().Profile.SupportsRenderTurn);
        Assert.False(new BulkPanelDiscovery(strip).Discover().Single().Profile.SupportsRenderTurn);
    }

    [Fact]
    public void A_turned_profile_keeps_the_glass_size_in_the_record_capabilities()
    {
        var (hub, _, _) = Attach();
        using var _h = hub;
        var profile = new BulkPanelDiscovery(hub).Discover().Single().Profile;
        var turned = profile with { CssWidth = profile.CssHeight, CssHeight = profile.CssWidth, RenderTurned = true };

        var caps = turned.BuildCapabilities();

        Assert.Equal((NativeW, NativeH), (caps.CssWidth, caps.CssHeight));
        Assert.Equal((NativeH, NativeW), (turned.CssWidth, turned.CssHeight));
        Assert.False(StreamedPanelCoordinator.ProfilesEqual(profile, turned));
    }
}
