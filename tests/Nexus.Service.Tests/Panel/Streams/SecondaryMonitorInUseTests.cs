using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed class SecondaryMonitorInUseTests : IDisposable
{
    private const string Handler = "twin";

    private readonly string _configPath = Path.Combine(Path.GetTempPath(), "nexus-inuse-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _storePath = Path.Combine(Path.GetTempPath(), "nexus-inuse-st-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly JsonConfigStore _config;
    private readonly PanelDeviceRegistry _registry;
    private readonly TwinDiscovery _discovery = new();
    private readonly StreamedPanelCoordinator _coordinator;

    public SecondaryMonitorInUseTests()
    {
        _config = new JsonConfigStore(_configPath);
        _registry = new PanelDeviceRegistry(_config);
        var gate = new DeviceControlGate(_config);
        gate.SetEnabled(Handler, true);
        foreach (var (serial, name) in new[] { ("a", "Holder Screen"), ("b", "Second Screen") })
        {
            _discovery.Devices.Add(new StreamedPanelDeviceInfo
            {
                Serial = serial,
                Profile = new StreamedPanelProfile
                {
                    Kind = Handler,
                    Family = Handler,
                    DisplayName = name,
                    Surface = "monitor",
                    CssWidth = 480,
                    CssHeight = 1920,
                    SupportsSecondaryMonitor = true,
                },
            });
        }
        _coordinator = new StreamedPanelCoordinator(
            new[] { _discovery }, new StreamedPanelStore(_storePath), _registry, gate,
            notifyOverlay: () => { }, nowMs: () => 1_000_000);
        _coordinator.FadeRunner.AutoRun = false;
        _coordinator.TickOnce();
    }

    public void Dispose()
    {
        _coordinator.StopAsync(default).GetAwaiter().GetResult();
        _coordinator.Dispose();
        _config.Dispose();
        try { File.Delete(_configPath); } catch { }
        try { File.Delete(_storePath); } catch { }
    }

    private string IdOf(string name) => _registry.List().Single(r => r.DisplayName == name).Id;

    [Fact]
    public void The_device_refused_the_monitor_is_never_named_as_its_holder_or_projected()
    {
        string holder = IdOf("Holder Screen");
        string second = IdOf("Second Screen");
        _discovery.Transports["a"].State = SecondaryMonitorStates.Active;
        _discovery.Transports["b"].State = SecondaryMonitorStates.InUse;

        Assert.Equal((holder, "Holder Screen"), _coordinator.MonitorOwner());
        Assert.Equal(SecondaryMonitorStates.InUse, _coordinator.SecondaryMonitorStates()[second]);
    }

    [Fact]
    public void With_only_the_refused_device_left_no_one_holds_the_monitor()
    {
        _discovery.Transports["b"].State = SecondaryMonitorStates.InUse;

        Assert.Null(_coordinator.MonitorOwner());
        Assert.Null(_coordinator.MonitorProjectedPanelId());
    }

    private sealed class TwinTransport : IStreamedPanelTransport, ISecondaryMonitorTransport
    {
        public TwinTransport(string serial) { Serial = serial; }
        public string Serial { get; }
        public bool IsOpen { get; private set; }
        public string? State { get; set; }
        public string? SecondaryMonitorState => State;
        public event Action? SecondaryMonitorStateChanged { add { } remove { } }
        public void BindSecondaryMonitor(Func<bool> source) { }
        public void BindFpsCap(Func<int?> source) { }
        public void ApplySecondaryMonitor() { }
        public void Open() => IsOpen = true;
        public void StartPlayer() { }
        public void Write(ReadOnlySpan<byte> payload) { }
        public void Dispose() => IsOpen = false;
    }

    private sealed class TwinDiscovery : IStreamedPanelDiscovery
    {
        public string HandlerId => Handler;
        public List<StreamedPanelDeviceInfo> Devices { get; } = new();
        public Dictionary<string, TwinTransport> Transports { get; } = new();
        public bool Withheld => false;
        public bool ListedWhileWithheld => false;
        public string? WithheldSerial => null;
        public IReadOnlyList<StreamedPanelDeviceInfo> Discover() => Devices.ToList();
        public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info) => Transports[info.Serial] = new TwinTransport(info.Serial);
    }
}
