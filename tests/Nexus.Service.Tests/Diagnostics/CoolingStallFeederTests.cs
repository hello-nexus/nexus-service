using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Models.Cooling;
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

    [Fact]
    public void Tick_skips_channels_on_a_super_io_reading_all_ones()
    {
        var fans = new ScriptedFans();
        var detector = new CoolingStallDetector();
        var feeder = new CoolingStallFeeder(fans, detector);

        fans.Channel = new FanChannel { Id = "lpc:pump", Name = "AIO Pump", Kind = FanKinds.Pump, Rpm = 2800, DutyPercent = 100 };
        feeder.Tick();
        fans.Channel = new FanChannel { Id = "lpc:pump", Name = "AIO Pump", Kind = FanKinds.Pump, Rpm = 0, DutyPercent = 100, SuperIoReadInvalid = true };
        feeder.Tick();
        Assert.Equal(2800, detector.Snapshot().Devices.Single().Rpm);

        // The same zero without the flag is observed.
        fans.Channel = new FanChannel { Id = "lpc:pump", Name = "AIO Pump", Kind = FanKinds.Pump, Rpm = 0, DutyPercent = 100 };
        feeder.Tick();
        Assert.Equal(0, detector.Snapshot().Devices.Single().Rpm);
    }

    [Fact]
    public void Flag_is_not_serialized()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new FanChannel { SuperIoReadInvalid = true }, Nexus.Service.Serialization.AppJsonContext.Default.FanChannel);
        Assert.DoesNotContain("SuperIoReadInvalid", json, StringComparison.OrdinalIgnoreCase);
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
