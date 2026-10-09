using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Audio;
using Nexus.Service.Deck;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Displays;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

public sealed class DeckDialValueServiceTests
{
    // ── LatestValueWriter ──

    [Fact]
    public async Task Writer_KeepsOnlyTheLatestValueQueuedBehindASlowWrite()
    {
        var calls = new List<double>();
        var gate = new TaskCompletionSource();
        var first = new TaskCompletionSource();
        var writer = new LatestValueWriter(async value =>
        {
            lock (calls) { calls.Add(value); }
            first.TrySetResult();
            await gate.Task;
        });

        writer.Submit(1);
        await first.Task;
        for (var v = 2; v <= 50; v++)
        {
            writer.Submit(v);
        }
        gate.SetResult();

        SpinWait.SpinUntil(() => { lock (calls) { return calls.Count >= 2; } }, TimeSpan.FromSeconds(3));
        lock (calls)
        {
            Assert.Equal(new double[] { 1, 50 }, calls);
        }
    }

    [Fact]
    public async Task Writer_AFailingWriteDoesNotStopLaterOnes()
    {
        var calls = 0;
        var done = new TaskCompletionSource();
        var writer = new LatestValueWriter(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("boom");
            }
            done.TrySetResult();
            return Task.CompletedTask;
        });

        writer.Submit(1);
        SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 1, TimeSpan.FromSeconds(3));
        writer.Submit(2);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    // ── Service over fakes ──

    private sealed class FakeVolume : IVolumeProvider
    {
        public readonly Dictionary<string, VolumeState> States = new() { [""] = new VolumeState { Supported = true, Volume = 0.4 } };
        public readonly List<(string Device, double Volume)> Sets = new();
        public readonly List<(string Device, bool Muted)> Mutes = new();

        public VolumeState GetState() => GetState("");
        public VolumeState GetState(string deviceId) => States.TryGetValue(deviceId, out var s) ? s : new VolumeState { Supported = false };
        public void SetVolume(double volume) => SetVolume("", volume);
        public void SetVolume(string deviceId, double volume)
        {
            lock (Sets) { Sets.Add((deviceId, volume)); }
        }
        public void SetMuted(bool muted) => SetMuted("", muted);
        public void SetMuted(string deviceId, bool muted)
        {
            lock (Mutes) { Mutes.Add((deviceId, muted)); }
        }
    }

    private sealed class FakeDevices : IAudioDeviceProvider
    {
        public int Lists;
        public AudioDeviceList ListDevices()
        {
            Lists++;
            return new AudioDeviceList { Inputs = { new AudioDevice { Id = "mic-default", IsDefault = true, Direction = "input" } } };
        }
        public bool SetDefaultOutput(string deviceId) => true;
        public bool SetDefaultInput(string deviceId) => true;
        public bool SetSpatial(string deviceId, string formatId) => false;
    }

    private sealed class FakeSessions : IAudioSessionProvider
    {
        public bool Supported { get; set; } = true;
        public List<AudioSessionDto> Sessions { get; } = new();
        public List<(string Id, double Volume)> VolumeWrites { get; } = new();
        public List<(string Id, bool Muted)> MuteWrites { get; } = new();
        public event Action? SessionsChanged { add { } remove { } }
        public IReadOnlyList<AudioSessionDto> GetSessions() => Sessions;
        public void SetVolume(string id, double volume) => VolumeWrites.Add((id, volume));
        public void SetMuted(string id, bool muted) => MuteWrites.Add((id, muted));
        public void StartStreaming() { }
        public void StopStreaming() { }
    }

    private sealed class FakeDisplays : IDisplayBrightnessProvider
    {
        public readonly Dictionary<string, int> Levels = new() { ["d1"] = 70 };
        public readonly List<(string Id, int Percent)> Writes = new();
        public string Hint => "";
        public IReadOnlyList<DisplayDto> Enumerate(IReadOnlyCollection<string>? excludedIds = null) => Array.Empty<DisplayDto>();
        public int? GetBrightness(string id) => Levels.TryGetValue(id, out var v) ? v : null;
        public DisplayBrightnessDto SetBrightness(string id, int percent)
        {
            lock (Writes) { Writes.Add((id, percent)); }
            return new DisplayBrightnessDto { Id = id, Brightness = percent, RequestedBrightness = percent, AppliedBrightness = percent, Status = DisplayBrightnessWriteStatuses.Applied };
        }
        public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id) => new()
        {
            ControlPath = DisplayBrightnessControlPaths.DdcCi,
            WriteMode = DisplayBrightnessWriteModes.Coalesced,
            MinWriteIntervalMs = 0,
        };
        public DisplayVcpDto? GetVcp(string id, byte code) => null;
        public bool SetVcp(string id, byte code, int value) => false;
        public string? FindDisplayIdByHardwareName(IReadOnlyList<string> nameFragments) => null;
    }

    private sealed class FakeY70 : IY70Provider
    {
        public bool Connected = true;
        public int Brightness = 30;
        public bool IsConnected() => Connected;
        public string GetOrientation() => "";
        public void SetOrientation(string orientation) { }
        public bool GetForceOrientation() => false;
        public void SetForceOrientation(bool forceOrientation) { }
        public void ApplyEffectiveOrientation() { }
        public int GetBrightness() => Brightness;
        public void SetBrightness(int brightness) => Brightness = brightness;
        public bool GetToggle() => false;
        public void SetToggle(bool toggle) { }
        public void SetGameModeScreenOff(bool screenOff) { }
        public bool IsRotated() => false;
    }

    private sealed class Rig
    {
        public readonly FakeVolume Volume = new();
        public readonly FakeDevices Devices = new();
        public readonly FakeSessions Sessions = new();
        public readonly FakeDisplays Displays = new();
        public readonly FakeY70 Y70 = new();
        public readonly InMemoryConfigStore Store = new();
        public readonly DeckDialValueService Service;

        public Rig()
        {
            var hub = new MultiplexHub();
            Service = new DeckDialValueService(
                Volume, Devices, new AudioMixerService(Sessions, Volume, Devices, Store, hub),
                new DisplayBrightnessController(Displays), Store, Y70, hub);
        }

        public static DeckDialAction Action(string type, string? device = null, string? app = null, string? display = null) =>
            new() { Type = type, DeviceId = device, AppId = app, DisplayId = display };

        public bool WaitFor(Func<bool> condition) => SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Volume_ReadsTheDefaultOutput_AndWritesOptimistically()
    {
        var rig = new Rig();
        var action = Rig.Action("volume");

        var reading = rig.Service.Read(action);
        Assert.True(reading.Supported);
        Assert.Equal(40, reading.Percent, 3);

        rig.Service.Write(action, 55);
        Assert.Equal(55, rig.Service.Read(action).Percent);
        Assert.True(rig.WaitFor(() => { lock (rig.Volume.Sets) { return rig.Volume.Sets.Count > 0; } }));
        Assert.Equal(("", 0.55), rig.Volume.Sets.Last());
    }

    [Fact]
    public void Volume_WithADeviceId_UsesThePerDeviceOverloads()
    {
        var rig = new Rig();
        rig.Volume.States["out-b"] = new VolumeState { Supported = true, Volume = 0.9, Muted = true };
        var action = Rig.Action("volume", device: "out-b");

        var reading = rig.Service.Read(action);
        Assert.Equal(90, reading.Percent, 3);
        Assert.True(reading.Muted);

        rig.Service.SetMuted(action, false);
        Assert.True(rig.WaitFor(() => { lock (rig.Volume.Mutes) { return rig.Volume.Mutes.Count > 0; } }));
        Assert.Equal(("out-b", false), rig.Volume.Mutes.Single());
        Assert.False(rig.Service.Read(action).Muted);
    }

    [Fact]
    public void Volume_UnknownDevice_IsUnsupported()
    {
        var rig = new Rig();

        Assert.False(rig.Service.Read(Rig.Action("volume", device: "gone")).Supported);
    }

    [Fact]
    public void MicVolume_IsWindowsOnly_ElsewhereItReadsUnsupported()
    {
        var rig = new Rig();
        rig.Volume.States["mic-default"] = new VolumeState { Supported = true, Volume = 0.25 };

        var reading = rig.Service.Read(Rig.Action("micVolume"));

        if (OperatingSystem.IsWindows())
        {
            Assert.True(reading.Supported);
            Assert.Equal(25, reading.Percent, 3);
        }
        else
        {
            Assert.False(reading.Supported);
            Assert.Equal(0, rig.Devices.Lists);
        }
    }

    [Fact]
    public void AppVolume_ReadsAndWritesTheMixerSession()
    {
        var rig = new Rig();
        rig.Sessions.Sessions.Add(new AudioSessionDto { Id = "spotify", Name = "Spotify", Volume = 0.6, Muted = false, Active = true });
        var action = Rig.Action("appVolume", app: "spotify");

        Assert.Equal(60, rig.Service.Read(action).Percent, 3);
        rig.Service.Write(action, 20);
        Assert.True(rig.WaitFor(() => rig.Sessions.VolumeWrites.Count > 0));
        Assert.Equal(("spotify", 0.2), rig.Sessions.VolumeWrites.Last());

        rig.Service.SetMuted(action, true);
        Assert.True(rig.WaitFor(() => rig.Sessions.MuteWrites.Count > 0));
        Assert.Equal(("spotify", true), rig.Sessions.MuteWrites.Single());
    }

    [Fact]
    public void AppVolume_MissingSession_IsUnsupported()
    {
        var rig = new Rig();

        Assert.False(rig.Service.Read(Rig.Action("appVolume", app: "nope")).Supported);
    }

    [Fact]
    public void DisplayBrightness_ReadsAndWritesThroughTheController()
    {
        var rig = new Rig();
        var action = Rig.Action("displayBrightness", display: "d1");

        Assert.Equal(70, rig.Service.Read(action).Percent);
        rig.Service.Write(action, 35.4);

        Assert.True(rig.WaitFor(() => { lock (rig.Displays.Writes) { return rig.Displays.Writes.Count > 0; } }));
        Assert.Equal(("d1", 35), rig.Displays.Writes.Last());
        Assert.False(rig.Service.Read(Rig.Action("displayBrightness", display: "gone")).Supported);
    }

    [Fact]
    public void LightingBrightness_ReadsAndPersistsGlobalBrightness()
    {
        var rig = new Rig();
        rig.Store.Update(s => s.Lighting.GlobalBrightness = 0.5f);
        var action = Rig.Action("lightingBrightness");

        Assert.Equal(50, rig.Service.Read(action).Percent, 3);
        rig.Service.Write(action, 80);

        Assert.True(rig.WaitFor(() => Math.Abs(rig.Store.Load().Lighting.GlobalBrightness - 0.8f) < 0.001f));
    }

    [Fact]
    public void Y70Brightness_IsUnsupportedWhileTheDisplayIsAbsent()
    {
        var rig = new Rig();
        var action = Rig.Action("y70Brightness");

        Assert.Equal(30, rig.Service.Read(action).Percent);
        rig.Service.Write(action, 64);
        Assert.True(rig.WaitFor(() => rig.Y70.Brightness == 64));

        rig.Y70.Connected = false;
        Assert.False(new DeckDialValueService(rig.Volume, rig.Devices, new AudioMixerService(rig.Sessions, rig.Volume, rig.Devices, rig.Store, new MultiplexHub()),
            new DisplayBrightnessController(rig.Displays), rig.Store, rig.Y70, new MultiplexHub()).Read(action).Supported);
    }

    [Fact]
    public void NonValueTypes_ReadUnsupported_AndIgnoreWrites()
    {
        var rig = new Rig();

        Assert.False(rig.Service.Read(Rig.Action("page")).Supported);
        Assert.False(rig.Service.Read(Rig.Action("deckBrightness")).Supported);
        rig.Service.Write(Rig.Action("custom"), 10);
        rig.Service.SetMuted(Rig.Action("displayBrightness", display: "d1"), true);
        Assert.Empty(rig.Volume.Sets);
    }

    [Fact]
    public void TwoDialsOnOneTarget_ShareTheOptimisticValue()
    {
        var rig = new Rig();
        rig.Service.Read(Rig.Action("volume"));

        rig.Service.Write(Rig.Action("volume"), 77);

        Assert.Equal(77, rig.Service.Read(Rig.Action("volume")).Percent);
    }

    [Fact]
    public void DialTypes_GroupTheBehaviourTable()
    {
        Assert.All(new[] { "volume", "micVolume", "appVolume" }, t => Assert.True(DeckDialTypes.IsMuteType(t)));
        Assert.All(new[] { "displayBrightness", "lightingBrightness", "y70Brightness" }, t => Assert.True(DeckDialTypes.IsZeroToggleType(t)));
        Assert.False(DeckDialTypes.IsServiceValueType("deckBrightness"));
        Assert.True(DeckDialTypes.IsValueType("deckBrightness"));
        Assert.False(DeckDialTypes.IsValueType("page"));
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
