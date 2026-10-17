using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// The Game Sync lighting mode no longer exists. A sync value naming it, from a
/// settings file, a profile, a layout preset, or a caller, lands on static, the
/// mode that restores the user's own static look.
/// </summary>
public static class RemovedSyncModes
{
    private const string GameSync = "gamesync";

    public static string Normalize(string? sync) =>
        string.Equals(sync, GameSync, StringComparison.OrdinalIgnoreCase) ? "static" : sync ?? "";

    /// <summary>Rewrites the live mode and every layout preset's look; true when anything changed.</summary>
    public static bool Apply(LightingSettings? lighting)
    {
        if (lighting is null)
        {
            return false;
        }
        var changed = false;
        if (IsRemoved(lighting.Sync))
        {
            lighting.Sync = "static";
            changed = true;
        }
        if (lighting.LayoutPresets is { } presets)
        {
            foreach (var preset in presets)
            {
                if (preset?.Look is { } look && IsRemoved(look.Sync))
                {
                    look.Sync = "static";
                    changed = true;
                }
            }
        }
        return changed;
    }

    private static bool IsRemoved(string? sync) => string.Equals(sync, GameSync, StringComparison.OrdinalIgnoreCase);
}
