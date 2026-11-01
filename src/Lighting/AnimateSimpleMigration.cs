using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Settings and profile files written before the per-effect simple flag stored none. The
/// dashboard mode is the only record of how the user ran their sweep, so the flag is
/// derived from it and the effect key; an advanced install is not given full-frame back.
/// </summary>
public static class AnimateSimpleMigration
{
    public const int SchemaVersion = 30;

    /// <summary>A settings document below v30, using its own persisted dashboard mode.</summary>
    public static void Apply(NexusSettings doc) => Derive(doc, IsSimple(doc.Ui?.LightingDashboardMode));

    /// <summary>A profile file or payload below v30. The dashboard mode lives at the settings root, not in a profile, so the caller passes it.</summary>
    public static void ApplyProfile(NexusSettings? data, string? rootDashboardMode)
    {
        if (data is null || data.SchemaVersion >= SchemaVersion)
        {
            return;
        }
        Derive(data, IsSimple(rootDashboardMode));
        data.SchemaVersion = SchemaVersion;
    }

    private static bool IsSimple(string? mode) => string.Equals(mode, "simple", System.StringComparison.Ordinal);

    private static void Derive(NexusSettings doc, bool simple)
    {
        if (doc.Lighting is not { } lighting)
        {
            return;
        }
        if (lighting.Animate is { } animate)
        {
            animate.Simple = simple && IsSweep(animate.Effect);
        }
        if (lighting.LayoutPresets is { } presets)
        {
            foreach (var preset in presets)
            {
                if (preset?.Look is { } look)
                {
                    look.AnimateSimple = simple && IsSweep(look.AnimateEffect);
                }
            }
        }
    }

    private static bool IsSweep(string? key) => !string.IsNullOrEmpty(key) && ShaderLibrary.IsSweepEffect(key);
}
