using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>Providers fill only the rect and rotation; the bridge sets every frame's flip from the stored layouts.</summary>
public class RgbBridgeFlipTests : IDisposable
{
    private sealed class OneStripController : IRgbController
    {
        public bool IsConnected => true;
        public event Action? DeviceListChanged { add { } remove { } }
        public event Action<bool>? DetectionStateChanged { add { } remove { } }
        public event Action? DetectionProgress { add { } remove { } }
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
        public async Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) =>
            (await GetDevicesAsync(ct)).Select(d => d.Address).ToList();
        public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RgbDevice>>(new List<RgbDevice>
            {
                new RgbDevice { Index = 0, Address = 0, Name = "Generic ARGB Strip", LedCount = 8, Location = "COM5" },
            });
        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HubContributor : ILightingFrameContributor
    {
        public event Action? DevicesChanged { add { } remove { } }
        public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex) =>
            new[] { new DeviceFrame(startingIndex, "hub-fan", 12, rotation: 90) };
    }

    private readonly string _tempDir;
    private readonly LightingEngine _engine = new();
    private readonly TestableConfigStore _store;
    private readonly RgbBridge _bridge;

    public RgbBridgeFlipTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-flip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new TestableConfigStore(Path.Combine(_tempDir, "settings.json"));
        _bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            new OneStripController(),
            _engine,
            _store,
            new StubUsbEnumerator(),
            frameContributors: new ILightingFrameContributor[] { new HubContributor() });
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Refresh_sets_flip_from_the_stored_layout_on_every_frame()
    {
        _store.Update(s => s.Lighting.DeviceLayouts["hub-fan"] = new DeviceLayout { Rotation = 90, Flip = true });

        _bridge.Activate();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && _engine.Devices.Length < 2)
        {
            await Task.Delay(10);
        }
        Assert.True(_engine.Devices.Length >= 2, "the bridge never seeded both frames");

        var hub = Assert.Single(_engine.Devices, f => f.Id == "hub-fan");
        var strip = Assert.Single(_engine.Devices, f => f.Id.StartsWith("openrgb-l-COM5", StringComparison.Ordinal));
        Assert.True(hub.Flip);
        Assert.Equal(90, hub.Rotation);
        Assert.False(strip.Flip);
    }
}
