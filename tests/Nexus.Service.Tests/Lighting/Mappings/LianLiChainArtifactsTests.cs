using Nexus.Service.Lighting.Mappings;

namespace Nexus.Service.Tests.Lighting.Mappings;

public class LianLiChainArtifactsTests
{
    private static MappingZone Zone(string key) => BuiltInMappingsCatalog.Find(key)!.Zones[0];

    [Theory]
    [InlineData("product:lianli-lian-li-sl120-infinity", "Lian Li UNIFan SL120 Infinity")]
    [InlineData("product:lianli-lian-li-sl140-infinity", "Lian Li UNIFan SL140 Infinity")]
    public void The_whole_fan_keeps_its_catalog_name_and_count_and_passes_lint(string key, string name)
    {
        var artifact = BuiltInMappingsCatalog.Find(key)!;
        Assert.Equal(name, artifact.Name);
        Assert.True(MappingLint.Validate(artifact).Ok);
        Assert.Equal(20, artifact.Zones[0].Leds.Count);
        Assert.Equal(1f, artifact.Zones[0].AspectRatio);
    }

    [Theory]
    [InlineData("product:lianli-lian-li-sl120-infinity")]
    [InlineData("product:lianli-lian-li-sl140-infinity")]
    public void The_inner_ring_starts_at_nine_and_runs_down_through_six_like_a_hub_port(string key)
    {
        var leds = Zone(key).Leds;
        Assert.True(leds[0].U < 0.5f);
        Assert.Equal(0.5f, leds[0].V, 4);
        Assert.Equal(0.5f, leds[2].U, 4);
        Assert.True(leds[2].V > 0.5f);
        Assert.True(leds[6].V < 0.5f);
    }

    [Theory]
    [InlineData("product:lianli-lian-li-sl120-infinity")]
    [InlineData("product:lianli-lian-li-sl140-infinity")]
    public void The_edge_is_a_left_to_right_strip_across_the_fan_middle(string key)
    {
        var edge = Zone(key).Leds.Skip(8).ToList();
        Assert.Equal(12, edge.Count);
        Assert.All(edge, l => Assert.Equal(0.5f, l.V, 4));
        for (var i = 1; i < edge.Count; i++)
            Assert.True(edge[i].U > edge[i - 1].U);
    }

    [Theory]
    [InlineData("product:lianli-sl120-infinity-inner", "product:lianli-sl120-infinity-outter")]
    [InlineData("product:lianli-sl140-infinity-inner", "product:lianli-sl140-infinity-outter")]
    public void The_halves_draw_exactly_like_the_whole_fan(string innerKey, string edgeKey)
    {
        var whole = Zone("product:lianli-lian-li-sl120-infinity").Leds;
        var inner = Zone(innerKey);
        var edge = Zone(edgeKey);
        Assert.Equal(whole.Take(8).Select(l => (l.U, l.V)), inner.Leds.Select(l => (l.U, l.V)));
        Assert.Equal(whole.Skip(8).Select(l => (l.U, l.V)), edge.Leds.Select(l => (l.U, l.V)));
        Assert.Equal(Enumerable.Range(0, 12), edge.Leds.Select(l => l.I));
        Assert.Equal(1f, inner.AspectRatio);
        Assert.Equal(1f, edge.AspectRatio);
    }

    [Fact]
    public void A_row_whose_hub_layout_is_unverified_keeps_the_catalog_geometry()
    {
        var al = Zone("product:lianli-lian-li-al120").Leds;
        Assert.Equal(0.35f, al[0].U, 4);
        Assert.Equal(0.45f, al[0].V, 4);
    }

    [Fact]
    public void A_row_with_another_count_is_left_alone()
    {
        var artifact = new MappingArtifact { Zones = { new MappingZone { ZoneIndex = 0, LedCount = 16 } } };
        Assert.False(LianLiChainArtifacts.Apply(LianLiChainArtifacts.SlInfinityKey, artifact));
        Assert.Empty(artifact.Zones[0].Leds);
    }
}
