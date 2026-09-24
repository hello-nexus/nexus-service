using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.LianLi;

public class LianLiLightingDeviceProviderTests
{
    private readonly LianLiHub _hub = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly LianLiLightingDeviceProvider _provider;

    public LianLiLightingDeviceProviderTests()
    {
        _provider = new LianLiLightingDeviceProvider(_hub, _store, new Np50IdentifyTracker());
    }

    private void Connect() => _hub.State.IsConnected = true;

    private static LianLiFanProfile Sli => LianLiFanProfiles.Default;

    private static LianLiFanProfile Sl
    {
        get { LianLiFanProfiles.TryGet(0xA100, out var p); return p; }
    }

    private void ConnectSl() => _hub.Attach(new HubTransportSpy(), Sl);

    private void SetCombine(bool combine) => _store.Update(s =>
        s.Devices.LightingComposition["lianli"] = new HubCompositionSettings { Mirror = false, CombineRings = combine });

    private LianLiSettings Fans => _store.Load().Devices.LianLi;

    private void OnlyPort0(int fans) => _store.Update(s =>
    {
        s.Devices.LianLi.SetFans(0, fans);
        s.Devices.LianLi.SetFans(1, 0);
        s.Devices.LianLi.SetFans(2, 0);
        s.Devices.LianLi.SetFans(3, 0);
    });

    [Fact]
    public void Default_fan_count_is_4_per_port()
    {
        var s = Fans;
        Assert.Equal(4, s.Port0Fans);
        Assert.Equal(4, s.Port3Fans);
    }

    [Fact]
    public void GetStructures_returns_empty_when_disconnected()
    {
        Assert.Empty(_provider.GetStructures());
    }

    // ── Default composition: per-port, rings combined ──

    [Fact]
    public void Default_composition_is_one_combined_device_per_active_port()
    {
        Connect();
        var structures = _provider.GetStructures();
        Assert.Equal(4, structures.Count);
        Assert.Equal("lianli:port0", structures[0].DeviceId);
        Assert.Equal(2, structures[0].Segments.Count);
        var zone = Assert.Single(structures[0].DefaultZones);
        Assert.Equal("lianli:port0", zone.Id);
        Assert.Equal(2, zone.Slices.Count);
    }

    [Fact]
    public void Default_combined_zone_led_count_spans_both_rings()
    {
        Connect();
        OnlyPort0(2);
        var st = Assert.Single(_provider.GetStructures());
        Assert.Equal(2 * LianLiProtocol.InnerLedsPerFan, st.Segments[0].LedCount);
        Assert.Equal(2 * LianLiProtocol.OuterLedsPerFan, st.Segments[1].LedCount);
        var zone = Assert.Single(st.DefaultZones);
        Assert.Equal(2 * (LianLiProtocol.InnerLedsPerFan + LianLiProtocol.OuterLedsPerFan), ZoneLedCount(zone));
    }

    [Fact]
    public void GetStructures_skips_port_with_zero_fans()
    {
        Connect();
        _store.Update(s =>
        {
            s.Devices.LianLi.SetFans(1, 0);
            s.Devices.LianLi.SetFans(3, 0);
        });
        Assert.Equal(2, _provider.GetStructures().Count);
    }

    // ── Combine off: two zones per port, legacy ids ──

    [Fact]
    public void Combine_off_yields_two_default_zones_with_legacy_ids()
    {
        Connect();
        SetCombine(false);
        var st = _provider.GetStructures()[0];
        Assert.Equal("lianli:port0", st.DeviceId);
        Assert.Equal(2, st.DefaultZones.Count);
        Assert.Equal("lianli:port0:inner", st.DefaultZones[0].Id);
        Assert.Equal("lianli:port0:outer", st.DefaultZones[1].Id);
    }

    // ── Mirror removed: Compose ignores the (legacy) mirror flag ──

    [Fact]
    public void Compose_ignores_mirror_and_yields_per_port()
    {
        var devices = LianLiZoneSupport.Compose(
            "lianli", Sli, new HubCompositionSettings { Mirror = true, CombineRings = true }, Fans);
        Assert.Equal(4, devices.Count);
        Assert.DoesNotContain(devices, d => d.Structure.DeviceId == "lianli:mirror");
        Assert.Equal("lianli:port0", devices[0].Structure.DeviceId);
    }

    [Fact]
    public void Per_port_channels_map_inner_2p_outer_2p_plus_1()
    {
        var devices = LianLiZoneSupport.Compose(
            "lianli", Sli, new HubCompositionSettings { Mirror = false, CombineRings = true }, Fans);
        Assert.Equal(4, devices.Count);
        for (var p = 0; p < 4; p++)
        {
            Assert.Equal(new[] { p * 2 }, devices[p].SegmentChannels[0].ToArray());
            Assert.Equal(new[] { p * 2 + 1 }, devices[p].SegmentChannels[1].ToArray());
        }
    }

    // ── SL v1: one channel per port, one 16-LED ring per fan, no rings axis ──

    [Fact]
    public void Sl_v1_composes_one_single_ring_device_per_port_with_channel_equal_port()
    {
        var devices = LianLiZoneSupport.Compose(
            "lianli", Sl, new HubCompositionSettings { Mirror = false, CombineRings = false }, Fans);
        Assert.Equal(4, devices.Count);
        for (var p = 0; p < 4; p++)
        {
            var d = devices[p];
            Assert.Equal($"lianli:port{p}", d.Structure.DeviceId);
            Assert.Single(d.Structure.Segments);
            Assert.Equal(4 * LianLiProtocol.SlLedsPerFan, d.Structure.Segments[0].LedCount);
            Assert.Single(d.SegmentChannels);
            Assert.Equal(new[] { p }, d.SegmentChannels[0].ToArray());
            var zone = Assert.Single(d.Structure.DefaultZones);
            Assert.Equal($"lianli:port{p}", zone.Id);
        }
    }

    [Fact]
    public void Sl_v1_cards_ignore_combine_and_key_on_its_own_pid()
    {
        ConnectSl();
        SetCombine(false);
        OnlyPort0(3);
        var cards = _provider.GetAll().Devices;
        var card = Assert.Single(cards);
        Assert.Equal("lianli:port0", card.Id);
        Assert.Equal(3 * LianLiProtocol.SlLedsPerFan, card.LedCount);
        Assert.Equal(DeviceKeyComputer.ForFirstParty(LianLiProtocol.VendorId, 0xA100, "port0"), card.DeviceKey);
    }

    [Fact]
    public void Sl_v1_DescribeComposition_has_no_rings_axis()
    {
        ConnectSl();
        var info = _provider.DescribeComposition("lianli");
        Assert.NotNull(info);
        Assert.False(info!.HasRingsAxis);
    }

    // ── Card counts across the cross product (the 1 / 2 / 4 / 8 device spectrum) ──

    [Theory]
    [InlineData(true, 4)]   // combined: one card per active port
    [InlineData(false, 8)]  // split: inner + outer per active port
    public void Card_count_matches_combine(bool combine, int expected)
    {
        Connect();
        SetCombine(combine);
        Assert.Equal(expected, _provider.GetAll().Devices.Count);
    }

    // ── Composition capability flags surfaced to the LED-map editor ──

    [Fact]
    public void DescribeComposition_lianli_hides_mirror_and_ports_keeps_rings()
    {
        Connect();
        var info = _provider.DescribeComposition("lianli");
        Assert.NotNull(info);
        Assert.Equal("lianli", info!.HubKind);
        Assert.True(info.HasRingsAxis);
        Assert.False(info.HasPortToggle);
        Assert.False(info.HasMirror);
        Assert.False(info.Mirror);
    }

    // ── Concentric rings ──

    [Fact]
    public void Outer_ring_is_a_left_to_right_strip()
    {
        Connect();
        OnlyPort0(3);
        var st = Assert.Single(_provider.GetStructures());
        var u = st.Segments[1].DefaultU!;
        var v = st.Segments[1].DefaultV!;
        // Panning only reads correctly when index order runs straight across:
        // u strictly increasing, v flat (each LED lights a mirrored top+bottom
        // pair, so a vertical spread would invent bands the hardware cannot show).
        for (var i = 1; i < u.Length; i++)
        {
            Assert.True(u[i] > u[i - 1], $"u must increase with index (at {i})");
        }
        Assert.Equal(0f, Spread(v));
    }

    [Fact]
    public void Inner_ring_is_a_round_ring()
    {
        Connect();
        OnlyPort0(1);
        var st = Assert.Single(_provider.GetStructures());
        Assert.True(Spread(st.Segments[0].DefaultV!) > 0f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Segment_defaultUV_length_equals_led_count(int fans)
    {
        Connect();
        OnlyPort0(fans);
        var st = Assert.Single(_provider.GetStructures());
        // Segment 0 is the inner ring (8 LEDs/fan), segment 1 the outer (12).
        foreach (var seg in st.Segments)
        {
            var perFan = seg.Index == 0 ? LianLiProtocol.InnerLedsPerFan : LianLiProtocol.OuterLedsPerFan;
            Assert.Equal(fans * perFan, seg.DefaultU!.Length);
            Assert.Equal(fans * perFan, seg.DefaultV!.Length);
        }
    }

    // ── IOpenRgbDeviceOwner ──

    [Fact]
    public void OwnsOpenRgbDevice_false_when_disconnected()
    {
        var device = new Nexus.Service.Lighting.Rgb.RgbDevice { Name = "Lian Li Uni Hub SL-Infinity" };
        Assert.False(_provider.OwnsOpenRgbDevice(device));
    }

    [Fact]
    public void OwnsOpenRgbDevice_false_when_name_does_not_match()
    {
        Connect();
        var device = new Nexus.Service.Lighting.Rgb.RgbDevice { Name = "HYTE NP50" };
        Assert.False(_provider.OwnsOpenRgbDevice(device));
    }

    [Theory]
    [InlineData("Lian Li Uni Hub")]
    [InlineData("Lian Li Uni Hub SL-Infinity")]
    [InlineData("lian li uni hub Controller")]
    public void OwnsOpenRgbDevice_true_when_connected_and_name_matches(string name)
    {
        Connect();
        var device = new Nexus.Service.Lighting.Rgb.RgbDevice { Name = name };
        Assert.True(_provider.OwnsOpenRgbDevice(device));
    }

    private static int ZoneLedCount(Nexus.Service.Lighting.Zones.DefaultZoneDef zone)
    {
        var n = 0;
        foreach (var s in zone.Slices) n += s.Count;
        return n;
    }

    private static float Spread(float[] xs)
    {
        float mn = xs[0], mx = xs[0];
        foreach (var x in xs)
        {
            if (x < mn) mn = x;
            if (x > mx) mx = x;
        }
        return mx - mn;
    }
}
