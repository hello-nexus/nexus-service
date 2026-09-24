using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

/// <summary>
/// The release a cooling reset runs: ApplyCooling has only arms that DRIVE
/// fans, so the default preset "off" reaches hardware through this path alone.
/// </summary>
public class LiveEngineSyncCoolingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestableConfigStore _store;
    private readonly RecordingFanProvider _fans;

    public LiveEngineSyncCoolingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-live-sync-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new TestableConfigStore(Path.Combine(_tempDir, "settings.json"));
        _fans = new RecordingFanProvider(new List<FanChannel>
        {
            new() { Id = "fan1", Name = "Fan 1" },
            new() { Id = "fan2", Name = "Fan 2" },
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ReleaseCoolingAfterReset_ReleasesEveryChannel()
    {
        LiveEngineSync.ReleaseCoolingAfterReset(_fans, new FeatureGates(_store));

        Assert.Equal(new[] { "fan1", "fan2" }, _fans.Released.OrderBy(id => id).ToArray());
    }

    [Fact]
    public void CoolingGateOff_WritesNothing()
    {
        _store.Update(s => s.Features.Cooling = false);

        LiveEngineSync.ReleaseCoolingAfterReset(_fans, new FeatureGates(_store));

        Assert.Empty(_fans.Released);
        Assert.Equal(0, _fans.WriteCount);
    }

    private sealed class RecordingFanProvider : IFanControlProvider
    {
        private readonly List<FanChannel> _channels;
        public List<string> Released { get; } = new();
        /// <summary>Every call that reaches hardware, release or drive - the
        /// Cooling-off contract is "no writes at all", not "no releases".</summary>
        public int WriteCount { get; private set; }

        public RecordingFanProvider(List<FanChannel> channels) => _channels = channels;

        public IReadOnlyList<FanChannel> GetFanChannels() => _channels;
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent)
        {
            WriteCount++;
            return dutyPercent;
        }
        public void DriveFanSpeed(string channelId, int dutyPercent) => WriteCount++;
        public void ReleaseFan(string channelId)
        {
            WriteCount++;
            Released.Add(channelId);
        }
        public void ReleaseAll()
        {
            WriteCount++;
            Released.AddRange(_channels.Select(c => c.Id));
        }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }
}
