using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.KeyReactive;

/// <summary>
/// One-way move of the keeb's own reactive settings (<see cref="KeebFirmwareLighting"/>'s
/// KeyReactive* fields) into <see cref="LightingSettings.KeyReactions"/> under the
/// keeb's device id, after which the legacy fields sit at their defaults and are
/// never read again. Runs per settings document, so a profile applied later migrates too.
/// </summary>
public static class KeebLegacyReactiveMigration
{
    public static bool Pending(NexusSettings s)
    {
        var k = s.Keeb.FirmwareLighting;
        return k.KeyReactive || k.KeyReactiveMask || k.KeyReactiveColor is { R: > 0 } or { G: > 0 } or { B: > 0 };
    }

    /// <summary>Moves the legacy settings to <paramref name="keebDeviceId"/> unless that device already has a key reaction.</summary>
    public static void Apply(NexusSettings s, string keebDeviceId)
    {
        if (!Pending(s)) return;
        var k = s.Keeb.FirmwareLighting;
        s.Lighting.KeyReactions.TryAdd(keebDeviceId, From(k));
        var defaults = new KeebFirmwareLighting();
        k.KeyReactive = defaults.KeyReactive;
        k.KeyReactiveMask = defaults.KeyReactiveMask;
        k.KeyReactiveMode = defaults.KeyReactiveMode;
        k.KeyReactiveColor = new RgbaColor();
    }

    internal static KeyReaction From(KeebFirmwareLighting k)
    {
        var c = k.KeyReactiveColor;
        var hasColor = c is { R: > 0 } or { G: > 0 } or { B: > 0 };
        return KeyReactionCatalog.Sanitize(new KeyReaction
        {
            Enabled = k.KeyReactive,
            Effect = k.KeyReactiveMode switch
            {
                "HorizontalLine" => KeyReactionCatalog.RowSweep,
                "VerticalLine" => KeyReactionCatalog.ColumnSweep,
                "Ripple" => KeyReactionCatalog.Ripple,
                _ => KeyReactionCatalog.Fade,
            },
            // The keeb's ripple was always its rainbow wheel.
            ColorMode = k.KeyReactiveMode == "Ripple" ? KeyReactionCatalog.ColorRainbow : KeyReactionCatalog.ColorCustom,
            Color = hasColor ? $"#{c.R:x2}{c.G:x2}{c.B:x2}" : KeyReactionCatalog.DefaultColor,
            Background = k.KeyReactiveMask ? KeyReactionCatalog.BackgroundReveal : KeyReactionCatalog.BackgroundEffect,
        });
    }
}
