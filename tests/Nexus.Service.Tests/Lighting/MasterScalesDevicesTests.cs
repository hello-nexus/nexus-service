using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

/// <summary>Master brightness as a cap versus as a multiplier of the per-device level,
/// and every writer family that joins the two through <see cref="MasterLevel"/>.</summary>
public sealed class MasterScalesDevicesTests
{
    private const string Id = "dev";

    [Theory]
    [InlineData(0.5, 0.8, false, 0.5)]
    [InlineData(0.8, 0.5, false, 0.5)]
    [InlineData(0.5, 0.8, true, 0.4)]
    [InlineData(0.8, 0.5, true, 0.4)]
    [InlineData(1.0, 0.3, true, 0.3)]
    [InlineData(0.0, 0.3, true, 0.0)]
    [InlineData(0.6, 1.0, true, 0.6)]
    [InlineData(0.6, 1.0, false, 0.6)]
    public void Combine_caps_when_off_and_multiplies_when_on(double device, double master, bool scales, double expected)
    {
        Assert.Equal(expected, MasterBrightness.Combine(device, master, scales), 6);
        Assert.Equal(expected, new MasterLevel((float)master, scales).Combine(device), 5);
    }

    [Theory]
    [InlineData(4, 0.5f, false, 2.0)]
    [InlineData(1, 0.5f, false, 1.0)]
    [InlineData(4, 0.5f, true, 2.0)]
    [InlineData(2, 0.5f, true, 1.0)]
    [InlineData(1, 0.5f, true, 0.5)]
    public void CombineSteps_keeps_the_device_scale(int step, float master, bool scales, double expected)
    {
        Assert.Equal(expected, new MasterLevel(master, scales).CombineSteps(step, 4.0), 6);
    }

    [Fact]
    public void Resolve_reads_level_and_mode_from_one_snapshot()
    {
        var lighting = new LightingSettings { GlobalBrightness = 0.4f, MasterScalesDevices = true };
        var level = MasterBrightness.Resolve(lighting);
        Assert.Equal(0.4f, level.Level);
        Assert.True(level.Scales);
        Assert.False(MasterBrightness.Resolve(new LightingSettings()).Scales);
    }

    [Fact]
    public void Default_is_off()
    {
        Assert.False(new LightingSettings().MasterScalesDevices);
    }

    public delegate double Mul(MasterLevel master, IReadOnlyDictionary<string, LightingDevicePreference> prefs);

    private static readonly IReadOnlyList<string> None = new List<string>();

    // Every per-writer brightness join that takes the master level.
    public static IEnumerable<object[]> Families() => new (string Name, Mul Fn)[]
    {
        ("SegmentFrameComposer (Keeb, IBP, SLV3, Lian Li, Flex, Galleon, HydroShift2 curve, Corsair link, Strimer)",
            (m, p) => SegmentFrameComposer.ComputeBrightnessMul(Id, None, None, p, m, out _)),
        ("Nollie and Asiahorse Umbra",
            (m, p) => NollieLightingFrameWriter.ComputeBrightnessMul(Id, None, None, p, m, out _)),
        ("Cnvs", (m, p) => CnvsLightingFrameWriter.ComputeBrightnessMul(Id, None, p, m, out _)),
        ("Kraken", (m, p) => KrakenLightingFrameWriter.ComputeBrightnessMul(Id, None, None, p, m, out _)),
        ("MiniHub", (m, p) => MiniHubLightingFrameWriter.ComputeBrightnessMul(Id, None, None, p, m, out _)),
        ("QSeries", (m, p) => QSeriesLightingFrameWriter.ComputeBrightnessMul(Id, None, p, m, out _)),
        ("SmartHub", (m, p) => SmartHubLightingFrameWriter.ComputeBrightnessMul(Id, None, None, p, m, out _)),
    }.Select(f => new object[] { f.Name, f.Fn });

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_family_caps_when_off(string name, Mul fn)
    {
        Assert.True(true, name);
        var prefs = Prefs(50);
        Assert.Equal(0.5, fn(new MasterLevel(0.8f, false), prefs), 5);
        Assert.Equal(0.3, fn(new MasterLevel(0.3f, false), prefs), 5);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_family_multiplies_when_on(string name, Mul fn)
    {
        Assert.True(true, name);
        var prefs = Prefs(50);
        Assert.Equal(0.4, fn(new MasterLevel(0.8f, true), prefs), 5);
        Assert.Equal(0.15, fn(new MasterLevel(0.3f, true), prefs), 5);
        // No preference entry: the device is at 100, so the master alone applies.
        Assert.Equal(0.3, fn(new MasterLevel(0.3f, true), new Dictionary<string, LightingDevicePreference>()), 5);
    }

    [Fact]
    public void Np50_caps_when_off_and_multiplies_when_on()
    {
        var prefs = Prefs(50);
        Assert.Equal(0.5, Np50LightingFrameWriter.ComputeBrightnessMul(Id, None, None, prefs, new MasterLevel(0.8f, false), out _), 5);
        Assert.Equal(0.4, Np50LightingFrameWriter.ComputeBrightnessMul(Id, None, None, prefs, new MasterLevel(0.8f, true), out _), 5);
    }

    [Theory]
    [InlineData(4, 0.5f, false, 2)]
    [InlineData(2, 0.5f, false, 2)]
    [InlineData(1, 0.5f, false, 1)]
    [InlineData(4, 0.5f, true, 2)]
    [InlineData(2, 0.5f, true, 1)]
    [InlineData(4, 0f, true, 0)]
    public void Strimer_firmware_brightness_follows_the_mode(int brightness, float master, bool scales, int expectedIndex)
    {
        var expected = StrimerLightingModes.BrightnessCodes[expectedIndex];
        Assert.Equal(expected, StrimerLightingFrameWriter.ComputeBrightnessByte(brightness, new MasterLevel(master, scales)));
    }

    [Theory]
    [InlineData(5, 1f, false, 255)]
    [InlineData(5, 0.5f, false, 153)]
    [InlineData(5, 0.5f, true, 153)]
    [InlineData(3, 0.5f, false, 153)]
    [InlineData(3, 0.5f, true, 51)]
    [InlineData(3, 1f, true, 153)]
    [InlineData(3, 0.05f, true, 50)]
    public void Umbra_firmware_level_follows_the_mode(int level, float master, bool scales, int expected)
    {
        Assert.Equal(expected, Nexus.Service.Peripherals.AsiahorseUmbra.UmbraEffects.ScaleBrightness(level, master, scales));
    }

    private static Dictionary<string, LightingDevicePreference> Prefs(int brightness) =>
        new() { [Id] = new LightingDevicePreference { Brightness = brightness } };
}
