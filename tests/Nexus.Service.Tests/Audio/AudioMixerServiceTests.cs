using System;
using System.Collections.Generic;
using Nexus.Service.Activity;
using Nexus.Service.Audio;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Audio;

/// <summary>
/// Covers the two things the mixer adds over the OS volume mixer: a level that
/// survives the app restarting, and a preset that applies to apps which are not
/// even running. The Core Audio session walk itself is faked here - it lives in
/// the user-session helper and has no headless oracle (see
/// WindowsAudioSessionEnumerator).
/// </summary>
public sealed class AudioMixerServiceTests
{
    [Fact]
    public void MidDragVolumeMovesTheLevelButRemembersNothing()
    {
        var (mixer, sessions, store) = Build(Strip("spotify", 0.8));

        mixer.SetVolume("spotify", 0.3, commit: false);

        Assert.Equal(new List<(string, double)> { ("spotify", 0.3) }, sessions.VolumeWrites);
        Assert.Empty(store.Load().AudioMixer.Levels);
    }

    [Fact]
    public void ReleasingTheSliderRemembersTheLevel()
    {
        var (mixer, _, store) = Build(Strip("spotify", 0.8, name: "Spotify"));

        mixer.SetVolume("spotify", 0.3, commit: true);

        var level = store.Load().AudioMixer.Levels["spotify"];
        Assert.Equal(0.3, level.Volume);
        Assert.Equal("Spotify", level.Name);
    }

    [Fact]
    public void MuteIsAlwaysRemembered()
    {
        var (mixer, _, store) = Build(Strip("discord", 1.0));

        mixer.SetMuted("discord", true);

        Assert.True(store.Load().AudioMixer.Levels["discord"].Muted);
    }

    [Fact]
    public void ARememberedLevelIsAppliedWhenTheAppComesBack()
    {
        var (mixer, sessions, store) = Build(Strip("game", 1.0));
        mixer.SetVolume("game", 0.25, commit: true);
        sessions.VolumeWrites.Clear();

        // The app exits and relaunches: Windows opens the new session at its own
        // level, which is the whole failure this feature exists to cover.
        sessions.Replace();
        sessions.Replace(Strip("game", 1.0));

        Assert.Contains(("game", 0.25), sessions.VolumeWrites);
        Assert.True(store.Load().AudioMixer.StickyLevels);
    }

    [Fact]
    public void ARememberedLevelIsAppliedOnceNotEveryFrame()
    {
        var (mixer, sessions, _) = Build(Strip("game", 1.0));
        mixer.SetVolume("game", 0.25, commit: true);
        sessions.VolumeWrites.Clear();

        // Meter-rate snapshots of a strip that never left must not keep
        // re-asserting the level over a user who has since moved it.
        sessions.Replace(Strip("game", 0.25));
        sessions.Replace(Strip("game", 0.25));
        sessions.Replace(Strip("game", 0.25));

        Assert.Empty(sessions.VolumeWrites);
    }

    [Fact]
    public void FirstSnapshotRestoresAlreadyRunningApps()
    {
        // Service start is an absent-to-present edge for everything, on purpose:
        // the apps were launched while Nexus was down, so their levels are
        // whatever Windows felt like and the remembered ones should win.
        var fake = new FakeSessions();
        var store = new InMemoryConfigStore();
        store.Update(s => s.AudioMixer.Levels["spotify"] = new AudioMixerLevelDto { Volume = 0.2 });
        _ = new AudioMixerService(fake, new FakeVolume(), new FakeDevices(), store, new MultiplexHub());

        fake.Replace(Strip("spotify", 1.0));

        Assert.Contains(("spotify", 0.2), fake.VolumeWrites);
    }

    [Fact]
    public void StickyOffLeavesANewSessionAlone()
    {
        var (mixer, sessions, _) = Build(Strip("game", 1.0));
        mixer.SetVolume("game", 0.25, commit: true);
        mixer.SetSticky(false);
        sessions.VolumeWrites.Clear();

        sessions.Replace();
        sessions.Replace(Strip("game", 1.0));

        Assert.Empty(sessions.VolumeWrites);
    }

    [Fact]
    public void SavingAPresetCapturesEveryRunningStripAndTheMaster()
    {
        var (mixer, _, _) = Build(Strip("game", 0.7, name: "Game"), Strip("discord", 0.4, name: "Discord"));

        var preset = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Chatting" }).Preset!;

        Assert.Equal("Chatting", preset.Name);
        Assert.Equal(0.5, preset.MasterVolume);
        Assert.Equal(2, preset.Apps.Count);
        Assert.Contains(preset.Apps, a => a.Id == "game" && a.Volume == 0.7 && a.Name == "Game");
    }

    [Fact]
    public void SavingAPresetSkipsAStripThatIsNotOnTheDefaultOutput()
    {
        var offDefault = Strip("recorder", 0.5, name: "Recorder");
        offDefault.OnDefault = false;
        var (mixer, _, _) = Build(Strip("game", 0.7, name: "Game"), offDefault);

        var preset = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Chatting" }).Preset!;

        Assert.Equal("game", Assert.Single(preset.Apps).Id);
    }

    [Fact]
    public void APresetCanLeaveTheMasterAlone()
    {
        var (mixer, _, _) = Build(Strip("game", 0.7));

        var preset = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Apps only", IncludeMaster = false }).Preset!;

        Assert.Null(preset.MasterVolume);
    }

    [Fact]
    public void ApplyingAPresetWritesTheLiveStripsAndTheMaster()
    {
        var (mixer, sessions, _) = Build(Strip("game", 1.0), Strip("discord", 1.0));
        var preset = mixer.SavePreset(new SaveAudioMixerPresetBody
        {
            Name = "Gaming",
            Apps = new List<AudioMixerPresetEntryDto>
            {
                new() { Id = "game", Name = "Game", Volume = 0.9 },
                new() { Id = "discord", Name = "Discord", Volume = 0.5, Muted = true },
            },
        }).Preset!;
        sessions.VolumeWrites.Clear();

        Assert.True(mixer.ApplyPreset(preset.Id));

        Assert.Contains(("game", 0.9), sessions.VolumeWrites);
        Assert.Contains(("discord", 0.5), sessions.VolumeWrites);
        Assert.Contains(("discord", true), sessions.MuteWrites);
    }

    [Fact]
    public void ApplyingAPresetAlsoRemembersItSoAClosedAppComesBackRight()
    {
        // The scene is picked before the game launches - which is the normal
        // order - so a live-only apply would miss the app that matters most.
        var (mixer, sessions, store) = Build();
        var preset = mixer.SavePreset(new SaveAudioMixerPresetBody
        {
            Name = "Gaming",
            Apps = new List<AudioMixerPresetEntryDto> { new() { Id = "game", Name = "Game", Volume = 0.9 } },
        }).Preset!;

        mixer.ApplyPreset(preset.Id);
        Assert.Equal(0.9, store.Load().AudioMixer.Levels["game"].Volume);

        sessions.VolumeWrites.Clear();
        sessions.Replace(Strip("game", 1.0));
        Assert.Contains(("game", 0.9), sessions.VolumeWrites);
    }

    [Fact]
    public void APresetCapturesTheOutputAndInputItWasSavedWith()
    {
        var (mixer, _, _) = Build(Strip("game", 0.7));

        var preset = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Gaming" }).Preset!;

        Assert.Equal("out-a", preset.OutputDeviceId);
        Assert.Equal("Headset", preset.OutputDeviceName);
        Assert.Equal("in-a", preset.InputDeviceId);
    }

    [Fact]
    public void ApplyingAPresetSwitchesBackToItsDevices()
    {
        var fake = new FakeSessions();
        var devices = new FakeDevices();
        var store = new InMemoryConfigStore();
        var mixer = new AudioMixerService(fake, new FakeVolume(), devices, store, new MultiplexHub());
        fake.Replace(Strip("game", 0.7));
        var preset = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Gaming" }).Preset!;

        devices.SetDefaultOutput("out-b");
        Assert.True(mixer.ApplyPreset(preset.Id));

        Assert.Equal("out-a", devices.DefaultOutput);
    }

    [Fact]
    public void APresetCanLeaveTheDevicesAlone()
    {
        var (mixer, _, _) = Build(Strip("game", 0.7));

        var preset = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "No dev", IncludeDevices = false }).Preset!;

        Assert.Equal("", preset.OutputDeviceId);
        Assert.Equal("", preset.InputDeviceId);
    }

    [Fact]
    public void PresetNamesAreCappedAndRenamedInPlace()
    {
        var (mixer, _, store) = Build(Strip("game", 0.7));
        var preset = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "A very long preset name" }).Preset!;
        Assert.Equal(AudioMixerService.MaxPresetNameLength, preset.Name.Length);

        Assert.Equal("", mixer.RenamePreset(preset.Id, "Chatting"));

        var stored = Assert.Single(store.Load().AudioMixer.Presets);
        Assert.Equal("Chatting", stored.Name);
        // A rename must not re-capture: the levels stay as they were saved.
        Assert.Equal("game", Assert.Single(stored.Apps).Id);
    }

    [Fact]
    public void TheEleventhPresetIsRefused()
    {
        var (mixer, _, store) = Build(Strip("game", 0.7));
        for (var i = 0; i < AudioMixerService.MaxPresets; i++)
        {
            Assert.NotNull(mixer.SavePreset(new SaveAudioMixerPresetBody { Name = $"P{i}" }).Preset);
        }

        var refused = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "over" });
        Assert.Null(refused.Preset);
        Assert.Equal(AudioMixerService.ErrorLimitReached, refused.Error);
        Assert.Equal(AudioMixerService.MaxPresets, store.Load().AudioMixer.Presets.Count);
    }

    [Fact]
    public void ControlCharactersAreStrippedFromAName()
    {
        // The tile's pager joins names with a newline to key its width cache; one
        // embedded in a name would desync that split from the list.
        var (mixer, _, _) = Build(Strip("game", 0.7));

        var preset = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Ga\nming" }).Preset!;

        Assert.Equal("Gaming", preset.Name);
    }

    [Fact]
    public void TwoPresetsCannotShareAName()
    {
        // The tile picks a preset by its name, so a duplicate is unpickable.
        var (mixer, _, store) = Build(Strip("game", 0.7));
        mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Gaming" });

        // Case is not what distinguishes them either.
        var refused = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "gaming" });

        Assert.Null(refused.Preset);
        Assert.Equal(AudioMixerService.ErrorDuplicateName, refused.Error);
        Assert.Single(store.Load().AudioMixer.Presets);
    }

    [Fact]
    public void OverwritingAPresetKeepsItsOwnName()
    {
        // The duplicate check must skip the preset being written, or re-saving
        // one under its existing name would refuse itself.
        var (mixer, _, _) = Build(Strip("game", 0.7));
        var first = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Gaming" }).Preset!;

        var again = mixer.SavePreset(new SaveAudioMixerPresetBody { Id = first.Id, Name = "Gaming" });

        Assert.NotNull(again.Preset);
    }

    [Fact]
    public void RenamingOntoAnotherPresetsNameIsRefused()
    {
        var (mixer, _, store) = Build(Strip("game", 0.7));
        mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Gaming" });
        var chat = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Chatting" }).Preset!;

        Assert.Equal(AudioMixerService.ErrorDuplicateName, mixer.RenamePreset(chat.Id, "GAMING"));

        Assert.Contains(store.Load().AudioMixer.Presets, p => p.Name == "Chatting");
    }

    [Fact]
    public void RenamingAPresetToItsOwnNameIsAllowed()
    {
        var (mixer, _, _) = Build(Strip("game", 0.7));
        var preset = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Gaming" }).Preset!;

        Assert.Equal("", mixer.RenamePreset(preset.Id, "Gaming"));
    }

    [Fact]
    public void ANameThatOnlyCollidesAfterTheLengthCapIsRefused()
    {
        // Both clamp to "Streaming", so the stored names would be identical
        // even though what was typed was not.
        var (mixer, _, _) = Build(Strip("game", 0.7));
        mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Streaming solo" });

        var refused = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Streaming duo" });

        Assert.Equal(AudioMixerService.ErrorDuplicateName, refused.Error);
    }

    [Fact]
    public void ApplyingAnUnknownPresetReportsFailure()
    {
        var (mixer, _, _) = Build();
        Assert.False(mixer.ApplyPreset("nope"));
        Assert.False(mixer.DeletePreset("nope"));
    }

    [Fact]
    public void SavingWithAnExistingIdOverwritesInPlace()
    {
        var (mixer, _, store) = Build(Strip("game", 0.7));
        var first = mixer.SavePreset(new SaveAudioMixerPresetBody { Name = "Gaming" }).Preset!;

        mixer.SavePreset(new SaveAudioMixerPresetBody { Id = first.Id, Name = "Gaming v2" });

        var presets = store.Load().AudioMixer.Presets;
        Assert.Single(presets);
        Assert.Equal("Gaming v2", presets[0].Name);
    }

    [Fact]
    public void ClearingLevelsDropsTheMemoryWithoutTouchingLiveStrips()
    {
        var (mixer, sessions, store) = Build(Strip("game", 1.0));
        mixer.SetVolume("game", 0.25, commit: true);
        sessions.VolumeWrites.Clear();

        mixer.ClearLevels();

        Assert.Empty(store.Load().AudioMixer.Levels);
        Assert.Empty(sessions.VolumeWrites);
    }

    [Fact]
    public void StateReportsWhetherAReaderIsAttached()
    {
        var (mixer, sessions, _) = Build(Strip("game", 1.0));
        Assert.True(mixer.GetState().Supported);

        sessions.Supported = false;
        Assert.False(mixer.GetState().Supported);
    }

    [Fact]
    public void StateHidesAStripThatIsNotOnTheDefaultOutput()
    {
        var onDefault = Strip("game", 0.7);
        var offDefault = Strip("recorder", 0.5);
        offDefault.OnDefault = false;
        var (mixer, _, _) = Build(onDefault, offDefault);

        var state = mixer.GetState();

        Assert.Equal("game", Assert.Single(state.Sessions).Id);
    }

    [Fact]
    public void StickyLevelsIgnoreAStripThatIsNotOnTheDefaultOutput()
    {
        var (mixer, sessions, _) = Build(Strip("recorder", 1.0));
        mixer.SetVolume("recorder", 0.25, commit: true);
        mixer.SetSticky(true);
        sessions.VolumeWrites.Clear();

        // The recorder app relaunches rendering to a non-default output; the
        // default-output-filtered presence tracking must never reapply its
        // remembered level for a strip it never considered present.
        var offDefault = Strip("recorder", 1.0);
        offDefault.OnDefault = false;
        sessions.Replace();
        sessions.Replace(offDefault);

        Assert.DoesNotContain(("recorder", 0.25), sessions.VolumeWrites);
    }

    private static AudioSessionDto Strip(string id, double volume, string? name = null) => new()
    {
        Id = id,
        Name = name ?? id,
        Volume = volume,
        Active = true,
    };

    private static (AudioMixerService Mixer, FakeSessions Sessions, InMemoryConfigStore Store) Build(
        params AudioSessionDto[] sessions)
    {
        var fake = new FakeSessions();
        var store = new InMemoryConfigStore();
        var mixer = new AudioMixerService(fake, new FakeVolume(), new FakeDevices(), store, new MultiplexHub());
        // Prime presence with one snapshot, so tests start from the steady state
        // rather than from service boot. FirstSnapshotRestoresAlreadyRunningApps
        // covers the boot edge on its own.
        fake.Replace(sessions);
        fake.VolumeWrites.Clear();
        fake.MuteWrites.Clear();
        return (mixer, fake, store);
    }

    private sealed class FakeSessions : IAudioSessionProvider
    {
        private List<AudioSessionDto> _sessions = new();

        public bool Supported { get; set; } = true;
        public List<(string Id, double Volume)> VolumeWrites { get; } = new();
        public List<(string Id, bool Muted)> MuteWrites { get; } = new();

        public event Action? SessionsChanged;

        /// <summary>Stands in for a helper snapshot arriving.</summary>
        public void Replace(params AudioSessionDto[] sessions)
        {
            _sessions = new List<AudioSessionDto>(sessions);
            SessionsChanged?.Invoke();
        }

        public IReadOnlyList<AudioSessionDto> GetSessions() => _sessions;
        public void SetVolume(string id, double volume) => VolumeWrites.Add((id, volume));
        public void SetMuted(string id, bool muted) => MuteWrites.Add((id, muted));
        public void StartStreaming() { }
        public void StopStreaming() { }
    }

    private sealed class FakeDevices : IAudioDeviceProvider
    {
        public string DefaultOutput { get; private set; } = "out-a";
        public string DefaultInput { get; private set; } = "in-a";

        public AudioDeviceList ListDevices() => new()
        {
            Outputs =
            {
                new AudioDevice { Id = "out-a", Name = "Headset", IsDefault = DefaultOutput == "out-a", Direction = "output" },
                new AudioDevice { Id = "out-b", Name = "Speakers", IsDefault = DefaultOutput == "out-b", Direction = "output" },
            },
            Inputs =
            {
                new AudioDevice { Id = "in-a", Name = "Mic", IsDefault = DefaultInput == "in-a", Direction = "input" },
            },
        };

        public bool SetDefaultOutput(string deviceId) { DefaultOutput = deviceId; return true; }
        public bool SetDefaultInput(string deviceId) { DefaultInput = deviceId; return true; }
        public bool SetSpatial(string deviceId, string formatId) => false;
    }

    private sealed class FakeVolume : IVolumeProvider
    {
        private double _volume = 0.5;

        public VolumeState GetState() => new() { Supported = true, Volume = _volume };
        public void SetVolume(double volume) => _volume = volume;
        public void SetMuted(bool muted) { }
    }

    private sealed class InMemoryConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();

        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }
}
