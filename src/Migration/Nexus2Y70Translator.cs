using System;
using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Migration;

/// <summary>Translates the active profile's widgets.faces.y70 node: layout
/// (widget mapping + placement), appearance (theme -> accent/background), and
/// the gallery-widget file references.</summary>
internal static class Nexus2Y70Translator
{
    private static readonly Dictionary<string, string> TypeMap = new()
    {
        ["clock"] = "clock",
        ["performance"] = "monitoring",
        ["gallery"] = "gallery",
        ["media"] = "media",
        ["weather"] = "weather",
        ["snakeGame"] = "snake",
        ["blocks"] = "blocks",
        ["calculator"] = "calculator",
        ["emoji"] = "emoji",
        ["stopwatch"] = "stopwatch",
        ["timer"] = "timer",
        ["screentime"] = "screentime",
        ["cooling"] = "cooling",
        ["lighting"] = "lighting",
    };

    public static Nexus2Y70LayoutResult TranslateLayout(JsonElement y70)
    {
        var result = new Nexus2Y70LayoutResult();
        var pagesEl = Nexus2Json.GetArray(y70, "pages");
        result.Pages = pagesEl?.GetArrayLength() ?? 0;

        var pages = new List<PanelPageDto>();
        var droppedTypesSeen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var widget in EnumerateWidgets(y70))
        {
            var n2Type = Nexus2Json.GetString(widget, "type");
            if (n2Type is null)
            {
                continue;
            }
            result.Widgets++;

            if (!TypeMap.TryGetValue(n2Type, out var n3Type))
            {
                // First-seen order, not hash order, so the report reads
                // deterministically the same way the source file does.
                if (droppedTypesSeen.Add(n2Type))
                {
                    result.DroppedTypes.Add(n2Type);
                }
                continue;
            }

            var config = BuildConfig(n2Type, widget);
            var size = MapSize(Nexus2Json.GetString(widget, "size"));
            var placed = new PanelWidgetDto
            {
                Id = Guid.NewGuid().ToString(),
                Type = n3Type,
                Size = size,
                Config = config,
            };
            if (Y70LayoutPlacement.Append(pages, placed, size))
            {
                result.MappedWidgets++;
            }
        }

        if (pages.Count > 0)
        {
            result.Layout = new PanelLayoutDto
            {
                LayoutSchemaVersion = 2,
                Surface = "y70",
                Pages = pages,
            };
        }
        return result;
    }

    public static Nexus2AppearanceResult TranslateAppearance(JsonElement y70)
    {
        var theme = Nexus2Json.GetObject(y70, "theme");
        var result = new Nexus2AppearanceResult { Available = theme is not null };
        if (theme is not { } t)
        {
            return result;
        }

        var accent = Nexus2Json.GetObject(t, "accent");
        if (accent is { } a)
        {
            result.AccentHex = Nexus2WidgetMapping.RgbTripletToHex(Nexus2Json.GetString(a, "main"));
        }

        var opacity = Nexus2Json.GetDouble(t, "opacity");
        if (opacity is not null)
        {
            result.BackgroundOpacity = Math.Clamp(opacity.Value, 0, 1);
        }

        if (Nexus2Json.GetBool(y70, "bgDisabled", false))
        {
            result.BackgroundMode = "solid";
        }
        else
        {
            result.BackgroundMode = "shader";
            result.BackgroundEffect = Nexus2WidgetMapping.MapBackgroundEffect(Nexus2Json.GetString(y70, "bgName"));
        }
        return result;
    }

    public static Nexus2GallerySourcesResult TranslateGallerySources(JsonElement y70, Func<string, bool> fileExists)
    {
        var result = new Nexus2GallerySourcesResult();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var widget in EnumerateWidgets(y70))
        {
            if (Nexus2Json.GetString(widget, "type") != "gallery")
            {
                continue;
            }
            result.Available = true;
            var files = Nexus2Json.GetArray(widget, "files");
            if (files is not { } arr)
            {
                continue;
            }
            foreach (var file in arr.EnumerateArray())
            {
                var path = Nexus2Json.GetString(file, "path");
                if (string.IsNullOrEmpty(path) || !seen.Add(path))
                {
                    continue;
                }
                if (fileExists(path))
                {
                    result.ExistingPaths.Add(path);
                }
                else
                {
                    result.Missing++;
                }
            }
        }
        return result;
    }

    /// <summary>Dock widgets first (folded into the first output page), then
    /// every page's widgets in source order - preserves N2 page grouping and
    /// widget order into the sequential placement scan.</summary>
    private static IEnumerable<JsonElement> EnumerateWidgets(JsonElement y70)
    {
        var dock = Nexus2Json.GetArray(y70, "dock");
        if (dock is { } d)
        {
            foreach (var w in d.EnumerateArray())
            {
                yield return w;
            }
        }
        var pages = Nexus2Json.GetArray(y70, "pages");
        if (pages is not { } p)
        {
            yield break;
        }
        foreach (var page in p.EnumerateArray())
        {
            var widgets = Nexus2Json.GetArray(page, "widgets");
            if (widgets is not { } wa)
            {
                continue;
            }
            foreach (var w in wa.EnumerateArray())
            {
                yield return w;
            }
        }
    }

    private static string MapSize(string? n2Size) => n2Size switch
    {
        "1x1" => "1x1",
        "2x2" => "2x2",
        "4x2" => "4x2",
        "4x4" => "4x4",
        "4x8" => "4x4",
        _ => "2x2",
    };

    private static Dictionary<string, JsonElement>? BuildConfig(string n2Type, JsonElement widget) => n2Type switch
    {
        "clock" => Nexus2ConfigBuilders.Clock(
            Nexus2WidgetMapping.MapClockDesign(Nexus2Json.GetString(widget, "design")),
            Nexus2Json.GetString(widget, "timeFormat"),
            Nexus2Json.GetBool(widget, "seconds", false),
            Nexus2Json.GetBool(widget, "displayTimezone", false),
            Nexus2Json.GetString(widget, "timezone")),
        "performance" => Nexus2ConfigBuilders.Monitoring(PerformanceSlots(widget)),
        "gallery" => BuildGalleryConfig(widget),
        "weather" => BuildWeatherConfig(widget),
        _ => null,
    };

    private static List<Nexus2SlotInput> PerformanceSlots(JsonElement widget)
    {
        var sizeKey = Nexus2Json.GetBool(widget, "isImmersive", false)
            ? "immersive"
            : Nexus2Json.GetString(widget, "size") ?? "2x2";
        var bag = Nexus2Json.GetObject(widget, sizeKey);
        var slots = new List<Nexus2SlotInput>();
        if (bag is not { } b)
        {
            return slots;
        }
        foreach (var slotId in SlotIdsForSizeKey(sizeKey))
        {
            var slot = Nexus2Json.GetObject(b, slotId);
            if (slot is not { } s)
            {
                continue;
            }
            var sensor = Nexus2Json.GetObject(s, "sensor");
            slots.Add(new Nexus2SlotInput(
                Nexus2Json.GetString(s, "device"),
                sensor is { } se ? Nexus2Json.GetString(se, "type") : null,
                sensor is { } se2 ? Nexus2Json.GetString(se2, "name") : null,
                null,
                Nexus2Json.GetString(s, "design")));
        }
        return slots;
    }

    private static string[] SlotIdsForSizeKey(string sizeKey) => sizeKey switch
    {
        "2x2" => new[] { "slot1" },
        "4x2" => new[] { "slot1", "slot2", "slot3", "slot4", "slot5" },
        "4x4" => new[] { "slot1", "slot2", "slot3", "slot4", "slot5", "slot6", "slot7" },
        "immersive" => new[] { "slot1", "slot2", "slot3", "slot4", "slot5", "slot6", "slot7", "slot8", "slot9" },
        _ => Array.Empty<string>(),
    };

    private static Dictionary<string, JsonElement> BuildGalleryConfig(JsonElement widget)
    {
        var mode = Nexus2Json.GetString(widget, "mode") == "playlist" ? "slideshow" : "single";
        var b = new Nexus2ConfigBuilder();
        b.String("mode", mode);
        if (mode == "slideshow")
        {
            b.Number("interval", Nexus2WidgetMapping.MapGalleryIntervalSeconds(Nexus2Json.GetString(widget, "interval")));
        }
        return b.Build();
    }

    private static Dictionary<string, JsonElement> BuildWeatherConfig(JsonElement widget)
    {
        var loc = Nexus2Json.GetObject(widget, "weatherLocation");
        return Nexus2ConfigBuilders.Weather(
            Nexus2Json.GetString(widget, "units"),
            loc is { } l ? Nexus2Json.GetDouble(l, "lat") : null,
            loc is { } l2 ? Nexus2Json.GetDouble(l2, "long") : null,
            loc is { } l3 ? Nexus2Json.GetString(l3, "display_name") : null);
    }

}
