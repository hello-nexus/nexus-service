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
        var settled = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < settled && (_bridge.DeviceListIsSettling || _engine.Devices.Length < 3))
        {
            await Task.Delay(20);
        }
        Assert.False(_bridge.DeviceListIsSettling);
    }

    private static ReadOnlyMemory<byte> Wire(DeviceFrame[] devices)
    {
        var bytes = new List<byte> { 0x03, 1, 0, 1, 0, 0, 0, 0, (byte)devices.Length };
        foreach (var d in devices)
        {
            bytes.Add(DeviceFrameIndex.WireByte(d));
            bytes.Add((byte)(d.LedCount & 0xFF));
            bytes.Add((byte)(d.LedCount >> 8));
            bytes.AddRange(d.LedBytes.ToArray());
        }
        return bytes.ToArray();
    }

    private void PaintAndPush(byte level)
    {
        foreach (var f in _engine.Devices)
        {
            for (var i = 0; i < f.LedCount; i++)
            {
                f.SetLed(i, (byte)(level + i), level, 10);
            }
            f.Publish();
        }
        _bridge.OnFrame(Wire(_engine.Devices));
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
        _bridge.BeginShutdownBlank();

        var report = await _bridge.BlackoutSettledAsync(Until(5));

        Assert.Equal(BlackoutSkip.None, report.Skipped);
        Assert.Equal(1, report.Pushed);
        Assert.Equal(1, report.Known);
        Assert.Equal("pushed 1/1", report.ToString());
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
    public async Task Engine_driven_fade_and_black_never_write_the_gigabyte_controllers_once_shutdown_starts()
    {
        await ActivateAsync();
        var coordinator = new SleepBlackoutCoordinator(
            _engine, new TestableConfigStore(Path.Combine(_tempDir, "coordinator-settings.json")), _bridge);

        PaintAndPush(100);
        Assert.Contains(GpuAddress, _controller.PushedAddresses);
        Assert.Contains(MotherboardAddress, _controller.PushedAddresses);
        while (_controller.PushedAddresses.TryTake(out _)) { }

        _bridge.BeginShutdownBlank();
        PaintAndPush(60);
        PaintAndPush(30);
        _engine.SetBlackout(true);
        PaintAndPush(0);
        coordinator.OnHostShutdown();
        PaintAndPush(1);

        var written = _controller.PushedAddresses.Distinct().ToArray();
        Assert.Equal(new[] { DramAddress }, written);
        Assert.True(_controller.PushedAddresses.Count(a => a == DramAddress) >= 3);
    }

    [Fact]
    public async Task Suspend_blank_drives_the_gigabyte_controllers_as_before()
    {
        await ActivateAsync();
        var coordinator = new SleepBlackoutCoordinator(
            _engine, new TestableConfigStore(Path.Combine(_tempDir, "coordinator-settings.json")), _bridge);

        coordinator.OnSuspending();
        PaintAndPush(100);

        Assert.Contains(GpuAddress, _controller.PushedAddresses);
        Assert.Contains(MotherboardAddress, _controller.PushedAddresses);
    }
}
