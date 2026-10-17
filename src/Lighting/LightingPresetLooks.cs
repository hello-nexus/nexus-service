using System.Collections.Generic;
using Nexus.Service.Defaults;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Moves the mode + effect selection between the live <see cref="LightingSettings"/>
/// and a layout preset. The selection is per-profile, so a preset carries its own
/// copy and switching presets swaps the look the same way switching profiles does.
/// </summary>
public static class LightingPresetLooks
{
    /// <summary>Snapshot the live selection. Absent state entries resolve through
    /// the template slots, so an untouched effect captures what it renders.</summary>
    public static LightingPresetLook Capture(LightingSettings lighting)
    {
        // Every dict here is dereferenced defensively: an explicit JSON null
        // survives the non-nullable initializers (see JsonConfigStore.Migrate),
        // and capture now runs on the keeb knob's thread, where an NRE would
        // escape into the frame writer rather than a request.
        var animate = lighting.Animate?.Effect ?? "";
        var still = lighting.Static?.Effect ?? "";
        var templates = lighting.Animate?.Templates;
        return new LightingPresetLook
        {
            Sync = lighting.Sync ?? "",
            AnimateEffect = animate,
            StaticEffect = still,
            StaticColor = lighting.Static?.Color,
            AnimateState = Clone(Resolve(templates, lighting.Animate?.States, animate)),
            StaticState = Clone(Resolve(templates, lighting.Static?.States, still)),
            AnimateSlot = SelectedSlot(templates, animate),
            StaticSlot = SelectedSlot(templates, still),
            GlobalBrightness = lighting.GlobalBrightness,
            MusicReactive = lighting.MusicReactive,
            LastMediaId = lighting.LastMediaId ?? "",
            ScreenEffect = ClonePostProcess(lighting.ScreenEffect),
            MediaEffect = ClonePostProcess(lighting.MediaEffect),
        };
    }

    /// <summary>Mirror the live selection into the active preset. No-op when no
    /// preset is selected or the id no longer resolves.</summary>
    public static void CaptureIntoActive(NexusSettings settings)
    {
        var id = settings.Lighting.ActiveLayoutPresetId;
        if (string.IsNullOrEmpty(id))
        {
            return;
        }
        var preset = settings.Lighting.LayoutPresets.Find(p => p.Id == id);
        if (preset is null)
        {
            return;
        }
        preset.Look = Capture(settings.Lighting);
    }

    /// <summary>Restore a captured selection. The caller engages the engine after.</summary>
    public static void Apply(LightingSettings lighting, LightingPresetLook look)
    {
        lighting.Sync = RemovedSyncModes.Normalize(look.Sync);
        // A non-finite level propagates through RgbBridge.OnFrame and zeroes
        // every LED, the same hazard POST /lighting/global-brightness guards.
        if (look.GlobalBrightness is { } brightness && float.IsFinite(brightness))
        {
            lighting.GlobalBrightness = System.Math.Clamp(brightness, 0f, 1f);
        }
        if (look.MusicReactive is { } reactive)
        {
            lighting.MusicReactive = reactive;
        }
        if (!string.IsNullOrEmpty(look.AnimateEffect))
        {
            lighting.Animate.Effect = look.AnimateEffect;
            SetSlot(lighting.Animate.Templates, look.AnimateEffect, look.AnimateSlot);
            WriteState(lighting, lighting.Animate.States, look.AnimateEffect, look.AnimateState);
        }
        lighting.Static.Color = StaticColorHex.TryParse(look.StaticColor, out _, out _, out _) ? look.StaticColor : null;
        if (!string.IsNullOrEmpty(look.StaticEffect))
        {
            lighting.Static.Effect = look.StaticEffect;
            SetSlot(lighting.Animate.Templates, look.StaticEffect, look.StaticSlot);
            WriteState(lighting, lighting.Static.States, look.StaticEffect, look.StaticState);
        }
        if (!string.IsNullOrEmpty(look.LastMediaId))
        {
            lighting.LastMediaId = look.LastMediaId;
        }
        if (ClonePostProcess(look.ScreenEffect) is { } screen)
        {
            lighting.ScreenEffect = screen;
        }
        if (ClonePostProcess(look.MediaEffect) is { } media)
        {
            lighting.MediaEffect = media;
        }
    }

    // A non-finite filter value reaches the shader uniforms and renders black.
    // Same treatment as the master-brightness route, which substitutes the
    // default; the effect routes themselves pass their body through unguarded.
    private static PostProcessSettings? ClonePostProcess(PostProcessSettings? pp)
    {
        if (pp is null)
        {
            return null;
        }
        return new PostProcessSettings
        {
            Hue = Finite(pp.Hue, InstallDefaults.Lighting.PostProcess.Hue),
            Colorize = Finite(pp.Colorize, InstallDefaults.Lighting.PostProcess.Colorize),
            Saturation = Finite(pp.Saturation, InstallDefaults.Lighting.PostProcess.Saturation),
            Contrast = Finite(pp.Contrast, InstallDefaults.Lighting.PostProcess.Contrast),
            FlipX = pp.FlipX,
            FlipY = pp.FlipY,
            Reactive = pp.Reactive,
            Reactivity = Finite(pp.Reactivity, InstallDefaults.Lighting.PostProcess.Reactivity),
            Intensity = Finite(pp.Intensity, InstallDefaults.Lighting.PostProcess.Intensity),
        };
    }

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    private static AnimateEffectState? Resolve(
        Dictionary<string, AnimateEffectTemplates>? templates,
        Dictionary<string, AnimateEffectState>? states,
        string effect)
    {
        if (effect.Length == 0)
        {
            return null;
        }
        if (states is not null && states.TryGetValue(effect, out var stored) && stored is not null)
        {
            return stored;
        }
        return AnimateTemplateDefaults.ResolveSelected(templates, effect);
    }

    // States holds only deltas from the effect's resolved slot look; a dense
    // entry equal to that look would survive as a phantom user edit.
    private static void WriteState(
        LightingSettings lighting, Dictionary<string, AnimateEffectState> states,
        string effect, AnimateEffectState? state)
    {
        if (state is null)
        {
            states.Remove(effect);
            return;
        }
        var baseline = AnimateTemplateDefaults.ResolveSelected(lighting.Animate.Templates, effect);
        if (baseline is not null && AnimateTemplateDefaults.StateEquals(state, baseline))
        {
            states.Remove(effect);
        }
        else
        {
            states[effect] = Clone(state)!;
        }
    }

    private static int SelectedSlot(Dictionary<string, AnimateEffectTemplates>? templates, string effect)
    {
        if (templates is null || effect.Length == 0
            || !templates.TryGetValue(effect, out var bundle) || bundle is null)
        {
            return -1;
        }
        return bundle.Selected;
    }

    private static void SetSlot(Dictionary<string, AnimateEffectTemplates> templates, string effect, int slot)
    {
        if (slot < 0)
        {
            return;
        }
        if (!templates.TryGetValue(effect, out var bundle) || bundle is null)
        {
            bundle = new AnimateEffectTemplates();
            templates[effect] = bundle;
        }
        bundle.Selected = slot;
    }

    private static AnimateEffectState? Clone(AnimateEffectState? state)
    {
        if (state is null)
        {
            return null;
        }
        return new AnimateEffectState
        {
            Speed = state.Speed,
            Intensity = state.Intensity,
            Hue = state.Hue,
            Colorize = state.Colorize,
            Saturation = state.Saturation,
            Contrast = state.Contrast,
            Params = state.Params is null
                ? new Dictionary<string, float>()
                : new Dictionary<string, float>(state.Params),
        };
    }
}
