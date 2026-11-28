using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Persistence;
using Nexus.Service.Plugins;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

/// <summary>
/// The per-channel "not controlled" marker. Its whole reason to exist is that
/// BIOS Control (an absent curve attachment) does not survive a preset apply,
/// so both halves are covered: the write gate and the preset exclusion.
/// </summary>
public class FanControlledStateTests
{
    private static CompositeFanControlProvider Composite(IFanControlProvider motherboard, IConfigStore store)
    {
        var noPorts = new NoPorts();
        return new CompositeFanControlProvider(
            motherboard,
            new Np50CoolingProvider(new Np50Hub(noPorts, _ => null!)),
            new MiniHubCoolingProvider(new MiniHubHub(noPorts, _ => null!)),
            new PluginProviderRegistry(),
            store);
    }

    [Fact]
    public void Uncontrolled_channel_takes_no_curve_write_and_no_manual_write()
    {
        var store = new InMemoryConfigStore();
        var fans = new RecordingWrites();
        var composite = Composite(fans, store);

        composite.DriveFanSpeed("fan-a", 70);
        composite.SetFanSpeed("fan-a", 70);
        Assert.Equal(2, fans.Writes.Count);

        FanControlledState.SetControlled("fan-a", false, store);
        composite.DriveFanSpeed("fan-a", 80);
        composite.SetFanSpeed("fan-a", 80);

        Assert.Equal(2, fans.Writes.Count);
        // A sibling channel is untouched by the marker.
        composite.DriveFanSpeed("fan-b", 40);
        Assert.Equal(3, fans.Writes.Count);
    }

    [Fact]
    public void Release_still_reaches_an_uncontrolled_channel()
    {
        var store = new InMemoryConfigStore();
        var fans = new RecordingWrites();
        var composite = Composite(fans, store);

        FanControlledState.SetControlled("fan-a", false, store);
        composite.ReleaseFan("fan-a");

        // Handing the channel back is the state the marker asks for, so the
        // gate must not swallow it.
        Assert.Equal(new[] { "fan-a" }, fans.Released);
    }

    [Fact]
    public void SetControlled_round_trips_and_is_idempotent()
    {
        var store = new InMemoryConfigStore();

        Assert.True(FanControlledState.IsControlled("fan-a", store.Load()));
        FanControlledState.SetControlled("fan-a", false, store);
        FanControlledState.SetControlled("fan-a", false, store);
        Assert.Single(store.Load().Cooling.UncontrolledFanChannels);
        Assert.False(FanControlledState.IsControlled("fan-a", store.Load()));

        FanControlledState.SetControlled("fan-a", true, store);
        Assert.Empty(store.Load().Cooling.UncontrolledFanChannels);
        Assert.True(FanControlledState.IsControlled("fan-a", store.Load()));
    }

    [Fact]
    public void A_preset_apply_does_not_reclaim_an_uncontrolled_channel()
    {
        var store = new InMemoryConfigStore();
        var fans = new TwoChannelFans();
        FanControlledState.SetControlled("fan-a", false, store);

        FanProfiles.Apply("balanced", fans, store);

        var attached = store.Load().Cooling.Curves
            .SelectMany(c => c.Outputs)
            .Select(o => o.Id)
            .ToHashSet();
        Assert.DoesNotContain("fan-a", attached);
        Assert.Contains("fan-b", attached);
    }

    [Fact]
    public void Preset_derivation_ignores_an_uncontrolled_channel()
    {
        var store = new InMemoryConfigStore();
        var fans = new TwoChannelFans();
        FanControlledState.SetControlled("fan-a", false, store);

        // fan-b alone on the preset curve still reads as that preset, rather
        // than "custom" because fan-a is not on it.
        FanProfiles.Apply("balanced", fans, store);
        Assert.Equal("balanced", FanProfiles.DerivePresetFromCurves(store, fans));
    }

    [Fact]
    public async Task Calibration_skips_an_uncontrolled_channel()
    {
        var store = new InMemoryConfigStore();
        var fans = new TwoChannelFans();
        var composite = Composite(fans, store);
        FanControlledState.SetControlled("fan-a", false, store);

        // Empty means "everything", which is the case that would otherwise
        // ramp duty on a channel the user handed to the motherboard.
        await composite.CalibrateAsync(Array.Empty<string>(), new Progress<FanCalibrationProgress>(), CancellationToken.None);

        Assert.Equal(new[] { "fan-b" }, fans.Calibrated);
    }

    [Fact]
    public void A_reconnecting_uncontrolled_channel_is_released_again()
    {
        // Marking a channel uncontrolled releases it, but that call reaches
        // nothing while its device is offline - so the fan would hold its last
        // driven duty forever once the write gate closed behind it.
        var store = new InMemoryConfigStore();
        var fans = new PresenceFans();
        var engine = new CurveEngine(fans, store, new MultiplexHub());
        FanControlledState.SetControlled("fan-a", false, store);

        fans.Present = false;
        engine.Tick();
        Assert.Empty(fans.Released);

        fans.Present = true;
        engine.Tick();
        Assert.Equal(new[] { "fan-a" }, fans.Released);

        // Once released it stays released; no per-tick re-issue.
        engine.Tick();
        Assert.Single(fans.Released);

        // A disconnect re-arms it, because the hub loses its duty state.
        fans.Present = false;
        engine.Tick();
        fans.Present = true;
        engine.Tick();
        Assert.Equal(2, fans.Released.Count);
    }

    [Fact]
    public void Entering_custom_does_not_restore_a_manual_duty_for_an_uncontrolled_channel()
    {
        // The entry would be inert while the marker stands, then replay onto
        // hardware the moment control came back.
        var store = new InMemoryConfigStore();
        var fans = new TwoChannelFans();
        store.Update(s =>
        {
            s.Cooling.ActivePreset = "off";
            s.Cooling.CustomManualSpeeds = new Dictionary<string, int> { ["fan-a"] = 80, ["fan-b"] = 40 };
        });
        FanControlledState.SetControlled("fan-a", false, store);

        FanProfiles.Apply("custom", fans, store);

        Assert.False(store.Load().Cooling.ManualSpeeds.ContainsKey("fan-a"));
        Assert.Equal(40, store.Load().Cooling.ManualSpeeds["fan-b"]);
    }

    [Fact]
    public void Apply_off_releases_the_fan_leaves_its_curves_drops_its_manual_duty_and_rederives_the_preset()
    {
        var store = new InMemoryConfigStore();
        var fans = new PresenceFans();
        store.Update(s =>
        {
            s.Cooling.ActivePreset = "silent";
            s.Cooling.ManualSpeeds["fan-a"] = 50;
            s.Cooling.Curves.Add(new CurveDocument { Id = "c1", Outputs = new List<CurveOutputDocument> { new() { Id = "fan-a" } } });
        });

        FanControlledState.Apply("fan-a", false, fans, store);

        Assert.Equal(new[] { "fan-a" }, fans.Released);
        Assert.DoesNotContain(store.Load().Cooling.Curves.SelectMany(c => c.Outputs), o => o.Id == "fan-a");
        Assert.False(store.Load().Cooling.ManualSpeeds.ContainsKey("fan-a"));
        Assert.False(FanControlledState.IsControlled("fan-a", store.Load()));
        // The only fan left the preset, so nothing is on it any more.
        Assert.Equal("custom", store.Load().Cooling.ActivePreset);
    }

    [Fact]
    public void Apply_on_clears_the_marker_without_releasing()
    {
        var store = new InMemoryConfigStore();
        var fans = new PresenceFans();
        FanControlledState.SetControlled("fan-a", false, store);

        FanControlledState.Apply("fan-a", true, fans, store);

        Assert.True(FanControlledState.IsControlled("fan-a", store.Load()));
        Assert.Empty(fans.Released);
    }

    private sealed class PresenceFans : IFanControlProvider
    {
        public bool Present { get; set; } = true;
        public List<string> Released { get; } = new();
        public IReadOnlyList<FanChannel> GetFanChannels() => Present
            ? new[] { new FanChannel { Id = "fan-a", Name = "Fan A" } }
            : Array.Empty<FanChannel>();
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) => Released.Add(channelId);
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private sealed class NoPorts : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover() => Array.Empty<Np50PortInfo>();
    }

    private sealed class RecordingWrites : IFanControlProvider
    {
        public List<(string Id, int Duty)> Writes { get; } = new();
        public List<string> Released { get; } = new();
        public IReadOnlyList<FanChannel> GetFanChannels() => Array.Empty<FanChannel>();
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) { Writes.Add((channelId, dutyPercent)); return dutyPercent; }
        public void DriveFanSpeed(string channelId, int dutyPercent) => Writes.Add((channelId, dutyPercent));
        public void ReleaseFan(string channelId) => Released.Add(channelId);
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private sealed class TwoChannelFans : IFanControlProvider
    {
        public List<string> Calibrated { get; } = new();
        private readonly FanChannel[] _channels =
        {
            new FanChannel { Id = "fan-a", Name = "Fan A" },
            new FanChannel { Id = "fan-b", Name = "Fan B" },
        };
        public IReadOnlyList<FanChannel> GetFanChannels() => _channels;
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() =>
            new[] { new TemperatureSource { Id = "cpu", Name = "CPU", Value = 40 } };
        public float? ReadTemperature(string sensorId) => 40f;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
        {
            Calibrated.AddRange(fanIds);
            return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
        }
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
}
