using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Plugins;
using Xunit;

namespace Nexus.Service.Tests.Plugins;

public class PluginProviderRegistryTests
{
    private static RegisteredProvider Provider(
        string appId,
        IFanControlProvider? fans = null,
        ISensorSource? sensors = null,
        IDeviceHandler? handler = null,
        IDfuFlashTarget? dfu = null)
        => new(
            PluginId: appId,
            Prefix: $"plugin:{appId}:",
            Grant: CapabilityGrant.FirstParty(appId),
            Fans: fans, Sensors: sensors, Handler: handler, Dfu: dfu);

    [Fact]
    public void Empty_registry_exposes_empty_snapshots_and_owns_nothing()
    {
        var r = new PluginProviderRegistry();
        Assert.Empty(r.FanSources);
        Assert.Empty(r.SensorSources);
        Assert.Empty(r.Handlers);
        Assert.Empty(r.DfuTargets);
        Assert.False(r.AssertOwns("plugin", "plugin:plugin:fan0"));
    }

    [Fact]
    public void Add_fan_source_is_prefix_scoped_and_host_built()
    {
        var r = new PluginProviderRegistry();
        r.Add(Provider("aaa", fans: new FakeFans()));

        var src = Assert.Single(r.FanSources);
        // The host builds Owns from the verified prefix - it matches the plugin's
        // own channels and rejects everything else (other plugins, first-party).
        Assert.True(src.Owns("plugin:aaa:fan0"));
        Assert.False(src.Owns("plugin:bbb:fan0"));
        Assert.False(src.Owns("np50:fan0"));
        Assert.False(src.Owns(""));
    }

    [Fact]
    public void AssertOwns_only_for_registered_plugin_with_its_prefix()
    {
        var r = new PluginProviderRegistry();
        r.Add(Provider("aaa", fans: new FakeFans()));

        Assert.True(r.AssertOwns("aaa", "plugin:aaa:fan0"));
        Assert.False(r.AssertOwns("aaa", "plugin:bbb:fan0")); // wrong prefix
        Assert.False(r.AssertOwns("aaa", "np50:fan0"));       // first-party id
        Assert.False(r.AssertOwns("bbb", "plugin:bbb:fan0")); // unregistered plugin
        Assert.False(r.AssertOwns("", "plugin:aaa:fan0"));
        Assert.False(r.AssertOwns("aaa", ""));
    }

    [Fact]
    public void Two_plugins_each_own_only_their_own_prefix()
    {
        var r = new PluginProviderRegistry();
        r.Add(Provider("aaa", fans: new FakeFans()));
        r.Add(Provider("bbb", fans: new FakeFans()));

        Assert.Equal(2, r.FanSources.Length);
        Assert.True(r.AssertOwns("aaa", "plugin:aaa:x"));
        Assert.False(r.AssertOwns("aaa", "plugin:bbb:x"));
        Assert.True(r.AssertOwns("bbb", "plugin:bbb:x"));
        Assert.False(r.AssertOwns("bbb", "plugin:aaa:x"));
    }

    [Fact]
    public void Add_same_plugin_replaces_not_duplicates()
    {
        var r = new PluginProviderRegistry();
        r.Add(Provider("aaa", fans: new FakeFans()));
        r.Add(Provider("aaa", fans: new FakeFans()));
        Assert.Single(r.FanSources);
        Assert.Single(r.PluginIds);
    }

    [Fact]
    public void Remove_clears_the_plugins_sources()
    {
        var r = new PluginProviderRegistry();
        r.Add(Provider("aaa", fans: new FakeFans(), sensors: new FakeSensors(),
            handler: new FakeHandler(), dfu: new FakeDfu()));
        Assert.Single(r.FanSources);
        Assert.Single(r.SensorSources);
        Assert.Single(r.Handlers);
        Assert.Single(r.DfuTargets);

        r.Remove("aaa");
        Assert.Empty(r.FanSources);
        Assert.Empty(r.SensorSources);
        Assert.Empty(r.Handlers);
        Assert.Empty(r.DfuTargets);
        Assert.False(r.AssertOwns("aaa", "plugin:aaa:x"));

        r.Remove("aaa"); // idempotent
        Assert.Empty(r.PluginIds);
    }

    [Fact]
    public void Only_the_facets_a_plugin_provides_appear()
    {
        var r = new PluginProviderRegistry();
        r.Add(Provider("aaa", sensors: new FakeSensors())); // sensors only
        Assert.Empty(r.FanSources);
        Assert.Single(r.SensorSources);
        Assert.Empty(r.Handlers);
        Assert.Empty(r.DfuTargets);
    }

    [Fact]
    public void FirstParty_grant_carries_every_surface()
    {
        var g = CapabilityGrant.FirstParty("aaa");
        Assert.True(g.HasSurface(CapabilityGrant.Surface.Cooling));
        Assert.True(g.HasSurface(CapabilityGrant.Surface.Lighting));
        Assert.True(g.HasSurface(CapabilityGrant.Surface.Sensor));
        Assert.False(g.HasSurface("nonsense"));
    }

    // ── minimal fakes (the registry stores them; it never calls their methods) ──

    private sealed class FakeFans : IFanControlProvider
    {
        public IReadOnlyList<FanChannel> GetFanChannels() => Array.Empty<FanChannel>();
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private sealed class FakeSensors : ISensorSource
    {
        public IReadOnlyList<HardwareSensor> GetSensors() => Array.Empty<HardwareSensor>();
    }

    private sealed class FakeHandler : IDeviceHandler
    {
        public string Id => "fake";
        public string Name => "Fake";
        public string Category => "controller";
        public IReadOnlyList<UsbId> Identifiers => Array.Empty<UsbId>();
        public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) => false;
        public string GetFirmwareVersion() => "";
    }

    private sealed class FakeDfu : IDfuFlashTarget
    {
        public bool IsConnected => false;
        public string FirmwareType => "";
        public bool EnterDfuMode() => false;
    }
}
