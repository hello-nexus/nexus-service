using System;
using System.Collections.Generic;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Panel;

/// <summary>
/// Named snapshots of the desktop dashboard, with the same model as
/// <see cref="PanelPresets"/>: the live <see cref="PanelSettings.DashboardLayout"/>
/// is the active preset's working copy, captured back into it when the user
/// switches away or saves a new preset.
/// </summary>
public static class DashboardPresets
{
    public const int Cap = PanelPresets.Cap;

    public static DashboardPresetsResponse ToResponse(PanelSettings panel) => new()
    {
        Presets = panel.DashboardPresets?.ConvertAll(p => new DashboardPresetDto { Id = p.Id, Name = p.Name }) ?? new(),
        ActiveId = panel.DashboardActivePresetId,
        Seeded = panel.DashboardPresets is not null,
    };

    /// <summary>Stores the starter set once; false when presets already exist. Blank names are dropped and the set is capped.</summary>
    public static bool Seed(PanelSettings panel, IEnumerable<DashboardPreset> seeds)
    {
        if (panel.DashboardPresets is not null)
            return false;
        var presets = new List<DashboardPreset>();
        foreach (var seed in seeds)
        {
            var name = (seed.Name ?? "").Trim();
            if (name.Length == 0 || presets.Count >= Cap)
                continue;
            presets.Add(new DashboardPreset { Id = NewId(), Name = name, Layout = PanelPresets.CloneLayout(seed.Layout) });
        }
        panel.DashboardPresets = presets;
        panel.DashboardActivePresetId = presets.Count > 0 ? presets[0].Id : null;
        return true;
    }

    /// <summary>Saves the live layout as a new active preset; false at <see cref="Cap"/>.</summary>
    public static bool Create(PanelSettings panel, string name)
    {
        panel.DashboardPresets ??= new List<DashboardPreset>();
        if (panel.DashboardPresets.Count >= Cap)
            return false;
        CaptureActive(panel);
        var preset = new DashboardPreset { Id = NewId(), Name = name, Layout = PanelPresets.CloneLayout(panel.DashboardLayout) };
        panel.DashboardPresets.Add(preset);
        panel.DashboardActivePresetId = preset.Id;
        return true;
    }

    public static bool Rename(PanelSettings panel, string presetId, string name)
    {
        var preset = panel.DashboardPresets?.Find(p => p.Id == presetId);
        if (preset is null)
            return false;
        preset.Name = name;
        return true;
    }

    /// <summary>
    /// Deleting the active preset loads the one that takes its place, so the
    /// live layout never outlives its preset and a later switch cannot drop it
    /// unsaved. Deleting the last one leaves the live layout with none loaded.
    /// </summary>
    public static void Delete(PanelSettings panel, string presetId)
    {
        var presets = panel.DashboardPresets;
        var index = presets?.FindIndex(p => p.Id == presetId) ?? -1;
        if (presets is null || index < 0)
            return;
        presets.RemoveAt(index);
        if (panel.DashboardActivePresetId != presetId)
            return;
        if (presets.Count == 0)
        {
            panel.DashboardActivePresetId = null;
            return;
        }
        var next = presets[Math.Min(index, presets.Count - 1)];
        panel.DashboardLayout = PanelPresets.CloneLayout(next.Layout);
        panel.DashboardActivePresetId = next.Id;
    }

    public static bool Activate(PanelSettings panel, string presetId)
    {
        var target = panel.DashboardPresets?.Find(p => p.Id == presetId);
        if (target is null)
            return false;
        CaptureActive(panel);
        panel.DashboardLayout = PanelPresets.CloneLayout(target.Layout);
        panel.DashboardActivePresetId = target.Id;
        return true;
    }

    private static void CaptureActive(PanelSettings panel)
    {
        var active = panel.DashboardPresets?.Find(p => p.Id == panel.DashboardActivePresetId);
        active?.Layout = PanelPresets.CloneLayout(panel.DashboardLayout);
    }

    private static string NewId() => Guid.NewGuid().ToString("n");
}
