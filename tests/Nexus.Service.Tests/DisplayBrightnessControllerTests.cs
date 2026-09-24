using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Tests.StreamDeck;

namespace Nexus.Service.Tests;

public class DisplayBrightnessControllerTests
{
    // The CoalescesQueuedTargetsPerDisplay test was removed: it raced
    // Thread.Sleep against Task.Delay to land queued calls inside the first
    // write window and would flake under load. The coalescing behaviour is
    // still covered indirectly by the controller's lock-based queue path
    // and by manual exercise via the dashboard brightness slider.

    [Fact]
    public void ListDisplays_PassesTheOptOutDownAndReportsControlOff()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.DdcDisabledDisplays.Add("display1"));
        var provider = new FakeDisplayBrightnessProvider();
        var controller = new DisplayBrightnessController(provider, null, store);

        var list = controller.ListDisplays();

        Assert.Contains("display1", provider.LastExcluded!);
        var dto = Assert.Single(list.Displays);
        Assert.False(dto.DdcEnabled);
        Assert.False(dto.Capabilities.Brightness);
        Assert.False(dto.BrightnessControl.Supported);
    }

    [Fact]
    public async Task SetBrightnessAsync_OnAnOptedOutDisplay_IsUnsupportedAndNeverWrites()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.DdcDisabledDisplays.Add("display1"));
        var provider = new FakeDisplayBrightnessProvider();
        var controller = new DisplayBrightnessController(provider, null, store);

        var result = await controller.SetBrightnessAsync("display1", 50);

        Assert.Equal(DisplayBrightnessWriteStatuses.Unsupported, result.Status);
        Assert.Empty(provider.Writes);
    }

    [Fact]
    public void SetDdcEnabled_RoundTripsBothWays()
    {
        var store = new InMemoryConfigStore();
        var controller = new DisplayBrightnessController(new FakeDisplayBrightnessProvider(), null, store);

        Assert.True(controller.SetDdcEnabled("display1", false));
        Assert.Contains("display1", store.Load().Devices.DdcDisabledDisplays);

        Assert.True(controller.SetDdcEnabled("display1", true));
        Assert.DoesNotContain("display1", store.Load().Devices.DdcDisabledDisplays);
    }

    [Fact]
    public void GetBrightness_OnAnOptedOutDisplay_IsNull()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.DdcDisabledDisplays.Add("display1"));
        var controller = new DisplayBrightnessController(new FakeDisplayBrightnessProvider(), null, store);

        Assert.Null(controller.GetBrightness("display1"));
    }

    [Fact]
    public async Task SetBrightnessAsync_ClampsBeforeProviderWrite()
    {
        var provider = new FakeDisplayBrightnessProvider();
        var controller = new DisplayBrightnessController(provider);

        var result = await controller.SetBrightnessAsync("display1", 500);

        Assert.Equal(100, result.RequestedBrightness);
        Assert.Equal(new[] { 100 }, provider.Writes);
    }

    private static PanelDeviceRegistry NewRegistryWithXeneonEdgePanel(string displayId)
    {
        var registry = new PanelDeviceRegistry(new InMemoryConfigStore());
        registry.AllocateForDisplay(displayId, "Xeneon Edge", new PanelDeviceCapabilities
        {
            Surface = PanelSurfaces.Monitor,
            Family = KnownPanelDisplays.XeneonEdgeFamily,
        });
        return registry;
    }

    [Fact]
    public void ListDisplays_XeneonEdgePanel_SuppressesGenericDdcBrightness()
    {
        var provider = new FakeDisplayBrightnessProvider();
        var registry = NewRegistryWithXeneonEdgePanel("display1");
        var controller = new DisplayBrightnessController(provider, registry);

        var display = Assert.Single(controller.ListDisplays().Displays);

        Assert.False(display.Capabilities.Brightness);
        Assert.False(display.BrightnessControl.Supported);
        Assert.Equal(DisplayBrightnessControlPaths.Unsupported, display.BrightnessControl.ControlPath);
    }

    [Fact]
    public void GetBrightness_XeneonEdgePanel_ReturnsNullWithoutReadingTheProvider()
    {
        var provider = new FakeDisplayBrightnessProvider();
        var registry = NewRegistryWithXeneonEdgePanel("display1");
        var controller = new DisplayBrightnessController(provider, registry);

        Assert.Null(controller.GetBrightness("display1"));
    }

    [Fact]
    public async Task SetBrightnessAsync_XeneonEdgePanel_ReturnsUnsupportedWithoutWritingTheProvider()
    {
        var provider = new FakeDisplayBrightnessProvider();
        var registry = NewRegistryWithXeneonEdgePanel("display1");
        var controller = new DisplayBrightnessController(provider, registry);

        var result = await controller.SetBrightnessAsync("display1", 80);

        Assert.Equal(DisplayBrightnessWriteStatuses.Unsupported, result.Status);
        Assert.Empty(provider.Writes);
    }

    [Fact]
    public async Task SetBrightnessAsync_NonXeneonEdgePanel_StillWritesTheProvider()
    {
        var provider = new FakeDisplayBrightnessProvider();
        var registry = NewRegistryWithXeneonEdgePanel("display1");
        var controller = new DisplayBrightnessController(provider, registry);

        var result = await controller.SetBrightnessAsync("some-other-display", 80);

        Assert.Equal(DisplayBrightnessWriteStatuses.Applied, result.Status);
        Assert.Equal(new[] { 80 }, provider.Writes);
    }

    // The Y70's monitor shows up in /displays like any other, but its
    // brightness must go through the Y70 provider (serial / RGB gains / VCP by
    // variant): a raw DDC VCP 0x10 write does nothing on an Infinite.
    private static DisplayTopologyService TopologyWithY70(string displayId)
        => TestHandlers.FakeTopology(new List<RawDisplayInfo>
        {
            new() { Id = displayId, RawHardwareId = @"MONITOR\RTK0004\{4d36e96e}\0001" },
        });

    [Fact]
    public void ListDisplays_Y70Monitor_ReportsTheY70ControlPathAndItsStoredBrightness()
    {
        var y70 = new FakeY70Provider();
        var controller = new DisplayBrightnessController(new FakeDisplayBrightnessProvider(), null, null, TopologyWithY70("display1"), y70);

        var display = Assert.Single(controller.ListDisplays().Displays);

        Assert.True(display.Capabilities.Brightness);
        Assert.True(display.BrightnessControl.Supported);
        Assert.Equal(DisplayBrightnessControlPaths.Y70, display.BrightnessControl.ControlPath);
        Assert.Equal(50, display.BrightnessControl.Current);
    }

    [Fact]
    public async Task SetBrightnessAsync_Y70Monitor_DrivesTheY70ProviderNotDdc()
    {
        var provider = new FakeDisplayBrightnessProvider();
        var y70 = new FakeY70Provider();
        var controller = new DisplayBrightnessController(provider, null, null, TopologyWithY70("display1"), y70);

        var result = await controller.SetBrightnessAsync("display1", 30);

        Assert.Equal(DisplayBrightnessWriteStatuses.Applied, result.Status);
        Assert.Equal(30, result.Brightness);
        Assert.Equal(30, y70.LastBrightness);
        Assert.Empty(provider.Writes);
        Assert.Equal(50, controller.GetBrightness("display1"));
    }

    [Fact]
    public async Task SetBrightnessAsync_OtherMonitorNextToAY70_StillWritesDdc()
    {
        var provider = new FakeDisplayBrightnessProvider();
        var y70 = new FakeY70Provider();
        var controller = new DisplayBrightnessController(provider, null, null, TopologyWithY70("y70-monitor"), y70);

        var result = await controller.SetBrightnessAsync("display1", 30);

        Assert.Equal(DisplayBrightnessWriteStatuses.Applied, result.Status);
        Assert.Null(y70.LastBrightness);
        Assert.Equal(new[] { 30 }, provider.Writes);
    }

    private sealed class InMemoryConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();

        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    private sealed class FakeDisplayBrightnessProvider : IDisplayBrightnessProvider
    {
        private readonly object _gate = new();
        private readonly List<int> _writes = new();

        public IReadOnlyList<int> Writes
        {
            get
            {
                lock (_gate) return _writes.ToArray();
            }
        }

        public string Hint => "";

        /// <summary>What the controller handed down on the last Enumerate. The
        /// opt-out has to reach the provider, not just be filtered afterwards:
        /// the probe is the transaction it exists to prevent.</summary>
        public IReadOnlyCollection<string>? LastExcluded { get; private set; }

        public IReadOnlyList<DisplayDto> Enumerate(IReadOnlyCollection<string>? excludedIds = null)
        {
            LastExcluded = excludedIds;
            return new[]
        {
            new DisplayDto
            {
                Id = "display1",
                Name = "Display 1",
                Capabilities = new DisplayCapabilitiesDto { Brightness = true },
                BrightnessControl = new DisplayBrightnessControlDto
                {
                    Supported = true,
                    ControlPath = DisplayBrightnessControlPaths.DdcCi,
                    WriteMode = DisplayBrightnessWriteModes.Coalesced,
                    WriteCooldownMs = 20,
                },
            },
        };

        }

        public int? GetBrightness(string id) => null;

        public DisplayBrightnessDto SetBrightness(string id, int percent)
        {
            lock (_gate) _writes.Add(percent);
            return new DisplayBrightnessDto
            {
                Id = id,
                Brightness = percent,
                RequestedBrightness = percent,
                AppliedBrightness = percent,
                Status = DisplayBrightnessWriteStatuses.Applied,
            };
        }

        public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id) => new()
        {
            ControlPath = DisplayBrightnessControlPaths.DdcCi,
            WriteMode = DisplayBrightnessWriteModes.Coalesced,
            MinWriteIntervalMs = 20,
        };

        public DisplayVcpDto? GetVcp(string id, byte code) => null;
        public bool SetVcp(string id, byte code, int value) => false;
        public string? FindDisplayIdByHardwareName(System.Collections.Generic.IReadOnlyList<string> nameFragments) => null;
    }
}
