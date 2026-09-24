using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Peripherals.Keeb;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

/// <summary>
/// AppBootstrap.InitializeProfiles is skipped for the WebApplicationFactory
/// test host (Program.cs gates it behind !testHost), so its OnProfileSwitched
/// reapply logic has no integration-test coverage. Drives the extracted
/// AppBootstrap.ReapplyAfterProfileSwitch directly against a hand-built
/// IServiceProvider instead.
/// </summary>
public class AppBootstrapProfileSwitchTests
{
    private sealed class RecordingFanControlProvider : IFanControlProvider
    {
        public int ReleaseAllCallCount { get; private set; }
        public IReadOnlyList<FanChannel> GetFanChannels() => Array.Empty<FanChannel>();
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() => ReleaseAllCallCount++;
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private sealed class NoDevices : IHidEnumerator
    {
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => Array.Empty<HidDeviceInfo>();
        public IReadOnlyList<HidDeviceInfo> FindAll() => Array.Empty<HidDeviceInfo>();
        public IHidDevice? Open(string path, bool forInput = false) => null;
    }

    private sealed class StubKeebProvider : IKeebProvider
    {
        public int ApplyPersistedAssignmentsCallCount { get; private set; }
        public KeyboardState GetState(int layer) => new();
        public GetKeebSettingsResponse GetSettings() => new();
        public string[] GetRotaryFunctions() => Array.Empty<string>();
        public void SetRotary(SetRotaryWheelsBody body) { }
        public void SetFirmwareLighting(SetFirmwareLightingBody body) { }
        public void SetPassiveLighting(SetPassiveLightingBody body) { }
        public void SetGameMode(SetGameModeBody body) { }
        public KeebMacro GetMacro(int index) => new();
        public SetMacroResponse SetMacro(int index, SetMacroBody body) => new();
        public SetLayerKeyResponse SetLayerKey(int layer, SetLayerKeyBody body) => new();
        public SetLayerKeyResponse ResetLayer(int layer) => new();
        public void ApplyPersistedAssignments() => ApplyPersistedAssignmentsCallCount++;
    }

    private static (IServiceProvider Sp, RecordingFanControlProvider Fans, CurveEngine CurveEngine, LightingEngine LightingEngine, StubLightingProvider Lighting, InMemoryConfigStore Store) Build()
    {
        var store = new InMemoryConfigStore();
        var fans = new RecordingFanControlProvider();
        var curveEngine = new CurveEngine(fans, store, new MultiplexHub());
        var lightingEngine = new LightingEngine();
        var lighting = new StubLightingProvider();
        var keebApplier = new KeebSettingsApplier(new KeebHub(new NoDevices()), store, new MultiplexHub());

        var services = new ServiceCollection();
        services.AddSingleton<IFanControlProvider>(fans);
        services.AddSingleton<IConfigStore>(store);
        services.AddSingleton<FeatureGates>();
        services.AddSingleton(keebApplier);
        services.AddSingleton<IKeebProvider>(new StubKeebProvider());
        var sp = services.BuildServiceProvider();

        return (sp, fans, curveEngine, lightingEngine, lighting, store);
    }

    // Minimal fake sufficient for LiveEngineSync.ApplyLighting/ApplyCooling to
    // no-op cleanly; GetSync/Sync mode default to "none" so no Start* fires.
    private sealed class StubLightingProvider : ILightingProvider
    {
        public string GetSync() => "none";
        public void SetSync(string sync) { }
        public void StopAll() { }
        public void Suspend() { }
        public bool IsPaused => false;
        public void SetPaused(bool paused) { }
        public void SetBrightness(Nexus.Service.Models.Lighting.BrightnessScale scale) { }
        public void SetSpeed(Nexus.Service.Models.Lighting.SpeedScale scale) { }
        public Nexus.Service.Models.Lighting.AnimateOptions GetAnimateOptions() => new();
        public Nexus.Service.Models.Lighting.AudioSyncOptions GetAudioSyncOptions() => new();
        public Nexus.Service.Models.Lighting.ScreenSyncOptions GetScreenSyncOptions() => new();
        public void StartAnimate(Nexus.Service.Models.Lighting.AnimateHeadlessStart body) { }
        public void StartStatic(Nexus.Service.Models.Lighting.StaticHeadlessStart body) { }
        public void StartMusic(Nexus.Service.Models.Lighting.MusicHeadlessStart body) { }
        public void StartScreen(Nexus.Service.Models.Lighting.ScreenHeadlessStart body) { }
        public void ReselectScreen() { }
        public bool StartMedia(string mediaId) => false;
        public void StartMediaIdle() { }
        public void StartGameSync() { }
        public Nexus.Service.Lighting.Engine.Effects.GameSyncEffect? ActiveGameSyncEffect() => null;
        public void UpdateScreenEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist, bool reactive = false, float reactivity = 0.5f, float intensity = 0.5f) { }
        public void UpdateMediaEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist) { }
        public (byte[] Bytes, string Tag)? CaptureAnimateThumbnail(string key, int slot, bool skipCache = false, bool frozen = false) => null;
        public void SaveAnimateTemplates(Dictionary<string, AnimateEffectTemplates> templates) { }
        public void SetMusicReactive(bool enabled) { }
        public void ReconcileAudioCapture() { }
        public void SetAudioCaptureDemand(bool demanded) { }
    }

    [Fact]
    public void CoolingEnabled_ReleasesFansOnProfileSwitch()
    {
        var (sp, fans, curveEngine, lightingEngine, lighting, store) = Build();

        AppBootstrap.ReapplyAfterProfileSwitch(sp, curveEngine, lightingEngine, lighting, store);

        Assert.Equal(1, fans.ReleaseAllCallCount);
    }

    // A profile reset while inactive carries a blank CoolingSettings; the
    // switch into it seeds the same four presets a clean install boots with.
    [Fact]
    public void UnseededProfile_GetsTheDefaultPresetCurvesOnSwitch()
    {
        var (sp, _, curveEngine, lightingEngine, lighting, store) = Build();

        AppBootstrap.ReapplyAfterProfileSwitch(sp, curveEngine, lightingEngine, lighting, store);

        Assert.Equal(
            new[] { "preset-silent", "preset-balanced", "preset-turbo", "preset-max" },
            store.Load().Cooling.Curves.Select(c => c.Id));
        Assert.True(store.Load().Cooling.CurvesSeeded);
    }

    [Fact]
    public void SeededProfile_KeepsItsCurvesOnSwitch()
    {
        var (sp, _, curveEngine, lightingEngine, lighting, store) = Build();
        // Seeded once, then every curve deleted: the switch must not resurrect them.
        store.Update(s => s.Cooling.CurvesSeeded = true);

        AppBootstrap.ReapplyAfterProfileSwitch(sp, curveEngine, lightingEngine, lighting, store);

        Assert.Empty(store.Load().Cooling.Curves);
    }

    [Fact]
    public void CoolingDisabled_SkipsReleaseAll_ButStillReappliesKeeb()
    {
        var (sp, fans, curveEngine, lightingEngine, lighting, store) = Build();
        store.Update(s => s.Features.Cooling = false);
        var keebProvider = (StubKeebProvider)sp.GetRequiredService<IKeebProvider>();

        AppBootstrap.ReapplyAfterProfileSwitch(sp, curveEngine, lightingEngine, lighting, store);

        Assert.Equal(0, fans.ReleaseAllCallCount);
        Assert.Equal(1, keebProvider.ApplyPersistedAssignmentsCallCount);
    }
}
