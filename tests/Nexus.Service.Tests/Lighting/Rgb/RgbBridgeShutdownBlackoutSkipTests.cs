using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Lighting.Rgb;

public class RgbBridgeShutdownBlackoutSkipTests : IDisposable
{
    private sealed class RecordingController : IRgbController
    {
        private int _pushes;
        public System.Collections.Concurrent.ConcurrentBag<int> PushedAddresses { get; } = new();

        public bool Connected { get; set; } = true;
        public bool IsConnected => Connected;
        public int Pushes => Volatile.Read(ref _pushes);
        public event Action? DeviceListChanged { add { } remove { } }
        public event Action<bool>? DetectionStateChanged;
        public event Action? DetectionProgress { add { } remove { } }
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }

        public void RaiseDetecting(bool detecting) => DetectionStateChanged?.Invoke(detecting);

        public IReadOnlyList<RgbDevice> Devices { get; set; } = Array.Empty<RgbDevice>();
        public async Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) =>
            (await GetDevicesAsync(ct)).Select(d => d.Address).ToList();
        public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default) => Task.FromResult(Devices);
        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;

        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _pushes);
            PushedAddresses.Add(address);
            return Task.CompletedTask;
        }

        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private const int MotherboardAddress = 0;
    private const int GpuAddress = 1;
    private const int DramAddress = 2;

    private readonly string _tempDir;
    private readonly RecordingController _controller = new();
    private readonly LightingEngine _engine = new();
    private readonly RgbBridge _bridge;

    public RgbBridgeShutdownBlackoutSkipTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-shutdownskip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _controller.Devices = new[]
        {
            new RgbDevice
            {
                Index = 0, Address = MotherboardAddress, Name = "X870E AORUS MASTER X3D ICE", LedCount = 4,
                Vendor = "Gigabyte", Location = @"HID: \\?\HID#VID_048D&PID_5711&MI_00#7&1&0&0000#{4d1e55b2}",
            },
            new RgbDevice
            {
                Index = 1, Address = GpuAddress, Name = "Gigabyte GeForce RTX 5070 Ti Aero OC", LedCount = 4,
                Vendor = "Gigabyte", Location = "I2C: NVIDIA i2c adapter 1, address 0x75",
            },
            new RgbDevice { Index = 2, Address = DramAddress, Name = "DRAM", LedCount = 4, Vendor = "ENE", Location = "I2C: SMBus, address 0x71" },
        };
        _bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            _controller,
            _engine,
            new TestableConfigStore(Path.Combine(_tempDir, "settings.json")),
            new StubUsbEnumerator());
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task ActivateAsync()
    {
        _bridge.Activate();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && _bridge.PhysicalBufferCount < 3)
        {
            await Task.Delay(20);
        }
        Assert.Equal(3, _bridge.PhysicalBufferCount);
        _controller.RaiseDetecting(false);
        await Task.Delay(600);
    }

    private static DateTime Until(double seconds) => DateTime.UtcNow + TimeSpan.FromSeconds(seconds);

    [Theory]
    [InlineData("Gigabyte", @"HID: \\?\HID#VID_048D&PID_5711&MI_00#7&1&0&0000", true)]
    [InlineData("Gigabyte", "I2C: NVIDIA i2c adapter 1, address 0x75", true)]
    [InlineData("Gigabyte", "I2C: NVIDIA i2c adapter 1, address 0x71", false)]
    [InlineData("Gigabyte", @"HID: \\?\HID#VID_048D&PID_8297&MI_00#7&1&0&0000", false)]
    [InlineData("ASUS", "I2C: NVIDIA i2c adapter 1, address 0x75", false)]
    [InlineData("ENE", "I2C: SMBus, address 0x71", false)]
    public void Matches_only_the_two_gigabyte_controllers(string vendor, string location, bool expected)
    {
        var d = new RgbDevice { Vendor = vendor, Location = location };
        Assert.Equal(expected, RgbBridge.SkipsBlackoutOnOsShutdown(d));
    }

    [Fact]
    public async Task Os_shutdown_blackout_skips_the_gigabyte_controllers_and_pushes_the_rest()
    {
        await ActivateAsync();

        var report = await _bridge.BlackoutSettledAsync(Until(5), forOsShutdown: true);

        Assert.Equal(BlackoutSkip.None, report.Skipped);
        Assert.Equal(1, report.Pushed);
        Assert.Equal(new[] { DramAddress }, _controller.PushedAddresses.Distinct().ToArray());
    }

    [Fact]
    public async Task Other_blackouts_still_push_every_device()
    {
        await ActivateAsync();

        var report = await _bridge.BlackoutSettledAsync(Until(5));

        Assert.Equal(3, report.Pushed);
        Assert.Equal(new[] { MotherboardAddress, GpuAddress, DramAddress }, _controller.PushedAddresses.Distinct().OrderBy(a => a).ToArray());
    }

    [Fact]
    public async Task Coordinator_skips_on_host_shutdown_but_not_on_suspend()
    {
        await ActivateAsync();
        var coordinator = new SleepBlackoutCoordinator(
            _engine, new TestableConfigStore(Path.Combine(_tempDir, "coordinator-settings.json")), _bridge);

        coordinator.OnSuspending();
        Assert.Contains(GpuAddress, _controller.PushedAddresses);
        Assert.Contains(MotherboardAddress, _controller.PushedAddresses);

        while (_controller.PushedAddresses.TryTake(out _)) { }
        coordinator.OnHostShutdown();
        Assert.Equal(new[] { DramAddress }, _controller.PushedAddresses.Distinct().ToArray());
    }
}
