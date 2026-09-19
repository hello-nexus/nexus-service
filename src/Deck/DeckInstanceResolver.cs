using Nexus.Service.Persistence;

namespace Nexus.Service.Deck;

/// <summary>
/// Resolves a deck instance id to its active preset, fitted to a concrete
/// grid. Shared by the Stream Deck worker/routes and the panel deck-dispatch
/// route so instance -> preset -> FitToGrid resolves identically everywhere.
/// </summary>
public static class DeckInstanceResolver
{
    private const string PhysicalPrefix = "streamdeck:";
    private const string WidgetPrefix = "widget:";

    public static string PhysicalInstanceId(string serial) => PhysicalPrefix + serial;
    public static string WidgetInstanceId(string widgetId) => WidgetPrefix + widgetId;

    /// <summary>The preset an instance currently points at, or null when the instance has none or its preset id no longer resolves.</summary>
    public static DeckPreset? ResolveActivePreset(StreamDeckSettings settings, string instanceId)
    {
        if (!settings.Instances.TryGetValue(instanceId, out var instance) || instance.ActivePresetId is null)
        {
            return null;
        }
        return settings.Presets.Find(p => p.Id == instance.ActivePresetId);
    }

    /// <summary>The mode an instance is in ("custom" | "recentApps" | "appAware"); unrecognized or missing resolves to "custom".</summary>
    public static string ResolveMode(StreamDeckSettings settings, string instanceId)
    {
        if (!settings.Instances.TryGetValue(instanceId, out var instance))
        {
            return "custom";
        }
        return instance.Mode is "custom" or "recentApps" or "appAware" ? instance.Mode : "custom";
    }

    /// <summary>
    /// The fitted config an instance currently shows on a targetCols x
    /// targetRows grid, or an empty single-page config when the instance has
    /// no resolvable preset.
    /// </summary>
    public static DeckConfig ResolveFittedConfig(StreamDeckSettings settings, string instanceId, int targetCols, int targetRows, DeckTargetKind kind)
    {
        var preset = ResolveActivePreset(settings, instanceId);
        return preset is null
            ? new DeckConfig()
            : DeckConfigNavigation.FitToGrid(preset.Cols, preset.Rows, preset.Deck, targetCols, targetRows, kind);
    }
}
