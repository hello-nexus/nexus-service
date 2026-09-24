using System.Linq;
using Nexus.Service.Lighting.Mappings;

namespace Nexus.Service.Tests.Lighting.Mappings;

/// <summary>
/// The built-in catalog is data, so these tests guard the two ways data goes
/// wrong: the embedded resource not being embedded at all (which degrades to
/// an empty picker with no error anywhere), and an entry that would be
/// rejected by the very lint every apply runs it through.
/// </summary>
public class BuiltInMappingsCatalogTests
{
    [Fact]
    public void Catalog_loads_from_the_embedded_resource()
    {
        // A missing LogicalName in the csproj fails exactly this way: no
        // exception, no log the user sees, just an empty picker.
        Assert.NotEmpty(BuiltInMappingsCatalog.All);
    }

    [Fact]
    public void Every_entry_has_a_product_key_and_a_unique_one()
    {
        var keys = BuiltInMappingsCatalog.All.Select(e => e.Key).ToList();
        Assert.All(keys, k => Assert.StartsWith("product:", k));
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Every_artifact_passes_the_lint_that_gates_apply()
    {
        foreach (var entry in BuiltInMappingsCatalog.All)
        {
            var result = MappingLint.Validate(entry.Artifact);
            Assert.True(result.Ok,
                $"{entry.Key}: {string.Join("; ", result.Errors)}");
            // A shipped default that lint would refuse to auto-apply is a
            // packaging mistake, not a user's deliberate hidden-fan layout.
            Assert.False(result.AutoApplyIneligible, entry.Key);
        }
    }

    [Fact]
    public void Every_artifact_declares_its_key_as_its_device_key()
    {
        // Assign persists MappingId from the catalog key and the artifact
        // carries device.key independently; a drift between them would make a
        // re-assign look like a different mapping.
        foreach (var entry in BuiltInMappingsCatalog.All)
            Assert.Equal(entry.Key, entry.Artifact.Device.Key);
    }

    [Fact]
    public void Find_returns_the_artifact_for_a_known_key_and_null_otherwise()
    {
        var known = BuiltInMappingsCatalog.All[0].Key;
        Assert.NotNull(BuiltInMappingsCatalog.Find(known));
        Assert.Null(BuiltInMappingsCatalog.Find("product:not-a-real-product"));
    }

    [Fact]
    public void Search_ranks_a_name_match_above_a_brand_match()
    {
        var rows = BuiltInMappingsCatalog.Search("corsair", type: null, limit: 200);
        Assert.NotEmpty(rows);
        var firstBrandOnly = rows.FindIndex(
            r => !r.Name.Contains("corsair", System.StringComparison.OrdinalIgnoreCase));
        var lastNameMatch = rows.FindLastIndex(
            r => r.Name.Contains("corsair", System.StringComparison.OrdinalIgnoreCase));
        if (firstBrandOnly >= 0)
            Assert.True(lastNameMatch < firstBrandOnly);
    }

    [Fact]
    public void Search_filters_by_type_and_honours_the_limit()
    {
        var strips = BuiltInMappingsCatalog.Search(null, "Strip", limit: 500);
        Assert.NotEmpty(strips);
        Assert.All(strips, r => Assert.Equal("Strip", r.Type));
        Assert.Equal(3, BuiltInMappingsCatalog.Search(null, "Strip", limit: 3).Count);
    }

    [Fact]
    public void The_generic_fan_and_strip_are_searchable()
    {
        // The two shapes a user is most likely to have on a header. They are
        // generated from a typed count rather than catalogued, so they come
        // from Search's own rows, not from the embedded resource.
        Assert.Contains(BuiltInMappingsCatalog.Search("Generic Fan", null, 5),
            r => r.Key == GenericChainArtifacts.FanKey);
        Assert.Contains(BuiltInMappingsCatalog.Search("Generic Strip", null, 5),
            r => r.Key == GenericChainArtifacts.StripKey);
    }
}

/// <summary>
/// The generic fan and strip are generated from a count rather than catalogued,
/// so they need their own coverage: the picker must offer them, and the
/// geometry must actually follow the number the user types.
/// </summary>
public class GenericChainArtifactTests
{
    [Fact]
    public void The_picker_offers_both_generics_ahead_of_catalogued_products()
    {
        var rows = BuiltInMappingsCatalog.Search("generic", type: null, limit: 20);
        Assert.Equal(GenericChainArtifacts.FanKey, rows[0].Key);
        Assert.Equal(GenericChainArtifacts.StripKey, rows[1].Key);
        Assert.All(rows.GetRange(0, 2), r => Assert.True(r.Parametric));
    }

    [Fact]
    public void No_discrete_generic_rows_remain_in_the_catalog()
    {
        // 62 near-identical rows used to sit between the user and a real product.
        Assert.DoesNotContain(BuiltInMappingsCatalog.All, e => e.Key.StartsWith("product:generic-argb-"));
    }

    [Fact]
    public void A_fan_is_a_ring_that_gets_denser_with_the_count()
    {
        foreach (var count in new[] { 6, 12, 34 })
        {
            var artifact = GenericChainArtifacts.Build(GenericChainArtifacts.FanKey, count);
            Assert.NotNull(artifact);
            var zone = artifact!.Zones[0];
            Assert.Equal(count, zone.LedCount);
            Assert.Equal(count, zone.Leds.Count);
            // Every LED sits on one circle about the centre.
            foreach (var led in zone.Leds)
            {
                var r = System.MathF.Sqrt((led.U - 0.5f) * (led.U - 0.5f) + (led.V - 0.5f) * (led.V - 0.5f));
                Assert.InRange(r, 0.41f, 0.43f);
            }
            Assert.True(MappingLint.Validate(artifact).Ok);
        }
    }

    [Fact]
    public void A_strip_is_a_line_and_declares_itself_linear()
    {
        var artifact = GenericChainArtifacts.Build(GenericChainArtifacts.StripKey, 30);
        Assert.NotNull(artifact);
        Assert.All(artifact!.Zones[0].Leds, led => Assert.Equal(0.5f, led.V));
        // A colinear cloud must say it is linear or the registry rejects it.
        Assert.Equal(1, artifact.Device.Match!.ZoneSignature![0].Type);
        Assert.True(MappingLint.Validate(artifact).Ok);
    }

    [Fact]
    public void A_non_generic_key_or_an_impossible_count_builds_nothing()
    {
        Assert.Null(GenericChainArtifacts.Build("product:corsair-qx-fan", 34));
        Assert.Null(GenericChainArtifacts.Build(GenericChainArtifacts.FanKey, 0));
        Assert.Null(GenericChainArtifacts.Build(GenericChainArtifacts.FanKey, 5000));
    }
}
