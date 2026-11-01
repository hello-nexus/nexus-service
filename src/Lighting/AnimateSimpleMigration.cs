using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// The stored sweep predates the per-effect simple flag. Only an install on the simple
/// dashboard ran its sweep full-frame, so the flag is derived from that persisted mode
/// and the effect key; an advanced install keeps the layout mapping it always showed.
/// </summary>
public static class AnimateSimpleMigration
{
    public static void Apply(NexusSettings doc)
    {
        var simple = string.Equals(doc.Ui?.LightingDashboardMode, "simple", System.StringComparison.Ordinal);
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
