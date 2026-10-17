using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// FeatureReconciler.Apply/ApplyPatch are the one-time transition side
/// effects fired from PATCH /preferences; per-tick gating lives in the
/// workers themselves. These tests drive them directly against recording
/// fakes, a real CurveEngine, and a real LightingEngine (for the blackout
/// side effect), the same shape McpTestHarness/DeckActionExecutorTests use
/// for ILightingProvider.
/// </summary>
public class FeatureReconcilerTests
{
    private sealed class RecordingLightingProvider : ILightingProvider
    {
        public List<AnimateHeadlessStart> StartAnimateCalls { get; } = new();
        public int SuspendCallCount { get; private set; }
        public int StopAllCallCount { get; private set; }

        public string GetSync() => "none";
        public void SetSync(string sync) { }
        public void StopAll() => StopAllCallCount++;
        public void Suspend() => SuspendCallCount++;
        public bool IsPaused => false;
        public void SetPaused(bool paused) { }
        public void SetBrightness(BrightnessScale scale) { }
        public void SetSpeed(SpeedScale scale) { }
        public AnimateOptions GetAnimateOptions() => new();
        public AudioSyncOptions GetAudioSyncOptions() => new();
        public ScreenSyncOptions GetScreenSyncOptions() => new();
        public void StartAnimate(AnimateHeadlessStart body) => StartAnimateCalls.Add(body);
        public void StartStatic(StaticHeadlessStart body) { }
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

    private sealed class RecordingFanControlProvider : IFanControlProvider
    {
        public int ReleaseAllCallCount { get; private set; }

        public IReadOnlyList<FanChannel> GetFanChannels() => System.Array.Empty<FanChannel>();
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => System.Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() => ReleaseAllCallCount++;
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, System.IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FanCalibration>>(System.Array.Empty<FanCalibration>());
    }

    // No real HID device: KeebSettingsApplier.Apply() reads _hub.IsConnected
    // (false) and returns without writing, so the OFF->ON path exercises the
    // call without touching hardware.
    private sealed class NoDevices : IHidEnumerator
    {
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => System.Array.Empty<HidDeviceInfo>();
        public IReadOnlyList<HidDeviceInfo> FindAll() => System.Array.Empty<HidDeviceInfo>();
        public IHidDevice? Open(string path, bool forInput = false) => null;
    }

    private static DeviceFrame[] MakeLitDevices(int count = 2, int leds = 4)
    {
        var frames = new DeviceFrame[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = new DeviceFrame(i, $"test-{i}", leds);
            frames[i].Fill(255, 128, 64);
            frames[i].Publish();
        }
        return frames;
    }

    private static bool AllBlack(DeviceFrame frame)
    {
        foreach (var b in frame.LedBytes)
        {
            if (b != 0) return false;
        }
        return true;
    }

    /// <summary>Forwards every call to the real store, capturing what a
    /// probe reports the instant the first Update lands - the only way to
    /// tell whether a side effect ran before or after the commit, since both
    /// orderings leave the same end state.</summary>
    private sealed class OrderingSpyConfigStore : IConfigStore
    {
        private readonly IConfigStore _inner;
        private readonly System.Func<bool> _probe;
        public bool? ProbeAtFirstUpdate { get; private set; }

        public OrderingSpyConfigStore(IConfigStore inner, System.Func<bool> probe)
        {
            _inner = inner;
            _probe = probe;
        }

        public NexusSettings Load() => _inner.Load();

        public void Update(System.Action<NexusSettings> mutator)
        {
            ProbeAtFirstUpdate ??= _probe();
            _inner.Update(mutator);
        }

        public void FlushNow() => _inner.FlushNow();
        public string SettingsPath => _inner.SettingsPath;
        public void Reload() => _inner.Reload();
        public event System.Action? OnChanged
        {
            add => _inner.OnChanged += value;
            remove => _inner.OnChanged -= value;
        }
    }

    private static (FeatureReconciler Reconciler, RecordingLightingProvider Lighting, RecordingFanControlProvider Fans, CurveEngine CurveEngine, LightingEngine Engine, DeviceFrame[] Devices, InMemoryConfigStore Store) Build(Nexus.Service.Conflicts.ConflictWatcher? conflicts = null, InMemoryConfigStore? existingStore = null)
    {
        var store = existingStore ?? new InMemoryConfigStore();
        var gates = new FeatureGates(store);
        var lighting = new RecordingLightingProvider();
        var fans = new RecordingFanControlProvider();
        var curveEngine = new CurveEngine(fans, store, new Nexus.Service.Sockets.MultiplexHub(), gates);
        var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var blackout = new SleepBlackoutCoordinator(engine, store, bridge: null, gates);
        var keeb = new KeebSettingsApplier(new KeebHub(new NoDevices()), store, new Nexus.Service.Sockets.MultiplexHub());
        var reconciler = new FeatureReconciler(lighting, curveEngine, blackout, store, keeb, conflicts);
        return (reconciler, lighting, fans, curveEngine, engine, devices, store);
    }

    [Fact]
    public void Apply_LightingOffToOn_ReplaysPersistedSync()
    {
        var (reconciler, lighting, _, _, _, _, store) = Build();
        store.Update(s => s.Lighting.Sync = "plasma");
        var before = new FeaturesSettings { Lighting = false, Cooling = true, Monitoring = true, Diagnostics = true };
        var after = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };

        reconciler.Apply(before, after);

        var call = Assert.Single(lighting.StartAnimateCalls);
        Assert.Equal("plasma", call.Effect);
        Assert.Equal(0, lighting.SuspendCallCount);
    }

    [Fact]
    public void Apply_LightingOnToOff_SuspendsOnce()
    {
        var (reconciler, lighting, _, _, _, _, _) = Build();
        var before = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };
        var after = new FeaturesSettings { Lighting = false, Cooling = true, Monitoring = true, Diagnostics = true };

        reconciler.Apply(before, after);

        Assert.Equal(1, lighting.SuspendCallCount);
        Assert.Equal(0, lighting.StopAllCallCount);
        Assert.Empty(lighting.StartAnimateCalls);
    }

    [Fact]
    public void Apply_LightingOnToOff_BlanksEveryDeviceBeforeSuspending()
    {
        var (reconciler, _, _, _, engine, devices, _) = Build();
        Assert.All(devices, d => Assert.False(AllBlack(d)));
        var before = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };
        var after = new FeaturesSettings { Lighting = false, Cooling = true, Monitoring = true, Diagnostics = true };

        reconciler.Apply(before, after);

        Assert.True(engine.Blackout);
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public void Apply_CoolingOnToOff_DoesNotReleaseImmediately_OnlyQueuesTheRequest()
    {
        // The release must go through CurveEngine's own serialized tick loop
        // (RequestRelease), never a direct fans.ReleaseAll() from the PATCH
        // thread, or an in-flight tick that already read the gate as
        // enabled could still write a duty after this call returns.
        var (reconciler, _, fans, _, _, _, _) = Build();
        var before = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };
        var after = new FeaturesSettings { Lighting = true, Cooling = false, Monitoring = true, Diagnostics = true };

        reconciler.Apply(before, after);

        Assert.Equal(0, fans.ReleaseAllCallCount);
    }

    [Fact]
    public void ApplyPatch_WhileUndecided_CoolingOff_QueuesNoRelease()
    {
        var (reconciler, _, fans, curveEngine, _, _, store) = Build();
        store.Update(s => s.FeaturesOnboardingCompleted = false);

        reconciler.ApplyPatch(s => s.Features.Cooling = false);
        curveEngine.Tick();

        Assert.Equal(0, fans.ReleaseAllCallCount);
    }

    [Fact]
    public void ApplyPatch_CompletingTheFeatureStep_SwitchesTheChosenPillarsOnLive()
    {
        var (reconciler, lighting, fans, curveEngine, _, _, store) = Build();
        store.Update(s =>
        {
            s.FeaturesOnboardingCompleted = false;
            s.Lighting.Sync = "plasma";
        });

        reconciler.ApplyPatch(s => s.FeaturesOnboardingCompleted = true);

        var call = Assert.Single(lighting.StartAnimateCalls);
        Assert.Equal("plasma", call.Effect);
        Assert.Equal(0, lighting.SuspendCallCount);
        curveEngine.Tick();
        Assert.Equal(0, fans.ReleaseAllCallCount);
    }

    [Fact]
    public void ApplyPatch_ThatChangesThePillars_RescansAndRepublishesTheConflictsBeforeReturning()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.FeaturesOnboardingCompleted = false);
        var processes = new Dictionary<string, List<(int Pid, string? Path)>>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["SignalRgb"] = new() { (100, null) },
        };
        var watcher = new Nexus.Service.Conflicts.ConflictWatcher(
            new Nexus.Service.Sockets.MultiplexHub(), null, new FeatureGates(store), null, () => processes);
        var (reconciler, _, _, _, _, _, _) = Build(watcher, store);
        Assert.Empty(watcher.GetConflicts());
        var before = watcher.PublishCount;

        reconciler.ApplyPatch(s => s.FeaturesOnboardingCompleted = true);

        Assert.True(watcher.PublishCount > before);
        Assert.Single(watcher.GetConflicts());
    }

    [Fact]
    public void ApplyPatch_ThatLeavesThePillarsAlone_DoesNotRescanTheConflicts()
    {
        var store = new InMemoryConfigStore();
        var scans = 0;
        var watcher = new Nexus.Service.Conflicts.ConflictWatcher(
            new Nexus.Service.Sockets.MultiplexHub(), null, new FeatureGates(store), null,
            () => { scans++; return new Dictionary<string, List<(int Pid, string? Path)>>(); });
        var (reconciler, _, _, _, _, _, _) = Build(watcher, store);

        reconciler.ApplyPatch(s => s.Features.Monitoring = false);

        Assert.Equal(0, scans);
    }

    [Fact]
    public void ApplyPatch_CompletingTheFeatureStepWithLightingOff_StartsNothing()
    {
        var (reconciler, lighting, _, _, _, _, store) = Build();
        store.Update(s =>
        {
            s.FeaturesOnboardingCompleted = false;
            s.Features.Lighting = false;
        });

        reconciler.ApplyPatch(s => s.FeaturesOnboardingCompleted = true);

        Assert.Empty(lighting.StartAnimateCalls);
    }

    [Fact]
    public void DrainPendingRelease_FulfillsAQueuedRelease_BeforeTheNextTick_AndOnlyOnce()
    {
        var (reconciler, _, fans, curveEngine, _, _, store) = Build();
        var before = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };
        var after = new FeaturesSettings { Lighting = true, Cooling = false, Monitoring = true, Diagnostics = true };
        reconciler.Apply(before, after);
        store.Update(s => s.Features.Cooling = false);

        curveEngine.DrainPendingRelease();
        curveEngine.DrainPendingRelease();
        curveEngine.Tick();

        Assert.Equal(1, fans.ReleaseAllCallCount);
    }

    [Fact]
    public void Apply_CoolingOnToOff_ReleasesOnce_OnTheNextDisabledTick()
    {
        var (reconciler, _, fans, curveEngine, _, _, store) = Build();
        var before = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };
        var after = new FeaturesSettings { Lighting = true, Cooling = false, Monitoring = true, Diagnostics = true };
        reconciler.Apply(before, after);
        store.Update(s => s.Features.Cooling = false);

        curveEngine.Tick();
        Assert.Equal(1, fans.ReleaseAllCallCount);

        curveEngine.Tick();
        Assert.Equal(1, fans.ReleaseAllCallCount);
    }

    [Fact]
    public void Apply_CoolingOffToOn_DoesNotReleaseOrDriveFans()
    {
        var (reconciler, _, fans, _, _, _, _) = Build();
        var before = new FeaturesSettings { Lighting = true, Cooling = false, Monitoring = true, Diagnostics = true };
        var after = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };

        reconciler.Apply(before, after);

        Assert.Equal(0, fans.ReleaseAllCallCount);
    }

    [Fact]
    public void Apply_NoTransition_TouchesNothing()
    {
        var (reconciler, lighting, fans, _, engine, devices, _) = Build();
        var same = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };

        reconciler.Apply(same, same);

        Assert.Equal(0, lighting.SuspendCallCount);
        Assert.Equal(0, lighting.StopAllCallCount);
        Assert.Empty(lighting.StartAnimateCalls);
        Assert.Equal(0, fans.ReleaseAllCallCount);
        Assert.False(engine.Blackout);
        Assert.All(devices, d => Assert.False(AllBlack(d)));
    }

    [Fact]
    public void ApplyPatch_CoolingOnToOff_MutatesStoreAndQueuesTheRelease()
    {
        var (reconciler, _, fans, curveEngine, _, _, store) = Build();

        reconciler.ApplyPatch(s => s.Features.Cooling = false);

        Assert.False(store.Load().Features.Cooling);
        Assert.Equal(0, fans.ReleaseAllCallCount);
        curveEngine.Tick();
        Assert.Equal(1, fans.ReleaseAllCallCount);
    }

    [Fact]
    public void ApplyPatch_LightingOnToOff_EndsBlackedOutAndSuspended()
    {
        var (reconciler, lighting, _, _, engine, devices, store) = Build();

        reconciler.ApplyPatch(s => s.Features.Lighting = false, lightingPatchValue: false);

        Assert.False(store.Load().Features.Lighting);
        Assert.True(engine.Blackout);
        Assert.All(devices, d => Assert.True(AllBlack(d)));
        Assert.Equal(1, lighting.SuspendCallCount);
    }

    [Fact]
    public void ApplyPatch_LightingOnToOff_BlanksBeforeTheStoreCommits()
    {
        // The end-state test above cannot distinguish correct sequencing
        // from a swapped one - both leave the store false and the devices
        // black. This spies on the moment of commit itself: if the blackout
        // ran first, the probe (device 0 already black) reads true at that
        // instant; a regression that commits before blackout would read false.
        var store = new InMemoryConfigStore();
        var gates = new FeatureGates(store);
        var lighting = new RecordingLightingProvider();
        var curveEngine = new CurveEngine(new RecordingFanControlProvider(), store, new Nexus.Service.Sockets.MultiplexHub(), gates);
        var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var blackout = new SleepBlackoutCoordinator(engine, store, bridge: null, gates);
        var keeb = new KeebSettingsApplier(new KeebHub(new NoDevices()), store, new Nexus.Service.Sockets.MultiplexHub());
        var spyStore = new OrderingSpyConfigStore(store, () => AllBlack(devices[0]));
        var reconciler = new FeatureReconciler(lighting, curveEngine, blackout, spyStore, keeb);

        reconciler.ApplyPatch(s => s.Features.Lighting = false, lightingPatchValue: false);

        Assert.True(spyStore.ProbeAtFirstUpdate);
    }

    [Fact]
    public void ApplyPatch_UnrelatedField_DoesNotTriggerATransition()
    {
        var (reconciler, lighting, fans, _, _, _, store) = Build();

        reconciler.ApplyPatch(s => s.StartupDelaySeconds = 5);

        Assert.Equal(5, store.Load().StartupDelaySeconds);
        Assert.Equal(0, lighting.SuspendCallCount);
        Assert.Equal(0, fans.ReleaseAllCallCount);
    }
}
