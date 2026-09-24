using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Layouts saved before free rotation stored a quarter-turned frame as its
/// turned footprint (width and height already swapped) and remapped the LEDs
/// inside it. A layout now stores the frame before it turns and the engine
/// turns it about the centre, so a 90 or 270 record from that era gets its
/// sides swapped back about the same centre. Runs once per lighting document
/// (the flag marks it done): on the live settings at schema v17, and on a
/// profile's document as it is loaded into the live settings. A document this
/// build creates carries the flag from the start.
/// </summary>
public static class LayoutRotationMigration
{
    public static void Apply(LightingSettings? lighting)
    {
        if (lighting is null || lighting.FreeRotationLayouts) return;
        Unswap(lighting.DeviceLayouts?.Values);
        if (lighting.LayoutPresets is { } presets)
        {
            foreach (var preset in presets) Unswap(preset.Layouts?.Values);
        }
        lighting.FreeRotationLayouts = true;
    }

    private static void Unswap(IEnumerable<DeviceLayout>? layouts)
    {
        if (layouts is null) return;
        foreach (var layout in layouts)
        {
            var rot = ((layout.Rotation % 360) + 360) % 360;
            if (rot != 90 && rot != 270) continue;
            var (w, h) = (layout.W, layout.H);
            layout.X += (w - h) * 0.5f;
            layout.Y += (h - w) * 0.5f;
            layout.W = h;
            layout.H = w;
        }
    }
}
