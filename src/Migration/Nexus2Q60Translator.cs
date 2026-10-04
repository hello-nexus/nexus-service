using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Nexus.Service.Migration;

/// <summary>Translates the active profile's q60.software node: the active
/// page's front becomes the q60 record's single widget, every other page's
/// translatable front is stashed by widget type, and the profile-level
/// background resolves against q60\web\user-media.</summary>
internal static class Nexus2Q60Translator
{
    internal static readonly string[] Q60UserMediaRelative = { "q60", "web", "user-media" };
    private static readonly string[] Q60StockRelative = { "q60", "web", "bgs" };

    public static Nexus2Q60FaceResult TranslateFace(JsonElement q60Software)
    {
        var result = new Nexus2Q60FaceResult();
        var pagesEl = Nexus2Json.GetArray(q60Software, "pages");
        if (pagesEl is not { } pages)
        {
            return result;
        }
        result.Available = pages.GetArrayLength() > 0;

        var activePageId = Nexus2Json.GetString(q60Software, "activePageId");
        foreach (var page in pages.EnumerateArray())
        {
            var front = Nexus2Json.GetObject(page, "front");
            if (front is not { } f)
            {
                continue;
            }
            var isActive = activePageId is not null && Nexus2Json.GetString(page, "id") == activePageId;
            var translated = TranslateFront(f);

            if (isActive)
            {
                var theme = Nexus2Json.GetObject(f, "theme");
                if (theme is { } th)
                {
                    result.AccentHex = Nexus2WidgetMapping.RgbTripletToHex(Nexus2Json.GetString(th, "accentColor"));
                }
                if (translated is { } t)
                {
                    result.ActiveWidgetType = t.Type;
                    result.ActiveConfig = t.Config;
                }
                continue;
            }

            if (translated is { } stash && !result.StashedConfigs.ContainsKey(stash.Type))
            {
                result.StashedConfigs[stash.Type] = stash.Config ?? new Dictionary<string, JsonElement>();
            }
        }
        return result;
    }

    private static (string Type, Dictionary<string, JsonElement>? Config)? TranslateFront(JsonElement front)
    {
        var type = Nexus2Json.GetString(front, "type");
        switch (type)
        {
            case "clock":
            {
                var design = Nexus2WidgetMapping.MapQ60ClockDesign(Nexus2Json.GetString(front, "design"));
                var config = Nexus2ConfigBuilders.Clock(
                    design,
                    Nexus2Json.GetString(front, "timeFormat"),
                    showSeconds: false,
                    showTimezone: false,
                    timezone: null);
                return ("clock", config);
            }
            case "performance":
                return ("monitoring", Nexus2ConfigBuilders.Monitoring(PerformanceSlots(front)));
            case "weather":
            {
                var location = Nexus2Json.GetObject(front, "location");
                var units = location is { } loc ? Nexus2Json.GetString(loc, "units") : null;
                var data = location is { } loc2 ? Nexus2Json.GetObject(loc2, "locationData") : null;
                return ("weather", Nexus2ConfigBuilders.Weather(
                    units,
                    data is { } d ? Nexus2Json.GetDouble(d, "lat") : null,
                    data is { } d2 ? Nexus2Json.GetDouble(d2, "long") : null,
                    data is { } d3 ? Nexus2Json.GetString(d3, "display_name") : null));
            }
            case "media":
                return ("media", null);
            default:
                return null;
        }
    }

    private static List<Nexus2SlotInput> PerformanceSlots(JsonElement front)
    {
        var slots = new List<Nexus2SlotInput>();
        foreach (var slotId in new[] { "slot1", "slot2", "slot3" })
        {
            var slot = Nexus2Json.GetObject(front, slotId);
            if (slot is not { } s)
            {
                continue;
            }
            slots.Add(new Nexus2SlotInput(
                Nexus2Json.GetString(s, "device"),
                null,
                null,
                Nexus2Json.GetString(s, "sensorId"),
                Nexus2Json.GetString(s, "design")));
        }
        return slots;
    }

    public static Nexus2WallpaperResult TranslateWallpaper(JsonElement q60Software, string configDir, Func<string, bool> fileExists)
    {
        var result = new Nexus2WallpaperResult();
        var background = Nexus2Json.GetObject(q60Software, "background");
        if (background is not { } bg)
        {
            return result;
        }
        result.Playlist = Nexus2Json.GetBool(bg, "playlistMode", false);
        if (Nexus2Json.GetDouble(bg, "playlistInterval") is { } intervalMs && intervalMs > 0)
        {
            result.PlaylistIntervalSec = Math.Max(1, (int)Math.Round(intervalMs / 1000));
        }
        var gallerySource = Nexus2Json.GetString(bg, "gallerySource");
        if (string.IsNullOrEmpty(gallerySource))
        {
            return result;
        }

        // The Q-Series frontend plays a stock name (particles, cyber, ...) from bgs\<name>.webm.
        var fullPath = Path.Combine(configDir, Path.Combine(Q60UserMediaRelative), gallerySource);
        var stockPath = Path.Combine(configDir, Path.Combine(Q60StockRelative), gallerySource + ".webm");
        if (!fileExists(fullPath))
        {
            var isMediaBackground = Nexus2Json.GetString(bg, "type") == "media-background";
            if (!isMediaBackground || Path.HasExtension(gallerySource) || !fileExists(stockPath))
            {
                return result;
            }
            fullPath = stockPath;
            gallerySource = Path.GetFileName(stockPath);
        }

        result.Available = true;
        result.AbsolutePath = fullPath;
        result.FileName = gallerySource;
        return result;
    }
}
