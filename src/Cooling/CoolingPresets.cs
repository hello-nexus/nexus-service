using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Defaults;
using Nexus.Service.Persistence;

namespace Nexus.Service.Cooling;

/// <summary>
/// User-saved cooling configurations, selectable from the Cooling page's
/// preset dropdown. A preset stores fan-to-curve assignments, manual duties,
/// per-fan offsets, the global speed modifier, and which built-in mode was
/// active - not copies of the curves, so the curve library stays shared.
///
/// Activation writes the snapshot into the same CustomFanCurveAssignments /
/// CustomManualSpeeds fields the Custom mode restores from, then defers to
/// <see cref="FanProfiles.Apply"/>, so presets and the mode tabs drive fans
/// through one code path.
/// </summary>
public static class CoolingPresets
{
    public const int Cap = 10;

    /// <summary>Snapshot the live cooling configuration into a preset. Mirrors what the Custom mode's exit snapshot captures, plus offsets and the modifier.</summary>
    public static CoolingPreset Capture(string id, string name, IConfigStore store, IFanControlProvider fans)
        => Capture(id, name, store.Load().Cooling, FanIds(fans));

    /// <summary>A preset at clean-install cooling defaults: the install's built-in mode, no curve assignments, manual duties or offsets, and the install's speed modifier.</summary>
    public static CoolingPreset CreateFresh(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Mode = InstallDefaults.Cooling.ActivePreset,
        GlobalSpeedModifier = InstallDefaults.Cooling.GlobalSpeedModifier,
    };

    /// <summary>Read outside IConfigStore.Update: a fan read inside a mutator deadlocks against LhmComputer.Update.</summary>
    public static HashSet<string> FanIds(IFanControlProvider fans) => fans.GetFanChannels().Select(c => c.Id).ToHashSet();

    /// <summary>Snapshot from settings the caller holds, so it can run inside IConfigStore.Update.</summary>
    public static CoolingPreset Capture(string id, string name, CoolingSettings cooling, HashSet<string> fanIds) => new()
    {
        Id = id,
        Name = name,
        FanCurveAssignments = FanProfiles.CaptureFanCurveMapping(cooling, fanIds),
        ManualSpeeds = new Dictionary<string, int>(cooling.ManualSpeeds),
        FanOffsets = new Dictionary<string, int>(cooling.FanOffsets),
        GlobalSpeedModifier = cooling.GlobalSpeedModifier,
        Mode = cooling.ActivePreset,
    };

    /// <summary>Overwrite an existing preset with the given configuration, keeping its id and name.</summary>
    public static void CaptureInto(CoolingPreset target, CoolingSettings cooling, HashSet<string> fanIds)
    {
        var captured = Capture(target.Id, target.Name, cooling, fanIds);
        target.FanCurveAssignments = captured.FanCurveAssignments;
        target.ManualSpeeds = captured.ManualSpeeds;
        target.FanOffsets = captured.FanOffsets;
        target.GlobalSpeedModifier = captured.GlobalSpeedModifier;
        target.Mode = captured.Mode;
    }

    /// <summary>Apply a saved preset and mark it active. Returns false when no preset carries the id.</summary>
    public static bool Activate(string id, IConfigStore store, IFanControlProvider fans)
    {
        var preset = store.Load().Cooling.Presets.Find(p => p.Id == id);
        if (preset is null) return false;

        var isCustom = preset.Mode == "custom";
        store.Update(s =>
        {
            // Custom is the only mode that reads the restore fields; for the
            // built-in modes the preset curve claims every fan, and leaving the
            // fields alone preserves the user's own custom arrangement.
            if (isCustom)
            {
                s.Cooling.CustomFanCurveAssignments = new Dictionary<string, string>(preset.FanCurveAssignments);
                s.Cooling.CustomManualSpeeds = new Dictionary<string, int>(preset.ManualSpeeds);
            }
            s.Cooling.FanOffsets = new Dictionary<string, int>(preset.FanOffsets);
            s.Cooling.GlobalSpeedModifier = preset.GlobalSpeedModifier;
        });

        FanProfiles.Apply(preset.Mode, fans, store, forceCustomRestore: isCustom);
        store.Update(s => s.Cooling.ActivePresetId = id);
        return true;
    }
}
