using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

public class CoolingPresetsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestableConfigStore _store;
    private readonly FakeFanProvider _fans;

    public CoolingPresetsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new TestableConfigStore(Path.Combine(_tempDir, "settings.json"));
        _fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Name = "Fan 1" },
                new() { Id = "fan2", Name = "Fan 2" },
            },
            temps: new List<TemperatureSource>
            {
                new() { Id = "cpu-package", Name = "CPU Package", Category = "CPU" },
            });
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void SeedCustom(string curveId = "user-curve-a", string fanId = "fan1")
    {
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = curveId,
                Name = "User A",
                Type = "Linear",
                Outputs = new List<CurveOutputDocument> { new() { Id = fanId, Type = "Fan" } },
            });
        });
    }

    [Fact]
    public void Capture_TakesAssignmentsManualSpeedsOffsetsAndMode()
    {
        SeedCustom();
        _store.Update(s =>
        {
            s.Cooling.ManualSpeeds["fan2"] = 44;
            s.Cooling.FanOffsets["fan1"] = 7;
            s.Cooling.GlobalSpeedModifier = 1.25;
        });

        var preset = CoolingPresets.Capture("p1", "Quiet night", _store);

        Assert.Equal("user-curve-a", preset.FanCurveAssignments["fan1"]);
        Assert.False(preset.FanCurveAssignments.ContainsKey("fan2"));
        Assert.Equal(44, preset.ManualSpeeds["fan2"]);
        Assert.Equal(7, preset.FanOffsets["fan1"]);
        Assert.Equal(1.25, preset.GlobalSpeedModifier);
        Assert.Equal("custom", preset.Mode);
    }

    [Fact]
    public void Capture_StoresNoCurveCopies_SoTheLibraryStaysShared()
    {
        SeedCustom();
        var preset = CoolingPresets.Capture("p1", "A", _store);

        // Editing the curve after capture is visible through the preset,
        // because the preset only names the curve.
        _store.Update(s => s.Cooling.Curves.First(c => c.Id == "user-curve-a").Name = "Renamed");

        Assert.Equal("user-curve-a", preset.FanCurveAssignments["fan1"]);
        Assert.Equal("Renamed", _store.Load().Cooling.Curves.First(c => c.Id == "user-curve-a").Name);
    }

    [Fact]
    public void Activate_RestoresCurveAssignments()
    {
        SeedCustom();
        var preset = CoolingPresets.Capture("p1", "A", _store);
        _store.Update(s => s.Cooling.Presets.Add(preset));

        // Move away: silent claims every fan.
        FanProfiles.Apply("silent", _fans, _store);
        Assert.Empty(_store.Load().Cooling.Curves.First(c => c.Id == "user-curve-a").Outputs);

        Assert.True(CoolingPresets.Activate("p1", _store, _fans));

        var s = _store.Load();
        Assert.Equal("custom", s.Cooling.ActivePreset);
        Assert.Equal("p1", s.Cooling.ActivePresetId);
        var userCurve = s.Cooling.Curves.First(c => c.Id == "user-curve-a");
        Assert.Equal("fan1", Assert.Single(userCurve.Outputs).Id);
    }

    [Fact]
    public void Activate_RestoresManualDutiesEvenWhenCustomIsAlreadyActive()
    {
        // The plain custom re-apply deliberately skips the manual restore, so
        // without forceCustomRestore a preset switch inside custom would be a
        // silent no-op for every manually driven fan.
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.ManualSpeeds["fan1"] = 30;
        });
        var quiet = CoolingPresets.Capture("p1", "Quiet", _store);
        _store.Update(s => s.Cooling.Presets.Add(quiet));

        _store.Update(s => s.Cooling.ManualSpeeds["fan1"] = 90);
        var loud = CoolingPresets.Capture("p2", "Loud", _store);
        _store.Update(s => s.Cooling.Presets.Add(loud));

        _fans.SpeedSet.Clear();
        Assert.True(CoolingPresets.Activate("p1", _store, _fans));

        Assert.Equal(30, _store.Load().Cooling.ManualSpeeds["fan1"]);
        Assert.Contains(("fan1", 30), _fans.SpeedSet);
    }

    [Fact]
    public void Activate_AppliesTheSavedBuiltInMode()
    {
        _store.Update(s => s.Cooling.ActivePreset = "silent");
        var preset = CoolingPresets.Capture("p1", "Silent night", _store);
        _store.Update(s => s.Cooling.Presets.Add(preset));

        FanProfiles.Apply("turbo", _fans, _store);
        Assert.True(CoolingPresets.Activate("p1", _store, _fans));

        var s = _store.Load();
        Assert.Equal("silent", s.Cooling.ActivePreset);
        var silent = s.Cooling.Curves.First(c => c.Preset == "silent");
        Assert.Equal(2, silent.Outputs.Count);
    }

    [Fact]
    public void Activate_RestoresOffsetsAndGlobalModifier()
    {
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.FanOffsets["fan1"] = 12;
            s.Cooling.GlobalSpeedModifier = 0.8;
        });
        var preset = CoolingPresets.Capture("p1", "A", _store);
        _store.Update(s =>
        {
            s.Cooling.Presets.Add(preset);
            s.Cooling.FanOffsets["fan1"] = 0;
            s.Cooling.GlobalSpeedModifier = 1.0;
        });

        Assert.True(CoolingPresets.Activate("p1", _store, _fans));

        var s = _store.Load();
        Assert.Equal(12, s.Cooling.FanOffsets["fan1"]);
        Assert.Equal(0.8, s.Cooling.GlobalSpeedModifier);
    }

    [Fact]
    public void Activate_UnknownIdReturnsFalseAndChangesNothing()
    {
        _store.Update(s => s.Cooling.ActivePreset = "turbo");

        Assert.False(CoolingPresets.Activate("nope", _store, _fans));

        var s = _store.Load();
        Assert.Equal("turbo", s.Cooling.ActivePreset);
        Assert.Null(s.Cooling.ActivePresetId);
    }

    [Fact]
    public void CaptureInto_OverwritesTheSnapshotButKeepsIdAndName()
    {
        SeedCustom();
        var preset = CoolingPresets.Capture("p1", "Original", _store);
        _store.Update(s => s.Cooling.Presets.Add(preset));

        _store.Update(s => s.Cooling.ManualSpeeds["fan2"] = 55);
        CoolingPresets.CaptureInto(preset, _store.Load().Cooling);

        Assert.Equal("p1", preset.Id);
        Assert.Equal("Original", preset.Name);
        Assert.Equal(55, preset.ManualSpeeds["fan2"]);
    }

    [Fact]
    public void PresetsAndActiveIdDefaultEmpty()
    {
        var s = _store.Load();
        Assert.Empty(s.Cooling.Presets);
        Assert.Null(s.Cooling.ActivePresetId);
    }

    private sealed class FakeFanProvider : IFanControlProvider
    {
        private readonly List<FanChannel> _channels;
        private readonly List<TemperatureSource> _temps;
        public List<string> Released { get; } = new();
        public List<(string Id, int Duty)> SpeedSet { get; } = new();

        public FakeFanProvider(List<FanChannel> channels, List<TemperatureSource> temps)
        {
            _channels = channels;
            _temps = temps;
        }

        public IReadOnlyList<FanChannel> GetFanChannels() => _channels;
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => _temps;
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent)
        {
            SpeedSet.Add((channelId, dutyPercent));
            return Math.Clamp(dutyPercent, 0, 100);
        }
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) => Released.Add(channelId);
        public void ReleaseAll() => Released.AddRange(_channels.Select(c => c.Id));
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds,
            IProgress<FanCalibrationProgress> progress,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }
}
