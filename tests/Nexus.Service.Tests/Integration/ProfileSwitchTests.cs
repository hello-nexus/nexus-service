using System.Collections.Concurrent;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// End-to-end coverage of the profile-switch &rarr; curve-apply flow. A
/// regression in any of these links looks "fine" in isolation: the profile API
/// returns success, settings.json has the new curves, the frontend shows the
/// new active profile. Meanwhile the curve engine is still driving fans with
/// the old profile's speeds and the user hears no change. That's the silent
/// failure this suite guards against.
///
/// What's exercised:
///   1. <see cref="JsonConfigStore"/> swap of the active profile's settings
///      into the cached in-memory doc via <see cref="ProfileManager.SwitchProfile"/>
///   2. <see cref="CurveEngine.Tick"/> reading the new curves from
///      <see cref="IConfigStore"/> on the next tick
///   3. <see cref="IFanControlProvider.SetFanSpeed"/> being called with the
///      duty derived from the NEW profile's curve, not the old one
///
/// Runs against real <see cref="ProfileManager"/> + <see cref="JsonConfigStore"/>
/// against a temp directory; the fan provider is an in-memory recorder so we
/// can assert on the duty writes without any hardware.
/// </summary>
public class ProfileSwitchTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;

    public ProfileSwitchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-profile-switch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        _store = new JsonConfigStore(settingsPath);
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // Minimal recording fan provider. Captures every hardware write so tests
    // can assert which duty cycles were actually pushed, regardless of whether
    // the caller was a user-intent SetFanSpeed or the engine's DriveFanSpeed.
    private sealed class RecordingFanProvider : IFanControlProvider
    {
        public readonly ConcurrentQueue<(string ChannelId, int DutyPercent)> Writes = new();
        public float Temperature { get; set; } = 60f;

        // CurveEngine only drives channels the provider reports as present,
        // so the fake must surface the fan the test curves target.
        public IReadOnlyList<FanChannel> GetFanChannels() =>
            new[] { new FanChannel { Id = "fan-1", Name = "fan-1" } };
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => Temperature;
        public int SetFanSpeed(string channelId, int dutyPercent)
        {
            Writes.Enqueue((channelId, dutyPercent));
            return dutyPercent;
        }
        public void DriveFanSpeed(string channelId, int dutyPercent) => Writes.Enqueue((channelId, dutyPercent));
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds,
            IProgress<FanCalibrationProgress> progress,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private static List<CurveDocument> MakeCurve(string fanId, string sensorId, int flatSpeed) => new()
    {
        new CurveDocument
        {
            Id = "c-" + flatSpeed,
            Name = "Fan " + flatSpeed,
            Type = "Flat",
            Input = new CurveInputDocument { Id = sensorId, Type = "Temperature", Device = "" },
            Outputs = new List<CurveOutputDocument>
            {
                new() { Id = fanId, Type = "Fan" },
            },
            Flat = new FlatCurveData { Speed = flatSpeed },
        },
    };

    [Fact]
    public void SwitchProfile_ReplacesCurvesInStore_AndNextTickWritesNewDuty()
    {
        // Active profile "Default" with a 30% flat curve.
        var activeId = _profiles.GetActiveEntry()!.Id;
        _store.Update(s => s.Cooling.Curves = MakeCurve("fan-1", "cpu-0", 30));
        _store.FlushNow();
        _profiles.SaveActiveProfile();

        // Second profile "Performance" with a 80% flat curve.
        var perfEntry = _profiles.CreateProfile("Performance");
        _store.Update(s => s.Cooling.Curves = MakeCurve("fan-1", "cpu-0", 80));
        _store.FlushNow();
        _profiles.SaveActiveProfile();

        // Back to Default (CreateProfile left us on Performance).
        _profiles.SwitchProfile(activeId);

        // First tick under the Default profile should write 30.
        var fans = new RecordingFanProvider();
        var engine = new CurveEngine(fans, _store, new MultiplexHub());
        // Production wires ProfileManager.OnProfileSwitched →
        // CurveEngine.ResetSmoothing in Program.cs so the engine's per-channel
        // 250ms rate limiter doesn't suppress the first post-switch write.
        // Replicate that here; otherwise the second Tick() below races the
        // limiter and the new curve never reaches the fan provider.
        _profiles.OnProfileSwitched += engine.ResetSmoothing;
        engine.Tick();

        var firstWrite = Assert.Single(fans.Writes);
        Assert.Equal("fan-1", firstWrite.ChannelId);
        Assert.Equal(30, firstWrite.DutyPercent);

        // Switch to Performance. The next Tick() must reflect the new curves;
        // a switch that doesn't swap in-memory settings would write 30 again.
        _profiles.SwitchProfile(perfEntry.Id);
        engine.Tick();

        Assert.Equal(2, fans.Writes.Count);
        var writes = fans.Writes.ToArray();
        Assert.Equal(30, writes[0].DutyPercent);
        Assert.Equal(80, writes[1].DutyPercent);
    }

    [Fact]
    public void SwitchProfile_FiresOnProfileSwitched_Once_PerSwitch()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        var second = _profiles.CreateProfile("Second");
        _profiles.SwitchProfile(activeId);

        int count = 0;
        _profiles.OnProfileSwitched += () => count++;

        _profiles.SwitchProfile(second.Id);
        Assert.Equal(1, count);

        // No-op switch (already active) must NOT re-fire the event; a
        // regression here would cause every /profiles/{id}/switch for the
        // current profile to reset cooling and lighting state.
        _profiles.SwitchProfile(second.Id);
        Assert.Equal(1, count);

        _profiles.SwitchProfile(activeId);
        Assert.Equal(2, count);
    }

    [Fact]
    public void CreateProfile_PersistsToDisk_AndIsLoadable_AfterDispose()
    {
        var entry = _profiles.CreateProfile("Quiet");
        _store.Update(s => s.Cooling.Curves = MakeCurve("fan-1", "cpu-0", 15));
        _store.FlushNow();
        _profiles.SaveActiveProfile();

        // Simulate a process restart: dispose + rebuild the manager against
        // the same settings dir. The profile + curve set must survive.
        _profiles.Dispose();
        _store.Dispose();

        var store2 = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        var pm2 = new ProfileManager(store2);
        pm2.Initialize();

        var manifest = pm2.GetManifest();
        Assert.Contains(manifest.Profiles, p => p.Id == entry.Id && p.Name == "Quiet");
        pm2.SwitchProfile(entry.Id);
        Assert.Equal(15, store2.Load().Cooling.Curves[0].Flat!.Speed);

        pm2.Dispose();
        store2.Dispose();
    }

    [Fact]
    public void SwitchProfile_UnswapsAQuarterTurnedLayoutSavedBeforeFreeRotation_Once()
    {
        var entry = _profiles.CreateProfile("Legacy");
        var other = _profiles.CreateProfile("Other");
        // Legacy is inactive from here, so nothing writes its file back before
        // the switch reads it.
        _profiles.SwitchProfile(other.Id);
        // Rewrite the profile file the way a build before free rotation did: a
        // 90-degree frame stored as its turned 40x250 footprint, no flag.
        var path = Path.Combine(_tempDir, $"profile-{entry.Id}.json");
        Assert.True(File.Exists(path));
        var doc = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path), Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;
        doc.Lighting.FreeRotationLayouts = false;
        doc.Lighting.DeviceLayouts["strip"] = new DeviceLayout { X = 300, Y = 100, W = 40, H = 250, Rotation = 90 };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(doc, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings));

        _profiles.SwitchProfile(entry.Id);

        var live = _store.Load().Lighting;
        Assert.True(live.FreeRotationLayouts);
        var strip = live.DeviceLayouts["strip"];
        Assert.Equal((195f, 205f, 250f, 40f), (strip.X, strip.Y, strip.W, strip.H));

        // A second round trip through the file keeps the new convention.
        _profiles.SaveActiveProfile();
        _profiles.SwitchProfile(other.Id);
        _profiles.SwitchProfile(entry.Id);
        Assert.Equal(250f, _store.Load().Lighting.DeviceLayouts["strip"].W);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("../settings")]
    [InlineData("x/../../settings")]
    public void ExportProfileJson_RejectsIdsOutsideManifest(string id)
    {
        Assert.Null(_profiles.ExportProfileJson(id));
    }

    [Fact]
    public void SwitchProfile_WithDevicePerProfile_LoadsTargetProfilesStreamDeck()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        // Device defaults to Shared; opt out so each profile's deck can
        // diverge, matching how a user would configure per-profile decks.
        _profiles.SetCategoryShared(ProfileSharing.Device, shared: false);

        _store.Update(s => s.StreamDeck.Decks = new() { ["SN-DEFAULT"] = new PhysicalDeckSettings { Name = "Default Deck" } });
        _store.FlushNow();
        _profiles.SaveActiveProfile();

        var second = _profiles.CreateProfile("Second");
        _store.Update(s => s.StreamDeck.Decks = new() { ["SN-SECOND"] = new PhysicalDeckSettings { Name = "Second Deck" } });
        _store.FlushNow();
        _profiles.SaveActiveProfile();

        _profiles.SwitchProfile(activeId);
        var afterDefault = _store.Load();
        Assert.True(afterDefault.StreamDeck.Decks.ContainsKey("SN-DEFAULT"));
        Assert.False(afterDefault.StreamDeck.Decks.ContainsKey("SN-SECOND"));

        _profiles.SwitchProfile(second.Id);
        var afterSecond = _store.Load();
        Assert.True(afterSecond.StreamDeck.Decks.ContainsKey("SN-SECOND"));
        Assert.False(afterSecond.StreamDeck.Decks.ContainsKey("SN-DEFAULT"));
    }

    [Fact]
    public void SwitchProfile_WithDevicePerProfile_LoadsTargetProfilesKeeb()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        // Device defaults to Shared; opt out so each profile's keeb settings
        // can diverge, matching how a user would configure per-profile keeb
        // personalization.
        _profiles.SetCategoryShared(ProfileSharing.Device, shared: false);

        _store.Update(s => s.Keeb.RotaryLeft = "Volume");
        _store.FlushNow();
        _profiles.SaveActiveProfile();

        var second = _profiles.CreateProfile("Second");
        _store.Update(s => s.Keeb.RotaryLeft = "Scroll");
        _store.FlushNow();
        _profiles.SaveActiveProfile();

        _profiles.SwitchProfile(activeId);
        Assert.Equal("Volume", _store.Load().Keeb.RotaryLeft);

        _profiles.SwitchProfile(second.Id);
        Assert.Equal("Scroll", _store.Load().Keeb.RotaryLeft);
    }
}
