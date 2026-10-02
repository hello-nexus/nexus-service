using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// Devices claimed by <see cref="IOpenRgbDeviceOwner"/> contributors must not
/// be seeded as engine frames. Without this exclusion the bridge pushes rendered
/// colors to the OpenRGB representation of a first-party device on every tick,
/// overriding disable and brightness control applied by the first-party writer.
/// </summary>
public class RgbBridgeFirstPartyExclusionTests : IDisposable
{
    private sealed class FakeControllerWithLianLi : IRgbController
    {
        private readonly SemaphoreSlim _devicesFetched = new(0);

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

        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
        {
            _devicesFetched.Release();
            return Task.FromResult<IReadOnlyList<RgbDevice>>(new List<RgbDevice>
            {
                new RgbDevice { Index = 0, Address = 0, Name = "Lian Li Uni Hub SL-Infinity", LedCount = 16, Location = "COM4" },
                new RgbDevice { Index = 1, Address = 1, Name = "Generic ARGB Strip", LedCount = 8, Location = "COM5" },
            });
        }

        public Task<bool> WaitForFetchAsync(TimeSpan timeout) => _devicesFetched.WaitAsync(timeout);

        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLianLiOwner : ILightingFrameContributor, IOpenRgbDeviceOwner
    {
        public event Action? DevicesChanged { add { } remove { } }

        public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex) =>
            Array.Empty<DeviceFrame>();

        public bool OwnsOpenRgbDevice(RgbDevice device) =>
            device.Name.Contains("Lian Li Uni Hub", StringComparison.OrdinalIgnoreCase);
    }

    private readonly string _tempDir;
    private readonly FakeControllerWithLianLi _controller = new();
    private readonly LightingEngine _engine = new();
    private readonly RgbBridge _bridge;

    public RgbBridgeFirstPartyExclusionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-excl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            _controller,
            _engine,
            new TestableConfigStore(Path.Combine(_tempDir, "settings.json")),
            new StubUsbEnumerator(),
            frameContributors: new ILightingFrameContributor[] { new FakeLianLiOwner() });
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Owned_OpenRgb_device_is_excluded_while_others_are_seeded()
    {
        _bridge.Activate();

        // Wait until the bridge has fetched the device list.
        Assert.True(await _controller.WaitForFetchAsync(TimeSpan.FromSeconds(5)));

        // The engine is updated on the refresh task after _bridge.Devices is set;
        // poll the engine itself so the assertions run against the seeded frames,
        // not a window where the refresh committed Devices but not yet the engine.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && _engine.Devices.Length == 0)
        {
            await Task.Yield();
        }

        // The non-owned strip must be seeded (positive control - proves the engine
        // ran and the filter is selective, not "everything excluded"); the
        // FakeLianLiOwner-claimed hub must not, on either ring/zone id.
        var ids = Array.ConvertAll(_engine.Devices, f => f.Id);
        Assert.Contains(ids, id => id.StartsWith("openrgb-l-COM5", StringComparison.Ordinal));
        Assert.DoesNotContain(ids, id => id.StartsWith("openrgb-l-COM4", StringComparison.Ordinal));
    }
}
