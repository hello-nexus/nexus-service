using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// RequestTopologyRefresh fallback: with the bridge active but the controller
/// transiently disconnected, a partition save must queue the debounced
/// refresh instead of dropping it, so engine frames rebuild as soon as the
/// controller is reachable again rather than waiting for the periodic poll.
/// </summary>
public class RgbBridgeTopologyRefreshTests : IDisposable
{
    private sealed class FakeRgbController : IRgbController
    {
        public volatile bool Connected;
        public readonly SemaphoreSlim TryConnectCalled = new(0);
        public readonly SemaphoreSlim GetDevicesCalled = new(0);

        public bool IsConnected => Connected;
        public event Action? DeviceListChanged { add { } remove { } }
        public event Action<bool>? DetectionStateChanged { add { } remove { } }
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
        public async Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) =>
            (await GetDevicesAsync(ct)).Select(d => d.Address).ToList();
        public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> TryConnectAsync(CancellationToken ct = default)
        {
            TryConnectCalled.Release();
            return Task.FromResult(Connected);
        }

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

    private readonly string _tempDir;
    private readonly FakeRgbController _controller = new();
    private readonly RgbBridge _bridge;

    public RgbBridgeTopologyRefreshTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-bridge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        // The process manager points at a nonexistent binary so Start() is a
        // harmless no-op; the bridge still flips to active.
        _bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            _controller,
            new LightingEngine(),
            new TestableConfigStore(Path.Combine(_tempDir, "settings.json")),
            new StubUsbEnumerator());
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Disconnected_refresh_request_is_queued_not_dropped()
    {
        _bridge.Activate();
        // Wait until the activation connect attempt has observed the
        // disconnected controller, so a late EnsureConnectedAsync can't be
        // the path that triggers the device fetch asserted below.
        Assert.True(await _controller.TryConnectCalled.WaitAsync(TimeSpan.FromSeconds(5)));

        _bridge.RequestTopologyRefresh();
        _controller.Connected = true;

        // The queued debounced refresh must fetch devices well before the
        // periodic poll would (the poll cadence is longer than this wait).
        Assert.True(await _controller.GetDevicesCalled.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
