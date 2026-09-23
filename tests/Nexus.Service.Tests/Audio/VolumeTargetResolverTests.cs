using System;
using System.Collections.Generic;
using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;
using Xunit;

namespace Nexus.Service.Tests.Audio;

/// <summary>
/// Covers every mode (auto/app/output) and their fallbacks against fake
/// providers - the resolver itself is pure branching over
/// IVolumeProvider/IAudioDeviceProvider/IAudioSessionProvider.
/// </summary>
public class VolumeTargetResolverTests
{
    [Fact]
    public void OutputMode_UsesTheNamedDeviceWhenPresent()
    {
        var resolver = Build(devices: Devices());

        var state = resolver.Resolve("output", "", "out-b");

        Assert.Equal("output", state.Kind);
        Assert.Equal("out-b", state.Id);
        Assert.Equal("Speakers", state.Name);
    }

    [Fact]
    public void OutputMode_EmptyDeviceIdFallsBackToDefault()
    {
        var resolver = Build(devices: Devices());

        var state = resolver.Resolve("output", "", "");

        Assert.Equal("out-a", state.Id);
        Assert.Equal("Headset", state.Name);
    }

    [Fact]
    public void OutputMode_UnknownDeviceIdFallsBackToDefault()
    {
        var resolver = Build(devices: Devices());

        var state = resolver.Resolve("output", "", "not-a-real-device");

        Assert.Equal("out-a", state.Id);
    }

    [Fact]
    public void OutputMode_NoDefaultReportsEmptyIdAndName()
    {
        var resolver = Build(devices: new AudioDeviceList());

        var state = resolver.Resolve("output", "", "");

        Assert.Equal("", state.Id);
        Assert.Equal("", state.Name);
    }

    [Fact]
    public void AppMode_MatchedStripReportsItsOwnLevel()
    {
        var strip = new AudioSessionDto { Id = "spotify", Name = "Spotify", Volume = 0.4, Muted = true };
        var resolver = Build(devices: Devices(), sessions: strip);

        var state = resolver.Resolve("app", "Spotify", "");

        Assert.Equal("app", state.Kind);
        Assert.Equal("spotify", state.Id);
        Assert.Equal("Spotify", state.Name);
        Assert.Equal(0.4, state.Volume);
        Assert.True(state.Muted);
    }

    [Fact]
    public void AppMode_NoMatchFallsThroughToAuto()
    {
        var resolver = Build(devices: Devices());

        var state = resolver.Resolve("app", "Nonexistent", "");

        Assert.Equal("output", state.Kind);
        Assert.Equal("out-a", state.Id);
    }

    [Fact]
    public void AutoMode_NoSourceResolvesTheDefaultOutput()
    {
        var resolver = Build(devices: Devices());

        var state = resolver.Resolve("auto", "", "");

        Assert.Equal("output", state.Kind);
        Assert.Equal("out-a", state.Id);
    }

    [Fact]
    public void AutoMode_StripOnTheDefaultEndpointResolvesTheDefaultOutput()
    {
        var strip = new AudioSessionDto { Id = "spotify", Name = "Spotify", OnDefault = true, DeviceIds = { "out-b" } };
        var resolver = Build(devices: Devices(), sessions: strip);

        var state = resolver.Resolve("auto", "Spotify", "");

        Assert.Equal("output", state.Kind);
        Assert.Equal("out-a", state.Id);
    }

    [Fact]
    public void AutoMode_StripOffDefaultResolvesTheFirstMatchingEndpoint()
    {
        var strip = new AudioSessionDto { Id = "spotify", Name = "Spotify", OnDefault = false, DeviceIds = { "out-b" } };
        var resolver = Build(devices: Devices(), sessions: strip);

        var state = resolver.Resolve("auto", "Spotify", "");

        Assert.Equal("output", state.Kind);
        Assert.Equal("out-b", state.Id);
        Assert.Equal("Speakers", state.Name);
    }

    [Fact]
    public void AutoMode_StripOffDefaultWithNoListedEndpointFallsBackToDefault()
    {
        var strip = new AudioSessionDto { Id = "spotify", Name = "Spotify", OnDefault = false, DeviceIds = { "unplugged-device" } };
        var resolver = Build(devices: Devices(), sessions: strip);

        var state = resolver.Resolve("auto", "Spotify", "");

        Assert.Equal("out-a", state.Id);
    }

    [Fact]
    public void UnknownModeBehavesLikeAuto()
    {
        var resolver = Build(devices: Devices());

        var state = resolver.Resolve("bogus", "", "");

        Assert.Equal("output", state.Kind);
        Assert.Equal("out-a", state.Id);
    }

    [Fact]
    public void ResolvingTheDefaultOutputByItsExplicitIdStillQueriesItAsDefault()
    {
        // A transient read failure on the default output must degrade the
        // same way whether it was reached by an empty id (auto/no source) or
        // by its own explicit id (output mode naming the current default) -
        // never as "unsupported" in one path and "supported" in the other.
        var volume = new RecordingFakeVolume();
        var resolver = new VolumeTargetResolver(volume, new FakeDevices(Devices()), new FakeSessions(Array.Empty<AudioSessionDto>()));

        resolver.Resolve("output", "", "out-a");

        Assert.Equal("", volume.LastQueriedDeviceId);
    }

    private sealed class RecordingFakeVolume : IVolumeProvider
    {
        public string LastQueriedDeviceId { get; private set; } = "unset";
        public VolumeState GetState() => GetState("");
        public VolumeState GetState(string deviceId)
        {
            LastQueriedDeviceId = deviceId;
            return new VolumeState { Supported = true, Volume = 0.5, Muted = false };
        }
        public void SetVolume(double volume) { }
        public void SetVolume(string deviceId, double volume) { }
        public void SetMuted(bool muted) { }
        public void SetMuted(string deviceId, bool muted) { }
    }

    private static AudioDeviceList Devices() => new()
    {
        Outputs =
        {
            new AudioDevice { Id = "out-a", Name = "Headset", IsDefault = true, Direction = "output" },
            new AudioDevice { Id = "out-b", Name = "Speakers", IsDefault = false, Direction = "output" },
        },
    };

    private static VolumeTargetResolver Build(AudioDeviceList devices, params AudioSessionDto[] sessions) =>
        new(new FakeVolume(), new FakeDevices(devices), new FakeSessions(sessions));

    private sealed class FakeVolume : IVolumeProvider
    {
        public VolumeState GetState() => GetState("");
        public VolumeState GetState(string deviceId) => new() { Supported = true, Volume = 0.5, Muted = false };
        public void SetVolume(double volume) { }
        public void SetVolume(string deviceId, double volume) { }
        public void SetMuted(bool muted) { }
        public void SetMuted(string deviceId, bool muted) { }
    }

    private sealed class FakeDevices : IAudioDeviceProvider
    {
        private readonly AudioDeviceList _list;
        public FakeDevices(AudioDeviceList list) => _list = list;
        public AudioDeviceList ListDevices() => _list;
        public bool SetDefaultOutput(string deviceId) => true;
        public bool SetDefaultInput(string deviceId) => true;
        public bool SetSpatial(string deviceId, string formatId) => false;
    }

    private sealed class FakeSessions : IAudioSessionProvider
    {
        private readonly List<AudioSessionDto> _sessions;
        public FakeSessions(IEnumerable<AudioSessionDto> sessions) => _sessions = new List<AudioSessionDto>(sessions);
        public bool Supported => true;
        public IReadOnlyList<AudioSessionDto> GetSessions() => _sessions;
        public void SetVolume(string id, double volume) { }
        public void SetMuted(string id, bool muted) { }
        public void StartStreaming() { }
        public void StopStreaming() { }
#pragma warning disable CS0067 // never raised: this fake has no state to change
        public event Action? SessionsChanged;
#pragma warning restore CS0067
    }
}
