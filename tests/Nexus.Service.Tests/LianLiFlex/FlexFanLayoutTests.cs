using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Tests.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexFanLayoutTests
{
    private const float Tol = 0.005f;

    // Cable side right, v down. Lower half: wire 0..12, upper half: 13..25.
    [Theory]
    [InlineData(0, 0.9395f, 0.7430f)]
    [InlineData(3, 0.7568f, 0.9410f)]
    [InlineData(4, 0.6796f, 0.9700f)]
    [InlineData(6, 0.5000f, 0.9700f)]
    [InlineData(8, 0.3204f, 0.9700f)]
    [InlineData(9, 0.2432f, 0.9410f)]
    [InlineData(12, 0.0605f, 0.7430f)]
    public void Tl_lower_half_positions(int led, float u, float v)
    {
        var p = FlexFanLayout.Position(FlexFamily.TlFlex, led);
        Assert.Equal(u, p.U, Tol);
        Assert.Equal(v, p.V, Tol);
    }

    [Theory]
    [InlineData(13, 0.9395f, 0.2570f)]
    [InlineData(17, 0.6796f, 0.0300f)]
    [InlineData(19, 0.5000f, 0.0300f)]
    [InlineData(25, 0.0605f, 0.2570f)]
    public void Tl_upper_half_mirrors_lower_on_the_midline(int led, float u, float v)
    {
        var p = FlexFanLayout.Position(FlexFamily.TlFlex, led);
        Assert.Equal(u, p.U, Tol);
        Assert.Equal(v, p.V, Tol);
        var mirror = FlexFanLayout.Position(FlexFamily.TlFlex, led - 13);
        Assert.Equal(mirror.U, p.U, Tol);
        Assert.Equal(1f, mirror.V + p.V, Tol);
    }

    [Fact]
    public void Tl_halves_split_four_five_four_between_diagonal_bar_diagonal()
    {
        foreach (var first in new[] { 0, 13 })
        {
            var diagRight = 0;
            var bar = 0;
            var diagLeft = 0;
            for (var k = 0; k < 13; k++)
            {
                var p = FlexFanLayout.Position(FlexFamily.TlFlex, first + k);
                if (Math.Abs(p.V - 0.5f) > 0.46f)
                {
                    bar++;
                }
                else if (p.U > 0.5f)
                {
                    diagRight++;
                }
                else
                {
                    diagLeft++;
                }
            }
            Assert.Equal((4, 5, 4), (diagRight, bar, diagLeft));
        }
    }

    [Fact]
    public void Tl_swap_exchanges_the_halves_exactly_as_the_writer_does()
    {
        for (var i = 0; i < 26; i++)
        {
            var swapped = FlexFanLayout.Position(FlexFamily.TlFlex, i, swap: true);
            var other = FlexFanLayout.Position(FlexFamily.TlFlex, (i + 13) % 26);
            Assert.Equal(other, swapped);
        }
    }

    [Fact]
    public void Tl_provider_zones_follow_the_swap()
    {
        var plain = FlexLightingDeviceProvider.BuildStructure("AA", FlexFamily.TlFlex, 2);
        var swapped = FlexLightingDeviceProvider.BuildStructure("AA", FlexFamily.TlFlex, 2, swap: true);
        var up = plain.Segments[0];
        var down = plain.Segments[1];
        Assert.Equal(26, up.DefaultV!.Length);
        for (var i = 0; i < 26; i++)
        {
            Assert.True(up.DefaultV[i] > 0.5f && down.DefaultV![i] < 0.5f);
            Assert.Equal(down.DefaultV![i], swapped.Segments[0].DefaultV![i], 5);
            Assert.Equal(down.DefaultU![i], swapped.Segments[0].DefaultU![i], 5);
            Assert.Equal(up.DefaultV[i], swapped.Segments[1].DefaultV![i], 5);
        }
        // Second fan sits in the right half of the device box, same cable-side-first order.
        Assert.InRange(up.DefaultU![13], 0.5f, 1f);
        Assert.True(up.DefaultU[13] > up.DefaultU[25]);
    }

    [Fact]
    public void SlInf_center_ring_starts_at_the_bottom_and_runs_clockwise()
    {
        var p = new (float U, float V)[8];
        for (var i = 0; i < 8; i++)
        {
            p[i] = FlexFanLayout.Position(FlexFamily.SlInfFlex, i);
        }
        Assert.Equal(0.5f, p[0].U, Tol);
        Assert.Equal(0.75f, p[0].V, Tol);
        Assert.Equal(0.25f, p[2].U, Tol);
        Assert.Equal(0.5f, p[2].V, Tol);
        Assert.Equal(0.5f, p[4].U, Tol);
        Assert.Equal(0.25f, p[4].V, Tol);
        Assert.Equal(0.75f, p[6].U, Tol);
        Assert.Equal(0.5f, p[6].V, Tol);
    }

    [Theory]
    [InlineData(8, true, 10)]
    [InlineData(18, true, 8)]
    [InlineData(26, false, 10)]
    [InlineData(36, false, 8)]
    public void SlInf_edge_strips_run_from_the_cable_side_to_the_far_side(int first, bool lower, int count)
    {
        var prevU = 2f;
        for (var k = 0; k < count; k++)
        {
            var p = FlexFanLayout.Position(FlexFamily.SlInfFlex, first + k);
            Assert.True(lower ? p.V > 0.5f : p.V < 0.5f);
            Assert.True(p.U < prevU);
            prevU = p.U;
        }
        Assert.True(FlexFanLayout.Position(FlexFamily.SlInfFlex, first).U > 0.8f);
        Assert.True(FlexFanLayout.Position(FlexFamily.SlInfFlex, first + count - 1).U < 0.2f);
    }

    [Fact]
    public void SlInf_outer_strip_sits_outside_the_inner_strip()
    {
        Assert.True(FlexFanLayout.Position(FlexFamily.SlInfFlex, 19 + 0).V > FlexFanLayout.Position(FlexFamily.SlInfFlex, 9).V - 0.2f);
        var innerTop = FlexFanLayout.Position(FlexFamily.SlInfFlex, 30).V;
        var outerTop = FlexFanLayout.Position(FlexFamily.SlInfFlex, 40).V;
        Assert.True(outerTop < innerTop);
        var innerBottom = FlexFanLayout.Position(FlexFamily.SlInfFlex, 12).V;
        var outerBottom = FlexFanLayout.Position(FlexFamily.SlInfFlex, 21).V;
        Assert.True(outerBottom > innerBottom);
    }

    [Fact]
    public void SlInf_provider_zone_arrays_follow_wire_tables()
    {
        var s = FlexLightingDeviceProvider.BuildStructure("BB", FlexFamily.SlInfFlex, 1);
        var inner = s.Segments[1];
        Assert.Equal(20, inner.DefaultU!.Length);
        for (var k = 0; k < 20; k++)
        {
            var wire = FlexProtocol.ZoneWireIndexes(FlexFamily.SlInfFlex, "inner")[k];
            var expected = FlexFanLayout.Position(FlexFamily.SlInfFlex, wire);
            Assert.Equal(expected.U, inner.DefaultU[k], 5);
            Assert.Equal(expected.V, inner.DefaultV![k], 5);
        }
        Assert.Equal(8, s.Segments[0].DefaultU!.Length);
        Assert.Equal(16, s.Segments[2].DefaultU!.Length);
    }

    [Theory]
    [InlineData(51, FlexFamily.TlFlex, new[] { "inner", "outer" }, new[] { 13, 13 })]
    [InlineData(43, FlexFamily.SlInfFlex, new[] { "center", "inner", "outer" }, new[] { 8, 20, 16 })]
    public void Wireless_flex_fans_get_the_real_zones_at_the_wired_positions(int fansType, FlexFamily family, string[] suffixes, int[] perFanLeds)
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        var mac = Convert.FromHexString("112233445566");
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = mac, MasterMac = net.MasterMac, RxType = 1, FanCount = 2, FansType = (byte)fansType });
        Assert.True(hub.DriveTick());
        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var structure = Assert.Single(provider.GetStructures());
        Assert.Equal(suffixes.Length, structure.Segments.Count);
        Assert.Equal(suffixes.Select(x => "lianli-wireless:112233445566:" + x), structure.DefaultZones.Select(z => z.Id));
        var zones = Slv3LightingDeviceProvider.FlexZonesOf(family);
        for (var i = 0; i < zones.Length; i++)
        {
            var seg = structure.Segments[i];
            var map = FlexProtocol.ZoneWireIndexes(family, zones[i].Key);
            Assert.Equal(perFanLeds[i], map.Count);
            Assert.Equal(2 * map.Count, seg.LedCount);
            for (var f = 0; f < 2; f++)
            {
                for (var k = 0; k < map.Count; k++)
                {
                    var p = FlexFanLayout.Position(family, map[k]);
                    Assert.Equal((f + p.U) / 2f, seg.DefaultU![(f * map.Count) + k], 5);
                    Assert.Equal(p.V, seg.DefaultV![(f * map.Count) + k], 5);
                }
            }
        }
    }

    [Theory]
    [InlineData(FlexFamily.TlFlex)]
    [InlineData(FlexFamily.SlInfFlex)]
    public void Scatter_places_every_zone_led_on_its_wire_index_once(FlexFamily family)
    {
        var zones = Slv3LightingDeviceProvider.FlexZonesOf(family);
        var perFan = FlexProtocol.LedsPerFan(family);
        const int fans = 3;
        var buffers = new RgbColor[zones.Length][];
        for (var z = 0; z < zones.Length; z++)
        {
            var n = FlexProtocol.ZoneWireIndexes(family, zones[z].Key).Count;
            buffers[z] = new RgbColor[fans * n];
            for (var i = 0; i < buffers[z].Length; i++)
            {
                buffers[z][i] = new RgbColor((byte)(z + 1), (byte)(i % 250), 7);
            }
        }
        var wire = new RgbColor[fans * perFan];
        Slv3LightingFrameWriter.ScatterFlexZones(family, fans, buffers, wire);
        Assert.All(wire, c => Assert.Equal(7, c.B));
        for (var z = 0; z < zones.Length; z++)
        {
            var map = FlexProtocol.ZoneWireIndexes(family, zones[z].Key);
            for (var f = 0; f < fans; f++)
            {
                for (var k = 0; k < map.Count; k++)
                {
                    Assert.Equal(buffers[z][(f * map.Count) + k], wire[(f * perFan) + map[k]]);
                }
            }
        }
    }

    [Fact]
    public void Scatter_with_swap_shifts_every_tl_led_by_half_a_fan()
    {
        var buffers = new[] { new RgbColor[13], new RgbColor[13] };
        for (var i = 0; i < 13; i++)
        {
            buffers[0][i] = new RgbColor(1, (byte)i, 0);
            buffers[1][i] = new RgbColor(2, (byte)i, 0);
        }
        var wire = new RgbColor[26];
        Assert.True(Slv3LightingFrameWriter.ScatterFlexZones(FlexFamily.TlFlex, 1, buffers, wire, swap: true));
        Assert.Equal(buffers[0][0], wire[13]);
        Assert.Equal(buffers[1][0], wire[0]);
    }

    [Fact]
    public void A_stored_tl_swap_swaps_the_wireless_positions_and_changes_the_signature()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = Convert.FromHexString("112233445566"), MasterMac = net.MasterMac, RxType = 1, FanCount = 2, FansType = 51 });
        Assert.True(hub.DriveTick());
        var store = new InMemoryConfigStore();
        var provider = new Slv3LightingDeviceProvider(hub, store, new Np50IdentifyTracker());
        var changes = 0;
        provider.DevicesChanged += () => changes++;
        provider.OnHubStateUpdated();
        Assert.Equal(1, changes);
        var plain = Assert.Single(provider.GetStructures()).Segments[0].DefaultU!.ToArray();

        store.Update(s => s.Devices.FlexUnits["112233445566"] = new Nexus.Service.Persistence.LianLiFlexUnitSettings { UpDownSwap = true });
        provider.OnHubStateUpdated();
        Assert.Equal(2, changes);
        var (expectedU, _) = FlexFanLayout.ZonePositions(FlexFamily.TlFlex, "up", 2, swap: true);
        var swapped = Assert.Single(provider.GetStructures()).Segments[0].DefaultU!;
        Assert.Equal(expectedU, swapped);
        Assert.NotEqual(plain, swapped);
    }
}
