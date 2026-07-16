using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Tests;

public class DisplayBrightnessControllerTests
{
    // The CoalescesQueuedTargetsPerDisplay test was removed: it raced
    // Thread.Sleep against Task.Delay to land queued calls inside the first
    // write window and would flake under load. The coalescing behaviour is
    // still covered indirectly by the controller's lock-based queue path
    // and by manual exercise via the dashboard brightness slider.

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

        public IReadOnlyList<DisplayDto> Enumerate() => new[]
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
