using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

public class FanProfilesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly TestableConfigStore _store;
    private readonly FakeFanProvider _fans;

    public FanProfilesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
        _store = new TestableConfigStore(_settingsPath);
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

    [Fact]
    public void DefaultActivePresetIsOff()
    {
        var settings = _store.Load();
        Assert.Equal("off", settings.Cooling.ActivePreset);
    }

    [Fact]
    public void ApplySilent_CreatesSinglePresetCurveAttachedToAllFans()
    {
        FanProfiles.Apply("silent", _fans, _store);

        var s = _store.Load();
        Assert.Equal("silent", s.Cooling.ActivePreset);
        var presetCurve = Assert.Single(s.Cooling.Curves, c => c.Preset == "silent");
        Assert.Equal("preset-silent", presetCurve.Id);
        Assert.Equal(2, presetCurve.Outputs.Count);
        Assert.Contains(presetCurve.Outputs, o => o.Id == "fan1");
        Assert.Contains(presetCurve.Outputs, o => o.Id == "fan2");
    }

    [Fact]
    public void ApplySilent_DoesNotDeleteUserCurves()
    {
        _store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "user-curve-a",
            Name = "User A",
            Type = "Linear",
            Outputs = new List<CurveOutputDocument> { new() { Id = "fan1", Type = "Fan" } },
        }));

        FanProfiles.Apply("silent", _fans, _store);

        var s = _store.Load();
        Assert.Contains(s.Cooling.Curves, c => c.Id == "user-curve-a");
    }

    [Fact]
    public void ApplySilent_DetachesFansFromUserCurves()
    {
        _store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "user-curve-a",
            Name = "User A",
            Type = "Linear",
            Outputs = new List<CurveOutputDocument>
            {
                new() { Id = "fan1", Type = "Fan" },
                new() { Id = "fan2", Type = "Fan" },
            },
        }));

        FanProfiles.Apply("silent", _fans, _store);

        var s = _store.Load();
        var userCurve = s.Cooling.Curves.First(c => c.Id == "user-curve-a");
        Assert.Empty(userCurve.Outputs);
    }

    [Fact]
    public void CustomToSilent_SnapshotsCustomMapping()
    {
        // Custom mapping: fan1 -> user-curve, fan2 -> BIOS (no curve).
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "user-curve-a",
                Name = "User A",
                Type = "Linear",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan1", Type = "Fan" } },
            });
        });

        FanProfiles.Apply("silent", _fans, _store);

        var s = _store.Load();
        Assert.Equal("user-curve-a", s.Cooling.CustomFanCurveAssignments["fan1"]);
        Assert.False(s.Cooling.CustomFanCurveAssignments.ContainsKey("fan2"));
    }

    [Fact]
    public void SilentToCustom_RestoresSnapshot()
    {
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "user-curve-a",
                Name = "User A",
                Type = "Linear",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan1", Type = "Fan" } },
            });
        });

        FanProfiles.Apply("silent", _fans, _store);
        FanProfiles.Apply("custom", _fans, _store);

        var s = _store.Load();
        Assert.Equal("custom", s.Cooling.ActivePreset);
        var userCurve = s.Cooling.Curves.First(c => c.Id == "user-curve-a");
        Assert.Single(userCurve.Outputs);
        Assert.Equal("fan1", userCurve.Outputs[0].Id);
        // Preset curve still exists but has no outputs.
        var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
        Assert.Empty(preset.Outputs);
    }

    [Fact]
    public void SilentToCustom_RestoresManualDutiesAndDrivesHardware()
    {
        // Live bug 2026-07-13: custom-mode manual levels survived a
        // silent -> custom round-trip only in the page's local state; the
        // hardware stayed at the silent curve's last duty and a page remount
        // revealed the loss. Manual duties must restore like curve
        // assignments do.
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.ManualSpeeds["fan1"] = 37;
            s.Cooling.ManualSpeeds["fan2"] = 81;
        });

        FanProfiles.Apply("silent", _fans, _store);
        FanProfiles.Apply("custom", _fans, _store);

        var s = _store.Load();
        Assert.Equal(37, s.Cooling.ManualSpeeds["fan1"]);
        Assert.Equal(81, s.Cooling.ManualSpeeds["fan2"]);
        Assert.Contains(("fan1", 37), _fans.SpeedSet);
        Assert.Contains(("fan2", 81), _fans.SpeedSet);
    }

    [Fact]
    public void OffToCustom_RestoresManualDutiesDeletedByTheRelease()
    {
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.ManualSpeeds["fan1"] = 42;
        });

        FanProfiles.Apply("off", _fans, _store);
        // The real providers' ReleaseFan (called by Apply("off") for every
        // channel) deletes the live ManualSpeeds entry; the fake records the
        // release without a store, so mirror the deletion here.
        Assert.Contains("fan1", _fans.Released);
        _store.Update(s => s.Cooling.ManualSpeeds.Clear());

        FanProfiles.Apply("custom", _fans, _store);

        var s = _store.Load();
        Assert.Equal(42, s.Cooling.ManualSpeeds["fan1"]);
        Assert.Contains(("fan1", 42), _fans.SpeedSet);
    }

    [Fact]
    public void SilentToCustom_ManualRestoreSkipsCurveAssignedFan()
    {
        // fan1 rides a user curve with a stale manual entry alongside it;
        // the restored curve attachment must win - no manual write for fan1.
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "user-curve-a",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan1", Type = "Fan" } },
            });
            s.Cooling.ManualSpeeds["fan1"] = 30;
            s.Cooling.ManualSpeeds["fan2"] = 60;
        });

        FanProfiles.Apply("silent", _fans, _store);
        FanProfiles.Apply("custom", _fans, _store);

        Assert.DoesNotContain(_fans.SpeedSet, w => w.Id == "fan1");
        Assert.Contains(("fan2", 60), _fans.SpeedSet);
    }

    [Fact]
    public void SilentToCustom_ManualRestoreSkipsLockedFan()
    {
        var fan1 = _fans.GetFanChannels().First(c => c.Id == "fan1");
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.ManualSpeeds["fan1"] = 40;
            s.Cooling.ManualSpeeds["fan2"] = 70;
        });
        FanProfiles.SetLockOverride(fan1, true, _store);

        FanProfiles.Apply("silent", _fans, _store);
        FanProfiles.Apply("custom", _fans, _store);

        // The locked fan keeps whatever drives it; only fan2 is re-driven.
        Assert.DoesNotContain(_fans.SpeedSet, w => w.Id == "fan1");
        Assert.Contains(("fan2", 70), _fans.SpeedSet);
    }

    [Fact]
    public void SilentToCustom_ManualRestoreSkipsExplicitlyLockedAbsentFan()
    {
        // A locked fan whose hub is disconnected at apply time is missing
        // from the live channel list (and so from lockedIds); its explicit
        // lock override must still exempt it from the snapshot restore, or
        // a duty changed while locked gets stomped and replayed on reconnect.
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.ManualSpeeds["ghost-fan"] = 20;
            s.Cooling.ManualSpeeds["fan2"] = 70;
            s.Cooling.FanLockOverrides["ghost-fan"] = true;
        });

        FanProfiles.Apply("silent", _fans, _store);
        // While the preset is active the user adjusts the locked fan.
        _store.Update(s => s.Cooling.ManualSpeeds["ghost-fan"] = 65);
        FanProfiles.Apply("custom", _fans, _store);

        var s = _store.Load();
        Assert.Equal(65, s.Cooling.ManualSpeeds["ghost-fan"]);
        Assert.Equal(70, s.Cooling.ManualSpeeds["fan2"]);
    }

    [Fact]
    public void ApplyCustom_ToleratesExplicitNullManualSnapshot()
    {
        // Settings persisted as "customManualSpeeds": null must not fault
        // the preset apply (same class as the assignments ?? guard).
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "silent";
            s.Cooling.CustomManualSpeeds = null!;
        });

        FanProfiles.Apply("custom", _fans, _store);

        Assert.Equal("custom", _store.Load().Cooling.ActivePreset);
    }

    [Fact]
    public void ReapplyCustomWhileCustom_DoesNotClobberLiveManualDuties()
    {
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.ManualSpeeds["fan1"] = 37;
        });
        FanProfiles.Apply("silent", _fans, _store);
        FanProfiles.Apply("custom", _fans, _store);

        // User raises the duty while already in custom, then custom is
        // re-applied (e.g. a second window): the newer value must survive.
        _store.Update(s => s.Cooling.ManualSpeeds["fan1"] = 90);
        FanProfiles.Apply("custom", _fans, _store);

        Assert.Equal(90, _store.Load().Cooling.ManualSpeeds["fan1"]);
    }

    [Fact]
    public void ApplyOff_DetachesAllFansAndReleases()
    {
        _store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "user-curve-a",
            Outputs = new List<CurveOutputDocument>
            {
                new() { Id = "fan1", Type = "Fan" },
                new() { Id = "fan2", Type = "Fan" },
            },
        }));

        FanProfiles.Apply("off", _fans, _store);

        var s = _store.Load();
        Assert.Equal("off", s.Cooling.ActivePreset);
        var userCurve = s.Cooling.Curves.First(c => c.Id == "user-curve-a");
        Assert.Empty(userCurve.Outputs);
        Assert.Equal(new[] { "fan1", "fan2" }.OrderBy(x => x), _fans.Released.OrderBy(x => x));
    }

    [Fact]
    public void EditingPresetCurve_SurvivesPresetRoundTrip()
    {
        FanProfiles.Apply("silent", _fans, _store);
        // User edits the preset curve's MinTemp.
        _store.Update(s =>
        {
            var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
            preset.Linear!.MinTemp = 50;
        });
        FanProfiles.Apply("custom", _fans, _store);
        FanProfiles.Apply("silent", _fans, _store);

        var s = _store.Load();
        var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
        Assert.Equal(50, preset.Linear!.MinTemp);
    }

    [Fact]
    public void DeletedPresetCurve_RecreatedWithDefaults()
    {
        FanProfiles.Apply("silent", _fans, _store);
        _store.Update(s => s.Cooling.Curves.RemoveAll(c => c.Preset == "silent"));
        FanProfiles.Apply("silent", _fans, _store);

        var s = _store.Load();
        var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
        var defaults = FanProfiles.PresetDefaults.For("silent");
        Assert.Equal(defaults.MinTemp, preset.Linear!.MinTemp);
        Assert.Equal(defaults.MaxTemp, preset.Linear.MaxTemp);
        Assert.Equal(defaults.MinSpeed, preset.Linear.MinSpeed);
        Assert.Equal(defaults.MaxSpeed, preset.Linear.MaxSpeed);
    }

    [Fact]
    public void DerivePresetFromCurves_ReturnsActivePresetWhenCovering()
    {
        FanProfiles.Apply("balanced", _fans, _store);
        Assert.Equal("balanced", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void DerivePresetFromCurves_ReturnsCustomWhenUserCurveDrivesAFan()
    {
        FanProfiles.Apply("balanced", _fans, _store);
        // User edits a non-preset curve to capture fan1.
        _store.Update(s =>
        {
            var preset = s.Cooling.Curves.First(c => c.Preset == "balanced");
            preset.Outputs.RemoveAll(o => o.Id == "fan1");
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "user-x",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan1", Type = "Fan" } },
            });
        });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void DerivePresetFromCurves_ReturnsOffWhenNothingAttached()
    {
        FanProfiles.Apply("off", _fans, _store);
        Assert.Equal("off", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void DerivePresetFromCurves_ManualOnAnyFanIsCustom()
    {
        // Custom mode with one fan attached to a user curve and one fan on Manual:
        // the manual override means the configuration is mixed, not Off.
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "user-a",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan1", Type = "Fan" } },
            });
            s.Cooling.ManualSpeeds["fan2"] = 60;
        });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void DerivePresetFromCurves_AllManualIsCustomNotOff()
    {
        // Both fans on Manual -> custom (each fan is software-controlled by the
        // user even though no curve targets them).
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.ManualSpeeds["fan1"] = 60;
            s.Cooling.ManualSpeeds["fan2"] = 60;
        });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void DerivePresetFromCurves_AttachedFanIgnoresManualSpeedsEntry()
    {
        // Belt-and-suspenders alongside the IFanControlProvider.DriveFanSpeed
        // split: CurveEngine no longer writes to ManualSpeeds, but a user can
        // still set manual then attach the fan to a curve, leaving a stale
        // entry. Derivation must ignore manual entries for fans that ARE
        // attached to a curve - otherwise the active preset flips to "custom"
        // even though every fan is being driven by the preset curve.
        FanProfiles.Apply("silent", _fans, _store);
        _store.Update(s =>
        {
            s.Cooling.ManualSpeeds["fan1"] = 25;
            s.Cooling.ManualSpeeds["fan2"] = 25;
        });
        Assert.Equal("silent", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void DerivePresetFromCurves_IgnoresUnresponsiveFan_KeepsPresetSelected()
    {
        // Two connected fans linked to silent + one disconnected fan must
        // still resolve to "silent". The disconnected fan can't be wired to
        // anything, so excluding it from the coverage check is the only way
        // to keep the preset chip lit while a fan is unplugged.
        var fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Name = "Fan 1", Classification = "Controllable" },
                new() { Id = "fan2", Name = "Fan 2", Classification = "Controllable" },
                new() { Id = "fan3", Name = "Fan 3", Classification = "Unresponsive" },
            },
            temps: new List<TemperatureSource> { new() { Id = "cpu", Category = "CPU" } });
        FanProfiles.Apply("silent", fans, _store);
        // Apply attaches every channel, including the unresponsive one (a
        // hardware no-op); the preset must still derive as selected.
        Assert.Equal("silent", FanProfiles.DerivePresetFromCurves(_store, fans));
    }

    [Fact]
    public void DerivePresetFromCurves_OneConnectedFanOnUserCurve_IsCustom()
    {
        // Mixed config: fan1 on silent, fan2 grabbed by a user curve, fan3
        // disconnected. The user breaking fan2 off the preset must drop the
        // chip to custom even though the disconnected fan is ignored.
        var fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Classification = "Controllable" },
                new() { Id = "fan2", Classification = "Controllable" },
                new() { Id = "fan3", Classification = "Unresponsive" },
            },
            temps: new List<TemperatureSource> { new() { Id = "cpu", Category = "CPU" } });
        FanProfiles.Apply("silent", fans, _store);
        _store.Update(s =>
        {
            var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
            preset.Outputs.RemoveAll(o => o.Id == "fan2");
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "user-x",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan2", Type = "Fan" } },
            });
        });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, fans));
    }

    [Fact]
    public void DerivePresetFromCurves_OneConnectedFanOnManual_IsCustom()
    {
        // fan1 on silent, fan2 yanked to manual, fan3 disconnected -> custom.
        var fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Classification = "Controllable" },
                new() { Id = "fan2", Classification = "Controllable" },
                new() { Id = "fan3", Classification = "Unresponsive" },
            },
            temps: new List<TemperatureSource> { new() { Id = "cpu", Category = "CPU" } });
        FanProfiles.Apply("silent", fans, _store);
        _store.Update(s =>
        {
            var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
            preset.Outputs.RemoveAll(o => o.Id == "fan2");
            s.Cooling.ManualSpeeds["fan2"] = 60;
        });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, fans));
    }

    [Fact]
    public void DerivePresetFromCurves_OneConnectedFanOnBios_IsCustom()
    {
        // fan1 on silent, fan2 detached (BIOS), fan3 disconnected -> custom.
        // Releasing a fan to BIOS while the disconnected one stays unwired
        // must still drop the chip.
        var fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Classification = "Controllable" },
                new() { Id = "fan2", Classification = "Controllable" },
                new() { Id = "fan3", Classification = "Unresponsive" },
            },
            temps: new List<TemperatureSource> { new() { Id = "cpu", Category = "CPU" } });
        FanProfiles.Apply("silent", fans, _store);
        _store.Update(s =>
        {
            var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
            preset.Outputs.RemoveAll(o => o.Id == "fan2");
        });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, fans));
    }

    [Fact]
    public void DerivePresetFromCurves_OnlyUnresponsiveFans_IsCustom()
    {
        // No controllable fans at all -> falls into the "no fans to check"
        // bucket and reports custom, the same as the empty-channels case.
        var fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Classification = "Unresponsive" },
                new() { Id = "fan2", Classification = "Unresponsive" },
            },
            temps: new List<TemperatureSource> { new() { Id = "cpu", Category = "CPU" } });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, fans));
    }

    [Fact]
    public void DerivePresetFromCurves_AllUnresponsiveAfterApplySilent_StaysSilent()
    {
        // Edge case: user applies silent with all fans connected, then a fan
        // becomes disconnected. With only unresponsive fans remaining, there
        // are no controllable fans to validate, so the derivation falls back
        // to custom. (Acceptable: the system has no controllable hardware to
        // honor the preset anyway, so the chip can't be honestly "lit".)
        var fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Classification = "Unresponsive" },
                new() { Id = "fan2", Classification = "Unresponsive" },
            },
            temps: new List<TemperatureSource> { new() { Id = "cpu", Category = "CPU" } });
        // Manually seed the store with a silent preset that attaches both fans.
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "silent";
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "preset-silent",
                Preset = "silent",
                Type = "Linear",
                Outputs = new List<CurveOutputDocument>
                {
                    new() { Id = "fan1", Type = "Fan" },
                    new() { Id = "fan2", Type = "Fan" },
                },
            });
        });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, fans));
    }

    [Fact]
    public void DetachFanFromCurves_RemovesFanFromEveryCurve()
    {
        _store.Update(s =>
        {
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "curve-a",
                Outputs = new List<CurveOutputDocument>
                {
                    new() { Id = "fan1", Type = "Fan" },
                    new() { Id = "fan2", Type = "Fan" },
                },
            });
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "curve-b",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan1", Type = "Fan" } },
            });
        });

        FanProfiles.DetachFanFromCurves("fan1", _store);

        var s = _store.Load();
        Assert.DoesNotContain(s.Cooling.Curves.SelectMany(c => c.Outputs), o => o.Id == "fan1");
        // Other fans on the same curves are untouched.
        Assert.Contains(s.Cooling.Curves.First(c => c.Id == "curve-a").Outputs, o => o.Id == "fan2");
    }

    [Fact]
    public void FanOnSilentPreset_ReleasedToBios_FlipsToCustom()
    {
        // Live bug 2026-05-20: clicking BIOS Control on a fan currently
        // driven by silent left the chip lit on Silent. Reproduces the new
        // /cooling/fan/{id}/auto flow: detach first, then release, then
        // remove the manual entry, then derive.
        FanProfiles.Apply("silent", _fans, _store);
        FanProfiles.DetachFanFromCurves("fan2", _store);
        _store.Update(s => s.Cooling.ManualSpeeds.Remove("fan2"));
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void FanOnSilentPreset_SwitchedToManual_FlipsToCustom()
    {
        // Same bug, manual-side: clicking Manual on a fan that was on silent
        // left it attached to the preset curve, so the manual entry was
        // filtered out as "stale" and the chip stayed lit. New
        // /cooling/fan/{id}/speed flow detaches before writing ManualSpeeds.
        FanProfiles.Apply("silent", _fans, _store);
        FanProfiles.DetachFanFromCurves("fan2", _store);
        _store.Update(s => s.Cooling.ManualSpeeds["fan2"] = 50);
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void FanOnSilentPreset_RewiredToBalancedPreset_FlipsToCustom()
    {
        // Wire-DnD scenario: fan2 dragged from preset-silent onto
        // preset-balanced via /cooling/curves/set. Not every fan is on the
        // same preset curve anymore, so the chip must drop to Custom.
        FanProfiles.Apply("silent", _fans, _store);
        _store.Update(s =>
        {
            var silent = s.Cooling.Curves.First(c => c.Preset == "silent");
            silent.Outputs.RemoveAll(o => o.Id == "fan2");
            // EnsurePresetCurve normally creates preset-balanced lazily on
            // first Apply("balanced"); for this test we add it directly so we
            // can target it with the rewire.
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "preset-balanced",
                Preset = "balanced",
                Type = "Linear",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan2", Type = "Fan" } },
            });
        });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void FanOnSilentPreset_RewiredToTurboPreset_FlipsToCustom()
    {
        FanProfiles.Apply("silent", _fans, _store);
        _store.Update(s =>
        {
            var silent = s.Cooling.Curves.First(c => c.Preset == "silent");
            silent.Outputs.RemoveAll(o => o.Id == "fan2");
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "preset-turbo",
                Preset = "turbo",
                Type = "Linear",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan2", Type = "Fan" } },
            });
        });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void FanOnBalancedPreset_ReleasedToBios_WithUnresponsiveFan_FlipsToCustom()
    {
        // Combined scenario: an unresponsive fan is present (which the earlier
        // fix taught derivation to skip), AND a connected fan is released to
        // BIOS. The chip must drop to Custom because the remaining connected
        // fan is no longer covered by every active preset.
        var fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Classification = "Controllable" },
                new() { Id = "fan2", Classification = "Controllable" },
                new() { Id = "fan3", Classification = "Unresponsive" },
            },
            temps: new List<TemperatureSource> { new() { Id = "cpu", Category = "CPU" } });
        FanProfiles.Apply("balanced", fans, _store);
        FanProfiles.DetachFanFromCurves("fan2", _store);
        _store.Update(s => s.Cooling.ManualSpeeds.Remove("fan2"));
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, fans));
    }

    [Fact]
    public void AutoIsTreatedAsOff()
    {
        FanProfiles.Apply("auto", _fans, _store);
        var s = _store.Load();
        Assert.Equal("off", s.Cooling.ActivePreset);
    }

[Fact]
    public void ResetPresetCurve_RestoresMultipointDefaults()
    {
        FanProfiles.Apply("silent", _fans, _store);
        // User edits the silent curve away from defaults and switches it to
        // Flat type.
        _store.Update(s =>
        {
            var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
            preset.Type = "Flat";
            preset.Flat = new FlatCurveData { Speed = 99 };
            preset.Linear!.MinTemp = 99;
        });

        FanProfiles.ResetPresetCurve("silent", _fans, _store);

        var s = _store.Load();
        var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
        var defaults = FanProfiles.PresetDefaults.For("silent");
        // Presets reset to a multi-point (Graph) curve whose end points sit on
        // the preset's min/max ramp; the Linear params stay populated as a
        // fallback for switching the type back.
        Assert.Equal("Graph", preset.Type);
        Assert.Null(preset.Flat);
        Assert.NotNull(preset.Graph);
        Assert.Equal(5, preset.Graph!.Points.Count);
        Assert.Equal(defaults.MinTemp, preset.Graph.Points[0].Temp);
        Assert.Equal(defaults.MinSpeed, preset.Graph.Points[0].Speed);
        Assert.Equal(defaults.MaxTemp, preset.Graph.Points[^1].Temp);
        Assert.Equal(defaults.MaxSpeed, preset.Graph.Points[^1].Speed);
        Assert.Equal(defaults.MinTemp, preset.Linear!.MinTemp);
        Assert.Equal(defaults.MaxTemp, preset.Linear.MaxTemp);
        Assert.Equal(defaults.MinSpeed, preset.Linear.MinSpeed);
        Assert.Equal(defaults.MaxSpeed, preset.Linear.MaxSpeed);
        Assert.Equal(defaults.ResponseTime, preset.Linear.ResponseTime);
    }

    [Fact]
    public void ResetPresetCurve_PreservesFanAttachments()
    {
        FanProfiles.Apply("balanced", _fans, _store);
        FanProfiles.ResetPresetCurve("balanced", _fans, _store);

        var s = _store.Load();
        var preset = s.Cooling.Curves.First(c => c.Preset == "balanced");
        Assert.Equal(2, preset.Outputs.Count);
        // Active preset is unaffected by a template-only reset.
        Assert.Equal("balanced", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void ResetPresetCurve_IgnoresUnknownPreset()
    {
        FanProfiles.Apply("silent", _fans, _store);
        // Mutate the silent curve so a regression that drops the canonical
        // guard would visibly clobber this value.
        _store.Update(s => s.Cooling.Curves.First(c => c.Preset == "silent").Linear!.MinTemp = 11);
        FanProfiles.ResetPresetCurve("custom", _fans, _store);
        FanProfiles.ResetPresetCurve("nonsense", _fans, _store);

        var s = _store.Load();
        Assert.Equal("silent", s.Cooling.ActivePreset);
        Assert.Equal(11, s.Cooling.Curves.First(c => c.Preset == "silent").Linear!.MinTemp);
    }

    [Fact]
    public void SeedDefaultPresetCurves_OnEmptyProfile_CreatesAllThreeAtDefaults()
    {
        var seeded = FanProfiles.SeedDefaultPresetCurves(_fans, _store);

        Assert.True(seeded);
        var s = _store.Load();
        Assert.Equal(3, s.Cooling.Curves.Count);
        foreach (var name in new[] { "silent", "balanced", "turbo" })
        {
            var c = Assert.Single(s.Cooling.Curves, c => c.Preset == name);
            Assert.Equal($"preset-{name}", c.Id);
            Assert.Equal("Graph", c.Type);
            // Not attached to any fan until the preset is activated.
            Assert.Empty(c.Outputs);
            // Bound to the preferred CPU sensor exposed by the fake provider.
            Assert.Equal("cpu-package", c.Input.Id);
            Assert.True(FanProfiles.IsPresetCurveAtDefaults(c));
        }
    }

    [Fact]
    public void SeedDefaultPresetCurves_NoOpWhenCurvesExist()
    {
        _store.Update(s => s.Cooling.Curves.Add(new CurveDocument { Id = "user-a", Type = "Linear" }));

        var seeded = FanProfiles.SeedDefaultPresetCurves(_fans, _store);

        Assert.False(seeded);
        var s = _store.Load();
        Assert.Single(s.Cooling.Curves);
        Assert.DoesNotContain(s.Cooling.Curves, c => c.Preset != null);
    }

    [Fact]
    public void SeedDefaultPresetCurves_DoesNotResurrectAfterUserClearsAllCurves()
    {
        Assert.True(FanProfiles.SeedDefaultPresetCurves(_fans, _store));
        // User empties every curve (e.g. an empty /cooling/curves/set), then a
        // later restart re-runs the seed: it must stay empty, not re-create.
        _store.Update(s => s.Cooling.Curves.Clear());

        var seededAgain = FanProfiles.SeedDefaultPresetCurves(_fans, _store);

        Assert.False(seededAgain);
        Assert.Empty(_store.Load().Cooling.Curves);
    }

    [Fact]
    public void DefaultPresetPoints_EaseGentlerAtBothEnds()
    {
        FanProfiles.Apply("silent", _fans, _store);
        FanProfiles.ResetPresetCurve("silent", _fans, _store);

        var pts = _store.Load().Cooling.Curves.First(c => c.Preset == "silent").Graph!.Points;
        Assert.Equal(5, pts.Count);
        double Slope(int i) => (pts[i + 1].Speed - pts[i].Speed) / (pts[i + 1].Temp - pts[i].Temp);
        var steepest = Enumerable.Range(0, pts.Count - 1).Max(Slope);
        Assert.True(Slope(0) < steepest);
        Assert.True(Slope(pts.Count - 2) < steepest);
        // Endpoints still anchor the preset's temp/speed band.
        var d = FanProfiles.PresetDefaults.For("silent");
        Assert.Equal(d.MinTemp, pts[0].Temp);
        Assert.Equal(d.MinSpeed, pts[0].Speed);
        Assert.Equal(d.MaxTemp, pts[^1].Temp);
        Assert.Equal(d.MaxSpeed, pts[^1].Speed);
    }

    [Fact]
    public void ApplySilent_ExcludesPump_AndPreservesItsCurve()
    {
        var fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Name = "Fan 1" },
                new() { Id = "pump1", Name = "AIO Pump", Kind = FanKinds.Pump },
            },
            temps: new List<TemperatureSource> { new() { Id = "cpu", Category = "CPU" } });
        _store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "pump-curve",
            Outputs = new List<CurveOutputDocument> { new() { Id = "pump1", Type = "Fan" } },
        }));

        FanProfiles.Apply("silent", fans, _store);

        var s = _store.Load();
        var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
        Assert.Contains(preset.Outputs, o => o.Id == "fan1");
        Assert.DoesNotContain(preset.Outputs, o => o.Id == "pump1");
        // The pump's own curve attachment is left intact.
        var pumpCurve = s.Cooling.Curves.First(c => c.Id == "pump-curve");
        Assert.Contains(pumpCurve.Outputs, o => o.Id == "pump1");
    }

    [Fact]
    public void DerivePresetFromCurves_IgnoresPump_KeepsPresetSelected()
    {
        // A pump is present but never joins the preset curve; the chip must
        // still derive as the active preset from the fans alone.
        var fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Classification = "Controllable" },
                new() { Id = "pump1", Kind = FanKinds.Pump, Classification = "Controllable" },
            },
            temps: new List<TemperatureSource> { new() { Id = "cpu", Category = "CPU" } });
        FanProfiles.Apply("silent", fans, _store);
        Assert.Equal("silent", FanProfiles.DerivePresetFromCurves(_store, fans));
    }

    [Fact]
    public void NoOverride_ApplyTurbo_ExcludesPump()
    {
        var fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Name = "Fan 1" },
                new() { Id = "pump1", Name = "AIO Pump", Kind = FanKinds.Pump },
            },
            temps: new List<TemperatureSource> { new() { Id = "cpu", Category = "CPU" } });

        FanProfiles.Apply("turbo", fans, _store);

        var s = _store.Load();
        var preset = s.Cooling.Curves.First(c => c.Preset == "turbo");
        Assert.Contains(preset.Outputs, o => o.Id == "fan1");
        Assert.DoesNotContain(preset.Outputs, o => o.Id == "pump1");
    }

    [Fact]
    public void LockedFan_StaysOnItsPreset_WhenAnotherPresetIsApplied()
    {
        FanProfiles.Apply("silent", _fans, _store);
        var fan1 = _fans.GetFanChannels().First(c => c.Id == "fan1");
        FanProfiles.SetLockOverride(fan1, true, _store);

        FanProfiles.Apply("turbo", _fans, _store);

        var s = _store.Load();
        var silent = s.Cooling.Curves.First(c => c.Preset == "silent");
        var turbo = s.Cooling.Curves.First(c => c.Preset == "turbo");
        Assert.Contains(silent.Outputs, o => o.Id == "fan1");
        Assert.DoesNotContain(turbo.Outputs, o => o.Id == "fan1");
        Assert.Contains(turbo.Outputs, o => o.Id == "fan2");
    }

    [Fact]
    public void LockedFan_AppliedOff_IsReleasedToBios()
    {
        FanProfiles.Apply("silent", _fans, _store);
        var fan1 = _fans.GetFanChannels().First(c => c.Id == "fan1");
        FanProfiles.SetLockOverride(fan1, true, _store);

        FanProfiles.Apply("off", _fans, _store);

        // Off ignores the lock: every channel detaches and releases to BIOS.
        var s = _store.Load();
        var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
        Assert.DoesNotContain(preset.Outputs, o => o.Id == "fan1");
        Assert.DoesNotContain(preset.Outputs, o => o.Id == "fan2");
        Assert.Contains(_fans.Released, id => id == "fan1");
        Assert.Contains(_fans.Released, id => id == "fan2");
    }

    [Fact]
    public void UnlockedPump_AppliedSilent_IsAttached()
    {
        var fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Name = "Fan 1" },
                new() { Id = "pump1", Name = "AIO Pump", Kind = FanKinds.Pump },
            },
            temps: new List<TemperatureSource> { new() { Id = "cpu", Category = "CPU" } });
        var pump = fans.GetFanChannels().First(c => c.Id == "pump1");
        FanProfiles.SetLockOverride(pump, false, _store);

        FanProfiles.Apply("silent", fans, _store);

        var s = _store.Load();
        var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
        Assert.Contains(preset.Outputs, o => o.Id == "pump1");
    }

    [Fact]
    public void DerivePresetFromCurves_LockedFanOffPreset_DoesNotForceCustom()
    {
        FanProfiles.Apply("silent", _fans, _store);
        var fan2 = _fans.GetFanChannels().First(c => c.Id == "fan2");
        FanProfiles.SetLockOverride(fan2, true, _store);
        FanProfiles.DetachFanFromCurves("fan2", _store);

        Assert.Equal("silent", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void SetLockOverride_CollapsesToDefault_AndRecordsDeviations()
    {
        var pump = new FanChannel { Id = "pump1", Kind = FanKinds.Pump };
        var fan = new FanChannel { Id = "fan1", Kind = FanKinds.Fan };

        // Locking a pump matches its default (locked) -> no override stored.
        FanProfiles.SetLockOverride(pump, true, _store);
        Assert.False(_store.Load().Cooling.FanLockOverrides.ContainsKey("pump1"));

        // Locking a fan deviates from its default (unlocked) -> stored as true.
        FanProfiles.SetLockOverride(fan, true, _store);
        Assert.True(_store.Load().Cooling.FanLockOverrides["fan1"]);

        // Unlocking that fan restores its default -> entry removed.
        FanProfiles.SetLockOverride(fan, false, _store);
        Assert.False(_store.Load().Cooling.FanLockOverrides.ContainsKey("fan1"));
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
