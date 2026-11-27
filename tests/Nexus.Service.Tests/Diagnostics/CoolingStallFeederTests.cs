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

public class CoolingStallFeederTests
{
    // A MiniHub port that agreed once and then lost agreement reports
    // Rpm = 0 with RpmUnavailable; observing that would read as a stopped fan.
    [Fact]
    public void Tick_skips_channels_whose_rpm_is_unavailable()
    {
        var fans = new ScriptedFans();
        var detector = new CoolingStallDetector();
        var feeder = new CoolingStallFeeder(fans, detector);

        fans.Channel = new FanChannel { Id = "minihub:x:port2", Name = "Port 2 Fans (3)", Rpm = 872, DutyPercent = 100 };
        feeder.Tick();
        Assert.Equal(872, detector.Snapshot().Devices.Single().Rpm);

        fans.Channel = new FanChannel { Id = "minihub:x:port2", Name = "Port 2 Fans (3)", Rpm = 0, DutyPercent = 60, RpmUnavailable = true };
        feeder.Tick();
        // Not observed: the last reading the detector holds is still the agreed one.
        Assert.Equal(872, detector.Snapshot().Devices.Single().Rpm);
    }

    // Control comes from settings: the provider's channel keeps Controlled at its default.
    [Theory]
    [InlineData(false, null, 60)]
    [InlineData(true, "Unresponsive", 0)]
    public void Tick_skips_fans_the_cooling_page_keeps_out(bool control, string? classification, int rpm)
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.FeaturesOnboardingCompleted = true;
            if (!control) s.Cooling.UncontrolledFanChannels.Add("f1");
        });
        var fans = new ScriptedFans { Channel = new FanChannel { Id = "f1", Name = "Rear", Rpm = rpm, DutyPercent = 60, Classification = classification } };
        var detector = new CoolingStallDetector();
        new CoolingStallFeeder(fans, detector, store: store).Tick();

        Assert.Empty(detector.Snapshot().Devices);
    }

    [Fact]
    public void Tick_observes_every_fan_while_the_cooling_feature_is_off()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => { s.FeaturesOnboardingCompleted = true; s.Features.Cooling = false; s.Cooling.UncontrolledFanChannels.Add("f1"); });
        var fans = new ScriptedFans { Channel = new FanChannel { Id = "f1", Name = "Rear", Rpm = 60, DutyPercent = 60 } };
        var detector = new CoolingStallDetector();
        new CoolingStallFeeder(fans, detector, store: store).Tick();

        Assert.Single(detector.Snapshot().Devices);
    }

    private sealed class ScriptedFans : IFanControlProvider
    {
        public FanChannel Channel = new();
        public IReadOnlyList<FanChannel> GetFanChannels() => new[] { Channel };
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
