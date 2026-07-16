using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Plugins;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public class CompositeFanControlProviderTests
{
    // Empty fanIds means "calibrate everything" to the motherboard provider, so
    // a selection that filters down to nothing must never reach it.
    [Fact]
    public async Task CalibrateAsync_hub_only_selection_does_not_calibrate_the_motherboard()
    {
        var noPorts = new NoPorts();
        var motherboard = new RecordingFans();
        var composite = new CompositeFanControlProvider(
            motherboard,
            new Np50CoolingProvider(new Np50Hub(noPorts, _ => null!)),
            new MiniHubCoolingProvider(new MiniHubHub(noPorts, _ => null!)),
            new PluginProviderRegistry());

        var results = await composite.CalibrateAsync(
            new[] { "np50:fan0" }, new Progress<FanCalibrationProgress>(), CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(motherboard.Calls);
    }

    [Fact]
    public async Task CalibrateAsync_empty_selection_still_means_all()
    {
        var noPorts = new NoPorts();
        var motherboard = new RecordingFans();
        var composite = new CompositeFanControlProvider(
            motherboard,
            new Np50CoolingProvider(new Np50Hub(noPorts, _ => null!)),
            new MiniHubCoolingProvider(new MiniHubHub(noPorts, _ => null!)),
            new PluginProviderRegistry());

        await composite.CalibrateAsync(
            Array.Empty<string>(), new Progress<FanCalibrationProgress>(), CancellationToken.None);

        Assert.Single(motherboard.Calls);
        Assert.Empty(motherboard.Calls[0]);
    }

    // Regression guard: Extras() must iterate _extras (the platform sources), NOT
    // itself. A self-call recursed infinitely and stack-overflowed the live app on
    // the first GetFanChannels - invisible to other tests because they never build
    // and enumerate the real composite.
    [Fact]
    public void GetFanChannels_aggregates_all_sources_without_recursing()
    {
        var noPorts = new NoPorts();
        var np50 = new Np50CoolingProvider(new Np50Hub(noPorts, _ => null!));
        var miniHub = new MiniHubCoolingProvider(new MiniHubHub(noPorts, _ => null!));

        var registry = new PluginProviderRegistry();
        registry.Add(new RegisteredProvider("aaa", "plugin:aaa:", CapabilityGrant.FirstParty("aaa"),
            Fans: new FakeFans("plugin:aaa:fan0")));

        var composite = new CompositeFanControlProvider(
            new FakeFans("mb:fan0"), np50, miniHub, registry,
            new CompositeFanControlProvider.FanSource(
                id => id.StartsWith("ext:", StringComparison.Ordinal), new FakeFans("ext:fan0")));

        var ids = composite.GetFanChannels().Select(c => c.Id).ToList();

        Assert.Contains("mb:fan0", ids);          // motherboard
        Assert.Contains("ext:fan0", ids);         // platform extra (_extras)
        Assert.Contains("plugin:aaa:fan0", ids);  // registry plugin source
    }

    // A generic provider surfacing an AIO pump head by name (e.g. a Tryx pump
    // wired to a mobo header) is reclassified as a pump; a normal fan and a
    // channel a provider already typed are left alone.
    [Fact]
    public void GetFanChannels_infers_pump_kind_from_hardware_name()
    {
        var noPorts = new NoPorts();
        var np50 = new Np50CoolingProvider(new Np50Hub(noPorts, _ => null!));
        var miniHub = new MiniHubCoolingProvider(new MiniHubHub(noPorts, _ => null!));

        var motherboard = new NamedFans(
            new FanChannel { Id = "mb:fan0", Name = "CPU Fan" },
            new FanChannel { Id = "mb:fan1", Name = "AIO Pump" },
            new FanChannel { Id = "mb:fan2", Name = "water pump 2" });

        var composite = new CompositeFanControlProvider(
            motherboard, np50, miniHub, new PluginProviderRegistry());

        var channels = composite.GetFanChannels().ToDictionary(c => c.Id);

        Assert.Equal(FanKinds.Fan, channels["mb:fan0"].Kind);
        Assert.Equal(FanKinds.Pump, channels["mb:fan1"].Kind);
        Assert.Equal(FanKinds.Pump, channels["mb:fan2"].Kind);
    }

    private sealed class NoPorts : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover() => Array.Empty<Np50PortInfo>();
    }

    private sealed class NamedFans : IFanControlProvider
    {
        private readonly FanChannel[] _channels;
        public NamedFans(params FanChannel[] channels) => _channels = channels;
        public IReadOnlyList<FanChannel> GetFanChannels() => _channels;
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

    private sealed class FakeFans : IFanControlProvider
    {
        private readonly string _id;
        public FakeFans(string id) => _id = id;
        public IReadOnlyList<FanChannel> GetFanChannels() => new[] { new FanChannel { Id = _id, Name = _id } };
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

    /// <summary>Records what the motherboard side was asked to calibrate.</summary>
    private sealed class RecordingFans : IFanControlProvider
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public IReadOnlyList<FanChannel> GetFanChannels() => Array.Empty<FanChannel>();
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
        {
            Calls.Add(fanIds);
            return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
        }
    }
}
