using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;

namespace Nexus.Service.Migration.FanControl;

/// <summary>
/// Reads a FanControl configuration and imports it: curves (including the
/// trigger, sync and auto modes ported for this), the fans they drive, the
/// calibration tables FanControl already measured, nicknames, per-fan offsets
/// and manual duties.
///
/// Curves this import created are tagged by an <c>fc-</c> id prefix, so
/// re-importing replaces them instead of stacking duplicates. Curves the user
/// made here are never touched.
/// </summary>
public sealed class FanControlImportService
{
    /// <summary>Id prefix marking a curve this import owns.</summary>
    internal const string ImportedIdPrefix = "fc-";

    public const string CategoryCurves = "curves";
    public const string CategoryCalibration = "calibration";
    public const string CategoryNames = "names";
    public const string CategoryOffsets = "offsets";
    public const string CategoryManual = "manual";

    private readonly IFanControlDetector _detector;
    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;

    public FanControlImportService(IFanControlDetector detector, IFanControlProvider fans, IConfigStore store)
    {
        _detector = detector;
        _fans = fans;
        _store = store;
    }

    public FanControlPreviewResponse Preview(string? configPath)
    {
        var plan = BuildPlan(configPath, out var error);
        if (plan is null)
        {
            return new FanControlPreviewResponse { Available = false, Error = true, Msg = error };
        }
        return plan.Preview;
    }

    public FanControlApplyResponse Apply(string? configPath, IReadOnlyList<string> categories)
    {
        var plan = BuildPlan(configPath, out var error, out var config);
        if (plan is null || config is null)
        {
            return new FanControlApplyResponse { Error = true, Msg = error };
        }

        var wanted = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return new FanControlApplyResponse { Error = true, Msg = "No categories selected" };
        }

        var response = new FanControlApplyResponse();

        // What the fans end up doing lives in a preset, not in loose bindings:
        // pressing any built-in mode reattaches every unlocked fan to that
        // mode's curve, which would erase an import the moment the user touched
        // the mode tabs. The curve library is shared, so the curves themselves
        // go in as ordinary curves and the preset only references them.
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var curve in plan.Curves)
        {
            foreach (var output in curve.Outputs)
            {
                assignments[output.Id] = curve.Id;
            }
        }

        var wantsPreset = wanted.Contains(CategoryCurves)
            || wanted.Contains(CategoryOffsets)
            || wanted.Contains(CategoryManual);
        var presetName = PresetNameFor(config);
        string? presetId = null;

        if (wantsPreset && !CanHoldPreset(presetName))
        {
            return new FanControlApplyResponse
            {
                Error = true,
                Msg = $"Delete a cooling preset first: the limit of {CoolingPresets.Cap} is reached",
            };
        }

        _store.Update(s =>
        {
            if (wanted.Contains(CategoryCurves))
            {
                // Drop the previous import's curves, then release the channels
                // this one claims from any user curve still driving them: two
                // curves on one channel would fight every tick.
                s.Cooling.ClearHeal();
                var importedChannels = new HashSet<string>(assignments.Keys, StringComparer.Ordinal);
                s.Cooling.Curves.RemoveAll(c => c.Id.StartsWith(ImportedIdPrefix, StringComparison.Ordinal));
                foreach (var existing in s.Cooling.Curves)
                {
                    existing.Outputs.RemoveAll(o => importedChannels.Contains(o.Id));
                }

                // Outputs are the preset's business; activating it binds them.
                foreach (var curve in plan.Curves)
                {
                    curve.Outputs.Clear();
                }
                s.Cooling.Curves.AddRange(plan.Curves);
                response.CurvesImported = plan.Curves.Count;
            }

            if (wanted.Contains(CategoryCalibration))
            {
                foreach (var (id, calibration) in plan.Calibrations)
                {
                    s.Cooling.FanCalibrations[id] = calibration;
                }
                response.CalibrationsImported = plan.Calibrations.Count;
            }

            if (wanted.Contains(CategoryNames))
            {
                foreach (var (id, name) in plan.Names)
                {
                    s.Cooling.FanNames[id] = name;
                }
                response.NamesImported = plan.Names.Count;
            }

            if (!wantsPreset)
            {
                return;
            }

            // Re-importing replaces the preset this import made last time
            // rather than stacking another copy of it.
            var preset = s.Cooling.Presets.Find(
                p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
            if (preset is null)
            {
                preset = new CoolingPreset { Id = Guid.NewGuid().ToString("n"), Name = presetName };
                s.Cooling.Presets.Add(preset);
            }

            preset.Mode = "custom";
            preset.GlobalSpeedModifier = Nexus.Service.Defaults.InstallDefaults.Cooling.GlobalSpeedModifier;
            preset.FanCurveAssignments = wanted.Contains(CategoryCurves)
                ? new Dictionary<string, string>(assignments)
                : new Dictionary<string, string>();
            preset.FanOffsets = wanted.Contains(CategoryOffsets)
                ? new Dictionary<string, int>(plan.Offsets)
                : new Dictionary<string, int>();
            preset.ManualSpeeds = new Dictionary<string, int>();
            if (wanted.Contains(CategoryManual))
            {
                foreach (var (id, duty) in plan.ManualSpeeds)
                {
                    // A fan this import put on a curve must not also carry a
                    // manual duty, or the replay fights the curve.
                    if (preset.FanCurveAssignments.ContainsKey(id))
                    {
                        continue;
                    }
                    preset.ManualSpeeds[id] = duty;
                    response.ManualImported++;
                }
            }

            response.OffsetsImported = preset.FanOffsets.Count;
            presetId = preset.Id;
        });

        if (presetId is not null)
        {
            // The import takes effect now: the preset is applied and left
            // selected, so the fans run what was imported.
            CoolingPresets.Activate(presetId, _store, _fans);
            response.PresetId = presetId;
            response.PresetName = presetName;
        }

        // Fans are on user curves now, so the profile bar has to agree.
        var derived = FanProfiles.DerivePresetFromCurves(_store, _fans);
        _store.Update(s => s.Cooling.ActivePreset = derived);

        return response;
    }

    /// <summary>The preset an import writes into, named for the configuration it came from.</summary>
    private static string PresetNameFor(FanControlConfigFile config) =>
        config.IsDefault ? "FanControl" : $"FanControl ({config.Name})";

    /// <summary>False when the preset list is full and none of it is ours to replace.</summary>
    private bool CanHoldPreset(string name)
    {
        var presets = _store.Load().Cooling.Presets;
        return presets.Count < CoolingPresets.Cap
            || presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private FanControlImportPlan? BuildPlan(string? configPath, out string error) =>
        BuildPlan(configPath, out error, out _);

    private FanControlImportPlan? BuildPlan(string? configPath, out string error, out FanControlConfigFile? source)
    {
        error = "Ok";
        source = null;
        var detection = _detector.Detect();
        if (detection.Configs.Count == 0)
        {
            error = "No FanControl configuration found";
            return null;
        }

        var config = string.IsNullOrEmpty(configPath)
            ? detection.Configs[0]
            : detection.Configs.FirstOrDefault(c =>
                string.Equals(c.Path, configPath, StringComparison.OrdinalIgnoreCase));
        if (config is null)
        {
            error = "That FanControl configuration no longer exists";
            return null;
        }

        source = config;
        var json = _detector.ReadConfig(config.Path);
        if (string.IsNullOrEmpty(json))
        {
            error = "Could not read the FanControl configuration";
            return null;
        }

        FanControlConfig parsed;
        try
        {
            parsed = FanControlConfigParser.Parse(json);
        }
        catch (Exception ex)
        {
            error = $"Could not read the FanControl configuration: {ex.Message}";
            return null;
        }

        // A channel the user marked not controlled is not the import's to claim:
        // the write gate would silently drop every duty, leaving the editor
        // showing a fan wired to a curve that drives nothing. Withholding it
        // from the match candidates is what keeps the marker meaningful across
        // an import, not just across a preset apply.
        var uncontrolled = _store.Load().Cooling.UncontrolledFanChannels;
        var channels = _fans.GetFanChannels()
            .Where(c => !uncontrolled.Contains(c.Id))
            .Select(c => new LhmIdentifierMatcher.Candidate(c.Id, c.Name))
            .ToList();
        var sensors = _fans.GetTemperatureSources()
            .Select(s => new LhmIdentifierMatcher.Candidate(s.Id, s.Name))
            .ToList();

        var plan = FanControlImportMapper.Build(parsed, channels, sensors);

        // Imported curves come from another app's file, so they go through the
        // same clamp as anything posted to /cooling/curves/set rather than
        // straight into the store.
        var sanitized = CoolingSafety.Sanitize(new SetCurvesBody
        {
            GlobalSpeedModifier = 1.0,
            Curves = plan.Curves.ConvertAll(CurveWireMapper.ToWire),
        });
        plan.Curves.Clear();
        plan.Curves.AddRange(sanitized.Curves.ConvertAll(CurveWireMapper.ToDocument));

        plan.Preview.ConfigName = config.Name;
        return plan;
    }
}
