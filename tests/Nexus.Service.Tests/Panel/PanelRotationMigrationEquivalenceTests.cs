using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.SteelSeries;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Tests.BulkPanels;
using Nexus.Service.Tests.SteelSeries;
using Xunit;

namespace Nexus.Service.Tests.Panel;

/// <summary>
/// A pre-rotation settings.json loaded through the real JsonConfigStore migration must send the glass
/// exactly what origin/main's pipeline sent for the same stored Flip180/Mirror/Portrait. The "old" side
/// here is a copy of the removed transform (flip180 + mirror) feeding the same transport unrotated.
/// </summary>
public class PanelRotationMigrationEquivalenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rot-equiv-" + Guid.NewGuid().ToString("N")[..8]);

    public PanelRotationMigrationEquivalenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private enum Kind
    {
        /// <summary>origin/main turned this surface's frames by Flip180 and Mirror.</summary>
        Oriented,
        /// <summary>origin/main never read either flag for this surface.</summary>
        Inert,
        /// <summary>The 8.8 inch strip: firmware portrait from Portrait, Flip180 and Mirror on the frame.</summary>
        Strip,
    }

    private sealed record Family(string Name, string Surface, string FamilyId, int Width, int Height, Kind Kind)
    {
        public override string ToString() => Name;

        public PanelDeviceCapabilities Caps() => new()
        {
            Surface = Surface,
            Family = FamilyId,
            CssWidth = Width,
            CssHeight = Height,
            SupportsPortrait = Kind == Kind.Strip ? true : null,
        };

        /// <summary>A frame of the same squareness at a size that keeps the sweep quick.</summary>
        public (int W, int H) Small => Kind == Kind.Strip ? (16, 4) : Width == Height ? (8, 8) : (12, 8);
    }

    private static readonly Family[] Families =
    [
        new("thermalright-square", PanelSurfaces.LcdSquare, "thermalright-lcd", 480, 480, Kind.Oriented),
        new("thermalright-640x480", PanelSurfaces.LcdSquare, "thermalright-lcd", 640, 480, Kind.Oriented),
        new("thermalright-wide", PanelSurfaces.LcdWide, "thermalright-lcd", 1600, 720, Kind.Oriented),
        new("hydroshift2-round", PanelSurfaces.LcdRound, HydroShift2LcdDriver.Id, 480, 480, Kind.Oriented),
        new("hydroshift2-square", PanelSurfaces.LcdSquare, HydroShift2LcdDriver.Id, 480, 480, Kind.Oriented),
        new("hydroshift2-curve", PanelSurfaces.LcdWide, HydroShift2CurveLcdDriver.Id, 1920, 462, Kind.Oriented),
        new("ryujin", PanelSurfaces.LcdSquare, "ryujin-lcd", 320, 240, Kind.Oriented),
        new("glacial", PanelSurfaces.LcdSquare, "glacial-lcd", 720, 1280, Kind.Oriented),
        new("zmatrices", PanelSurfaces.LcdWide, "zmatrices-lcd", 1120, 540, Kind.Oriented),
        new("apex-oled", PanelSurfaces.LcdWide, ApexOledPanelDiscovery.Id, 128, 40, Kind.Oriented),
        new("hydroshift-lcd", PanelSurfaces.LcdSquare, JpegPanelModel.HydroShiftLcd.HandlerId, 480, 480, Kind.Oriented),
        new("galahad2-lcd", PanelSurfaces.LcdSquare, JpegPanelModel.GalahadIiLcd.HandlerId, 480, 480, Kind.Oriented),
        new("capellix-lcd", PanelSurfaces.LcdRound, JpegPanelModel.CorsairCapellix.HandlerId, 480, 480, Kind.Oriented),
        new("fan-tl", PanelSurfaces.LcdRound, JpegPanelModel.TlLcd.HandlerId, 400, 400, Kind.Oriented),
        new("fan-flex", PanelSurfaces.LcdRound, Nexus.Service.Peripherals.LianLiFlex.FlexScreens.PanelFamilyId, 400, 400, Kind.Oriented),
        new("fan-slv3", PanelSurfaces.LcdRound, Nexus.Service.Peripherals.LianLiWireless.Slv3LcdScreens.PanelFamilyId, 400, 400, Kind.Oriented),
        new("corsair-link", PanelSurfaces.LcdRound, CorsairLinkLcd.DeviceId, 480, 480, Kind.Oriented),
        new("screen88", PanelSurfaces.Monitor, UniversalScreen88Driver.Id, 1920, 480, Kind.Strip),
        new("kraken", PanelSurfaces.LegacyKraken, "nzxt-kraken", 320, 320, Kind.Inert),
        new("y70", PanelSurfaces.Y70, "", 1920, 480, Kind.Inert),
        new("q60", PanelSurfaces.Q60, "", 720, 1280, Kind.Inert),
        new("phone", PanelSurfaces.Phone, "", 390, 844, Kind.Inert),
        new("monitor-kiosk", PanelSurfaces.Monitor, "xeneon-edge", 2560, 720, Kind.Inert),
    ];

    public static IEnumerable<object[]> FamilyNames() => Families.Select(f => new object[] { f.Name });

    private static readonly bool?[] Tri = [null, false, true];

    /// <summary>Every stored combination, including those the old UI could not produce.</summary>
    public static IEnumerable<object[]> Combinations()
    {
        foreach (var family in Families)
        {
            foreach (var flip in Tri)
            {
                foreach (var mirror in Tri)
                {
                    foreach (var portrait in Tri)
                    {
                        yield return new object[] { family.Name, flip!, mirror!, portrait! };
                    }
                }
            }
        }
    }

    private static Family Get(string name) => Families.Single(f => f.Name == name);

    // The removed BgraOrientation.Apply, verbatim.
    private static byte[] OldApply(byte[] src, int width, int height, bool flip180, bool mirror)
    {
        var dest = new byte[src.Length];
        int stride = width * 4;
        bool flipX = flip180 ^ mirror;
        for (int y = 0; y < height; y++)
        {
            int sy = flip180 ? height - 1 - y : y;
            for (int x = 0; x < width; x++)
            {
                int sx = flipX ? width - 1 - x : x;
                Array.Copy(src, (sy * stride) + (sx * 4), dest, (y * stride) + (x * 4), 4);
            }
        }
        return dest;
    }

    private static byte[] Asymmetric(int width, int height)
    {
        var frame = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            frame[i * 4] = (byte)(i * 7 + 1);
            frame[(i * 4) + 1] = (byte)((i >> 8) * 13 + 5);
            frame[(i * 4) + 2] = (byte)((i * 31) ^ (i >> 4));
            frame[(i * 4) + 3] = 255;
        }
        return frame;
    }

    private string _path = "";
    private JsonConfigStore? _store;

    /// <summary>Writes a settings file as the previous build left it and loads it through the real migration.</summary>
    private (PanelDeviceRecord Clone, PanelDeviceRecord Stored, NexusSettings Loaded) LoadMigrated(
        Family family, bool? flip, bool? mirror, bool? portrait, Action<NexusSettings>? seed = null)
    {
        var pre = new NexusSettings { SchemaVersion = 30 };
        seed?.Invoke(pre);
        pre.PanelDevices["p"] = new PanelDeviceRecord { Id = "p", Capabilities = family.Caps(), Flip180 = flip, Mirror = mirror, Portrait = portrait };
        _path = Path.Combine(_dir, Guid.NewGuid().ToString("N")[..6] + ".json");
        File.WriteAllText(_path, JsonSerializer.Serialize(pre, PersistenceJsonContext.Default.NexusSettings));
        _store?.Dispose();
        _store = new JsonConfigStore(_path);
        var loaded = _store.Load();
        return (new PanelDeviceRegistry(_store).Get("p")!, loaded.PanelDevices["p"], loaded);
    }

    // What the coordinator binds into every orientable transport.
    private static Func<(int Rotation, bool Mirror)> Source(PanelDeviceRecord clone) => () => (clone.Rotation ?? 0, clone.Mirror ?? false);

    [Theory]
    [MemberData(nameof(Combinations))]
    public void The_frame_the_filter_sends_matches_what_the_old_flags_sent(string name, bool? flip, bool? mirror, bool? portrait)
    {
        var family = Get(name);
        var (clone, stored, _) = LoadMigrated(family, flip, mirror, portrait);
        var (w, h) = family.Small;
        var frame = Asymmetric(w, h);

        Assert.Null(stored.Flip180);
        Assert.Null(stored.Portrait);
        Assert.Equal(mirror, stored.Mirror);

        var filter = new PanelOrientationFilter();
        switch (family.Kind)
        {
            case Kind.Oriented:
                filter.Bind(Source(clone));
                Assert.Equal(OldApply(frame, w, h, flip == true, mirror == true), filter.Apply(frame, w, h).ToArray());
                break;
            case Kind.Inert:
                // The transport never turned frames: no rotation may be left to act on one, and Mirror is as stored.
                Assert.Equal(0, clone.Rotation);
                Assert.Equal(mirror, clone.Mirror);
                break;
            case Kind.Strip:
                Assert.Equal(portrait == true, PanelRotation.IsPortrait(clone.Rotation ?? 0));
                var rotation = clone.Rotation ?? 0;
                filter.Bind(() => (rotation >= 180 ? 180 : 0, mirror == true));
                Assert.Equal(OldApply(frame, w, h, flip == true, mirror == true), filter.Apply(frame, w, h).ToArray());
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void A_touch_lands_on_the_same_source_pixel_as_before(string name, bool? flip, bool? mirror, bool? portrait)
    {
        var family = Get(name);
        if (family.Kind == Kind.Inert)
        {
            return;
        }
        var (clone, _, _) = LoadMigrated(family, flip, mirror, portrait);
        var (w, h) = family.Small;
        var (rotation, mir) = Source(clone)();
        if (family.Kind == Kind.Strip)
        {
            rotation = rotation >= 180 ? 180 : 0;
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var (ox, oy) = (x, y);
                if (flip == true)
                {
                    (ox, oy) = (w - 1 - ox, h - 1 - oy);
                }
                if (mirror == true)
                {
                    ox = w - 1 - ox;
                }
                Assert.Equal((ox, oy), Nexus.Service.Peripherals.PixelFormats.BgraOrientation.MapToSource(x, y, w, h, rotation, mir));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void The_curve_video_cache_key_and_mount_are_the_same(string name, bool? flip, bool? mirror, bool? portrait)
    {
        var family = Get(name);
        var (clone, _, _) = LoadMigrated(family, flip, mirror, portrait);

        // The old player keyed its encode on (Flip180 == true, Mirror == true), for any record.
        var expected = family.Kind == Kind.Oriented ? (flip == true, mirror == true) : (false, mirror == true);
        if (family.Name == "hydroshift2-curve")
        {
            Assert.Equal(expected, HydroShift2CurvePlayer.MountOf(clone));
        }
    }

    // The same device bytes: the new pipeline from the migrated record against the old transform feeding an unrotated transport.
    private static void AssertSameBytes(IReadOnlyList<byte[]> expected, IReadOnlyList<byte[]> actual)
    {
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    public static IEnumerable<object[]> TransportCombinations() =>
        Combinations().Where(c => (string)c[0] is "fan-tl" or "fan-flex" or "fan-slv3" or "corsair-link" or "hydroshift-lcd" or "galahad2-lcd" or "capellix-lcd" or "apex-oled" or "kraken");

    [Theory]
    [MemberData(nameof(TransportCombinations))]
    public void The_real_transport_writes_the_same_bytes_to_the_device(string name, bool? flip, bool? mirror, bool? portrait)
    {
        var family = Get(name);
        var (clone, _, _) = LoadMigrated(family, flip, mirror, portrait);
        // origin/main read both flags for these transports, except the Kraken, which never read them.
        var oldFlip = family.Kind == Kind.Oriented && flip == true;
        var oldMirror = family.Kind == Kind.Oriented && mirror == true;

        var oldSide = Send(family, null, frame => OldApply(frame, family.Width, family.Height, oldFlip, oldMirror));
        var newSide = Send(family, Source(clone), frame => frame);

        AssertSameBytes(oldSide, newSide);
    }

    /// <summary>Feeds one frame to the family's real transport and returns what reached the device.</summary>
    private static List<byte[]> Send(Family family, Func<(int Rotation, bool Mirror)>? orientation, Func<byte[], byte[]> prepare)
    {
        var frame = prepare(Asymmetric(family.Width, family.Height));
        switch (family.Name)
        {
            case "fan-tl":
            case "fan-flex":
            case "fan-slv3":
            {
                var screens = new FanScreens(family.FamilyId);
                screens.SetPanelOwned("a", true);
                using var transport = new FanLcdPanelTransport(screens, "a", family.FamilyId + ":a");
                if (orientation is not null) { transport.BindOrientation(orientation); }
                transport.Open();
                transport.Write(frame);
                return screens.Frames;
            }
            case "corsair-link":
            {
                var (lcd, device) = Panel.Streams.CorsairLinkLcdPanelTests.AttachLcd();
                using var transport = new CorsairLinkLcdStreamTransport(lcd, "S1");
                if (orientation is not null) { transport.BindOrientation(orientation); }
                transport.Open();
                transport.Write(frame);
                return device.Writes;
            }
            case "hydroshift-lcd":
            case "galahad2-lcd":
            case "capellix-lcd":
            {
                var model = family.Name switch
                {
                    // Its built-in quarter turn stays; only the reference-session handshake, which waits on a real glass, is swapped out.
                    "hydroshift-lcd" => JpegPanelModel.HydroShiftLcd with { Handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24) },
                    "galahad2-lcd" => JpegPanelModel.GalahadIiLcd,
                    _ => JpegPanelModel.CorsairCapellix,
                };
                using var hub = new JpegPanelHub(model);
                var device = new HidRecorder();
                hub.Attach(device);
                device.Writes.Clear();
                using var transport = new JpegPanelStreamTransport(hub, "serial");
                if (orientation is not null) { transport.BindOrientation(orientation); }
                transport.Write(frame);
                return device.Writes;
            }
            case "apex-oled":
            {
                var hid = new ApexOledStreamTransportTests.FakeHid();
                var screen = hid.Add(0x1628);
                var found = ApexOledScreens.Find(hid, true).First(s => s.Model.ProductId == 0x1628);
                using var transport = new ApexOledStreamTransport(hid, found.Model, ApexOledPanelDiscovery.PanelSerial(found), null, null, null, 20, 1000, true);
                if (orientation is not null) { transport.BindOrientation(orientation); }
                transport.Open();
                transport.Write(frame);
                return screen.Features.Concat(screen.Outputs).ToList();
            }
            default:
                // The Kraken transport never took an orientation, so there is nothing to bind and nothing to send.
                return new List<byte[]> { frame };
        }
    }

    public static IEnumerable<object[]> StripCombinations() => Combinations().Where(c => (string)c[0] == "screen88");

    [Theory]
    [MemberData(nameof(StripCombinations))]
    public void The_strip_keeps_its_portrait_geometry_firmware_flag_and_frame(string name, bool? flip, bool? mirror, bool? portrait)
    {
        var family = Get(name);
        var (clone, _, _) = LoadMigrated(family, flip, mirror, portrait);
        var wantPortrait = portrait == true;
        var newPortrait = PanelRotation.IsPortrait(clone.Rotation ?? 0);
        Assert.Equal(wantPortrait, newPortrait);

        var (oldGeometry, oldFlag, oldPayload) = Strip(wantPortrait, hubOrientation: null, frameFlip: flip == true, frameMirror: mirror == true);
        var (newGeometry, newFlag, newPayload) = Strip(newPortrait, hubOrientation: Source(clone), frameFlip: false, frameMirror: false);

        Assert.Equal(oldGeometry, newGeometry);
        Assert.Equal(oldFlag, newFlag);
        Assert.Equal(oldPayload, newPayload);
    }

    private static ((int W, int H), byte, byte[]) Strip(bool portrait, Func<(int Rotation, bool Mirror)>? hubOrientation, bool frameFlip, bool frameMirror)
    {
        var driver = new UniversalScreen88Driver();
        driver.BindPortrait(() => portrait);
        var pipe = new UniversalScreen88Tests.GlassPipe();
        using var hub = new BulkPanelHub(driver);
        Assert.True(hub.Attach(pipe, null));
        var rotateFlag = pipe.Params[2][0];
        using var transport = new BulkPanelStreamTransport(hub, "serial");
        if (hubOrientation is not null) { transport.BindOrientation(hubOrientation); }
        var frame = Asymmetric(hub.Width, hub.Height);
        if (frameFlip || frameMirror)
        {
            frame = OldApply(frame, hub.Width, hub.Height, frameFlip, frameMirror);
        }
        pipe.Clear();
        transport.Write(frame);
        return ((hub.Width, hub.Height), rotateFlag, pipe.Payloads.Single());
    }

    [Fact]
    public void Firmware_rotation_settings_are_untouched_by_the_migration()
    {
        void Seed(NexusSettings s)
        {
            s.Devices.Corsair.LcdRotation = 3;
            s.Devices.TlLcdScreens["tl"] = new LianLiWirelessScreenSettings { Rotation = 2 };
            s.Devices.FlexLcdScreens["fx"] = new LianLiWirelessScreenSettings { Rotation = 1 };
            s.Devices.LianLiWireless.Screens["sl"] = new LianLiWirelessScreenSettings { Rotation = 3 };
            s.Y70.Orientation = "Landscape";
            s.Y70.ForceOrientation = false;
            s.QSeries.Orientation = "PortraitFlipped";
        }

        var (_, _, loaded) = LoadMigrated(Get("fan-tl"), true, true, true, Seed);

        var before = new NexusSettings { SchemaVersion = 30 };
        Seed(before);
        Assert.Equal(Rest(before), Rest(loaded));
        Assert.Equal(3, loaded.Devices.Corsair.LcdRotation);
        Assert.Equal(2, loaded.Devices.TlLcdScreens["tl"].Rotation);
        Assert.Equal("PortraitFlipped", loaded.QSeries.Orientation);
    }

    // Everything except the panel records and the schema stamp, as stored.
    private static string Rest(NexusSettings s)
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(s, PersistenceJsonContext.Default.NexusSettings), PersistenceJsonContext.Default.NexusSettings)!;
        copy.PanelDevices.Clear();
        copy.Diagnostics.SensorAlerts.Clear();
        copy.Diagnostics.BuiltInSensorsVersion = 0;
        copy.Diagnostics.HealthChecksVersion = 0;
        copy.SchemaVersion = 0;
        return JsonSerializer.Serialize(copy, PersistenceJsonContext.Default.NexusSettings);
    }

    [Fact]
    public void A_second_migration_and_a_second_load_change_nothing()
    {
        var (_, _, first) = LoadMigrated(Get("corsair-link"), true, true, null);
        var afterFirst = File.ReadAllText(_path);

        Assert.False(PanelRotationMigration.Apply(first));
        _store!.Dispose();
        _store = new JsonConfigStore(_path);
        var second = _store.Load();

        Assert.Equal(180, second.PanelDevices["p"].Rotation);
        Assert.Equal(afterFirst, File.ReadAllText(_path));
    }

    [Fact]
    public void A_file_already_at_v31_is_read_as_it_is()
    {
        var current = new NexusSettings { SchemaVersion = 31 };
        current.PanelDevices["p"] = new PanelDeviceRecord { Id = "p", Capabilities = Get("hydroshift-lcd").Caps(), Rotation = 90, Mirror = true };
        _path = Path.Combine(_dir, "v31.json");
        File.WriteAllText(_path, JsonSerializer.Serialize(current, PersistenceJsonContext.Default.NexusSettings));

        using var store = new JsonConfigStore(_path);
        var record = new PanelDeviceRegistry(store).Get("p")!;

        Assert.Equal(90, record.Rotation);
        Assert.True(record.Mirror);
    }

    [Fact]
    public void A_v31_backup_is_recovered_with_its_rotation_and_a_v30_backup_is_migrated()
    {
        var v31 = new NexusSettings { SchemaVersion = 31 };
        v31.PanelDevices["p"] = new PanelDeviceRecord { Id = "p", Capabilities = Get("fan-tl").Caps(), Rotation = 180 };
        var v30 = new NexusSettings { SchemaVersion = 30 };
        v30.PanelDevices["p"] = new PanelDeviceRecord { Id = "p", Capabilities = Get("fan-tl").Caps(), Flip180 = true };

        foreach (var (backup, name) in new[] { (v31, "v31"), (v30, "v30") })
        {
            var path = Path.Combine(_dir, name + ".json");
            File.WriteAllText(path, "{ not json");
            File.WriteAllText(AtomicJsonFile.BackupPathFor(path), JsonSerializer.Serialize(backup, PersistenceJsonContext.Default.NexusSettings));

            using var store = new JsonConfigStore(path);

            Assert.Equal(180, store.Load().PanelDevices["p"].Rotation);
            Assert.Null(store.Load().PanelDevices["p"].Flip180);
        }
    }

    private sealed class FanScreens : IFanLcdScreens
    {
        private readonly FanLcdPanelOwnership _ownership = new();
        public FanScreens(string family) => PanelFamily = family;
        public List<byte[]> Frames { get; } = new();
        public string HandlerId => "fake";
        public string LogTag => "fake";
        public IReadOnlyList<string> AttachedSerials() => new[] { "a" };
        public Dictionary<string, LianLiWirelessScreenSettings> SettingsOf(DevicesSettings devices) => devices.TlLcdScreens;
        public void PrepareFrames() { }
        public void SyncBrightness(string serial, LianLiWirelessScreenSettings settings, ref int applied, ref bool lockSlept) { }
        public bool SetRotation(string serial, byte rotation) => throw new InvalidOperationException("the panel path must not write firmware rotation");
        public bool PushImage(string serial, byte[] jpeg) { Frames.Add(jpeg); return true; }
        public void Release(string serial) { }
        public void SetPanelOwned(string serial, bool owned) => _ownership.Set(serial, owned);
        public bool PushPanelFrame(string serial, byte[] jpeg) => _ownership.Push(serial, () => PushImage(serial, jpeg));
        public string PanelFamily { get; }
        public int PanelFps => 12;
        public string PanelName(string serial) => serial;
    }

    private sealed class HidRecorder : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public int VendorId => 0x1B1C;
        public int ProductId => 0x0C39;
        public string Path => "/dev/fake";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF00;
        public int Usage => 1;
        public bool Write(ReadOnlySpan<byte> report) { Writes.Add(report.ToArray()); return true; }
        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }
}
