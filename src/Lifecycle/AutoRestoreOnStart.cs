using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Common;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// One-shot startup task that replays the persisted lighting + cooling state
/// to the hardware. Without this, the engines come up with empty in-memory
/// state and the user has to visit each tab to "kick" the saved profile.
///
/// Runs after a short delay so the fan provider has enumerated channels
/// (CurveEngine itself waits 3s for the same reason). On PawnIoBootGate-armed
/// hosts it additionally waits for LHM's open to finish, since the preset
/// apply rebuilds fan attachments from the channel list that open populates.
/// The lighting restore can run before the OpenRGB daemon exists - the bridge
/// applies the persisted state once it connects.
/// </summary>
internal sealed class AutoRestoreOnStart : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan LhmOpenWait = TimeSpan.FromSeconds(30);

    private readonly IConfigStore _store;
    private readonly ILightingProvider _lighting;
    private readonly IFanControlProvider _fans;
    private readonly MultiplexHub _hub;
    private readonly FeatureGates _gates;

    // Snapshot at construction so we can tell, when ExecuteAsync wakes up
    // 4s later, whether the user changed anything in the meantime via the
    // dashboard. If they did, we leave their choice in place instead of
    // stomping it with the previously-persisted value.
    private readonly string _coolingPresetAtBoot;
    private readonly string _lightingSyncAtBoot;

    public AutoRestoreOnStart(
        IConfigStore store,
        ILightingProvider lighting,
        IFanControlProvider fans,
        MultiplexHub hub,
        FeatureGates? gates = null)
    {
        _store = store;
        _lighting = lighting;
        _fans = fans;
        _hub = hub;
        _gates = gates ?? FeatureGates.AllEnabled;
        var initial = _store.Load();
        _coolingPresetAtBoot = initial.Cooling.ActivePreset ?? "";
        _lightingSyncAtBoot = initial.Lighting.Sync ?? "";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        // FanProfiles.Apply rebuilds the active preset's outputs from the
        // current channel list, so applying before LHM's open has surfaced
        // the motherboard channels detaches every motherboard fan for the
        // session (hub-fan channels arrive earlier and cannot stand in). On
        // armed hosts the open can additionally be held behind a PawnIO
        // install/repair, pushing it well past the initial delay - wait for
        // the open itself, not a guess at its duration.
        if (PawnIoBootGate.IsArmed)
        {
            // The startup-delay window holds the open itself back, so the cap
            // has to clear it or the restore runs against an unenumerated LHM
            // and detaches every motherboard fan.
            await PawnIoBootGate.WaitForLhmOpenAsync(LhmOpenWait + StartupDelayGate.Configured);
            if (stoppingToken.IsCancellationRequested) return;
        }

        // Broadcast after each restoration: clients that connected during the
        // 4s init window fetched /lighting/status (or /cooling/status) before
        // the engine had the persisted state in memory, so they're showing a
        // stale "none" / "off" view. The topic ping forces them to refetch.
        try { if (RestoreCooling()) PanelTopics.BroadcastCooling(_hub); }
        catch (Exception ex) { ServiceLog.Error($"[auto-restore] cooling failed: {ex.Message}"); }

        try { if (RestoreLighting()) PanelTopics.BroadcastLighting(_hub); }
        catch (Exception ex) { ServiceLog.Error($"[auto-restore] lighting failed: {ex.Message}"); }
    }

    private bool RestoreCooling()
    {
        // Blank install: create Silent/Balanced/Turbo/Max so all four are present
        // the first time the cooling page loads. No-op once any curve exists.
        var seeded = FanProfiles.SeedDefaultPresetCurves(_fans, _store);

        var current = _store.Load().Cooling.ActivePreset ?? "";
        // If the user picked a different preset in the dashboard during the
        // 4s init window, respect their choice.
        if (!string.Equals(current, _coolingPresetAtBoot, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[auto-restore] cooling preset changed since boot ({_coolingPresetAtBoot} -> {current}), leaving as-is");
            return seeded;
        }
        // "custom" needs no re-apply (the curves already carry their fan
        // assignments). "off" was already idle; skipping avoids stomping on
        // a user who manually set a fan duty before we reached this point.
        if (current is "silent" or "balanced" or "turbo" or "max")
        {
            if (!_gates.Cooling)
            {
                return seeded;
            }
            FanProfiles.Apply(current, _fans, _store);
            Console.WriteLine($"[auto-restore] cooling preset re-applied: {current}");
            return true;
        }
        return seeded;
    }

    private bool RestoreLighting()
    {
        if (!_gates.Lighting)
        {
            return false;
        }
        var s = _store.Load().Lighting;
        var sync = s.Sync ?? "";
        // Same guard as cooling: if the user already kicked off a different
        // effect via the dashboard, don't overwrite it.
        if (!string.Equals(sync, _lightingSyncAtBoot, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[auto-restore] lighting sync changed since boot ({_lightingSyncAtBoot} -> {sync}), leaving as-is");
            return false;
        }
        if (string.IsNullOrEmpty(sync) || string.Equals(sync, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        switch (sync.ToLowerInvariant())
        {
            case "music":
                _lighting.StartMusic(new MusicHeadlessStart());
                Console.WriteLine("[auto-restore] lighting: music");
                return true;

            case "screen":
                _lighting.StartScreen(new ScreenHeadlessStart());
                Console.WriteLine("[auto-restore] lighting: screen");
                return true;

            case "gif":
                // No persisted path list to restore from; explicit case keeps
                // "gif" from falling through to the shader-name default below.
                return false;

            case "gamesync":
                _lighting.StartGameSync();
                Console.WriteLine("[auto-restore] lighting: gamesync");
                return true;

            case "static":
            {
                var staticEffect = StaticEffectCatalog.Coerce(s.Static.Effect);
                if (!s.Static.States.TryGetValue(staticEffect, out var look) || look is null)
                {
                    look = Nexus.Service.Lighting.AnimateTemplateDefaults.ResolveSelected(s.Animate.Templates, staticEffect)
                        ?? new AnimateEffectState();
                }
                _lighting.StartStatic(new StaticHeadlessStart
                {
                    Effect = staticEffect,
                    Intensity = look.Intensity,
                    Hue = look.Hue,
                    Colorize = look.Colorize,
                    Saturation = look.Saturation,
                    Contrast = look.Contrast,
                    Params = AnimateParamsToList(look.Params),
                });
                Console.WriteLine($"[auto-restore] lighting: static/{staticEffect}");
                return true;
            }

            case "media":
                var mediaId = s.LastMediaId;
                if (!string.IsNullOrEmpty(mediaId))
                {
                    if (_lighting.StartMedia(mediaId))
                    {
                        Console.WriteLine($"[auto-restore] lighting: media ({mediaId})");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"[auto-restore] lighting: media item {mediaId} missing, skipped");
                    }
                }
                return false;

            default:
                // Animate: Sync is the shader effect name. States holds only
                // deltas from the selected preset look; an absent entry means
                // "the resolved slot look", not base defaults.
                var effect = sync;
                if (!s.Animate.States.TryGetValue(effect, out var saved) || saved is null)
                {
                    saved = Nexus.Service.Lighting.AnimateTemplateDefaults.ResolveSelected(s.Animate.Templates, effect)
                        ?? new AnimateEffectState();
                }
                _lighting.StartAnimate(new AnimateHeadlessStart
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
                Console.WriteLine($"[auto-restore] lighting: animate/{effect}");
                return true;
        }
    }

    private static List<ShaderParam> AnimateParamsToList(Dictionary<string, float>? d)
    {
        var list = new List<ShaderParam>();
        if (d is null) return list;
        foreach (var kv in d)
        {
            list.Add(new ShaderParam { Name = kv.Key, Value = kv.Value });
        }
        return list;
    }
}
