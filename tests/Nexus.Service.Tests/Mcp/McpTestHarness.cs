using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Mcp;
using Nexus.Service.Mcp.Tools;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Mcp;

/// <summary>Shared fakes and wiring for the Mcp test suites: the real
/// day-one and write tools, backed by minimal stub providers instead of
/// hardware. Excludes list_profiles/apply_profile, which need a real
/// ProfileManager lifecycle - those get dedicated coverage in
/// ProfileToolsTests.cs instead.</summary>
internal static class McpTestHarness
{
    public static IReadOnlyList<IMcpTool> BuildRealTools(IConfigStore store, out StubSensorProvider sensors, out StubFanControlProvider fans)
    {
        sensors = new StubSensorProvider();
        fans = new StubFanControlProvider();
        var lighting = new StubLightingProvider();
        var engine = new LightingEngine();
        var curves = new StubCoolingProvider(store);
        var hub = new MultiplexHub();
        return new IMcpTool[]
        {
            new GetSystemOverviewTool(sensors, fans, store),
            new GetSensorsTool(sensors),
            new GetCoolingStateTool(fans, store),
            new GetLightingStateTool(lighting, engine, store),
            new ApplyCoolingPresetTool(fans, store, hub),
            new SetGlobalFanSpeedTool(curves, fans, store, hub),
            new SetFanCurveTool(curves, fans, store, hub),
            new ApplyLightingScenarioTool(lighting, hub),
            new SetStaticColorTool(lighting, store, hub),
            new SetBrightnessTool(store, hub),
            new StopLightingTool(lighting, hub),
        };
    }

    internal sealed class StubSensorProvider : ISensorProvider
    {
        public List<HardwareSensor> Cpu { get; set; } = new();
        public List<HardwareSensor> Gpu { get; set; } = new();
        public List<GpuReadout> Gpus { get; set; } = new();
        public List<HardwareSensor> Memory { get; set; } = new();
        public List<HardwareSensor> Motherboard { get; set; } = new();
        public Dictionary<string, StorageComponent> Storage { get; set; } = new();

        public string GetCpuModel() => "Test CPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Cpu;
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 10f);
        public IReadOnlyList<string> GetGpuModels() => new List<string> { "Test GPU" };
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Gpu;
        public IReadOnlyList<GpuReadout> GetGpus() => Gpus;
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Memory;
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => Storage;
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Motherboard;
        public string GetMotherboardModel() => "Test Board";
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class StubFanControlProvider : IFanControlProvider
    {
        public List<FanChannel> Channels { get; set; } = new();
        public List<TemperatureSource> Sources { get; set; } = new();

        public IReadOnlyList<FanChannel> GetFanChannels() => Channels;
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Sources;
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    internal sealed class StubLightingProvider : ILightingProvider
    {
        public List<AnimateHeadlessStart> StartAnimateCalls { get; } = new();
        public List<StaticHeadlessStart> StartStaticCalls { get; } = new();
        public int StopAllCallCount { get; private set; }

        public string GetSync() => "none";
        public void SetSync(string sync) { }
        public void StopAll() => StopAllCallCount++;
        public void Suspend() { }
        public bool IsPaused => false;
        public void SetPaused(bool paused) { }
        public void SetBrightness(BrightnessScale scale) { }
        public void SetSpeed(SpeedScale scale) { }
        public AnimateOptions GetAnimateOptions() => new();
        public AudioSyncOptions GetAudioSyncOptions() => new();
        public ScreenSyncOptions GetScreenSyncOptions() => new();
        public void StartAnimate(AnimateHeadlessStart body) => StartAnimateCalls.Add(body);
        public void StartStatic(StaticHeadlessStart body) => StartStaticCalls.Add(body);
        public void StartMusic(MusicHeadlessStart body) { }
        public void StartScreen(ScreenHeadlessStart body) { }
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
}
