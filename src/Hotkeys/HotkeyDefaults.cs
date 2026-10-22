using System.Collections.Generic;
using Nexus.Service.Deck;

namespace Nexus.Service.Hotkeys;

/// <summary>The built-in bindings seeded once and restored by reset-defaults.</summary>
public static class HotkeyDefaults
{
    public const string ToggleWindow = "builtin:toggle-window";
    public const string LightingToggle = "builtin:lighting-toggle";
    public const string BrightnessUp = "builtin:lighting-brightness-up";
    public const string BrightnessDown = "builtin:lighting-brightness-down";

    /// <summary>Fraction of full scale one brightness hotkey press moves the master level.</summary>
    public const double BrightnessStep = 0.1;

    public static bool IsBuiltin(string id) => id.StartsWith("builtin:", System.StringComparison.Ordinal);

    /// <summary>Cmd+Opt on macOS, Ctrl+Alt elsewhere.</summary>
    public static List<HotkeyBinding> Build(bool macos)
    {
        var prefix = macos ? "Meta+Alt+" : "Ctrl+Alt+";
        return new List<HotkeyBinding>
        {
            Make(ToggleWindow, prefix + "n", "toggleWindow", null),
            Make(LightingToggle, prefix + "l", "lightingPowerToggle", null),
            Make(BrightnessUp, prefix + "=", "lightingBrightnessStep", BrightnessStep),
            Make(BrightnessDown, prefix + "-", "lightingBrightnessStep", -BrightnessStep),
        };
    }

    private static HotkeyBinding Make(string id, string combo, string op, double? value) => new()
    {
        Id = id,
        Combo = HotkeyCombo.Canonicalize(combo),
        Enabled = true,
        Target = new HotkeyTarget
        {
            Kind = HotkeyTarget.KindAction,
            Action = new DeckAction
            {
                Type = "nexus",
                NexusAction = new DeckNexusAction { Op = op, Value = value },
            },
        },
    };
}
