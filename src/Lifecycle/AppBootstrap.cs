using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Activity;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Peripherals.Keeb;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lifecycle;

// Post-Build wiring that runs once before the middleware pipeline goes up:
// GPU eager-init, profile manager init + reapply hook, BeatsProvider/audio
// → MultiplexHub event bridge, panel-presence topic refresh.
internal static class AppBootstrap
{
    // GPU shader-engine warmup, kept OFF the startup-critical path: deferred to
    // ApplicationStarted (host bound, SCM told RUNNING) and run on a background
    // thread, so a stalled GL driver never blocks boot (the Error 1053 /
    // installer-stall fix). A hang leaves the GPU unavailable (shader effects
    // don't render; static and firmware lighting still work); it never restarts
    // the service. The real GL work runs on GpuContext's own nexus-gl thread.
    public static void ScheduleGpuWarmup(WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var thread = new Thread(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                GpuContext.Log("[gpu] warmup: starting (deferred, post-start)");
                try
                {
                    var gpu = app.Services.GetRequiredService<GpuContext>();
#if WINDOWS
                    // Windows + "auto": pick a card that yields a working context
                    // (prefer integrated, fall back to discrete) without ever
                    // restarting - see GpuRenderSelect. A user-pinned card skips
                    // this and inits directly on their choice.
                    var choice = app.Services.GetService<Nexus.Service.Persistence.IConfigStore>()
                        ?.Load().Lighting.RenderGpu ?? "auto";
                    if (string.Equals(choice, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        Nexus.Service.Lighting.Engine.Gpu.GpuRenderSelect.SelectAndWarm(gpu, sw);
                        return;
                    }
#endif
                    lock (gpu.Lock)
                    {
                        gpu.EnsureInitializedLocked();
                    }
                    GpuContext.Log(gpu.Available
                        ? $"[gpu] warmup: GPU shader engine ready in {sw.ElapsedMilliseconds}ms"
                        : $"[gpu] warmup: GPU unavailable after {sw.ElapsedMilliseconds}ms (shader effects off; static/firmware lighting unaffected)");
                }
                catch (Exception ex)
                {
                    GpuContext.Log($"[gpu] warmup: threw after {sw.ElapsedMilliseconds}ms ({ex.GetType().Name}: {ex.Message})");
                }
            })
            { IsBackground = true, Name = "nexus-gpu-warmup" };
            thread.Start();
        });
    }

    // Creates Default profile if none exists, then wires the profile-switch
    // hook to release fan control, reset curve smoothing, and stop the
    // lighting engine so the incoming profile starts clean.
    public static void InitializeProfiles(WebApplication app)
    {
        BootTimer.Mark("InitializeProfiles: resolve ProfileManager");
        var profileManager = app.Services.GetRequiredService<ProfileManager>();
        BootTimer.Mark("InitializeProfiles: ProfileManager resolved");
        try
        {
            profileManager.Initialize();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[profiles] initialization failed: {ex.Message}");
        }
        BootTimer.Mark("InitializeProfiles: ProfileManager.Initialize done");

        var curveEngine = app.Services.GetRequiredService<CurveEngine>();
        BootTimer.Mark("InitializeProfiles: CurveEngine resolved");
        var lightingEngine = app.Services.GetRequiredService<LightingEngine>();
        BootTimer.Mark("InitializeProfiles: LightingEngine resolved");
        var lightingProvider = app.Services.GetRequiredService<ILightingProvider>();
        BootTimer.Mark("InitializeProfiles: ILightingProvider resolved");
        var configStore = app.Services.GetRequiredService<IConfigStore>();
        BootTimer.Mark("InitializeProfiles: IConfigStore resolved");

        // IFanControlProvider is resolved off the startup critical path by
        // LhmWarmupService (post-StartAsync BackgroundService) so its
        // transitive LhmComputer.ctor doesn't block Kestrel bind. The
        // profile-switch callback resolves it lazily; once warm it's a
        // dictionary lookup against the DI cache.
        var sp = app.Services;
        profileManager.OnProfileSwitched += () =>
        {
            try
            {
                var fans = sp.GetRequiredService<IFanControlProvider>();
                fans.ReleaseAll();
                curveEngine.ResetSmoothing();
                lightingEngine.Stop();
                // Re-engage engines with the incoming profile's settings so
                // a profile that has "silent" cooling + a plasma effect
                // resumes after the switch instead of leaving the engines
                // idle until the user clicks something.
                LiveEngineSync.Apply(configStore, fans, lightingProvider);

                // Keeb is profile-scoped via the Device sharing category;
                // push the incoming profile's game mode/firmware lighting/
                // rotary and re-send persisted key overrides + macros so the
                // physical keyboard follows the switch. Both no-op when no
                // keyboard is connected.
                var keebApplier = sp.GetRequiredService<KeebSettingsApplier>();
                var keebProvider = sp.GetRequiredService<IKeebProvider>();
                keebApplier.Apply();
                keebProvider.ApplyPersistedAssignments();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[profiles] reapply failed: {ex.Message}");
            }
        };
    }

    // BeatsProvider.OnBeat fires each analysis window while capture runs; when
    // the "audio" topic has subscribers it broadcasts the latest
    // level/bass/mid/high/beat/spectrum snapshot. Phone-presence is wired here
    // too because it shares the same hub.
    public static void WireBeatsAndPresence(WebApplication app)
    {
        BootTimer.Mark("WireBeatsAndPresence: resolve IBeatsProvider");
        var beatsProvider = app.Services.GetRequiredService<IBeatsProvider>();
        BootTimer.Mark("WireBeatsAndPresence: IBeatsProvider resolved");
        var muxHub = app.Services.GetRequiredService<MultiplexHub>();
        BootTimer.Mark("WireBeatsAndPresence: MultiplexHub resolved");
        beatsProvider.OnBeat += () =>
        {
            if (muxHub.TopicHasSubscribers("audio"))
            {
                var snap = new Nexus.Service.Models.Lighting.AudioStateSnapshot
                {
                    Level = AudioState.Level,
                    Bass = AudioState.Bass,
                    Mid = AudioState.Mid,
                    High = AudioState.High,
                    Beat = AudioState.Beat,
                    Spectrum = new List<float>(AudioState.Spectrum),
                    Spectrum64 = new List<float>(AudioState.Spectrum64),
                };
                var audioEnv = WsEnvelope.Build("audio", snap, AppJsonContext.Default.AudioStateSnapshot);
                _ = muxHub.BroadcastTopicAsync("audio", audioEnv);
            }
        };

        // Phone presence: when the first phone subscribes (or the last leaves)
        // the dashboard's connected-count needs to refresh. Reuse the existing
        // panel/device topic so usePanelDevices subscribes once and gets both
        // the device-list and the connected-count signal.
        muxHub.OnTopicFirstSubscriber += topic =>
        {
            if (topic == Nexus.Service.Panel.PanelPhonePairingService.PresenceTopic)
                PanelTopics.BroadcastPanelDevice(muxHub, "presence");
        };
        muxHub.OnTopicLastUnsubscriber += topic =>
        {
            if (topic == Nexus.Service.Panel.PanelPhonePairingService.PresenceTopic)
                PanelTopics.BroadcastPanelDevice(muxHub, "presence");
        };

        // A smart light flipping online/offline refreshes the lighting list so
        // its card drops or returns. Decoupled from DevicesChanged, which forces
        // a full rebuild.
        var smartLights = app.Services.GetRequiredService<Nexus.Service.Lighting.Smart.SmartLightProvider>();
        smartLights.OnlineChanged += () => PanelTopics.BroadcastLighting(muxHub);

        // Probe smart-light reachability only while a lighting view is open. A
        // smart light gives no event when it drops off the LAN (Govee frames are
        // fire-and-forget UDP), so an active per-brand probe is the only offline
        // signal - gated on subscribers like the beats provider so it costs
        // nothing when no one is watching.
        muxHub.OnTopicFirstSubscriber += topic =>
        { if (topic == PanelTopics.Lighting) smartLights.StartReachabilityPolling(); };
        muxHub.OnTopicLastUnsubscriber += topic =>
        { if (topic == PanelTopics.Lighting && !muxHub.TopicHasSubscribers(PanelTopics.Lighting)) smartLights.StopReachabilityPolling(); };
        if (muxHub.TopicHasSubscribers(PanelTopics.Lighting)) smartLights.StartReachabilityPolling();

        // Resolve ILightingProvider to force its construction (wires OnEffectChanged),
        // then reconcile capture: start only when MusicReactive is on and an audio-reactive
        // effect is active.
        var lighting = app.Services.GetRequiredService<ILightingProvider>();
        BootTimer.Mark("WireBeatsAndPresence: ILightingProvider resolved");
        lighting.ReconcileAudioCapture();
        BootTimer.Mark("WireBeatsAndPresence: ReconcileAudioCapture called");
    }
}
