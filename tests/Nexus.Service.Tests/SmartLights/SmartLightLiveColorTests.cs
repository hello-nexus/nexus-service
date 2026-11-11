using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Smart;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.SmartLights;

/// <summary>The dashboard shows a smart light as the bulb does: one averaged color on every LED, or the per-zone colors, at the bulb brightness.</summary>
public class SmartLightLiveColorTests : IDisposable
{
    private sealed class StubDriver : ILightDriver
    {
        public bool Average { get; init; }
        public string Brand => "stub";
        public Task<IReadOnlyList<DiscoveredLight>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DiscoveredLight>>(Array.Empty<DiscoveredLight>());
        public Task<PairResult> PairAsync(DiscoveredLight target, CancellationToken ct) => Task.FromResult(new PairResult());
        public LightFramePlan PlanFrames(SmartLight dev) => new(3, Average);
        public Task SendAsync(SmartLight dev, LightFrame frame, CancellationToken ct) => Task.CompletedTask;
        public Task IdentifyAsync(SmartLight dev, CancellationToken ct) => Task.CompletedTask;
        public int MinIntervalMs(SmartLight dev) => 100;
        public string RateLimitKey(SmartLight dev) => dev.Id;
        public Task<bool> PingAsync(SmartLight dev, CancellationToken ct) => Task.FromResult(true);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-sl-live-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NetworkSendThrottle _throttle = new();

    public SmartLightLiveColorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _throttle.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (SmartLightFrameWriter Writer, LightingEngine Engine, DeviceFrame Frame) Build(bool average)
    {
        var store = new TestableConfigStore(Path.Combine(_dir, "settings.json"));
        store.Update(s =>
        {
            s.SmartLights.Devices.Add(new SmartLightConfig { Id = "stub:1", Brand = "stub", Name = "Bulb" });
            s.SmartLights.BrandEnabled["stub"] = true;
        });
        var provider = new SmartLightProvider(new ILightDriver[] { new StubDriver { Average = average } }, store, _throttle);
        var frame = provider.BuildFrames(0)[0];
        var engine = new LightingEngine();
        engine.Live.Enabled = true;
        byte[] leds = { 30, 60, 90, 90, 120, 150, 150, 180, 210 };
        for (var i = 0; i < 3; i++)
        {
            frame.SetLed(i, leds[i * 3], leds[i * 3 + 1], leds[i * 3 + 2]);
        }
        frame.Publish();
        return (new SmartLightFrameWriter(engine, provider, store), engine, frame);
    }

    [Fact]
    public void AverageToSingle_ShowsTheAveragedColorOnEveryLed_AtBulbBrightness()
    {
        var (writer, engine, frame) = Build(average: true);
        writer.ReportLive(frame, DeviceColorAdjust.Identity, 0.5f);
        // Average (90, 120, 150) at half brightness.
        Assert.Equal(new byte[] { 45, 60, 75, 45, 60, 75, 45, 60, 75 }, engine.Live.TryGetFresh(frame.Id));
    }

    [Fact]
    public void PerZonePlan_ShowsEachZoneAtBulbBrightness()
    {
        var (writer, engine, frame) = Build(average: false);
        writer.ReportLive(frame, DeviceColorAdjust.Identity, 0.5f);
        Assert.Equal(new byte[] { 15, 30, 45, 45, 60, 75, 75, 90, 105 }, engine.Live.TryGetFresh(frame.Id));
    }

    [Fact]
    public void Trim_RunsBeforeTheAverage_WithoutTheOrderSwap()
    {
        var (writer, engine, frame) = Build(average: true);
        var adjust = DeviceColorAdjust.Create(0.5f, 1f, 1f, 0f, 1f, "GRB");
        writer.ReportLive(frame, adjust, 1f);
        Assert.Equal(new byte[] { 45, 120, 150, 45, 120, 150, 45, 120, 150 }, engine.Live.TryGetFresh(frame.Id));
    }
}
