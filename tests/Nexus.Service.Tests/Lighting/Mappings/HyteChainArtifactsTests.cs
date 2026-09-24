using System.Linq;
using Nexus.Service.Lighting.Mappings;

namespace Nexus.Service.Tests.Lighting.Mappings;

/// <summary>
/// Our own accessories are authored in code, not in the generated catalog
/// (which is derived from third-party plugin data and describes none of them),
/// so nothing regenerates them and nothing else checks them. The counts are
/// the load-bearing part: they come from Nexus 2's LEDCountMapForControlBox,
/// and a wrong one silently lights the wrong length of strip.
/// </summary>
public class HyteChainArtifactsTests
{
    [Theory]
    [InlineData("product:hyte-fr12", "FR12", 33)]
    [InlineData("product:hyte-fr12-trio", "FR12 Trio", 68)]
    [InlineData("product:hyte-ln80", "LN80", 45)]
    [InlineData("product:hyte-y50-solo", "Y50 Solo Fan", 8)]
    [InlineData("product:hyte-y50-trio", "Y50 Trio Fan", 24)]
    public void Each_product_keeps_its_validated_led_count(string key, string name, int ledCount)
    {
        var artifact = HyteChainArtifacts.Build(key);

        Assert.NotNull(artifact);
        Assert.Equal(name, artifact!.Name);
        var zone = Assert.Single(artifact.Zones);
        Assert.Equal(ledCount, zone.LedCount);
        Assert.Equal(ledCount, zone.Leds.Count);
    }

    [Fact]
    public void Every_artifact_passes_the_lint_that_gates_apply()
    {
        foreach (var product in HyteChainArtifacts.All)
        {
            var artifact = HyteChainArtifacts.Build(product.Key);
            Assert.NotNull(artifact);
            var result = MappingLint.Validate(artifact!);
            Assert.True(result.Ok, $"{product.Key}: {string.Join("; ", result.Errors)}");
            Assert.False(result.AutoApplyIneligible, product.Key);
        }
    }

    [Fact]
    public void A_trio_lays_its_fans_out_side_by_side()
    {
        var trio = HyteChainArtifacts.Build("product:hyte-y50-trio")!;
        var leds = trio.Zones[0].Leds;

        // 24 across 3 fans divides evenly: 8 per ring, each ring in its own
        // third of the frame. A single blob would mean the split was lost.
        Assert.All(leds.Take(8), l => Assert.InRange(l.U, 0f, 1f / 3f));
        Assert.All(leds.Skip(8).Take(8), l => Assert.InRange(l.U, 1f / 3f, 2f / 3f));
        Assert.All(leds.Skip(16), l => Assert.InRange(l.U, 2f / 3f, 1f));
    }

    [Fact]
    public void An_indivisible_count_gives_the_remainder_to_the_earlier_fans()
    {
        // FR12 Trio's 68 across 3 fans is 23/23/22.
        var trio = HyteChainArtifacts.Build("product:hyte-fr12-trio")!;
        var leds = trio.Zones[0].Leds;

        var perFan = leds.GroupBy(l => l.U < 1f / 3f ? 0 : l.U < 2f / 3f ? 1 : 2)
            .OrderBy(g => g.Key)
            .Select(g => g.Count())
            .ToArray();
        Assert.Equal(new[] { 23, 23, 22 }, perFan);
    }

    [Fact]
    public void A_strip_product_runs_in_a_line()
    {
        var ln80 = HyteChainArtifacts.Build("product:hyte-ln80")!;
        var leds = ln80.Zones[0].Leds;

        Assert.All(leds, l => Assert.Equal(0.5f, l.V));
        // Cell-centred and strictly increasing, so no two LEDs stack.
        Assert.Equal(leds.Select(l => l.U).OrderBy(u => u).ToArray(), leds.Select(l => l.U).ToArray());
        Assert.Equal(leds.Count, leds.Select(l => l.U).Distinct().Count());
    }

    [Fact]
    public void Products_resolve_through_the_catalog_the_chain_endpoint_uses()
    {
        foreach (var product in HyteChainArtifacts.All)
        {
            // The chain endpoint resolves every link through Find; a key that
            // only HyteChainArtifacts knows about would be rejected as unknown.
            var artifact = BuiltInMappingsCatalog.Find(product.Key);
            Assert.NotNull(artifact);
            Assert.Equal(product.LedCount, artifact!.Zones[0].LedCount);
        }
    }

    [Fact]
    public void Searching_the_brand_finds_them_even_though_the_names_omit_it()
    {
        var rows = BuiltInMappingsCatalog.Search("HYTE", null, 50);
        var keys = rows.Select(r => r.Key).ToHashSet();

        foreach (var product in HyteChainArtifacts.All)
        {
            Assert.Contains(product.Key, keys);
        }
    }

    [Fact]
    public void Searching_a_product_name_puts_it_first()
    {
        var rows = BuiltInMappingsCatalog.Search("FR12", null, 50);

        Assert.NotEmpty(rows);
        Assert.Equal("product:hyte-fr12", rows[0].Key);
        Assert.Equal(33, rows[0].LedCount);
        Assert.False(rows[0].Parametric);
    }

    [Fact]
    public void The_reported_total_counts_what_matched_not_the_whole_catalog()
    {
        BuiltInMappingsCatalog.Search("HYTE", null, 50, out var matched);
        Assert.Equal(HyteChainArtifacts.All.Count, matched);

        // Unfiltered, the total covers the generics and our own rows too -
        // the packed file alone would under-report the picker's own list.
        BuiltInMappingsCatalog.Search(null, null, 0, out var all);
        Assert.Equal(BuiltInMappingsCatalog.All.Count + HyteChainArtifacts.All.Count + 2, all);
    }
}
