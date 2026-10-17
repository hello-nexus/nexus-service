using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Serialization;
using static Nexus.Service.Widgets.AppActions.AppActionHelpers;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>Host actions for lighting: read the current sync mode, set it
/// (none/animate/music/screen/gif). Gated through the manifest's
/// capabilities.dispatch allowlist.</summary>
public static class LightingActions
{
    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("lighting.state", (services, _, _) =>
        {
            var l = services.GetRequiredService<ILightingProvider>();
            var dto = new CurrentSyncResponse { Sync = l.GetSync() };
            var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.CurrentSyncResponse);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });

        registry.Register("lighting.setMode", (services, args, _) =>
        {
            var mode = Str(args, "mode") ?? Str(args, "sync");
            if (string.IsNullOrEmpty(mode))
                return Task.FromResult<JsonElement?>(Ack(false, "missing mode"));
            var l = services.GetRequiredService<ILightingProvider>();
            if (mode is "none" or "off")
            {
                l.StopAll();
            }
            else
            {
                mode = Nexus.Service.Lighting.RemovedSyncModes.Normalize(mode);
                l.SetSync(mode);
            }
            return Task.FromResult<JsonElement?>(Ack(true, applied: mode));
        });

        // Set the lights to a solid colour from a hex string. Lighting is
        // shader-driven (no per-zone RGB write), so this drives the same
        // solid-fill "simple" effect the built-in simple<colour> presets use,
        // tinted to the chosen colour's HUE (simplered=0.00, simplegreen=0.33,
        // simpleblue=0.62 -> Hue is degrees/360). NOTE: only hue is reproduced;
        // the simple presets are full-saturation/brightness, so a pastel or dark
        // pick lands at its hue, not its exact RGB. Exact RGB needs a dedicated
        // colour shader (follow-up). This is the ui-color counterpart.
        registry.Register("lighting.setColor", (services, args, _) =>
        {
            var hex = Str(args, "hex") ?? Str(args, "color");
            if (string.IsNullOrEmpty(hex))
                return Task.FromResult<JsonElement?>(Ack(false, "missing hex"));
            var clean = hex!.TrimStart('#');
            if (clean.Length != 6 || !int.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return Task.FromResult<JsonElement?>(Ack(false, "hex must be #RRGGBB"));

            float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, b = (rgb & 0xFF) / 255f;
            var l = services.GetRequiredService<ILightingProvider>();
            // The flat "simple" fill is one HSV swatch, so only hue + saturation
            // reach the shader; colorize / speed / contrast / intensity are unused
            // for it and left at their defaults. Full saturation renders the
            // picked hue at its most vivid.
            l.StartAnimate(new AnimateHeadlessStart
            {
                Effect = "simple",
                Hue = HueOf(r, g, b), // 0..1, matches the simple<colour> preset signatures
                Saturation = 1f,
                Persist = true,
            });
            return Task.FromResult<JsonElement?>(Ack(true, applied: $"hue {HueOf(r, g, b):0.00} (#{clean})"));
        });
    }

    /// <summary>RGB (0..1 components) -> hue normalised to 0..1, matching the
    /// engine's Hue convention (degrees/360).</summary>
    private static float HueOf(float r, float g, float b)
    {
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float d = max - min;
        if (d < 1e-6f) return 0f;
        float h;
        if (max == r) h = (g - b) / d % 6f;
        else if (max == g) h = (b - r) / d + 2f;
        else h = (r - g) / d + 4f;
        h *= 60f;
        if (h < 0f) h += 360f;
        return h / 360f;
    }

    public static IReadOnlyList<string> AllActions => new[] { "lighting.state", "lighting.setMode", "lighting.setColor" };
}
