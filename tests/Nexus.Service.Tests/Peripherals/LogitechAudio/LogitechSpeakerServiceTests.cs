using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.LogitechAudio;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Peripherals.LogitechAudio;

public class LogitechSpeakerServiceTests
{
    private const string EndpointId = "{0.0.0.00000000}.{0a97e697-f9f4-4c5f-bf45-682666fe96b2}";

    private sealed class FakeApo : ILogitechSpeakerApo
    {
        public List<LogitechSpeakerEndpoint> Endpoints { get; } = new() { new(EndpointId, "Logitech G560 Gaming Speaker", "G560", true) };
        public int? SelectedEq { get; set; } = 1;
        public float[]? Bands { get; set; } = { 4, 3, 2, 0, -4, -3, -3, 1, 2, 4 };
        public int? SelectedSurround { get; set; } = 2;
        public bool? DtsOn { get; set; } = false;
        public int? DtsRoom { get; set; } = 1;
        public float[]? Gains { get; set; }
        public List<LogitechSpeakerApoChange> Writes { get; } = new();
        public float Volume { get; set; } = 0.7f;
        public List<int> Tests { get; } = new();

        public IReadOnlyList<LogitechSpeakerEndpoint> FindEndpoints() => Endpoints;

        public LogitechSpeakerApoValues Read(string endpointId) => new()
        {
            SelectedEq = SelectedEq,
            AdvancedBands = Bands?.ToArray(),
            SelectedSurround = SelectedSurround,
            DtsOn = DtsOn,
            DtsRoom = DtsRoom,
            ChannelGains = Gains?.ToArray(),
        };

        public bool Write(string endpointId, LogitechSpeakerApoChange change)
        {
            Assert.Equal(EndpointId, endpointId);
            Writes.Add(change);
            SelectedEq = change.SelectedEq ?? SelectedEq;
            Bands = change.AdvancedBands ?? Bands;
            SelectedSurround = change.SelectedSurround ?? SelectedSurround;
            DtsOn = change.DtsOn ?? DtsOn;
            DtsRoom = change.DtsRoom ?? DtsRoom;
            Gains = change.ChannelGains ?? Gains;
            return true;
        }

        public float? GetVolume(string endpointId) => Volume;

        public bool SetVolume(string endpointId, float level)
        {
            Volume = level;
            return true;
        }

        public bool PlayTestSound(string endpointId, int channel)
        {
            Tests.Add(channel);
            return true;
        }
    }

    private sealed class FakeBass : ILogitechSpeakerBass
    {
        public bool Present { get; set; } = true;
        public int Generation { get; set; } = 1;
        public int Level { get; set; } = 85;
        public List<int> Sets { get; } = new();

        public bool IsPresent() => Present;
        public int? GetLevel() => Present ? Level : null;

        public bool SetLevel(int level)
        {
            Sets.Add(level);
            Level = level;
            return Present;
        }
    }

    private sealed class Store : IConfigStore
    {
        public NexusSettings Settings { get; } = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => Settings;
        public void Update(Action<NexusSettings> mutator) { mutator(Settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    private readonly FakeApo _apo = new();
    private readonly FakeBass _bass = new();
    private readonly Store _store = new();

    private LogitechSpeakerService Service() => new(_apo, _bass, _store, TimeProvider.System);

    [Fact]
    public void PresentModel_IsTheSpeakerWithTheEffect_OrNull()
    {
        Assert.Equal("G560", Service().PresentModel());

        _apo.Endpoints.Clear();
        Assert.Null(new LogitechSpeakerService(_apo, _bass, _store, TimeProvider.System).PresentModel());
    }

    [Fact]
    public void Handler_ListsTheSpeakerUnderItsModel_WithAPageAndNoSwitch()
    {
        IDeviceHandler handler = new LogitechSpeakerHandler(Service());

        Assert.Equal("logitech-speaker", handler.Id);
        Assert.Equal("Logitech G560", handler.Name);
        Assert.Equal("speaker", handler.Category);
        Assert.Empty(handler.Identifiers);
        Assert.True(handler.IsConnected(Array.Empty<UsbDeviceEntry>()));
        Assert.True(handler.HasPage);
        Assert.False(handler.SupportsNexusControl);
    }

    [Fact]
    public void Handler_IsAbsent_WithoutTheEffect_OrWithoutTheService()
    {
        _apo.Endpoints.Clear();
        Assert.False(new LogitechSpeakerHandler(Service()).IsConnected(Array.Empty<UsbDeviceEntry>()));

        IDeviceHandler elsewhere = new LogitechSpeakerHandler();
        Assert.False(elsewhere.IsConnected(Array.Empty<UsbDeviceEntry>()));
        Assert.Equal("Logitech G560", elsewhere.Name);
    }

    [Fact]
    public void State_ReadsTheEffectAndTheSpeaker()
    {
        _apo.DtsOn = true;
        _apo.DtsRoom = 2;

        var state = Service().GetState();

        Assert.True(state.Present);
        Assert.Equal("G560", state.Model);
        Assert.True(state.EqEnabled);
        Assert.Equal(new[] { 4f, 3f, 2f, 0f, -4f, -3f, -3f, 1f, 2f, 4f }, state.Bands);
        Assert.True(state.SurroundEnabled);
        Assert.Equal("movie", state.Room);
        Assert.Equal(Enumerable.Repeat(1f, 8), state.ChannelGains);
        Assert.Equal(85, state.SubwooferLevel);
        Assert.Equal(0.7f, state.Volume);
        Assert.Contains(state.Presets, p => p.Name == "Bass Boost" && p.BuiltIn);
    }

    [Fact]
    public void State_WithoutASpeaker_IsNotPresent()
    {
        _apo.Endpoints.Clear();

        var state = Service().GetState();

        Assert.False(state.Present);
        Assert.Equal(10, state.Bands.Count);
        Assert.Empty(_apo.Writes);
    }

    [Fact]
    public void Preset_WritesItsBandsTurnsTheEqOnAndIsRemembered()
    {
        _apo.SelectedEq = 0;
        var service = Service();

        Assert.True(service.SetEq(null, null, "bass boost"));

        var write = Assert.Single(_apo.Writes);
        Assert.Equal(LogitechSpeakerProtocol.EqAdvanced, write.SelectedEq);
        Assert.Equal(LogitechSpeakerProtocol.BuiltInPresets.First(p => p.Name == "Bass Boost").Bands, write.AdvancedBands);
        Assert.Equal("bass boost", _store.Settings.LogitechSpeaker.EqPreset);
    }

    [Fact]
    public void BandsMovedByHand_ClearThePresetName()
    {
        _store.Settings.LogitechSpeaker.EqPreset = "Flat";

        Assert.True(Service().SetEq(null, new float[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, null));

        Assert.Null(_store.Settings.LogitechSpeaker.EqPreset);
    }

    [Fact]
    public void TurningTheEqOff_LeavesTheBandsAlone()
    {
        Assert.True(Service().SetEq(false, null, null));

        var write = Assert.Single(_apo.Writes);
        Assert.Equal(LogitechSpeakerProtocol.EqOff, write.SelectedEq);
        Assert.Null(write.AdvancedBands);
    }

    [Fact]
    public void UnknownPresetOrWrongBandCount_ChangeNothing()
    {
        var service = Service();

        Assert.False(service.SetEq(null, null, "nope"));
        Assert.False(service.SetEq(null, new float[] { 1, 2, 3 }, null));
        Assert.Empty(_apo.Writes);
    }

    [Fact]
    public void Surround_TurnsDtsOnInItsRoom()
    {
        var service = Service();

        Assert.True(service.SetSurround(true, "movie"));
        Assert.Equal(LogitechSpeakerProtocol.SurroundDts, _apo.SelectedSurround);
        Assert.True(_apo.DtsOn);
        Assert.Equal(LogitechSpeakerProtocol.Rooms.Movie, _apo.DtsRoom);

        Assert.True(service.SetSurround(false, null));
        Assert.False(_apo.DtsOn);
        Assert.Null(_apo.Writes[^1].SelectedSurround);

        Assert.False(service.SetSurround(null, "concert"));
    }

    [Fact]
    public void ChannelGain_ChangesOneChannel_AndResetPutsAllBack()
    {
        var service = Service();

        Assert.True(service.SetChannelGain(3, 1.5f));
        Assert.Equal(new[] { 1f, 1f, 1f, 1.5f, 1f, 1f, 1f, 1f }, _apo.Gains);

        Assert.True(service.SetChannelGain(0, 9f));
        Assert.Equal(2f, _apo.Gains![0]);

        Assert.True(service.SetChannelGain(null, 0f));
        Assert.Equal(Enumerable.Repeat(1f, 8), _apo.Gains);

        Assert.False(service.SetChannelGain(8, 1f));
    }

    [Fact]
    public void Subwoofer_IsRememberedAndSentAgainAfterTheSpeakerReconnects()
    {
        var service = Service();
        Assert.True(service.SetSubwoofer(60));
        Assert.Equal(60, _store.Settings.LogitechSpeaker.SubwooferLevel);
        Assert.Equal(new[] { 60 }, _bass.Sets);

        // Same connection: nothing to send again.
        service.Tick();
        Assert.Equal(new[] { 60 }, _bass.Sets);

        _bass.Generation++;
        _bass.Level = 70;
        service.Tick();
        service.Tick();
        Assert.Equal(new[] { 60, 60 }, _bass.Sets);
    }

    [Fact]
    public void Subwoofer_IsAskedOncePerConnection()
    {
        var service = Service();
        Assert.Equal(85, service.GetState().SubwooferLevel);

        _bass.Level = 40; // changed on the speaker's side, unseen until it reconnects
        Assert.Equal(85, service.GetState().SubwooferLevel);

        _bass.Generation++;
        Assert.Equal(40, service.GetState().SubwooferLevel);
    }

    [Fact]
    public void Subwoofer_NeverSetHere_IsLeftAlone()
    {
        var service = Service();
        service.Tick();
        _bass.Generation++;
        service.Tick();

        Assert.Empty(_bass.Sets);
    }

    [Fact]
    public void Subwoofer_WithoutTheSpeaker_IsNotSaved()
    {
        _bass.Present = false;

        Assert.False(Service().SetSubwoofer(50));
        Assert.Null(_store.Settings.LogitechSpeaker.SubwooferLevel);
    }

    [Fact]
    public void Presets_SaveReplaceAndDelete()
    {
        var service = Service();
        var bands = new float[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        Assert.False(service.SavePreset("Flat", bands));
        Assert.False(service.SavePreset("  ", bands));
        Assert.True(service.SavePreset(" Mine ", bands));
        Assert.True(service.SavePreset("mine", new float[10]));

        var mine = Assert.Single(_store.Settings.LogitechSpeaker.CustomPresets);
        Assert.Equal("mine", mine.Name);
        Assert.True(service.SetEq(null, null, "MINE"));
        Assert.Equal("MINE", _store.Settings.LogitechSpeaker.EqPreset);

        Assert.True(service.DeletePreset("Mine"));
        Assert.Empty(_store.Settings.LogitechSpeaker.CustomPresets);
        Assert.Null(_store.Settings.LogitechSpeaker.EqPreset);
        Assert.False(service.DeletePreset("Mine"));
    }

    [Fact]
    public void SavedPreset_IsClampedToTheSliderRange()
    {
        Assert.True(Service().SavePreset("Loud", new float[] { 12, 0, 0, 0, 0, 0, 0, 0, 0, -40 }));

        var bands = _store.Settings.LogitechSpeaker.CustomPresets[0].Bands;
        Assert.Equal(6f, bands[0]);
        Assert.Equal(-20f, bands[9]);
    }

    [Fact]
    public void VolumeAndTestSound_GoToTheSpeakersOutput()
    {
        var service = Service();

        Assert.True(service.SetVolume(1.4f));
        Assert.Equal(1f, _apo.Volume);
        Assert.True(service.TestChannel(2));
        Assert.Equal(new[] { 2 }, _apo.Tests);
    }
}
