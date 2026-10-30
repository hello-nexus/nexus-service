using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed class UsbMonitorPanelTests : IDisposable
{
    private const string Handler = "usb-panel";
    private const string VirtualDisplayId = "NXS0001-9";

    private readonly string _configPath = Path.Combine(Path.GetTempPath(), "nexus-usbmon-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _storePath = Path.Combine(Path.GetTempPath(), "nexus-usbmon-st-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly JsonConfigStore _config;
    private readonly PanelDeviceRegistry _registry;
    private readonly DeviceControlGate _gate;
    private readonly FakeDiscovery _discovery = new();
    private readonly StreamedPanelCoordinator _coordinator;
    private readonly List<int> _overlayPokes = new();
    private long _nowMs = 1_000_000;

    public UsbMonitorPanelTests()
    {
        _config = new JsonConfigStore(_configPath);
        _registry = new PanelDeviceRegistry(_config);
        _gate = new DeviceControlGate(_config);
        _gate.SetEnabled(Handler, true);
        _discovery.Devices.Add(new StreamedPanelDeviceInfo
        {
            Serial = "usb1",
            Profile = new StreamedPanelProfile
            {
                Kind = Handler,
                Family = Handler,
                DisplayName = "Lian Li Universal Screen 8.8",
                Surface = "monitor",
                CssWidth = 480,
                CssHeight = 1920,
                SupportsSecondaryMonitor = true,
            },
        });
        _coordinator = new StreamedPanelCoordinator(
            new[] { _discovery }, new StreamedPanelStore(_storePath), _registry, _gate,
            notifyOverlay: () => _overlayPokes.Add(1), nowMs: () => _nowMs);
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

    private string PanelId => _registry.List().Single(r => r.Capabilities?.Family == Handler).Id;

    private void SetMonitor(bool on)
    {
        _registry.Patch(PanelId, new PanelDevicePatch { SecondaryMonitor = on });
        _coordinator.ApplySecondaryMonitor(PanelId);
        _discovery.Transport!.Apply();
    }

    private void SetControl(bool on)
    {
        _gate.SetEnabled(Handler, on);
        _coordinator.TickOnce();
    }

    private bool HasSession => _coordinator.LivePanelDeviceIds().Contains(PanelId);

    [Fact]
    public void Control_on_monitor_off_streams_directly_and_projects_nothing()
    {
        Assert.Single(_coordinator.GetAssignments().Assignments);
        Assert.Null(_coordinator.MonitorProjectedPanelId());
        Assert.Null(_coordinator.MonitorOwner());
    }

    [Fact]
    public void Control_on_monitor_on_closes_the_direct_stream_and_projects_the_device_panel()
    {
        SetMonitor(true);

        Assert.Empty(_coordinator.GetAssignments().Assignments);
        Assert.Equal(PanelId, _coordinator.MonitorProjectedPanelId());
        Assert.Equal((PanelId, "Lian Li Universal Screen 8.8"), _coordinator.MonitorOwner());
    }

    [Fact]
    public void Control_off_monitor_on_keeps_the_session_and_monitor_but_drops_the_projected_panel()
    {
        SetMonitor(true);

        SetControl(false);

        Assert.True(HasSession);
        Assert.True(_gate.IsClaimed(Handler));
        Assert.Null(_coordinator.MonitorProjectedPanelId());
        Assert.NotNull(_coordinator.MonitorOwner());
        Assert.False(_discovery.Transport!.Disposed);
    }

    [Fact]
    public void Control_off_monitor_off_releases_the_device()
    {
        SetControl(false);

        Assert.False(_gate.IsClaimed(Handler));
        Assert.False(HasSession);
    }

    [Fact]
    public void Turning_control_back_on_projects_the_panel_again_without_a_new_session()
    {
        SetMonitor(true);
        SetControl(false);
        var transport = _discovery.Transport;

        SetControl(true);

        Assert.Equal(PanelId, _coordinator.MonitorProjectedPanelId());
        Assert.Same(transport, _discovery.Transport);
        Assert.False(transport!.Disposed);
    }

    [Fact]
    public void Monitor_off_while_control_is_off_releases_the_device()
    {
        SetMonitor(true);
        SetControl(false);

        SetMonitor(false);
        _coordinator.TickOnce();

        Assert.False(_gate.IsClaimed(Handler));
        Assert.False(HasSession);
    }

    [Fact]
    public void Device_disconnect_while_monitor_on_ends_the_session_and_the_owner()
    {
        SetMonitor(true);
        _discovery.Devices.Clear();

        _nowMs += 120_000;
        _coordinator.TickOnce();

        Assert.False(HasSession);
        Assert.Null(_coordinator.MonitorOwner());
        Assert.Null(_coordinator.MonitorProjectedPanelId());
        Assert.True(_discovery.Transport!.Disposed);
    }

    [Fact]
    public void A_competing_app_holding_the_device_off_is_not_overridden_by_the_monitor()
    {
        SetMonitor(true);

        _gate.SetPausedByApp(new Dictionary<string, string> { [Handler] = "vendor-app" });

        Assert.False(_gate.IsClaimed(Handler));
    }

    [Fact]
    public void Control_change_pokes_the_overlay()
    {
        var before = _overlayPokes.Count;

        _gate.SetEnabled(Handler, false);

        Assert.True(_overlayPokes.Count > before);
    }

    [Fact]
    public void Assignments_bind_the_virtual_display_to_the_device_panel_and_hide_the_old_record()
    {
        var topology = Topology(out var host);
        var old = _registry.AllocateForDisplay(VirtualDisplayId, "NXS 0001", new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor }, null).Record;
        var other = _registry.AllocateForDisplay("DEL41B7-1", "Desk", new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor }, null).Record;
        SetMonitor(true);
        topology.GetTopology();

        var assignments = DisplayRoutes.ComposeAssignments(_registry, topology, _coordinator);

        Assert.Equal(PanelId, assignments.Single(a => a.DisplayId == VirtualDisplayId).PanelDeviceId);
        Assert.DoesNotContain(assignments, a => a.PanelDeviceId == old.Id);
        Assert.Contains(assignments, a => a.PanelDeviceId == other.Id);
        Assert.NotNull(_registry.Get(old.Id));
        GC.KeepAlive(host);
    }

    [Fact]
    public void Assignments_leave_the_virtual_display_to_the_desktop_while_control_is_off()
    {
        var topology = Topology(out _);
        SetMonitor(true);
        topology.GetTopology();
        SetControl(false);

        var assignments = DisplayRoutes.ComposeAssignments(_registry, topology, _coordinator);

        Assert.DoesNotContain(assignments, a => a.DisplayId == VirtualDisplayId);
    }

    [Fact]
    public void The_virtual_display_is_named_after_the_owning_device_and_managed_by_it()
    {
        var topology = Topology(out _);
        SetMonitor(true);

        var entry = topology.GetTopology().Displays.Single(d => d.Id == VirtualDisplayId);
        var desk = topology.GetTopology().Displays.Single(d => d.Id == "DEL41B7-1");

        Assert.Equal("Lian Li Universal Screen 8.8", entry.Name);
        Assert.True(entry.OwnedByDevicePanel);
        Assert.False(entry.HostingSupported);
        Assert.Equal(PanelId, entry.AssignedPanelDeviceId);
        Assert.Equal("DEL 41B7", desk.Name);
        Assert.False(desk.OwnedByDevicePanel);
    }

    [Fact]
    public void The_virtual_display_keeps_its_windows_name_when_no_device_owns_it()
    {
        var topology = Topology(out _);

        var entry = topology.GetTopology().Displays.Single(d => d.Id == VirtualDisplayId);

        Assert.Equal("NXS 0001", entry.Name);
        Assert.False(entry.OwnedByDevicePanel);
    }

    [Fact]
    public void A_leftover_panel_record_on_the_virtual_display_is_not_listed_as_attached()
    {
        var old = _registry.AllocateForDisplay(VirtualDisplayId, "NXS 0001", new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor }, null).Record;
        var desk = _registry.AllocateForDisplay("DEL41B7-1", "Desk", new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor }, null).Record;
        var devices = new List<PanelDeviceRecord> { old, desk };

        PanelRoutes.MarkDisplayAttached(devices, new HashSet<string> { VirtualDisplayId, "DEL41B7-1" }, VirtualDisplayId);

        Assert.False(old.DisplayAttached);
        Assert.True(desk.DisplayAttached);
    }

    private DisplayTopologyService Topology(out FakeHost host)
    {
        host = new FakeHost(_coordinator);
        var provider = new FakeProvider
        {
            Displays = new List<RawDisplayInfo>
            {
                new() { Id = "DEL41B7-1", Name = "DEL 41B7", Manufacturer = "DEL", Model = "41B7", X = 0, Y = 0, Width = 2560, Height = 1440, ResolutionWidth = 2560, ResolutionHeight = 1440, IsPrimary = true },
                new() { Id = VirtualDisplayId, Name = "NXS 0001", Manufacturer = "NXS", Model = "0001", X = 2560, Y = 0, Width = 480, Height = 1920, ResolutionWidth = 480, ResolutionHeight = 1920, AdapterName = "Owned Adapter" },
            },
        };
        var service = new DisplayTopologyService(provider, _registry, null, host);
        service.DevicePanelOwner = () => _coordinator.MonitorOwner();
        return service;
    }

    private sealed class FakeProvider : IDisplayTopologyProvider
    {
        public List<RawDisplayInfo> Displays { get; init; } = new();
        public bool PositionsAvailable => true;
        public IReadOnlyList<RawDisplayInfo>? Enumerate() => Displays;
    }

    private sealed class FakeHost : IVirtualMonitorHost
    {
        private readonly StreamedPanelCoordinator _coordinator;
        public FakeHost(StreamedPanelCoordinator coordinator) { _coordinator = coordinator; }
        public bool IsAvailable => true;
        public event Action? OwnedDisplayClaimed { add { } remove { } }
        public double? OwnedDisplayDpi(string adapterName) => null;
        public bool OwnsAdapter(string adapterName) => adapterName == "Owned Adapter" && _coordinator.MonitorOwner() is not null;
        public IVirtualMonitor? Create(int width, int height, double? dpi, System.Threading.CancellationToken ct, out string failureState)
        {
            failureState = SecondaryMonitorStates.Failed;
            return null;
        }
    }

    private sealed class MonitorTransport : IStreamedPanelTransport, ISecondaryMonitorTransport
    {
        private Func<bool>? _wants;
        private string? _state;
        public MonitorTransport(string serial) { Serial = serial; }
        public string Serial { get; }
        public bool IsOpen { get; private set; }
        public bool Disposed { get; private set; }
        public string? SecondaryMonitorState => _state;
        public event Action? SecondaryMonitorStateChanged;
        public void BindSecondaryMonitor(Func<bool> source) => _wants = source;
        public void ApplySecondaryMonitor() => Apply();
        public void Apply()
        {
            var next = _wants?.Invoke() == true ? SecondaryMonitorStates.Active : null;
            if (next == _state) return;
            _state = next;
            SecondaryMonitorStateChanged?.Invoke();
        }
        public void Open() => IsOpen = true;
        public void StartPlayer() { }
        public void Write(ReadOnlySpan<byte> payload) { }
        public void Dispose()
        {
            IsOpen = false;
            Disposed = true;
        }
    }

    private sealed class FakeDiscovery : IStreamedPanelDiscovery
    {
        public string HandlerId => Handler;
        public List<StreamedPanelDeviceInfo> Devices { get; } = new();
        public MonitorTransport? Transport { get; private set; }
        public bool Withheld => false;
        public bool ListedWhileWithheld => false;
        public string? WithheldSerial => null;
        public IReadOnlyList<StreamedPanelDeviceInfo> Discover() => Devices.ToList();
        public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info) => Transport = new MonitorTransport(info.Serial);
    }
}
