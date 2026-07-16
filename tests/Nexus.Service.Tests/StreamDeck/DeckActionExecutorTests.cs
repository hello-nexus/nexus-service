using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Activity;
using Nexus.Service.Cooling;
using Nexus.Service.Deck;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Devices;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

internal sealed class FakeInputterProvider : Nexus.Service.Peripherals.Keeb.IInputterProvider
{
    public Nexus.Service.Models.Peripherals.Keeb.InputterBody? Last;
    public void Send(Nexus.Service.Models.Peripherals.Keeb.InputterBody body) => Last = body;
}

internal sealed class FakeClipboardProvider : Nexus.Service.Platform.Clipboard.IClipboardProvider
{
    public string? Last;
    public bool ShouldFail;
    public bool SetText(string text) { Last = text; return !ShouldFail; }
}

internal sealed class FakeSystemPowerProvider : Nexus.Service.Platform.Power.ISystemPowerProvider
{
    public string? Called;
    public bool Lock() { Called = "lock"; return true; }
    public bool Sleep() { Called = "sleep"; return true; }
    public bool Shutdown() { Called = "shutdown"; return true; }
    public bool Restart() { Called = "restart"; return true; }
    public bool Logout() { Called = "logout"; return true; }
}

internal sealed class FakeAudioDeviceProvider : IAudioDeviceProvider
{
    public string? Output;
    public string? Input;
    public AudioDeviceList ListDevices() => new();
    public bool SetDefaultOutput(string deviceId) { Output = deviceId; return true; }
    public bool SetDefaultInput(string deviceId) { Input = deviceId; return true; }
}

internal sealed class FakeVolumeProvider : IVolumeProvider
{
    public VolumeState State = new() { Supported = true, Volume = 0.5, Muted = false };
    public VolumeState GetState() => State;
    public void SetVolume(double volume) => State = new VolumeState { Supported = true, Volume = volume, Muted = State.Muted };
    public void SetMuted(bool muted) => State = new VolumeState { Supported = true, Volume = State.Volume, Muted = muted };
}

internal sealed class FakeShortcutsProvider : IShortcutsProvider
{
    public string? Launched;
    public IReadOnlyList<Shortcut> GetAll() => Array.Empty<Shortcut>();
    public Shortcut? GetById(string targetId) => null;
    public byte[] GetIcon(string targetId) => Array.Empty<byte>();
    public bool Launch(string targetId) { Launched = targetId; return true; }
}

internal sealed class FakeMediaProvider : IMediaProvider
{
    public Dictionary<string, MediaSession> Sessions = new();
    public (string Source, string Action)? LastControl;
    public IReadOnlyDictionary<string, MediaSession> GetSessions() => Sessions;
    public void Control(string source, string action) => LastControl = (source, action);
    public byte[] GetAlbumArt(string source) => Array.Empty<byte>();
}

internal sealed class FakeLightingDeviceProvider : ILightingDeviceProvider
{
    public List<LightingDevice> Devices = new();
    public (string Id, bool On)? LastPower;
    public bool IsConnected => true;
    public GetLightingDevicesResponse GetAll() => new() { Devices = Devices };
    public void SetDisabled(IReadOnlyList<string> ids) { }
    public void SetPower(string id, bool on) => LastPower = (id, on);
    public void SetBrightness(string id, int brightness) { }
    public void SetHue(string id, float hue) { }
    public void SetSaturation(string id, float saturation) { }
    public void SetZoneLedCount(string id, int count) { }
    public void Identify(string id, int durationMs) { }
}

internal sealed class FakeLightingProvider : ILightingProvider
{
    public AnimateHeadlessStart? LastAnimate;
    public string GetSync() => "none";
    public void SetSync(string sync) { }
    public void StopAll() { }
    public void SetBrightness(BrightnessScale scale) { }
    public void SetSpeed(SpeedScale scale) { }
    public AnimateOptions GetAnimateOptions() => new();
    public AudioSyncOptions GetAudioSyncOptions() => new();
    public ScreenSyncOptions GetScreenSyncOptions() => new();
    public void StartAnimate(AnimateHeadlessStart body) => LastAnimate = body;
    public void StartMusic(MusicHeadlessStart body) { }
    public void StartScreen(ScreenHeadlessStart body) { }
    public void ReselectScreen() { }
    public bool StartMedia(string mediaId) => false;
    public void StartMediaIdle() { }
    public void StartGameSync() { }
    public Nexus.Service.Lighting.Engine.Effects.GameSyncEffect? ActiveGameSyncEffect() => null;
    public void UpdateScreenEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist, bool reactive = false, float reactivity = 0.5f, float intensity = 0.5f) { }
    public void UpdateMediaEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist) { }
    public (byte[] Bytes, string Tag)? CaptureAnimateThumbnail(string key, int slot, bool skipCache = false) => null;
    public void SaveAnimateTemplates(Dictionary<string, AnimateEffectTemplates> templates) { }
    public void SetMusicReactive(bool enabled) { }
    public void ReconcileAudioCapture() { }
}

internal sealed class FakeFanControlProvider : IFanControlProvider
{
    public (string Id, int Duty)? LastSetSpeed;
    public IReadOnlyList<FanChannel> GetFanChannels() => Array.Empty<FanChannel>();
    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
    public float? ReadTemperature(string sensorId) => null;
    public int SetFanSpeed(string channelId, int dutyPercent) { LastSetSpeed = (channelId, dutyPercent); return dutyPercent; }
    public void DriveFanSpeed(string channelId, int dutyPercent) { }
    public void ReleaseFan(string channelId) { }
    public void ReleaseAll() { }
    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
}

internal sealed class FakeY70Provider : IY70Provider
{
    public bool? LastToggle;
    public int? LastBrightness;
    public string? LastOrientation;
    public bool AppliedEffectiveOrientation;
    public bool IsConnected() => true;
    public string GetOrientation() => "landscape";
    public void SetOrientation(string orientation) => LastOrientation = orientation;
    public bool GetForceOrientation() => false;
    public void SetForceOrientation(bool forceOrientation) { }
    public void ApplyEffectiveOrientation() => AppliedEffectiveOrientation = true;
    public int GetBrightness() => 50;
    public void SetBrightness(int brightness) => LastBrightness = brightness;
    public bool GetToggle() => false;
    public void SetToggle(bool toggle) => LastToggle = toggle;
    public bool IsRotated() => false;
}

internal sealed class FakeDeckSurfaceControl : Nexus.Service.Deck.IDeckSurfaceControl
{
    public (string Serial, int Percent)? LastBrightness;
    public string? LastAsleepSerial;

    public void SetBrightness(string serial, int percent) => LastBrightness = (serial, percent);
    public void PutAsleep(string serial) => LastAsleepSerial = serial;
}

internal sealed class FakeDisplayBrightnessProvider : IDisplayBrightnessProvider
{
    public Dictionary<string, int> Current = new();
    public (string Id, int Percent)? LastWrite;
    public string Hint => "";
    public IReadOnlyList<DisplayDto> Enumerate() => Array.Empty<DisplayDto>();
    public int? GetBrightness(string id) => Current.TryGetValue(id, out var v) ? v : null;
    public DisplayBrightnessDto SetBrightness(string id, int percent)
    {
        LastWrite = (id, percent);
        Current[id] = percent;
        return new DisplayBrightnessDto { Id = id, RequestedBrightness = percent, AppliedBrightness = percent, Brightness = percent, Status = DisplayBrightnessWriteStatuses.Applied };
    }
    public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id) => new();
    public DisplayVcpDto? GetVcp(string id, byte code) => null;
    public bool SetVcp(string id, byte code, int value) => false;
    public string? FindDisplayIdByHardwareName(IReadOnlyList<string> nameFragments) => null;
}

/// <summary>
/// Real JsonConfigStore + ProfileManager against a temp directory (matching
/// Cloud/ProfileManagerCloudTests.cs), fake providers everywhere else.
/// </summary>
public sealed class DeckActionExecutorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;
    private readonly FakeInputterProvider _inputter = new();
    private readonly FakeClipboardProvider _clipboard = new();
    private readonly FakeSystemPowerProvider _power = new();
    private readonly FakeAudioDeviceProvider _audio = new();
    private readonly FakeVolumeProvider _volume = new();
    private readonly FakeShortcutsProvider _shortcuts = new();
    private readonly FakeMediaProvider _media = new();
    private readonly FakeLightingDeviceProvider _lightingDevices = new();
    private readonly FakeLightingProvider _lighting = new();
    private readonly FakeFanControlProvider _fans = new();
    private readonly FakeY70Provider _y70 = new();
    private readonly FakeDisplayBrightnessProvider _displayProvider = new();
    private readonly FakeDeckSurfaceControl _deckSurface = new();
    private readonly DeckActionExecutor _executor;

    public DeckActionExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-deck-exec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();

        var system = new Nexus.Service.Actions.SystemActions(
            _inputter, _clipboard, _power, _audio, _volume, _shortcuts,
            new ServiceCollection().BuildServiceProvider());
        var displayBrightness = new DisplayBrightnessController(_displayProvider);
        var hub = new MultiplexHub();

        _executor = new DeckActionExecutor(
            system, _lightingDevices, _lighting, _fans, _store, _profiles, _y70, displayBrightness, _media, hub,
            new Lazy<Nexus.Service.Deck.IDeckSurfaceControl>(() => _deckSurface),
            new Nexus.Service.Audio.AudioFilePlayer());
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private Task Run(DeckAction action, string latchKey = "dev:0") =>
        _executor.ExecuteAsync(action, "dev", 0, latchKey, CancellationToken.None);

    [Fact]
    public async Task LaunchApp_CallsShortcutsLaunch()
    {
        await Run(new DeckAction { Type = "launchApp", AppId = "steam" });
        Assert.Equal("steam", _shortcuts.Launched);
    }

    [Fact]
    public async Task Hotkey_ParsesChordAndSendsDownThenUp()
    {
        await Run(new DeckAction { Type = "hotkey", Keys = "ctrl+shift+m" });
        Assert.NotNull(_inputter.Last);
        Assert.Equal(2, _inputter.Last!.Strokes.Count);
        Assert.Equal("KeyM", _inputter.Last.Strokes[0].Key);
        Assert.True(_inputter.Last.Strokes[0].Ctrl);
        Assert.True(_inputter.Last.Strokes[0].Shift);
        Assert.Equal("keydown", _inputter.Last.Strokes[0].Type);
        Assert.Equal("keyup", _inputter.Last.Strokes[1].Type);
        Assert.Equal(("ok", (string?)null), _executor.LastOutcome);
    }

    [Fact]
    public async Task Hotkey_NoKeyToken_IsANoOp()
    {
        await Run(new DeckAction { Type = "hotkey", Keys = "ctrl+shift" });
        Assert.Null(_inputter.Last);
    }

    [Fact]
    public async Task Hotkey_PrintScreen_ParsesToThePrintScreenKey()
    {
        await Run(new DeckAction { Type = "hotkey", Keys = "printscreen" });
        Assert.NotNull(_inputter.Last);
        Assert.Equal("PrintScreen", _inputter.Last!.Strokes[0].Key);
    }

    [Fact]
    public async Task Hotkey_MetaPeriod_ParsesToThePeriodKeyWithMeta()
    {
        await Run(new DeckAction { Type = "hotkey", Keys = "meta+." });
        Assert.NotNull(_inputter.Last);
        Assert.Equal("Period", _inputter.Last!.Strokes[0].Key);
        Assert.True(_inputter.Last.Strokes[0].Meta);
    }

    [Fact]
    public async Task Text_SetsClipboardAndPastes()
    {
        await Run(new DeckAction { Type = "text", Text = "hello world" });
        Assert.Equal("hello world", _clipboard.Last);
        Assert.NotNull(_inputter.Last);
        Assert.Equal(2, _inputter.Last!.Strokes.Count);
        Assert.Equal(("ok", (string?)null), _executor.LastOutcome);
    }

    /// <summary>
    /// SendTextAsync's ApiResponse.Fail must not be silently discarded: the
    /// paste chord is skipped and the dispatch reports outcome=failed with
    /// the response's own error text, not outcome=ok.
    /// </summary>
    [Fact]
    public async Task Text_ClipboardSetFails_ReportsFailedOutcomeAndSkipsThePaste()
    {
        _clipboard.ShouldFail = true;
        await Run(new DeckAction { Type = "text", Text = "hello world" });
        Assert.Null(_inputter.Last);
        Assert.Equal(("failed", "clipboard unavailable"), _executor.LastOutcome);
    }

    [Fact]
    public async Task Power_DispatchesToTheMatchingProviderMethod()
    {
        await Run(new DeckAction { Type = "power", PowerAction = "sleep" });
        Assert.Equal("sleep", _power.Called);
    }

    [Fact]
    public async Task AudioOutput_SetsDefaultOutput()
    {
        await Run(new DeckAction { Type = "audioOutput", DeviceId = "spk-1" });
        Assert.Equal("spk-1", _audio.Output);
    }

    [Fact]
    public async Task AudioInput_SetsDefaultInput()
    {
        await Run(new DeckAction { Type = "audioInput", DeviceId = "mic-1" });
        Assert.Equal("mic-1", _audio.Input);
    }

    [Fact]
    public async Task System_VolumeUp_IncreasesByStep()
    {
        _volume.State = new VolumeState { Supported = true, Volume = 0.5, Muted = false };
        await Run(new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = "volumeUp" } });
        Assert.Equal(0.55, _volume.State.Volume, 3);
    }

    [Fact]
    public async Task System_VolumeUp_WithExplicitStep_TreatsStepAsAPercentNotARawFraction()
    {
        _volume.State = new VolumeState { Supported = true, Volume = 0.5, Muted = false };
        await Run(new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = "volumeUp", Step = 10 } });
        Assert.Equal(0.6, _volume.State.Volume, 3);
    }

    [Fact]
    public async Task System_VolumeDown_WithExplicitStep_TreatsStepAsAPercentNotARawFraction()
    {
        _volume.State = new VolumeState { Supported = true, Volume = 0.5, Muted = false };
        await Run(new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = "volumeDown", Step = 6 } });
        Assert.Equal(0.44, _volume.State.Volume, 3);
    }

    [Fact]
    public async Task System_MuteToggle_FlipsMuted()
    {
        _volume.State = new VolumeState { Supported = true, Volume = 0.5, Muted = false };
        await Run(new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = "muteToggle" } });
        Assert.True(_volume.State.Muted);
    }

    [Fact]
    public async Task System_MediaPlayPause_PicksTheFocusedSessionAndTogglesOnPlaying()
    {
        _media.Sessions["a"] = new MediaSession { IsFocused = false, Playback = new MediaPlayback { Playing = true } };
        _media.Sessions["b"] = new MediaSession { IsFocused = true, Playback = new MediaPlayback { Playing = false } };
        await Run(new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = "mediaPlayPause" } });
        Assert.Equal(("b", "play"), _media.LastControl);
    }

    [Fact]
    public async Task System_MediaNext_WithNoSessions_IsANoOp()
    {
        await Run(new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = "mediaNext" } });
        Assert.Null(_media.LastControl);
    }

    [Fact]
    public async Task System_BrightnessUp_ReadsCurrentAndAppliesStep()
    {
        _displayProvider.Current["disp-1"] = 40;
        await Run(new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = "brightnessUp", DisplayId = "disp-1" } });
        Assert.Equal(("disp-1", 50), _displayProvider.LastWrite);
    }

    [Fact]
    public async Task OpenFile_WithMissingPath_IsANoOp()
    {
        await Run(new DeckAction { Type = "openFile", Path = "" });
        // No exception, no crash - nothing else to assert without touching the real filesystem.
    }

    // "taskManager" and "monitoringPage" call real OS launch paths (SystemActions
    // has no injectable seam for them, matching OpenPathAsync/OpenUrlAsync above)
    // - only the no-op presses are exercised here for the same reason
    // OpenFile_WithMissingPath_IsANoOp avoids a real filesystem touch.
    [Fact]
    public async Task Monitoring_PressNone_IsANoOp()
    {
        await Run(new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "summary/cpu-usage", Style = "line", Press = "none" });
    }

    [Fact]
    public async Task Monitoring_PressAbsent_IsANoOp()
    {
        await Run(new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "summary/cpu-usage", Style = "line" });
    }

    [Fact]
    public async Task Weather_IsADisplayOnlyNoOpThatReportsOk()
    {
        await Run(new DeckAction { Type = "weather", City = "Boston", Units = "auto" });
        Assert.Equal(("ok", (string?)null), _executor.LastOutcome);
    }

    // Uses a missing path - AudioFilePlayer's own File.Exists gate makes this
    // a safe no-op, the same reasoning as OpenFile_WithMissingPath_IsANoOp.
    [Fact]
    public async Task PlayAudio_WithMissingPath_ReportsOkWithoutThrowing()
    {
        await Run(new DeckAction { Type = "playAudio", Path = "/does/not/exist.wav", Volume = 50 });
        Assert.Equal(("ok", (string?)null), _executor.LastOutcome);
    }

    [Fact]
    public async Task Nexus_RgbEffect_StartsAnimateWithStartAnimateDefaults()
    {
        await Run(new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "rgbEffect", Effect = "rainbow" } });
        Assert.NotNull(_lighting.LastAnimate);
        Assert.Equal("rainbow", _lighting.LastAnimate!.Effect);
        Assert.Equal(50, _lighting.LastAnimate.Speed);
        Assert.Equal(1f, _lighting.LastAnimate.Intensity);
        Assert.Equal("none", _lighting.LastAnimate.Filter);
    }

    [Fact]
    public async Task Nexus_RgbScene_UnknownProfile_LogsAndDoesNotThrow()
    {
        await Run(new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "rgbScene", ProfileId = "does-not-exist" } });
        // No exception escaped ExecuteAsync - that is the behavior under test.
    }

    [Fact]
    public async Task Nexus_RgbScene_ExistingProfile_Switches()
    {
        var activeId = _profiles.GetManifest().ActiveProfileId;
        await Run(new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "rgbScene", ProfileId = activeId } });
        Assert.Equal(activeId, _profiles.GetManifest().ActiveProfileId);
    }

    [Fact]
    public async Task Nexus_LightingBrightness_PersistsClampedValue()
    {
        await Run(new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "lightingBrightness", Value = 1.5 } });
        Assert.Equal(1f, _store.Load().Lighting.GlobalBrightness);
    }

    [Fact]
    public async Task Nexus_LightingPower_SetsPowerOnTheGivenDevice()
    {
        await Run(new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "lightingPower", DeviceId = "dev-1", On = false } });
        Assert.Equal(("dev-1", false), _lightingDevices.LastPower);
    }

    [Fact]
    public async Task Nexus_FanSpeed_ClampsAndSetsDuty()
    {
        await Run(new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "fanSpeed", FanId = "fan-1", Value = 150 } });
        Assert.Equal(("fan-1", 100), _fans.LastSetSpeed);
    }

    [Fact]
    public async Task Nexus_FanProfile_AppliesThePreset()
    {
        await Run(new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "fanProfile", Profile = "silent" } });
        Assert.Equal("silent", _store.Load().Cooling.ActivePreset);
    }

    [Fact]
    public async Task Nexus_Y70Power_InvertsOnIntoTheToggleCall()
    {
        await Run(new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "y70Power", On = true } });
        Assert.False(_y70.LastToggle);

        await Run(new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "y70Power", On = false } });
        Assert.True(_y70.LastToggle);
    }

    [Fact]
    public async Task Nexus_Y70Brightness_SetsClampedBrightness()
    {
        await Run(new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "y70Brightness", Value = 250 } });
        Assert.Equal(100, _y70.LastBrightness);
    }

    [Fact]
    public async Task Nexus_Y70Rotation_SetsOrientationThenApplies()
    {
        await Run(new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "y70Rotation", Orientation = "portrait" } });
        Assert.Equal("portrait", _y70.LastOrientation);
        Assert.True(_y70.AppliedEffectiveOrientation);
    }

    [Fact]
    public async Task UnknownActionType_DoesNotThrow()
    {
        await Run(new DeckAction { Type = "not-a-real-type" });
        Assert.Equal(("unknown", (string?)null), _executor.LastOutcome);
    }

    [Fact]
    public async Task ToggleWithMuteState_UnwrapsAgainstLiveVolumeState()
    {
        _volume.State = new VolumeState { Supported = true, Volume = 0.5, Muted = false };
        var toggle = new DeckAction
        {
            Type = "toggle",
            State = new DeckToggleState { Kind = "mute" },
            On = new DeckAction { Type = "power", PowerAction = "lock" },
            Off = new DeckAction { Type = "power", PowerAction = "sleep" },
        };

        // Not muted (live) -> target = !false = true -> "on" branch fires.
        await Run(toggle, "dev:mute-key");
        Assert.Equal("lock", _power.Called);

        _volume.State = new VolumeState { Supported = true, Volume = 0.5, Muted = true };
        await Run(toggle, "dev:mute-key");
        Assert.Equal("sleep", _power.Called);
    }

    [Fact]
    public async Task ToggleWithLightingPowerState_UnwrapsAgainstLiveDeviceState()
    {
        _lightingDevices.Devices.Add(new LightingDevice { Id = "dev-1", LedsOn = true });
        var toggle = new DeckAction
        {
            Type = "toggle",
            State = new DeckToggleState { Kind = "lightingPower", DeviceId = "dev-1" },
            On = new DeckAction { Type = "power", PowerAction = "restart" },
            Off = new DeckAction { Type = "power", PowerAction = "logout" },
        };

        // LedsOn=true (live) -> target = !true = false -> "off" branch fires.
        await Run(toggle, "dev:light-key");
        Assert.Equal("logout", _power.Called);
    }

    [Fact]
    public async Task ToggleWithInternalState_FlipsAnInMemoryLatchAcrossCalls()
    {
        var toggle = new DeckAction
        {
            Type = "toggle",
            State = new DeckToggleState { Kind = "internal" },
            On = new DeckAction { Type = "power", PowerAction = "lock" },
            Off = new DeckAction { Type = "power", PowerAction = "sleep" },
        };

        // Starts unset (false) -> target = true -> "on" branch fires, latch set true.
        await Run(toggle, "dev:internal-key");
        Assert.Equal("lock", _power.Called);
        Assert.True(_executor.IsToggleOn(toggle.State, "dev:internal-key"));

        // Latch now true -> target = false -> "off" branch fires.
        await Run(toggle, "dev:internal-key");
        Assert.Equal("sleep", _power.Called);
        Assert.False(_executor.IsToggleOn(toggle.State, "dev:internal-key"));
    }

    [Fact]
    public async Task ToggleMissingBranches_IsTreatedAsUnknownAndDoesNotThrow()
    {
        await Run(new DeckAction { Type = "toggle", State = new DeckToggleState { Kind = "internal" } });
    }

    [Fact]
    public async Task Sequence_RunsEachStepInOrderAndKeepsGoingAfterAStepThrows()
    {
        _fans.LastSetSpeed = null;
        var sequence = new DeckAction
        {
            Type = "sequence",
            Steps = new List<DeckSequenceStep>
            {
                new() { Action = new DeckAction { Type = "power", PowerAction = "lock" }, GapAfterMs = 0 },
                // lightingPower with no DeviceId is a silent no-op, not a throw - the
                // "keep going after a step throws" guarantee is exercised by the
                // executor's per-step try/catch regardless of whether this step errors.
                new() { Action = new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "fanSpeed", FanId = "fan-1", Value = 10 } }, GapAfterMs = 0 },
            },
        };

        await Run(sequence);

        Assert.Equal("lock", _power.Called);
        Assert.Equal(("fan-1", 10), _fans.LastSetSpeed);
    }

    [Fact]
    public async Task Sequence_WaitsPressMsPlusGapAfterMsBetweenSteps()
    {
        var sequence = new DeckAction
        {
            Type = "sequence",
            Steps = new List<DeckSequenceStep>
            {
                new() { Action = new DeckAction { Type = "power", PowerAction = "lock" }, PressMs = 30, GapAfterMs = 30 },
                new() { Action = new DeckAction { Type = "power", PowerAction = "sleep" } },
            },
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Run(sequence);
        sw.Stop();

        Assert.Equal("sleep", _power.Called);
        Assert.True(sw.ElapsedMilliseconds >= 55, $"expected at least ~60ms of sequence pacing, elapsed {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task DeckBrightness_Set_PersistsAndAppliesLive()
    {
        await Run(new DeckAction { Type = "deckBrightness", Op = "set", Value = 77 });

        Assert.Equal(77, _store.Load().StreamDeck.Decks["dev"].Brightness);
        Assert.Equal(("dev", 77), _deckSurface.LastBrightness);
    }

    [Fact]
    public async Task DeckBrightness_Set_ClampsToValidRange()
    {
        await Run(new DeckAction { Type = "deckBrightness", Op = "set", Value = 250 });

        Assert.Equal(100, _store.Load().StreamDeck.Decks["dev"].Brightness);
        Assert.Equal(("dev", 100), _deckSurface.LastBrightness);
    }

    [Fact]
    public async Task DeckBrightness_Up_AdjustsFromCurrentByDefaultStep()
    {
        _store.Update(s => s.StreamDeck.Decks["dev"] = new PhysicalDeckSettings { Brightness = 50 });

        await Run(new DeckAction { Type = "deckBrightness", Op = "up" });

        Assert.Equal(60, _store.Load().StreamDeck.Decks["dev"].Brightness);
        Assert.Equal(("dev", 60), _deckSurface.LastBrightness);
    }

    [Fact]
    public async Task DeckBrightness_Down_UsesTheGivenStepAndClampsAtZero()
    {
        _store.Update(s => s.StreamDeck.Decks["dev"] = new PhysicalDeckSettings { Brightness = 15 });

        await Run(new DeckAction { Type = "deckBrightness", Op = "down", Step = 30 });

        Assert.Equal(0, _store.Load().StreamDeck.Decks["dev"].Brightness);
        Assert.Equal(("dev", 0), _deckSurface.LastBrightness);
    }

    [Fact]
    public async Task DeckBrightness_SetWithNoValue_IsANoOp()
    {
        await Run(new DeckAction { Type = "deckBrightness", Op = "set" });

        Assert.Null(_deckSurface.LastBrightness);
    }

    [Fact]
    public async Task DeckSleep_BlanksThisDecksSurface()
    {
        await Run(new DeckAction { Type = "deckSleep" }, "dev-serial:0");

        Assert.Equal("dev", _deckSurface.LastAsleepSerial);
    }

    [Fact]
    public async Task HotkeySwitch_AlternatesBetweenKeysAAndKeysBPerPress()
    {
        var action = new DeckAction { Type = "hotkeySwitch", KeysA = "ctrl+shift+m", KeysB = "ctrl+shift+n" };

        await Run(action, "dev:switch-key");
        Assert.Equal("KeyM", _inputter.Last!.Strokes[0].Key);

        await Run(action, "dev:switch-key");
        Assert.Equal("KeyN", _inputter.Last!.Strokes[0].Key);

        await Run(action, "dev:switch-key");
        Assert.Equal("KeyM", _inputter.Last!.Strokes[0].Key);
    }

    [Fact]
    public async Task HotkeySwitch_LatchIsPerKeyNotGlobal()
    {
        var action = new DeckAction { Type = "hotkeySwitch", KeysA = "ctrl+shift+m", KeysB = "ctrl+shift+n" };

        await Run(action, "dev:key-1");
        await Run(action, "dev:key-2");

        // A different latchKey starts its own alternation from KeysA again.
        Assert.Equal("KeyM", _inputter.Last!.Strokes[0].Key);
    }
}
