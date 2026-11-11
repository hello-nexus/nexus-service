using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>The OpenRGB bridge reports what it pushes (before the channel swap) to the dashboard sink.</summary>
public class RgbBridgeLiveLedTests : IDisposable
{
    private sealed class QuietController : IRgbController
    {
        public bool IsConnected => true;
        public event Action? DeviceListChanged { add { } remove { } }
        public event Action<bool>? DetectionStateChanged { add { } remove { } }
        public event Action? DetectionProgress { add { } remove { } }
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
        public IReadOnlyList<RgbDevice> Devices { get; set; } = Array.Empty<RgbDevice>();
        public async Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) =>
            (await GetDevicesAsync(ct)).Select(d => d.Address).ToList();
        public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default) => Task.FromResult(Devices);
        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly string _tempDir;
    private readonly LightingEngine _engine = new();
    private readonly TestableConfigStore _store;
    private readonly RgbBridge _bridge;

    public RgbBridgeLiveLedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-liveled-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        var controller = new QuietController
        {
            Devices = new[] { new RgbDevice { Index = 0, Address = 0, Name = "DRAM", LedCount = 4 } },
        };
        _store = new TestableConfigStore(Path.Combine(_tempDir, "settings.json"));
        _bridge = new RgbBridge(new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")), controller, _engine, _store, new StubUsbEnumerator());
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task<DeviceFrame> ActivateAsync()
    {
        _bridge.Activate();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && (_bridge.PhysicalBufferCount == 0 || _engine.Devices.Length == 0))
        {
            await Task.Delay(20);
        }
        Assert.True(_bridge.PhysicalBufferCount > 0);
        var frame = _engine.Devices[0];
        for (var i = 0; i < frame.LedCount; i++)
        {
            frame.SetLed(i, (byte)(200 - i * 20), (byte)(100 + i * 10), (byte)(40 + i));
        }
        frame.Publish();
        return frame;
    }

    private static byte[] Expected(DeviceFrame frame, double mul, DeviceColorAdjust adjust)
    {
        var bytes = new byte[frame.LedCount * 3];
        LiveLedSink.Compute(bytes, frame.LedBytes, frame.LedCount, mul, adjust);
        return bytes;
    }

    [Fact]
    public async Task Dashboard_bytes_are_brightness_and_trim_without_the_order_swap()
    {
        var frame = await ActivateAsync();
        _store.Update(s => s.Devices.LightingDevicePrefs[frame.Id] = new LightingDevicePreference
        {
            Brightness = 60,
            AdjustRed = 1.2f,
            AdjustBlue = 0.7f,
            ColorOrder = "GRB",
        });
        var settings = _store.Load();
        var pref = settings.Devices.LightingDevicePrefs[frame.Id];
        var mul = MasterBrightness.Resolve(settings).Combine(0.6);

        _engine.Live.Enabled = true;
        _engine.SerializeForTest();

        Assert.Equal(Expected(frame, mul, DeviceColorAdjust.For(pref)), _engine.Live.TryGetFresh(frame.Id));
    }

    [Fact]
    public async Task Disabled_device_reports_black_and_uncontrolled_device_reports_nothing()
    {
        var frame = await ActivateAsync();
        _engine.Live.Enabled = true;

        _store.Update(s => s.Devices.DisabledLightingDevices = new List<string> { frame.Id });
        _engine.SerializeForTest();
        Assert.Equal(new byte[frame.LedCount * 3], _engine.Live.TryGetFresh(frame.Id));

        _store.Update(s =>
        {
            s.Devices.DisabledLightingDevices = new List<string>();
            s.Devices.UncontrolledLightingDevices = new List<string> { frame.Id };
        });
        _engine.SerializeForTest();
        Assert.Null(_engine.Live.TryGetFresh(frame.Id));
    }
}
