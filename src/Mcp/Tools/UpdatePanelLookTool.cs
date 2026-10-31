using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>
/// Write tool: patches a panel's look through PanelDeviceRegistry.Patch, the same call
/// POST /panel/devices/{id} makes. Only look fields are accepted; layout, hardware and
/// restricted-mode fields stay with their own tools and the desktop UI.
/// </summary>
public sealed class UpdatePanelLookTool : IMcpTool
{
    // nexus-web types/lighting.ts TEMPLATE_COUNT and panelBackground.ts DEFAULT_PANEL_BACKGROUND_EFFECT.
    private const int TemplateCount = 4;
    private const string DefaultEffect = "plasma";

    private static readonly string[] ThemeModes = ["system", "dark", "light"];
    private static readonly string[] BackgroundModes = ["solid", "shader", "media"];
    private static readonly string[] TextColorModes = ["adaptive", "theme", "custom"];
    // nexus-web panel/theme/panelFont.ts PANEL_FONTS.
    private static readonly string[] Fonts = ["lexend", "oswald", "tektur", "jura", "piazzolla"];

    private readonly PanelDeviceRegistry _registry;
    private readonly PanelBgLibrary _media;
    private readonly MultiplexHub _hub;

    public UpdatePanelLookTool(PanelDeviceRegistry registry, PanelBgLibrary media, MultiplexHub hub)
    {
        _registry = registry;
        _media = media;
        _hub = hub;
    }

    public string Name => "update_panel_look";
    public string Title => "Update Panel Look";

    public string Description =>
        "Changes how a panel looks: theme, accent colour, background (solid colour, animated shader " +
        "effect, or imported media), widget opacity, labels, padding, text colour, font and the gauge " +
        "colour ramp. Send only the fields to change; the rest stay as they are. The panel redraws at " +
        "once, and the change also lands in the panel's loaded preset. Shader effects use the same keys " +
        "as the lighting effects (e.g. plasma, aurora, nebula, galaxy, synthwave, starfield, " +
        "gradientlinear, simpleblue); backgroundTemplate picks one of that effect's 4 colour presets. " +
        "backgroundMediaId must be an id from get_panel's backgroundMedia. Accent and theme follow " +
        "the desktop while accentSyncWithDesktop / themeSyncWithDesktop are true, so set those false " +
        "to make a custom accent or theme stick.";

    public McpCapability Capability => McpCapability.Panels;
    public bool ReadOnly => false;

    private const string Hex = "\"type\":\"string\",\"pattern\":\"^#[0-9a-fA-F]{6}$\"";

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"panel\":{\"type\":\"string\",\"description\":\"" + PanelToolSupport.PanelArgDescription + "\"}," +
        "\"displayName\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Panel name shown in Nexus.\"}," +
        "\"themeMode\":{\"type\":\"string\",\"enum\":[\"system\",\"dark\",\"light\"]}," +
        "\"themeSyncWithDesktop\":{\"type\":\"boolean\"}," +
        "\"accentColor\":{" + Hex + ",\"description\":\"Accent as #rrggbb.\"}," +
        "\"accentSyncWithDesktop\":{\"type\":\"boolean\"}," +
        "\"backgroundMode\":{\"type\":\"string\",\"enum\":[\"solid\",\"shader\",\"media\"]}," +
        "\"backgroundColor\":{" + Hex + ",\"description\":\"Solid background in dark theme.\"}," +
        "\"backgroundColorLight\":{" + Hex + ",\"description\":\"Solid background in light theme.\"}," +
        "\"backgroundEffect\":{\"type\":\"string\",\"description\":\"Shader effect key for backgroundMode shader.\"}," +
        "\"backgroundTemplate\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":3,\"description\":\"Colour preset of the effect.\"}," +
        "\"backgroundOpacity\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1}," +
        "\"backgroundFrostLevel\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100,\"description\":\"Frosted-glass blur over the background, percent.\"}," +
        "\"backgroundMediaId\":{\"type\":\"string\",\"description\":\"Media id from get_panel, or empty to clear.\"}," +
        "\"backgroundMediaSlideshow\":{\"type\":\"boolean\",\"description\":\"Cycle through every imported media item.\"}," +
        "\"backgroundMediaInterval\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Seconds per slideshow item.\"}," +
        "\"backgroundMediaShuffle\":{\"type\":\"boolean\"}," +
        "\"immersiveBackdrop\":{\"type\":\"boolean\",\"description\":\"Full-screen widget views paint a solid backdrop.\"}," +
        "\"widgetOpacity\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1}," +
        "\"widgetLabels\":{\"type\":\"boolean\"}," +
        "\"widgetPadding\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100,\"description\":\"Space between widgets, percent.\"}," +
        "\"textColorMode\":{\"type\":\"string\",\"enum\":[\"adaptive\",\"theme\",\"custom\"]}," +
        "\"textColor\":{" + Hex + ",\"description\":\"Widget text colour for textColorMode custom.\"}," +
        "\"font\":{\"type\":\"string\",\"description\":\"lexend, oswald, tektur, jura, piazzolla, system:<installed family>, or empty for the app font.\"}," +
        "\"gaugeGradient\":{\"type\":\"array\",\"minItems\":2,\"description\":\"Colour ramp for value-coloured gauges.\",\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"at\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1},\"color\":{\"type\":\"string\",\"description\":\"#rrggbb or accent.\"}" +
        "},\"required\":[\"at\",\"color\"],\"additionalProperties\":false}}" +
        "},\"required\":[\"panel\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var record = PanelToolSupport.Resolve(_registry, McpArgs.StringArg(args, "panel"), out var error);
        if (record is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error(error));
        }

        var patch = new PanelDevicePatch();
        var changed = new List<string>();
        var problems = new List<string>();
        foreach (var prop in args!.Value.EnumerateObject())
        {
            if (prop.Name == "panel")
            {
                continue;
            }
            if (!Apply(prop, record, patch, problems))
            {
                continue;
            }
            changed.Add(prop.Name);
        }
        if (problems.Count > 0)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Nothing was changed. " + string.Join(" ", problems)));
        }
        if (changed.Count == 0)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Send at least one look field to change; see get_panel for the current look."));
        }

        ResolveTemplate(record, patch);
        var updated = _registry.Patch(record.Id, patch);
        if (updated is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Panel not found."));
        }
        PanelTopics.BroadcastPanelDevice(_hub, record.Id);

        var result = new McpPanelLookResult { Panel = record.Id, Changed = changed, Look = PanelToolSupport.LookOf(updated) };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpPanelLookResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }

    /// <summary>Copies one argument onto the patch. False (with a problem recorded) when the value is invalid.</summary>
    private bool Apply(JsonProperty prop, PanelDeviceRecord record, PanelDevicePatch patch, List<string> problems)
    {
        var v = prop.Value;
        var name = prop.Name;
        switch (name)
        {
            case "displayName":
                return Text(v, name, problems, s => s.Trim().Length > 0, "a non-empty name", s => patch.DisplayName = s);
            case "themeMode":
                return OneOf(v, name, ThemeModes, problems, s => patch.ThemeMode = s);
            case "backgroundMode":
                return OneOf(v, name, BackgroundModes, problems, s => patch.BackgroundMode = s);
            case "textColorMode":
                return OneOf(v, name, TextColorModes, problems, s => patch.TextColorMode = s);
            case "accentColor":
                return Text(v, name, problems, IsHexColor, "#rrggbb", s => patch.AccentColor = s.ToLowerInvariant());
            case "backgroundColor":
                return Text(v, name, problems, IsHexColor, "#rrggbb", s => patch.BackgroundColor = s.ToLowerInvariant());
            case "backgroundColorLight":
                return Text(v, name, problems, IsHexColor, "#rrggbb", s => patch.BackgroundColorLight = s.ToLowerInvariant());
            case "textColor":
                return Text(v, name, problems, IsHexColor, "#rrggbb", s => patch.TextColor = s.ToLowerInvariant());
            case "backgroundEffect":
                return Text(v, name, problems, IsKnownEffect,
                    "a shader effect key such as plasma, aurora, nebula, galaxy, synthwave or gradientlinear",
                    s => patch.BackgroundEffect = s);
            case "font":
                return Text(v, name, problems, IsValidFont,
                    $"one of {string.Join(", ", Fonts)}, system:<installed family>, or empty for the app font",
                    s => patch.Font = s);
            case "backgroundMediaId":
                return Text(v, name, problems, s => s.Length == 0 || _media.GetItem(record.Id, s) is not null,
                    "an id from get_panel's backgroundMedia, or empty to clear", s => SetMedia(record.Id, s, patch));
            case "backgroundTemplate":
                return Integer(v, name, 0, TemplateCount - 1, problems, n => patch.BackgroundTemplate = n);
            case "backgroundMediaInterval":
                return Integer(v, name, 1, 86_400, problems, n => patch.BackgroundMediaInterval = n);
            case "backgroundOpacity":
                return Number(v, name, 0, 1, problems, d => patch.BackgroundOpacity = d);
            case "widgetOpacity":
                return Number(v, name, 0, 1, problems, d => patch.WidgetOpacity = d);
            case "backgroundFrostLevel":
                return Number(v, name, 0, 100, problems, d => patch.BackgroundFrostLevel = d);
            case "widgetPadding":
                return Number(v, name, 0, 100, problems, d => patch.WidgetPadding = d);
            case "themeSyncWithDesktop":
                return Bool(v, name, problems, b => patch.ThemeSyncWithDesktop = b);
            case "accentSyncWithDesktop":
                return Bool(v, name, problems, b => patch.AccentSyncWithDesktop = b);
            case "backgroundMediaSlideshow":
                return Bool(v, name, problems, b => patch.BackgroundMediaSlideshow = b);
            case "backgroundMediaShuffle":
                return Bool(v, name, problems, b => patch.BackgroundMediaShuffle = b);
            case "immersiveBackdrop":
                return Bool(v, name, problems, b => patch.ImmersiveBackdrop = b);
            case "widgetLabels":
                return Bool(v, name, problems, b => patch.WidgetLabels = b);
            case "gaugeGradient":
                return GaugeGradient(v, problems, stops => patch.GaugeGradient = stops);
            default:
                problems.Add($"'{name}' is not a look field this tool changes.");
                return false;
        }
    }

    private void SetMedia(string deviceId, string id, PanelDevicePatch patch)
    {
        var item = id.Length == 0 ? null : _media.GetItem(deviceId, id);
        patch.BackgroundMediaId = item?.Id ?? "";
        patch.BackgroundMediaType = item?.Type ?? "";
        patch.BackgroundMediaAlpha = item?.Alpha ?? false;
    }

    // Mirrors the editor: a new effect opens on this panel's remembered preset for it, and a
    // template choice is remembered for the effect it is made on.
    private static void ResolveTemplate(PanelDeviceRecord record, PanelDevicePatch patch)
    {
        var effect = patch.BackgroundEffect ?? record.BackgroundEffect ?? DefaultEffect;
        if (patch.BackgroundTemplate is { } template)
        {
            var templates = record.BackgroundTemplates is null
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : new Dictionary<string, int>(record.BackgroundTemplates);
            templates[effect] = template;
            patch.BackgroundTemplates = templates;
        }
        else if (patch.BackgroundEffect is not null)
        {
            patch.BackgroundTemplate = record.BackgroundTemplates?.GetValueOrDefault(effect) ?? 0;
        }
    }

    internal static bool IsHexColor(string s) =>
        s.Length == 7 && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    private static bool IsKnownEffect(string key) => ShaderLibrary.IsKnownKey(key) || StaticEffectCatalog.IsFill(key);

    private static bool IsValidFont(string font) =>
        font.Length == 0
        || Fonts.Contains(font)
        || (font.StartsWith(PanelFontValue.SystemPrefix, StringComparison.Ordinal) && PanelFontValue.TryNormalize(font, out _));

    private static bool Text(JsonElement v, string name, List<string> problems, Func<string, bool> valid, string expected, Action<string> set)
    {
        if (v.ValueKind != JsonValueKind.String || !valid(v.GetString()!))
        {
            problems.Add($"'{name}' must be {expected}.");
            return false;
        }
        set(v.GetString()!);
        return true;
    }

    private static bool OneOf(JsonElement v, string name, string[] allowed, List<string> problems, Action<string> set) =>
        Text(v, name, problems, allowed.Contains, "one of " + string.Join(", ", allowed), set);

    private static bool Number(JsonElement v, string name, double min, double max, List<string> problems, Action<double> set)
    {
        if (v.ValueKind != JsonValueKind.Number || v.GetDouble() is var d && (!double.IsFinite(d) || d < min || d > max))
        {
            problems.Add($"'{name}' must be a number from {min} to {max}.");
            return false;
        }
        set(v.GetDouble());
        return true;
    }

    private static bool Integer(JsonElement v, string name, int min, int max, List<string> problems, Action<int> set)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < min || n > max)
        {
            problems.Add($"'{name}' must be a whole number from {min} to {max}.");
            return false;
        }
        set(n);
        return true;
    }

    private static bool Bool(JsonElement v, string name, List<string> problems, Action<bool> set)
    {
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            problems.Add($"'{name}' must be true or false.");
            return false;
        }
        set(v.GetBoolean());
        return true;
    }

    private static bool GaugeGradient(JsonElement v, List<string> problems, Action<List<PanelGaugeGradientStop>> set)
    {
        const string expected = "'gaugeGradient' must be an array of at least 2 stops like {\"at\":0.5,\"color\":\"#ffaa00\"}, at from 0 to 1, color #rrggbb or accent.";
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 2)
        {
            problems.Add(expected);
            return false;
        }
        var stops = new List<PanelGaugeGradientStop>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.Number
                || at.GetDouble() is var position && (position < 0 || position > 1)
                || !item.TryGetProperty("color", out var color) || color.ValueKind != JsonValueKind.String
                || color.GetString() is not { } c || !(c == "accent" || IsHexColor(c)))
            {
                problems.Add(expected);
                return false;
            }
            stops.Add(new PanelGaugeGradientStop { At = at.GetDouble(), Color = c == "accent" ? c : c.ToLowerInvariant() });
        }
        set(stops.OrderBy(s => s.At).ToList());
        return true;
    }
}
