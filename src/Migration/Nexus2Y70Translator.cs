using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Service.Deck;
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

    public static Nexus2Y70LayoutResult TranslateLayout(JsonElement y70, Func<string, bool>? fileExists = null)
    {
        var result = new Nexus2Y70LayoutResult();
        var pagesEl = Nexus2Json.GetArray(y70, "pages");
        result.Pages = pagesEl?.GetArrayLength() ?? 0;

        var pages = new List<PanelPageDto>();
        var droppedTypesSeen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var widget in EnumerateWidgets(y70))
        {
            var n2Type = Nexus2Json.GetString(widget, "type");
            // Nexus 3 panels do not support 1x1 widgets: left out, not reported as dropped.
            if (n2Type is null || Nexus2Json.GetString(widget, "size") == "1x1")
            {
                continue;
            }
            result.Widgets++;

            if (n2Type == "iframe")
            {
                TranslateIframe(widget, pages, result);
                continue;
            }

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

        // The dock goes in after every other widget so none of them moves for it.
        if (TranslateDock(y70, fileExists ?? File.Exists) is { } dock)
        {
            var deck = new PanelWidgetDto { Id = Guid.NewGuid().ToString(), Type = "deck", Size = DockSize };
            // Bottom row of page 1; when a widget holds it the strip follows the normal first-free-cell scan.
            if (Y70LayoutPlacement.TryPlaceAt(pages, deck, DockSize, 0, Y70LayoutPlacement.GridRows - 1)
                || Y70LayoutPlacement.Append(pages, deck, DockSize))
            {
                dock.WidgetId = deck.Id;
                result.Dock = dock;
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

    private const string WebViewType = "web-view";
    private const string WebViewSize = "4x4";
    private const string WebViewUrlKey = "url";
    private const string WebViewZoomKey = "zoom";
    private const string WebViewRefreshKey = "refreshMinutes";
    private const double WebViewZoomMin = 0.25;
    private const double WebViewZoomMax = 3;

    internal const string NoteIframeInvalidUrl = "iframe:invalid-url";
    internal const string NoteIframeNoSrc = "iframe:no-src";
    internal const string NoteIframeNoSpace = "iframe:no-space";

    private static readonly Regex IframeSrc = new(
        "<iframe\\b[^>]*?\\bsrc\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static void TranslateIframe(JsonElement widget, List<PanelPageDto> pages, Nexus2Y70LayoutResult result)
    {
        var code = Nexus2Json.GetString(widget, "mode") == "code";
        var raw = code ? ExtractIframeSrc(Nexus2Json.GetString(widget, "code")) : Nexus2Json.GetString(widget, "url");
        if (code && raw is null)
        {
            result.Notes.Add(NoteIframeNoSrc);
            return;
        }
        var url = NormalizeWebViewUrl(raw);
        if (url is null)
        {
            result.Notes.Add(NoteIframeInvalidUrl);
            return;
        }

        var zoom = Nexus2Json.GetDouble(widget, "zoom");
        var clamped = zoom is { } z && double.IsFinite(z) ? Math.Clamp(z, WebViewZoomMin, WebViewZoomMax) : 1;
        var config = new Nexus2ConfigBuilder()
            .String(WebViewUrlKey, url)
            .Number(WebViewZoomKey, clamped)
            .Number(WebViewRefreshKey, 0)
            .Build();
        var placed = new PanelWidgetDto
        {
            Id = Guid.NewGuid().ToString(),
            Type = WebViewType,
            Size = WebViewSize,
            Config = config,
        };
        if (Y70LayoutPlacement.Append(pages, placed, WebViewSize))
        {
            result.MappedWidgets++;
        }
        else
        {
            result.Notes.Add(NoteIframeNoSpace);
        }
    }

    private static string? ExtractIframeSrc(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }
        try
        {
            var m = IframeSrc.Match(html);
            if (!m.Success)
            {
                return null;
            }
            var src = WebUtility.HtmlDecode((m.Groups[1].Success ? m.Groups[1] : m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value).Trim();
            return src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? src
                : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>Same rule as the Web View widget: bare hosts get https://, only http/https survive.</summary>
    internal static string? NormalizeWebViewUrl(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            text = "https:" + text;
        }
        else if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }
        return uri.AbsoluteUri;
    }

    private const string DockSize = "4x1";
    private const int DockKeysPerRow = 4;

    /// <summary>Nexus 2's Y70 dock as deck key pages (4 keys each). Null when the dock is hidden, empty, or
    /// holds nothing that maps to a Nexus 3 key action: an all-empty deck would carry nothing over.</summary>
    internal static Nexus2DockDeck? TranslateDock(JsonElement y70, Func<string, bool> fileExists)
    {
        if (Nexus2Json.GetBool(y70, "hideDock", false) || Nexus2Json.GetArray(y70, "dock") is not { } dockEl)
        {
            return null;
        }
        var items = dockEl.EnumerateArray()
            .Select((item, index) => (item, index))
            .OrderBy(t => Nexus2Json.GetDouble(t.item, "positionHorizontal") ?? Nexus2Json.GetDouble(t.item, "position") ?? t.index)
            .ThenBy(t => t.index)
            .Select(t => t.item)
            .ToList();
        var slots = items.Select(item => DockItemToSlot(item, fileExists)).ToList();
        var mapped = slots.Count(s => s.Action is not null);
        if (mapped == 0)
        {
            return null;
        }
        // One authored page of 4 columns: a dock past four keys makes the preset larger than the 4x1 widget, so
        // DeckConfigNavigation.FitToGrid pages it and adds the next/prev keys that reach the later keys.
        var rows = (slots.Count + DockKeysPerRow - 1) / DockKeysPerRow;
        while (slots.Count < rows * DockKeysPerRow)
        {
            slots.Add(new DeckSlot());
        }
        var config = new DeckConfig { Pages = { new DeckPage { Slots = slots } } };
        return new Nexus2DockDeck { Config = config, Rows = rows, Items = items.Count, MappedItems = mapped };
    }

    /// <summary>One dock item as a deck key: a macros item whose macro has a Nexus 3 key action keeps it (and its title);
    /// every other item (a dock widget, a keystroke recording that is not one key press, a missing audio clip) is an empty key.</summary>
    private static DeckSlot DockItemToSlot(JsonElement item, Func<string, bool> fileExists)
    {
        var action = Nexus2Json.GetString(item, "type") == "macros" && Nexus2Json.GetObject(item, "macro") is { } macro
            ? DockMacroToAction(macro, fileExists)
            : null;
        if (action is null)
        {
            return new DeckSlot();
        }
        var title = Nexus2Json.GetObject(item, "title") is { } t ? Nexus2Json.GetString(t, "value") : null;
        return new DeckSlot { Action = action, Label = string.IsNullOrWhiteSpace(title) ? null : title };
    }

    /// <summary>A Windows path on a local drive ("C:\\..."); judged by text so it reads the same on any host.</summary>
    private static bool IsDriveLetterPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    private static DeckAction? DockMacroToAction(JsonElement macro, Func<string, bool> fileExists)
    {
        switch (Nexus2Json.GetString(macro, "type"))
        {
            case "shortcut":
                // launchApp resolves a Start-menu id (a Store app's AUMID has a '!'); an exe path has no entry
                // there, so it opens as a file.
                if (Nexus2Json.GetString(macro, "targetId") is not { Length: > 0 } target)
                {
                    return null;
                }
                return IsDriveLetterPath(target) && !target.Contains('!')
                    ? new DeckAction { Type = "openFile", Path = target }
                    : new DeckAction { Type = "launchApp", AppId = target };
            case "website":
                return Nexus2Json.GetString(macro, "url")?.Trim() is { Length: > 0 } url && (url.Contains("://") || !url.Contains(':'))
                    ? new DeckAction { Type = "openUrl", Url = url.Contains("://") ? url : "https://" + url }
                    : null;
            case "audio":
                return Nexus2Json.GetString(macro, "clipPath") is { Length: > 0 } clip && fileExists(clip)
                    ? new DeckAction
                    {
                        Type = "playAudio",
                        Path = clip,
                        Volume = Nexus2Json.GetDouble(macro, "volume") is { } v ? (int)Math.Clamp(Math.Round(v), 0, 100) : null,
                    }
                    : null;
            case "recording":
                return RecordingToHotkey(macro) is { } keys ? new DeckAction { Type = "hotkey", Keys = keys } : null;
            default:
                return null;
        }
    }

    /// <summary>A recording of one key press (down then up, modifiers held) as a deck hotkey combo; anything longer has no key equivalent.</summary>
    private static string? RecordingToHotkey(JsonElement macro)
    {
        if (Nexus2Json.GetArray(macro, "strokes") is not { } strokes || strokes.GetArrayLength() != 2)
        {
            return null;
        }
        var down = strokes[0];
        var up = strokes[1];
        var code = Nexus2Json.GetString(down, "key");
        if (code is null || Nexus2Json.GetString(down, "type") != "keydown"
            || Nexus2Json.GetString(up, "type") != "keyup" || Nexus2Json.GetString(up, "key") != code
            || HotkeyToken(code) is not { } token)
        {
            return null;
        }
        var parts = new List<string>();
        if (Nexus2Json.GetBool(down, "ctrl", false)) { parts.Add("ctrl"); }
        if (Nexus2Json.GetBool(down, "shift", false)) { parts.Add("shift"); }
        if (Nexus2Json.GetBool(down, "alt", false)) { parts.Add("alt"); }
        if (Nexus2Json.GetBool(down, "meta", false)) { parts.Add("meta"); }
        parts.Add(token);
        return string.Join('+', parts);
    }

    /// <summary>A KeyboardEvent.code as the token DeckActionExecutor.ParseHotkey reads; null for codes without one.</summary>
    private static string? HotkeyToken(string code)
    {
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && code[3] is >= 'A' and <= 'Z')
        {
            return char.ToLowerInvariant(code[3]).ToString();
        }
        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && code[5] is >= '0' and <= '9')
        {
            return code[5].ToString();
        }
        if (code.Length is 2 or 3 && code[0] == 'F' && int.TryParse(code.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            return "f" + fn;
        }
        return code switch
        {
            "Space" => "space",
            "Enter" => "enter",
            "Tab" => "tab",
            "Escape" => "escape",
            "Backspace" => "backspace",
            "Delete" => "delete",
            "Insert" => "insert",
            "Home" => "home",
            "End" => "end",
            "PageUp" => "pageup",
            "PageDown" => "pagedown",
            "ArrowUp" => "up",
            "ArrowDown" => "down",
            "ArrowLeft" => "left",
            "ArrowRight" => "right",
            _ => null,
        };
    }

    // Nexus 2's isValidVideo / isValidImage: a bg with one of these is a file, anything else is CSS.
    private static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".webm", ".avi", ".mkv", ".flv" };
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
    // Chromium's <video> cannot open avi/mkv/flv, so Nexus 2 showed its particles fallback for them.
    private static readonly string[] UnplayableVideoExtensions = { ".avi", ".mkv", ".flv" };

    public static Nexus2AppearanceResult TranslateAppearance(JsonElement y70, Func<string, bool> fileExists)
    {
        var theme = Nexus2Json.GetObject(y70, "theme");
        var result = new Nexus2AppearanceResult { Available = theme is not null };
        if (theme is not { } t)
        {
            return result;
        }

        result.Transparent = Nexus2Json.GetBool(t, "transparent", false);
        if (Nexus2Json.GetDouble(t, "opacity") is { } opacity)
        {
            result.WidgetOpacity = Math.Clamp(opacity, 0, 1);
        }

        // Nexus 2's "Primary color" (the one its picker sets from a custom
        // background's dominant colour) becomes the Nexus 3 accent.
        var primary = Nexus2Json.GetObject(t, "primary");
        if (primary is { } p)
        {
            result.AccentHex = Nexus2WidgetMapping.RgbTripletToHex(Nexus2Json.GetString(p, "main"));
        }

        // Mirrors Y70Background.tsx: empty bg renders the bundled particles, a
        // media path renders the file and falls back to particles when it
        // fails to load, any other string is a CSS background.
        var bg = (Nexus2Json.GetString(y70, "bg") ?? "").Split('?')[0];
        var isVideo = HasExtension(bg, VideoExtensions);
        if (bg.Length == 0)
        {
            result.Background = Nexus2Y70BackgroundSource.BundledParticles;
        }
        else if (isVideo || HasExtension(bg, ImageExtensions))
        {
            if (!HasExtension(bg, UnplayableVideoExtensions) && fileExists(bg))
            {
                result.Background = Nexus2Y70BackgroundSource.File;
                result.BackgroundPath = bg;
            }
            else
            {
                result.Background = Nexus2Y70BackgroundSource.BundledParticles;
            }
        }
        else if (Nexus2Gradient.Parse(bg) is { } gradient)
        {
            result.Background = Nexus2Y70BackgroundSource.Gradient;
            result.BackgroundGradient = gradient;
        }

        // bgDisabled freezes a video on its first frame (FirstFrameVideo); an image is unaffected.
        var playsVideo = result.Background == Nexus2Y70BackgroundSource.BundledParticles
            || (result.Background == Nexus2Y70BackgroundSource.File && isVideo);
        result.BackgroundStill = playsVideo && Nexus2Json.GetBool(y70, "bgDisabled", false);
        return result;
    }

    /// <summary>A file Nexus 2 can show as the Y70 background.</summary>
    public static bool IsShowableFile(string path) =>
        (HasExtension(path, VideoExtensions) || HasExtension(path, ImageExtensions)) && !HasExtension(path, UnplayableVideoExtensions);

    private static bool HasExtension(string path, string[] extensions) =>
        Array.Exists(extensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

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
