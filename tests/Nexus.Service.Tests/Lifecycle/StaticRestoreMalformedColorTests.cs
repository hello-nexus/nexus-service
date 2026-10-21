using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Lifecycle;

/// <summary>
/// A persisted global Static color that does not parse (an imported profile or
/// a cloud pull can carry one) must not reach StartStatic, which throws on it.
/// </summary>
public class StaticRestoreMalformedColorTests
{
    private sealed class RecordingLighting : ILightingProvider
    {
        public readonly List<StaticHeadlessStart> Starts = new();
        public string GetSync() => "static";
        public void SetSync(string sync) { }
        public void StopAll() { }
        public void Suspend() { }
        public bool IsPaused => false;
        public void SetPaused(bool paused) { }
        public void SetBrightness(BrightnessScale scale) { }
        public void SetSpeed(SpeedScale scale) { }
        public AnimateOptions GetAnimateOptions() => new();
        public AudioSyncOptions GetAudioSyncOptions() => new();
        public ScreenSyncOptions GetScreenSyncOptions() => new();
        public void StartAnimate(AnimateHeadlessStart body) { }
        public void StartStatic(StaticHeadlessStart body) => Starts.Add(body);
        public void StartMusic(MusicHeadlessStart body) { }
        public void StartScreen(ScreenHeadlessStart body) { }
        public void ReselectScreen() { }
        public bool StartMedia(string mediaId) => false;
        public bool StartMedia(string mediaId, bool keepMode) => false;
        public void StartMediaIdle() { }
        public void UpdateScreenEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist, bool reactive = false, float reactivity = 0.5f, float intensity = 0.5f) { }
        public void UpdateMediaEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist) { }
        public (byte[] Bytes, string Tag)? CaptureAnimateThumbnail(string key, int slot, bool skipCache = false, bool frozen = false) => null;
        public void SaveAnimateTemplates(Dictionary<string, AnimateEffectTemplates> templates) { }
        public void SetMusicReactive(bool enabled) { }
        public void ReconcileAudioCapture() { }
        public void SetAudioCaptureDemand(bool demanded) { }
    }

    private static InMemoryConfigStore StoreWith(string color)
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Lighting.Sync = "static";
            s.Lighting.Static.Effect = "gradientlinear";
            s.Lighting.Static.Color = color;
        });
        return store;
    }

    [Fact]
    public void A_never_configured_static_look_restores_the_default_pattern()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Lighting.Sync = RemovedSyncModes.Normalize("gamesync"));
        var lighting = new RecordingLighting();

        LiveEngineSync.ApplyLighting(store, lighting);

        var start = Assert.Single(lighting.Starts);
        Assert.Equal(StaticEffectCatalog.DefaultEffect, start.Effect);
        Assert.Null(start.Color);
        Assert.True(start.Intensity > 0);
    }

    [Theory]
    [InlineData("not-a-color", null)]
    [InlineData("#12", null)]
    [InlineData("#00ff7f", "#00ff7f")]
    public void Live_sync_passes_only_a_parseable_color(string persisted, string? expected)
    {
        var lighting = new RecordingLighting();

        LiveEngineSync.ApplyLighting(StoreWith(persisted), lighting);

        var start = Assert.Single(lighting.Starts);
        Assert.Equal("gradientlinear", start.Effect);
        Assert.Equal(expected, start.Color);
    }

    [Theory]
    [InlineData("not-a-color", null)]
    [InlineData("#00ff7f", "#00ff7f")]
    public void Auto_restore_passes_only_a_parseable_color(string persisted, string? expected)
    {
        var lighting = new RecordingLighting();
        var store = StoreWith(persisted);
        var restore = new AutoRestoreOnStart(store, lighting, new NoFans(), new MultiplexHub());

        var restoreLighting = typeof(AutoRestoreOnStart).GetMethod(
            "RestoreLighting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.True((bool)restoreLighting.Invoke(restore, null)!);

        var start = Assert.Single(lighting.Starts);
        Assert.Equal(expected, start.Color);
    }

    private sealed class NoFans : IFanControlProvider
    {
        public IReadOnlyList<FanChannel> GetFanChannels() => Array.Empty<FanChannel>();
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }
}
