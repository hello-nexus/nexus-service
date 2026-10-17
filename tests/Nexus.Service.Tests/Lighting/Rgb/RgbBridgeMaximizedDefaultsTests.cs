using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// The engine frames a refresh builds follow the install's default placement:
/// unplaced frames cover the canvas only on a MaximizedCanvasDefaults install.
/// </summary>
public sealed class RgbBridgeMaximizedDefaultsTests : IDisposable
{
    private sealed class ConnectedEmptyController : IRgbController
    {
        public bool IsConnected => true;
        public event Action? DeviceListChanged { add { } remove { } }
        public event Action<bool>? DetectionStateChanged { add { } remove { } }
        public event Action? DetectionProgress { add { } remove { } }
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
        public Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
        public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RgbDevice>>(Array.Empty<RgbDevice>());
        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHub : ILightingFrameContributor
    {
        public event Action? DevicesChanged { add { } remove { } }

        public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex) => new[]
        {
            new DeviceFrame(startingIndex, "hub:placed", 4, x: 10, y: 20, w: 140, h: 120),
            new DeviceFrame(startingIndex + 1, "hub:new", 4, x: 10, y: 20, w: 140, h: 120),
        };
    }

    private readonly string _tempDir;

    public RgbBridgeMaximizedDefaultsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-maxdef-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unplaced_frames_cover_the_canvas_only_on_a_flagged_install(bool flagged)
    {
        var store = new TestableConfigStore(Path.Combine(_tempDir, $"settings-{flagged}.json"));
        store.Update(s =>
        {
            s.MaximizedCanvasDefaults = flagged;
            s.Lighting.DeviceLayouts["hub:placed"] = new DeviceLayout { X = 10, Y = 20, W = 140, H = 120 };
        });
        var engine = new LightingEngine();
        var setChanged = new SemaphoreSlim(0);
        var bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            new ConnectedEmptyController(),
            engine,
            store,
            new StubUsbEnumerator(),
            frameContributors: new[] { new FakeHub() });
        bridge.DeviceSetChanged += () => setChanged.Release();
        try
        {
            bridge.Activate();
            Assert.True(await setChanged.WaitAsync(TimeSpan.FromSeconds(5)));

            var placed = engine.Devices.Single(f => f.Id == "hub:placed");
            var fresh = engine.Devices.Single(f => f.Id == "hub:new");
            Assert.Equal((10f, 20f, 140f, 120f), (placed.X, placed.Y, placed.W, placed.H));
            Assert.Equal(
                flagged ? (0f, 0f, 1000f, 600f) : (10f, 20f, 140f, 120f),
                (fresh.X, fresh.Y, fresh.W, fresh.H));
        }
        finally
        {
            bridge.Dispose();
            bridge.AwaitShutdown();
        }
    }
}
