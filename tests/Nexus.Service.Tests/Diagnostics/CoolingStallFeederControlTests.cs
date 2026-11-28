using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class CoolingStallFeederControlTests
{
    [Fact]
    public void An_uncontrolled_channel_is_never_observed()
    {
        var store = new MemoryStore();
        store.Load().FeaturesOnboardingCompleted = true;
        FanControlledState.SetControlled("hdr6", false, store);
        var fans = new ScriptedFans();
        var detector = new CoolingStallDetector();
        var feeder = new CoolingStallFeeder(fans, detector, store: store);

        fans.Channels = new[] { new FanChannel { Id = "hdr6", Name = "Fan #6", Rpm = 0, DutyPercent = 100 } };
        for (var i = 0; i < 3; i++)
        {
            feeder.Tick();
        }

        Assert.Empty(detector.Snapshot().Devices);
    }

    [Fact]
    public void A_channel_switched_off_control_leaves_the_snapshot_at_once()
    {
        var store = new MemoryStore();
        store.Load().FeaturesOnboardingCompleted = true;
        var fans = new ScriptedFans { Channels = new[] { new FanChannel { Id = "hdr6", Name = "Fan #6", Rpm = 900, DutyPercent = 50 } } };
        var detector = new CoolingStallDetector();
        var feeder = new CoolingStallFeeder(fans, detector, store: store);
        feeder.Tick();
        Assert.Single(detector.Snapshot().Devices);

        FanControlledState.SetControlled("hdr6", false, store);
        feeder.Tick();

        Assert.Empty(detector.Snapshot().Devices);
    }

    [Fact]
    public void A_controlled_channel_is_still_observed()
    {
        var store = new MemoryStore();
        store.Load().FeaturesOnboardingCompleted = true;
        var fans = new ScriptedFans { Channels = new[] { new FanChannel { Id = "hdr1", Name = "Fan #1", Rpm = 900, DutyPercent = 50 } } };
        var detector = new CoolingStallDetector();

        new CoolingStallFeeder(fans, detector, store: store).Tick();

        Assert.Equal("hdr1", detector.Snapshot().Devices.Single().Id);
    }

    [Fact]
    public void While_the_cooling_feature_is_off_the_flag_is_not_consulted()
    {
        var store = new MemoryStore();
        store.Load().FeaturesOnboardingCompleted = true;
        FanControlledState.SetControlled("hdr6", false, store);
        store.Load().Features.Cooling = false;
        var fans = new ScriptedFans { Channels = new[] { new FanChannel { Id = "hdr6", Name = "Fan #6", Rpm = 900, DutyPercent = 50 } } };
        var detector = new CoolingStallDetector();

        new CoolingStallFeeder(fans, detector, new Nexus.Service.Lifecycle.FeatureGates(store), store).Tick();

        Assert.Single(detector.Snapshot().Devices);
    }

    [Fact]
    public void A_pump_is_observed_whether_or_not_it_is_under_nexus_control()
    {
        var store = new MemoryStore();
        store.Load().FeaturesOnboardingCompleted = true;
        FanControlledState.SetControlled("pump1", false, store);
        var fans = new ScriptedFans { Channels = new[] { new FanChannel { Id = "pump1", Name = "Pump", Kind = FanKinds.Pump, Rpm = 2000, DutyPercent = 60 } } };
        var detector = new CoolingStallDetector();

        new CoolingStallFeeder(fans, detector, store: store).Tick();

        Assert.Equal("pump", detector.Snapshot().Devices.Single().Type);
    }

    private sealed class MemoryStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) => mutator(_settings);
        public void FlushNow() { }
        public string SettingsPath => "";
        public void Reload() { }
#pragma warning disable CS0067
        public event Action? OnChanged;
#pragma warning restore CS0067
    }

    private sealed class ScriptedFans : IFanControlProvider
    {
        public IReadOnlyList<FanChannel> Channels = Array.Empty<FanChannel>();
        public IReadOnlyList<FanChannel> GetFanChannels() => Channels;
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
}
