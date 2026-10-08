using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// DeviceSetChanged fires when a refresh changes which devices the engine
/// drives, and stays quiet for a refresh that rebuilds the same set, so the
/// lighting broadcast it drives is one per real change.
/// </summary>
public class RgbBridgeDeviceSetChangedTests : IDisposable
{
    private sealed class ConnectedEmptyController : IRgbController
    {
        public readonly SemaphoreSlim GetDevicesCalled = new(0);

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

        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
        {
            GetDevicesCalled.Release();
            return Task.FromResult<IReadOnlyList<RgbDevice>>(Array.Empty<RgbDevice>());
        }

        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHub : ILightingFrameContributor
    {
        public volatile string[] Ids = Array.Empty<string>();
        public event Action? DevicesChanged;

        public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex) =>
            Ids.Select((id, i) => new DeviceFrame(startingIndex + i, id, 4)).ToList();

        public void Raise() => DevicesChanged?.Invoke();
    }

    private readonly string _tempDir;
    private readonly ConnectedEmptyController _controller = new();
    private readonly FakeHub _hub = new();
    private readonly LightingEngine _engine = new();
    private readonly RgbBridge _bridge;
    // Engine frame count at each DeviceSetChanged, in order.
    private readonly List<int> _framesAtEvent = new();
    private readonly SemaphoreSlim _setChanged = new(0);

    public RgbBridgeDeviceSetChangedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-setchg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            _controller,
            _engine,
            new TestableConfigStore(Path.Combine(_tempDir, "settings.json")),
            new StubUsbEnumerator(),
            frameContributors: new[] { _hub });
        _bridge.DeviceSetChanged += () =>
        {
            lock (_framesAtEvent)
            {
                _framesAtEvent.Add(_engine.Devices.Length);
            }
            _setChanged.Release();
        };
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Fires_once_per_device_set_change_and_not_for_an_unchanged_refresh()
    {
        _hub.Ids = new[] { "hub:a" };
        _bridge.Activate();
        Assert.True(await _setChanged.WaitAsync(TimeSpan.FromSeconds(5)));

        // Same set: the refresh runs (it fetches devices) but raises nothing. It
        // holds the refresh lock from the fetch on, so it finishes before the
        // next refresh can raise.
        while (_controller.GetDevicesCalled.CurrentCount > 0)
        {
            await _controller.GetDevicesCalled.WaitAsync();
        }
        _hub.Raise();
        Assert.True(await _controller.GetDevicesCalled.WaitAsync(TimeSpan.FromSeconds(5)));

        _hub.Ids = new[] { "hub:a", "hub:b" };
        _hub.Raise();
        while (!FramesAtEvent().Contains(2))
        {
            Assert.True(await _setChanged.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(new[] { 1, 2 }, FramesAtEvent());
    }

    private int[] FramesAtEvent()
    {
        lock (_framesAtEvent)
        {
            return _framesAtEvent.ToArray();
        }
    }
}
