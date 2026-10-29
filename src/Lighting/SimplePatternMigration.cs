using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Deck;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Simple mode's rainbow, bars and breathing tiles became presets of the advanced
/// rainbow, sharplines and breathing effects. A key naming a retired tile, from a
/// settings file, a profile, a layout preset, a deck button or a caller, lands on
/// the effect that replaced it. The rename is idempotent and runs on every load;
/// the slot shift for the advanced rainbow runs once, behind
/// <see cref="NexusSettings.CurrentSchemaVersion"/> 28 (profile files by their stamp).
/// </summary>
public static class SimplePatternMigration
{
    public const int SchemaVersion = 28;

    private const string Rainbow = "rainbow";
    private const float OldRainbowAspect = 1f;
    private const float OldRainbowTempo = 0.15f;
    private const int LastSlot = AnimateTemplateDefaults.SlotCount - 1;

    private static readonly (string Old, string New)[] Aliases =
    [
        ("sweeprainbow", "rainbow"),
        ("sweepbars", "sharplines"),
        ("sweepbreathing", "breathing"),
    ];

    public static string Normalize(string? key)
    {
        if (key is null)
        {
            return "";
        }
        foreach (var (old, replacement) in Aliases)
        {
            if (string.Equals(key, old, StringComparison.OrdinalIgnoreCase))
            {
                return replacement;
            }
        }
        return key;
    }

    public static bool IsRetired(string? key) => !string.IsNullOrEmpty(key) && !string.Equals(Normalize(key), key, StringComparison.Ordinal);

    /// <summary>Profile data read from a file or payload: shifts only a pre-28 document, then stamps it so a second call cannot shift again.</summary>
    public static bool ApplyProfile(NexusSettings? data)
    {
        if (data is null)
        {
            return false;
        }
        var shift = data.SchemaVersion < SchemaVersion;
        var changed = Apply(data, shift);
        if (shift)
        {
            data.SchemaVersion = SchemaVersion;
            changed = true;
        }
        return changed;
    }

    /// <summary>True when anything changed. <paramref name="shiftSlots"/> moves the advanced rainbow's stored presets one slot up; pass it once per document.</summary>
    public static bool Apply(NexusSettings? doc, bool shiftSlots)
    {
        if (doc is null)
        {
            return false;
        }
        var changed = Apply(doc.Lighting, shiftSlots);
        if (doc.Ui?.RecentAnimations is { } recents)
        {
            changed |= NormalizeRecents(recents);
        }
        if (doc.Panel is { } panel)
        {
            changed |= ApplyPanel(panel.BackgroundEffect, e => panel.BackgroundEffect = e ?? "", panel.BackgroundTemplate, t => panel.BackgroundTemplate = t ?? 0, null, shiftSlots);
        }
        if (doc.PanelDevices is { } records)
        {
            foreach (var record in records.Values)
            {
                if (record is null)
                {
                    continue;
                }
                changed |= ApplyPanel(record.BackgroundEffect, e => record.BackgroundEffect = e, record.BackgroundTemplate, t => record.BackgroundTemplate = t, record.BackgroundTemplates, shiftSlots);
            }
        }
        if (doc.StreamDeck is { } deck)
        {
            changed |= ApplyDeck(deck);
        }
        return changed;
    }

    public static bool Apply(LightingSettings? lighting, bool shiftSlots)
    {
        if (lighting is null)
        {
            return false;
        }
        var changed = false;
        lighting.Animate ??= new AnimateSettings();
        var animate = lighting.Animate;
        animate.States ??= new();
        animate.Templates ??= new();
        var templatesTouched = false;

        if (shiftSlots)
        {
            templatesTouched |= ShiftRainbow(lighting);
        }

        foreach (var (old, replacement) in Aliases)
        {
            var active = string.Equals(lighting.Sync, old, StringComparison.OrdinalIgnoreCase)
                || string.Equals(animate.Effect, old, StringComparison.OrdinalIgnoreCase);
            animate.States.TryGetValue(old, out var legacyState);
            var hadState = animate.States.Remove(old);
            var hadTemplates = animate.Templates.Remove(old);
            if (active)
            {
                // The old tile ran the stock look; only its direction is user state.
                SelectStock(animate, replacement, reverse: legacyState is { Speed: < 0 });
                templatesTouched = true;
            }
            changed |= hadState || hadTemplates || active;
        }

        if (IsRetired(lighting.Sync))
        {
            lighting.Sync = Normalize(lighting.Sync);
            changed = true;
        }
        if (IsRetired(animate.Effect))
        {
            animate.Effect = Normalize(animate.Effect);
            changed = true;
        }

        changed |= ApplyPins(lighting.DevicePins);
        if (lighting.LayoutPresets is { } presets)
        {
            foreach (var preset in presets)
            {
                if (preset is null)
                {
                    continue;
                }
                changed |= ApplyPins(preset.DevicePins);
                if (preset.Look is { } look)
                {
                    changed |= ApplyLook(lighting, look, shiftSlots);
                }
            }
        }

        if (templatesTouched)
        {
            animate.Templates = AnimateTemplateDefaults.Prune(animate.Templates);
            AnimateTemplateDefaults.PruneStates(animate);
            changed = true;
        }
        return changed;
    }

    /// <summary>Old advanced rainbow slot i is new slot i + 1: the new first slot is the former simple tile, and the old fourth slot has no place left.</summary>
    private static bool ShiftRainbow(LightingSettings lighting)
    {
        var animate = lighting.Animate;
        var touched = false;
        if (animate.Templates.TryGetValue(Rainbow, out var bundle) && bundle is not null)
        {
            var shifted = new List<AnimateEffectState?> { null };
            foreach (var slot in (bundle.Slots ?? new()).Take(LastSlot))
            {
                shifted.Add(slot is null ? null : OldRainbow(slot));
            }
            bundle.Slots = shifted;
            bundle.Selected = Math.Min(bundle.Selected + 1, LastSlot);
            touched = true;
        }
        if (animate.States.TryGetValue(Rainbow, out var state) && state is not null)
        {
            animate.States[Rainbow] = OldRainbow(state);
            touched = true;
        }
        return touched;
    }

    /// <summary>The same look on the merged shader, whose defaults are the former simple tile.</summary>
    private static AnimateEffectState OldRainbow(AnimateEffectState state)
    {
        var copy = Clone(state);
        copy.Params.TryAdd("u_aspect", OldRainbowAspect);
        copy.Params.TryAdd("u_tempo", OldRainbowTempo);
        return copy;
    }

    private static void SelectStock(AnimateSettings animate, string effect, bool reverse)
    {
        if (!animate.Templates.TryGetValue(effect, out var bundle) || bundle is null)
        {
            bundle = new AnimateEffectTemplates();
            animate.Templates[effect] = bundle;
        }
        bundle.Selected = 0;
        if (!reverse)
        {
            animate.States.Remove(effect);
            return;
        }
        animate.States[effect] = ReversedStock(animate.Templates, effect);
    }

    private static AnimateEffectState ReversedStock(Dictionary<string, AnimateEffectTemplates> templates, string effect)
    {
        var state = Clone(AnimateTemplateDefaults.ResolveSlot(templates, effect, 0) ?? new AnimateEffectState());
        state.Speed = -Math.Abs(state.Speed);
        return state;
    }

    private static bool ApplyLook(LightingSettings lighting, LightingPresetLook look, bool shiftSlots)
    {
        var changed = false;
        var wasRainbow = string.Equals(look.AnimateEffect, Rainbow, StringComparison.Ordinal);
        if (shiftSlots && wasRainbow)
        {
            if (look.AnimateSlot >= 0)
            {
                look.AnimateSlot = Math.Min(look.AnimateSlot + 1, LastSlot);
            }
            if (look.AnimateState is { } dense)
            {
                look.AnimateState = OldRainbow(dense);
            }
            changed = true;
        }
        if (IsRetired(look.AnimateEffect))
        {
            var replacement = Normalize(look.AnimateEffect);
            var reverse = look.AnimateState is { Speed: < 0 };
            look.AnimateEffect = replacement;
            look.AnimateSlot = 0;
            look.AnimateState = ReversedOrStock(lighting.Animate.Templates, replacement, reverse);
            changed = true;
        }
        if (IsRetired(look.Sync))
        {
            look.Sync = Normalize(look.Sync);
            changed = true;
        }
        return changed;
    }

    private static AnimateEffectState ReversedOrStock(Dictionary<string, AnimateEffectTemplates> templates, string effect, bool reverse)
    {
        if (reverse)
        {
            return ReversedStock(templates, effect);
        }
        return Clone(AnimateTemplateDefaults.ResolveSlot(templates, effect, 0) ?? new AnimateEffectState());
    }

    private static bool ApplyPins(Dictionary<string, DevicePin>? pins)
    {
        if (pins is null)
        {
            return false;
        }
        var changed = false;
        foreach (var pin in pins.Values)
        {
            if (pin is { Kind: DevicePinKinds.Animate } && IsRetired(pin.Effect))
            {
                pin.Effect = Normalize(pin.Effect);
                changed = true;
            }
        }
        return changed;
    }

    private static bool NormalizeRecents(List<string> recents)
    {
        if (!recents.Any(IsRetired))
        {
            return false;
        }
        var kept = recents.Select(Normalize).Distinct(StringComparer.Ordinal).ToList();
        recents.Clear();
        recents.AddRange(kept);
        return true;
    }

    private static bool ApplyPanel(
        string? effect, Action<string?> setEffect, int? template, Action<int?> setTemplate,
        Dictionary<string, int>? templates, bool shiftSlots)
    {
        var changed = false;
        if (shiftSlots)
        {
            if (templates is not null && templates.TryGetValue(Rainbow, out var picked))
            {
                templates[Rainbow] = Math.Min(picked + 1, LastSlot);
                changed = true;
            }
            if (string.Equals(effect, Rainbow, StringComparison.Ordinal) && template is { } index)
            {
                setTemplate(Math.Min(index + 1, LastSlot));
                changed = true;
            }
        }
        if (templates is not null)
        {
            foreach (var (old, replacement) in Aliases)
            {
                if (templates.Remove(old))
                {
                    templates[replacement] = 0;
                    changed = true;
                }
            }
        }
        if (IsRetired(effect))
        {
            setEffect(Normalize(effect));
            setTemplate(0);
            changed = true;
        }
        return changed;
    }

    private static bool ApplyDeck(StreamDeckSettings deck)
    {
        var changed = false;
        foreach (var preset in deck.Presets ?? new())
        {
            changed |= ApplyDeckConfig(preset?.Deck);
        }
        foreach (var physical in deck.Decks?.Values ?? Enumerable.Empty<PhysicalDeckSettings>())
        {
            changed |= ApplyDeckConfig(physical?.LegacyDeck);
            foreach (var preset in physical?.LegacyPresets ?? new())
            {
                changed |= ApplyDeckConfig(preset?.Deck);
            }
        }
        return changed;
    }

    /// <summary>Rewrites every rgbEffect button and dial action of a deck config; also used on configs that arrive over the deck routes.</summary>
    public static bool ApplyDeckConfig(DeckConfig? config)
    {
        if (config?.Pages is null)
        {
            return false;
        }
        var changed = false;
        foreach (var page in config.Pages)
        {
            changed |= ApplySlots(page?.Slots);
            changed |= ApplyDials(page?.Dials);
        }
        return changed;
    }

    private static bool ApplySlots(List<DeckSlot>? slots)
    {
        var changed = false;
        foreach (var slot in slots ?? new())
        {
            if (slot is null)
            {
                continue;
            }
            changed |= ApplyAction(slot.Action);
            if (slot.Folder is { } folder)
            {
                changed |= ApplySlots(folder.Slots);
                changed |= ApplyDials(folder.Dials);
            }
        }
        return changed;
    }

    private static bool ApplyDials(List<DeckDial>? dials)
    {
        var changed = false;
        foreach (var dial in DeckDials.Flatten(dials))
        {
            foreach (var action in DeckDials.CustomActions(dial))
            {
                changed |= ApplyAction(action);
            }
        }
        return changed;
    }

    private static bool ApplyAction(DeckAction? action)
    {
        if (action is null)
        {
            return false;
        }
        var changed = false;
        if (action.NexusAction is { Op: "rgbEffect" } nexus && IsRetired(nexus.Effect))
        {
            nexus.Effect = Normalize(nexus.Effect);
            changed = true;
        }
        foreach (var step in action.Steps ?? new())
        {
            changed |= ApplyAction(step?.Action);
        }
        changed |= ApplyAction(action.On);
        changed |= ApplyAction(action.Off);
        return changed;
    }

    private static AnimateEffectState Clone(AnimateEffectState s) => new()
    {
        Speed = s.Speed,
        Intensity = s.Intensity,
        Hue = s.Hue,
        Colorize = s.Colorize,
        Saturation = s.Saturation,
        Contrast = s.Contrast,
        Params = s.Params is not null ? new Dictionary<string, float>(s.Params) : new(),
        Sensors = SensorBinding.CopyMap(s.Sensors),
    };
}
