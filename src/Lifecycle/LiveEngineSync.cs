using System;
using System.Linq;
using System.Collections.Generic;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Common;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Engages the live cooling + lighting engines from the current persisted
/// settings. Used by flows that change settings out from under the engines
/// without going through a user-driven endpoint that would otherwise apply
/// the new state: profile switch (the swap stops engines but never restarts
/// them with the new profile's values) and profile / category reset (settings
/// flip to defaults but the engines stay idle).
///
/// Mirrors the subset of <see cref="AutoRestoreOnStart"/> that re-engages the
/// engines from store state. Kept here so callers triggered by user actions
/// can run synchronously instead of deferring to the boot-time background
/// task.
/// </summary>
public static class LiveEngineSync
{
    /// <summary>Apply cooling preset + lighting sync from the current store state.</summary>
    public static void Apply(IConfigStore store, IFanControlProvider fans, ILightingProvider lighting, FeatureGates gates)
    {
        ApplyCooling(store, fans, gates);
        ApplyLighting(store, lighting);
    }

    /// <summary>Hand every fan back to hardware control after a cooling reset.
    /// The default preset ("off") is a hardware state no <see cref="Apply"/> arm
    /// applies, and the reset already defaulted the settings, so no write
    /// belongs here.</summary>
    public static void ReleaseCoolingAfterReset(IFanControlProvider fans, FeatureGates gates)
    {
        // Same rule as ApplyCooling: no fan write at all while Cooling is off.
        if (!gates.Cooling)
        {
            return;
        }
        try
        { fans.ReleaseAll(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[live-sync] cooling release failed: {ex.Message}");
        }
    }

    private static void ApplyCooling(IConfigStore store, IFanControlProvider fans, FeatureGates gates)
    {
        // Profile switch / reset must not drive fans while Cooling is off;
        // unlike lighting, FanProfiles.Apply below writes hardware directly
        // with no per-tick gate downstream to catch it.
        if (!gates.Cooling)
        {
            return;
        }
        try
        {
            var preset = (store.Load().Cooling.ActivePreset ?? "").ToLowerInvariant();
            if (preset is "silent" or "balanced" or "turbo" or "max")
            {
                FanProfiles.Apply(preset, fans, store);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[live-sync] cooling failed: {ex.Message}");
        }
    }

    /// <summary>Engage the lighting engine from the persisted sync mode. Layout
    /// preset activation reuses this so a preset switch and a profile switch
    /// engage identically.</summary>
    public static void ApplyLighting(IConfigStore store, ILightingProvider lighting)
    {
        try
        {
            ApplyPostProcess(store, lighting);
            var s = store.Load().Lighting;
            var sync = (s.Sync ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(sync) || sync == "none") return;

            switch (sync)
            {
                case "music":
                    lighting.StartMusic(new MusicHeadlessStart());
                    break;
                case "screen":
                    lighting.StartScreen(new ScreenHeadlessStart());
                    break;
                case "gif":
                    // No persisted path list to restore from.
                    break;
                case "media":
                    var mediaId = s.LastMediaId;
                    if (!string.IsNullOrEmpty(mediaId)) lighting.StartMedia(mediaId);
                    break;
                case "static":
                {
                    // Without this the profile falls to the default arm and starts
                    // an effect literally named "static", which resolves to rainbow.
                    var key = StaticEffectCatalog.Coerce(s.Static.Effect);
                    if (!s.Static.States.TryGetValue(key, out var look) || look is null)
                    {
                        look = Nexus.Service.Lighting.AnimateTemplateDefaults.ResolveSelected(s.Animate.Templates, key)
                            ?? new AnimateEffectState();
                    }
                    lighting.StartStatic(new StaticHeadlessStart
                    {
                        Effect = key,
                        Intensity = look.Intensity,
                        Hue = look.Hue,
                        Colorize = look.Colorize,
                        Saturation = look.Saturation,
                        Contrast = look.Contrast,
                        Params = look.Params.Select(kv => new ShaderParam { Name = kv.Key, Value = kv.Value }).ToList(),
                    });
                    break;
                }
                default:
                    // Animate: Sync is the shader effect name. States holds
                    // only deltas from the selected preset look; an absent
                    // entry means "the resolved slot look", not base defaults.
                    var effect = sync;
                    if (!s.Animate.States.TryGetValue(effect, out var saved) || saved is null)
                    {
                        saved = Nexus.Service.Lighting.AnimateTemplateDefaults.ResolveSelected(s.Animate.Templates, effect)
                            ?? new AnimateEffectState();
                    }
                    lighting.StartAnimate(new AnimateHeadlessStart
                    {
                        Effect = effect,
                        Speed = saved.Speed,
                        Intensity = saved.Intensity,
                        Hue = saved.Hue,
                        Colorize = saved.Colorize,
                        Saturation = saved.Saturation,
                        Contrast = saved.Contrast,
                        Params = AnimateParamsToList(saved.Params),
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[live-sync] lighting failed: {ex.Message}");
        }
    }

    /// <summary>Push the persisted Mirror/Media filters into the provider's live
    /// holders. A running effect samples those holders, and they are otherwise
    /// only loaded at construction and mutated by the effect endpoints - so any
    /// flow that swaps settings underneath the engine (profile switch, profile
    /// reset, boot, preset activation) renders the previous filter without this.
    /// persist:false - the store is the source here, not the destination.</summary>
    public static void ApplyPostProcess(IConfigStore store, ILightingProvider lighting)
    {
        var s = store.Load().Lighting;
        var screen = s.ScreenEffect;
        lighting.UpdateScreenEffect(screen.Hue, screen.Colorize, screen.Saturation, screen.Contrast,
            screen.FlipX, screen.FlipY, persist: false, screen.Reactive, screen.Reactivity, screen.Intensity);
        var media = s.MediaEffect;
        lighting.UpdateMediaEffect(media.Hue, media.Colorize, media.Saturation, media.Contrast,
            media.FlipX, media.FlipY, persist: false);
    }

    private static List<ShaderParam> AnimateParamsToList(Dictionary<string, float>? d)
    {
        var list = new List<ShaderParam>();
        if (d is null) return list;
        foreach (var kv in d) list.Add(new ShaderParam { Name = kv.Key, Value = kv.Value });
        return list;
    }
}
