using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

public class RgbBridgeOwnershipViewTests : IDisposable
{
    private sealed class FakeController : IRgbController
    {
        public volatile bool Connected = true;
        public volatile IReadOnlyList<RgbDevice> Devices = Array.Empty<RgbDevice>();
        public readonly SemaphoreSlim GetDevicesCalled = new(0);

        public bool IsConnected => Connected;
        public event Action? DeviceListChanged { add { } remove { } }
        public event Action<bool>? DetectionStateChanged;
        public event Action? DetectionProgress { add { } remove { } }
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
        public void RaiseDetection(bool detecting) => DetectionStateChanged?.Invoke(detecting);
        public Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
        public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
        {
            GetDevicesCalled.Release();
            return Task.FromResult(Devices);
        }
        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly string _tempDir;
    private readonly FakeController _controller = new();
    private readonly RgbBridge _bridge;
    private readonly SemaphoreSlim _setChanged = new(0);

    public RgbBridgeOwnershipViewTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-ownview-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _controller.Devices = new[]
        {
            new RgbDevice { Index = 0, Address = 0, Name = "Corsair Commander Core", Location = "HID: /dev/hidraw14", LedCount = 4 },
        };
        _bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            _controller,
            new LightingEngine(),
            new TestableConfigStore(Path.Combine(_tempDir, "settings.json")),
            new StubUsbEnumerator());
        _bridge.DeviceSetChanged += () => _setChanged.Release();
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Inactive_ThenKnownAfterFirstRefresh_ThenResetByDeactivate()
    {
        Assert.False(_bridge.GetOwnershipView().Active);

        _bridge.Activate();
        Assert.True(await _setChanged.WaitAsync(TimeSpan.FromSeconds(5)));
        var view = _bridge.GetOwnershipView();
        Assert.True(view.Active);
        Assert.False(view.ListKnown);

        _controller.RaiseDetection(false);
        view = _bridge.GetOwnershipView();
        Assert.True(view.ListKnown);
        Assert.Equal("HID: /dev/hidraw14", Assert.Single(view.Controllers).Location);

        _bridge.Deactivate();
        view = _bridge.GetOwnershipView();
        Assert.False(view.Active);
        Assert.False(view.ListKnown);
        Assert.Empty(view.Controllers);
    }

    [Fact]
    public async Task Disconnected_IsNotKnown()
    {
        _bridge.Activate();
        Assert.True(await _setChanged.WaitAsync(TimeSpan.FromSeconds(5)));
        _controller.RaiseDetection(false);
        Assert.True(_bridge.GetOwnershipView().ListKnown);
        _controller.Connected = false;
        Assert.False(_bridge.GetOwnershipView().ListKnown);
    }

    [Fact]
    public async Task ListDuringDetection_IsNotKnownUntilDetectionCompletes()
    {
        _bridge.Activate();
        Assert.True(await _setChanged.WaitAsync(TimeSpan.FromSeconds(5)));
        _controller.RaiseDetection(true);
        Assert.False(_bridge.GetOwnershipView().ListKnown);

        _controller.RaiseDetection(false);
        Assert.True(_bridge.GetOwnershipView().ListKnown);
    }
}
