using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Widgets;

namespace Nexus.Service.Telemetry;

/// <summary>Install, update, removal and layout placement events for SDK apps. The only properties are ids, versions and a reason token.</summary>
public static class AppLifecycleTelemetry
{
    public const string ReasonUser = "user";
    public const string ReasonAuto = "auto";
    public const string ReasonYank = "yank";

    public static void Installed(ITelemetry? telemetry, string appId, string appVersion, string reason) =>
        telemetry?.Capture(TelemetryEvents.AppInstalled, ("app_id", appId), ("app_version", appVersion), ("reason", reason));

    public static void Updated(ITelemetry? telemetry, string appId, string appVersion, string fromVersion, string reason) =>
        telemetry?.Capture(TelemetryEvents.AppUpdated, ("app_id", appId), ("app_version", appVersion), ("from_version", fromVersion), ("reason", reason));

    public static void Uninstalled(ITelemetry? telemetry, string appId, string appVersion, string reason) =>
        telemetry?.Capture(TelemetryEvents.AppUninstalled, ("app_id", appId), ("app_version", appVersion), ("reason", reason));

    /// <summary>The version in an app folder's manifest, or null when unreadable.</summary>
    internal static string? ReadVersion(string appDir)
    {
        try
        {
            var path = Path.Combine(appDir, "manifest.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>App widget instance id to app id for one layout; empty for a null layout.</summary>
    internal static Dictionary<string, string> AppWidgets(PanelLayoutDto? layout)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (layout is null) return result;
        foreach (var page in layout.Pages)
        {
            foreach (var w in page.Widgets)
            {
                if (WidgetSettingsService.MarketplaceIdFromType(w.Type) is { } appId && AppIds.IsValid(appId))
                    result[w.Id] = appId;
            }
        }
        return result;
    }

    /// <summary>
    /// Emits app_placed / app_removed for the app widget instances that appeared in or left the layout. A save that
    /// keeps the same instances emits nothing. A null <paramref name="before"/> is a layout seen for the first time,
    /// not a placement, so it emits nothing. <paramref name="surfaceOverride"/> names the surface when the layout is not a device's (the desktop dashboard).
    /// </summary>
    public static void LayoutChanged(ITelemetry telemetry, PanelLayoutDto? before, PanelLayoutDto? after, string? surfaceOverride = null)
    {
        if (before is null || after is null) return;
        var was = AppWidgets(before);
        var now = AppWidgets(after);
        var surface = surfaceOverride ?? after.Surface;
        foreach (var (instance, appId) in now)
        {
            if (!was.ContainsKey(instance))
                telemetry.Capture(TelemetryEvents.AppPlaced, ("app_id", appId), ("surface", surface));
        }
        foreach (var (instance, appId) in was)
        {
            if (!now.ContainsKey(instance))
                telemetry.Capture(TelemetryEvents.AppRemoved, ("app_id", appId), ("surface", surface));
        }
    }
}
