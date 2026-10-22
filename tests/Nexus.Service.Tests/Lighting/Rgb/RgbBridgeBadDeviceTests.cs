using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>One device reporting a garbage zone size (a missing layout entry produced a keyboard zone of size minus 40) must not stop the refresh from seeding the other devices.</summary>
public class RgbBridgeBadDeviceTests : IDisposable
{
    private sealed class FakeController : IRgbController
    {
        private readonly SemaphoreSlim _devicesFetched = new(0);

        public List<RgbDevice> Devices { get; } = new();

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
            return Task.FromResult<IReadOnlyList<RgbDevice>>(Devices);
        }

        public Task<bool> WaitForFetchAsync(TimeSpan timeout) => _devicesFetched.WaitAsync(timeout);

        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Contributes one first-party frame and throws for the device named "Throws", standing in for any per-device failure.</summary>
    private sealed class FakeOwner : ILightingFrameContributor, IOpenRgbDeviceOwner
    {
        public event Action? DevicesChanged { add { } remove { } }

        public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex) =>
            new[] { new DeviceFrame(startingIndex, "first-party-a", 4) };

        public bool OwnsOpenRgbDevice(RgbDevice device) =>
            device.Name == "Throws" ? throw new IndexOutOfRangeException() : false;
    }

    private readonly string _tempDir;
    private readonly FakeController _controller = new();
    private readonly LightingEngine _engine = new();
    private readonly RgbBridge _bridge;

    public RgbBridgeBadDeviceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-bad-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            _controller,
            _engine,
            new TestableConfigStore(Path.Combine(_tempDir, "settings.json")),
            new StubUsbEnumerator(),
            frameContributors: new ILightingFrameContributor[] { new FakeOwner() });
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static RgbZone Zone(string name, int leds) => new()
    {
        Name = name,
        ZoneType = 1,
        LedCount = leds,
        LedsMin = (uint)leds,
        LedsMax = (uint)leds,
    };

    private static RgbDevice RoccatWithZones(int firstZone)
    {
        var d = new RgbDevice { Index = 1, Address = 1, Name = "Roccat Vulcan II Max", Type = 5, LedCount = 0, Location = "HID: roccat" };
        d.Zones.Add(Zone("Keyboard", firstZone));
        foreach (var n in new[] { 15, 3, 1, 3, 1, 1, 16 })
        {
            d.Zones.Add(Zone("Z" + n, n));
        }
        return d;
    }

    private async Task<string[]> RefreshAndGetIdsAsync()
    {
        _bridge.Activate();
        Assert.True(await _controller.WaitForFetchAsync(TimeSpan.FromSeconds(5)));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !_engine.Devices.Any(f => f.Id == "first-party-a"))
        {
            await Task.Delay(10);
        }
        return _engine.Devices.Select(f => f.Id).ToArray();
    }

    private static void AssertNeighborsSeeded(string[] ids)
    {
        Assert.Contains(ids, id => id.StartsWith("openrgb-l-I2C__a", StringComparison.Ordinal));
        Assert.Contains(ids, id => id.StartsWith("openrgb-l-I2C__b", StringComparison.Ordinal));
        Assert.Contains("first-party-a", ids);
    }

    [Theory]
    [InlineData(-40)]
    [InlineData(int.MinValue)]
    public async Task Negative_zone_size_does_not_drop_the_other_devices(int badSize)
    {
        _controller.Devices.Add(new RgbDevice { Index = 0, Address = 0, Name = "Good RAM", LedCount = 10, Location = "I2C: a" });
        _controller.Devices.Add(RoccatWithZones(badSize));
        _controller.Devices.Add(new RgbDevice { Index = 2, Address = 2, Name = "Good GPU", LedCount = 1, Location = "I2C: b" });

        var ids = await RefreshAndGetIdsAsync();

        AssertNeighborsSeeded(ids);
        Assert.All(_engine.Devices, f => Assert.True(f.LedCount >= 0));
    }

    [Fact]
    public async Task A_device_that_throws_while_framing_is_skipped_and_the_rest_are_seeded()
    {
        _controller.Devices.Add(new RgbDevice { Index = 0, Address = 0, Name = "Good RAM", LedCount = 10, Location = "I2C: a" });
        _controller.Devices.Add(new RgbDevice { Index = 1, Address = 1, Name = "Throws", LedCount = 4, Location = "HID: bad" });
        _controller.Devices.Add(new RgbDevice { Index = 2, Address = 2, Name = "Good GPU", LedCount = 1, Location = "I2C: b" });

        var ids = await RefreshAndGetIdsAsync();

        AssertNeighborsSeeded(ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("openrgb-l-HID__bad", StringComparison.Ordinal));
        Assert.Equal(Enumerable.Range(0, _engine.Devices.Length), _engine.Devices.Select(f => f.Index).OrderBy(i => i));
    }
}
