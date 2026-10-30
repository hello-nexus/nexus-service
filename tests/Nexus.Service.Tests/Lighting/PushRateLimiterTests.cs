using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting;

internal sealed class NullRgbController : IRgbController
{
    public bool IsConnected => false;
    public event Action? DeviceListChanged { add { } remove { } }
    public event Action<bool>? DetectionStateChanged { add { } remove { } }
    public event Action? DetectionProgress { add { } remove { } }
    public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
    public Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
    public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task DisconnectAsync() => Task.CompletedTask;
    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RgbDevice>>(Array.Empty<RgbDevice>());
    public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
    public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class PushRateLimiterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rate-limiter-" + Guid.NewGuid().ToString("N")[..8]);

    public PushRateLimiterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private RgbBridge NewBridge() => new(
        new OpenRgbProcessManager(Path.Combine(_dir, "missing-openrgb")),
        new NullRgbController(),
        new Nexus.Service.Lighting.Engine.LightingEngine(),
        new TestableConfigStore(Path.Combine(_dir, "settings.json")),
        new StubUsbEnumerator());

    [Theory]
    [InlineData(30)]
    [InlineData(20)]
    [InlineData(15)]
    [InlineData(10)]
    [InlineData(5)]
    [InlineData(1)]
    public void Average_rate_over_engine_ticks_matches_the_cap(int hz)
    {
        var limiter = new PushRateLimiter<int>();
        const int tickMs = 33;
        const int seconds = 120;
        var passes = 0;
        for (long now = 0; now < seconds * 1000; now += tickMs)
        {
            if (limiter.TryPass(0, hz, now))
            {
                passes++;
            }
        }
        var avg = passes / (double)seconds;
        Assert.InRange(avg, hz * 0.95, hz * 1.05);
    }

    [Fact]
    public void Twenty_hz_passes_about_two_of_every_three_ticks()
    {
        var limiter = new PushRateLimiter<int>();
        var passes = 0;
        for (var i = 0; i < 300; i++)
        {
            if (limiter.TryPass(0, 20, i * 33L))
            {
                passes++;
            }
        }
        Assert.InRange(passes, 190, 210);
    }

    [Fact]
    public void Raising_the_cap_takes_effect_within_the_new_period()
    {
        var limiter = new PushRateLimiter<int>();
        Assert.True(limiter.TryPass(0, 1, 0));
        Assert.False(limiter.TryPass(0, 1, 20));
        Assert.True(limiter.TryPass(0, 30, 40));
    }

    [Fact]
    public void Cap_of_25_hz_paces_at_exactly_40_ms()
    {
        Assert.Equal(40, PushRateLimiter<int>.PeriodMs(25));
        var limiter = new PushRateLimiter<int>();
        Assert.True(limiter.TryPass(0, 25, 0));
        Assert.False(limiter.TryPass(0, 25, 30));
        Assert.True(limiter.TryPass(0, 25, 40));
    }

    [Fact]
    public void A_long_stall_re_anchors_instead_of_bursting()
    {
        var limiter = new PushRateLimiter<int>();
        Assert.True(limiter.TryPass(0, 10, 0));
        Assert.True(limiter.TryPass(0, 10, 10_000));
        Assert.False(limiter.TryPass(0, 10, 10_033));
        Assert.True(limiter.TryPass(0, 10, 10_100));
    }

    [Fact]
    public void Keys_are_independent_and_zero_cap_never_drops()
    {
        var limiter = new PushRateLimiter<int>();
        Assert.True(limiter.TryPass(0, 1, 1000));
        Assert.True(limiter.TryPass(1, 1, 1000));
        Assert.True(limiter.TryPass(0, 0, 1001));
        Assert.True(limiter.TryPass(0, 0, 1002));
    }

    [Fact]
    public void Frames_sharing_a_device_resolve_the_cap_from_its_stable_id()
    {
        var frames = new[]
        {
            new DeviceFrame(0, "openrgb-s-MB-0", 4, physicalIndex: 2),
            new DeviceFrame(1, "openrgb-s-MB-1", 4, physicalIndex: 2),
            new DeviceFrame(2, "openrgb-s-OTHER", 4, physicalIndex: 3),
            new DeviceFrame(3, "keeb-1", 4, physicalIndex: 2),
        };
        var bridgeIds = new HashSet<string> { "openrgb-s-MB-0", "openrgb-s-MB-1", "openrgb-s-OTHER" };
        var stable = new Dictionary<int, string> { [2] = "openrgb-s-MB", [3] = "openrgb-s-OTHER" };
        var caps = new Dictionary<int, int> { [99] = 1 };

        RgbBridge.ComputePhysicalCaps(frames, bridgeIds, stable, new Dictionary<string, int> { ["openrgb-s-MB"] = 10 }, caps);

        Assert.Equal(new Dictionary<int, int> { [2] = 10 }, caps);
    }

    [Fact]
    public void Dropped_frame_is_delivered_on_a_later_tick_with_the_newest_content()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 0, 0) };
        Assert.True(bridge.ShouldPush(0, buf, 10, 1000));

        buf[0] = new RgbColor(2, 0, 0);
        Assert.False(bridge.ShouldPush(0, buf, 10, 1033));
        buf[0] = new RgbColor(3, 0, 0);
        Assert.False(bridge.ShouldPush(0, buf, 10, 1066));
        Assert.True(bridge.ShouldPush(0, buf, 10, 1100));
        // The send recorded the newest content as the baseline.
        Assert.False(bridge.ShouldPush(0, buf, 10, 1133));
    }

    [Fact]
    public void Identical_frames_do_not_spend_the_gap()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 0, 0) };
        Assert.True(bridge.ShouldPush(0, buf, 10, 1000));
        Assert.False(bridge.ShouldPush(0, buf, 10, 1010));
        buf[0] = new RgbColor(2, 0, 0);
        Assert.False(bridge.ShouldPush(0, buf, 10, 1050));
        Assert.True(bridge.ShouldPush(0, buf, 10, 1100));
    }

    [Fact]
    public void Device_reshuffle_clears_the_gap_state()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 0, 0) };
        Assert.True(bridge.ShouldPush(0, buf, 1, 1000));
        buf[0] = new RgbColor(2, 0, 0);
        Assert.False(bridge.ShouldPush(0, buf, 1, 1100));

        bridge.SyncPhysicalBuffers(Array.Empty<RgbDevice>());
        Assert.True(bridge.ShouldPush(0, buf, 1, 1101));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(30, true)]
    [InlineData(1, true)]
    [InlineData(25, true)]
    [InlineData(24, false)]
    [InlineData(0, false)]
    [InlineData(60, false)]
    [InlineData(-5, false)]
    public void Only_listed_rates_are_valid(int? hz, bool valid)
    {
        Assert.Equal(valid, DeviceRateCap.IsValid(hz));
    }
}
