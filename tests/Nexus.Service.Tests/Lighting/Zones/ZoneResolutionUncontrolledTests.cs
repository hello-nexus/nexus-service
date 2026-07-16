using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// <see cref="ZoneResolution.IsFullyUncontrolled"/> and
/// <see cref="ZoneResolution.IsSegmentFullyUncontrolled"/> gate frame writers
/// (Corsair, Keeb, LianLi, Slv3) against the LIVE resolved zone ids rather
/// than a provider's default card id, so a custom partition's zone ids still
/// mark a device or segment fully uncontrolled correctly.
/// </summary>
public class ZoneResolutionUncontrolledTests
{
    private static ResolvedZone Zone(string id, int segment) => new()
    {
        Id = id,
        Slices = new List<ZoneSlice> { new() { Segment = segment, Start = 0, Count = 1 } },
    };

    [Fact]
    public void IsFullyUncontrolled_false_when_uncontrolled_list_is_empty()
    {
        var zones = new[] { Zone("corsair:ch1", 0) };
        Assert.False(ZoneResolution.IsFullyUncontrolled(zones, new List<string>()));
    }

    [Fact]
    public void IsFullyUncontrolled_true_when_every_zone_id_is_uncontrolled()
    {
        var zones = new[] { Zone("corsair:ch1", 0) };
        Assert.True(ZoneResolution.IsFullyUncontrolled(zones, new List<string> { "corsair:ch1" }));
    }

    [Fact]
    public void IsFullyUncontrolled_recognizes_custom_partition_zone_ids()
    {
        // Under a custom partition, a device's default card id (corsair:ch1)
        // never appears as a card - only the resolved custom zone id does.
        var zones = new[] { Zone("corsair:ch1:z0", 0), Zone("corsair:ch1:z1", 1) };

        Assert.False(ZoneResolution.IsFullyUncontrolled(zones, new List<string> { "corsair:ch1" }));
        Assert.False(ZoneResolution.IsFullyUncontrolled(zones, new List<string> { "corsair:ch1:z0" }));
        Assert.True(ZoneResolution.IsFullyUncontrolled(zones, new List<string> { "corsair:ch1:z0", "corsair:ch1:z1" }));
    }

    [Fact]
    public void IsSegmentFullyUncontrolled_false_when_no_zone_touches_the_segment()
    {
        var zones = new[] { Zone("keeb:SER1:keys", 0) };
        Assert.False(ZoneResolution.IsSegmentFullyUncontrolled(zones, segment: 1, new List<string> { "keeb:SER1:keys" }));
    }

    [Fact]
    public void IsSegmentFullyUncontrolled_true_for_default_keeb_partition()
    {
        var zones = new[] { Zone("keeb:SER1:keys", 0), Zone("keeb:SER1:underglow", 1) };
        var uncontrolled = new List<string> { "keeb:SER1:keys" };

        Assert.True(ZoneResolution.IsSegmentFullyUncontrolled(zones, segment: 0, uncontrolled));
        Assert.False(ZoneResolution.IsSegmentFullyUncontrolled(zones, segment: 1, uncontrolled));
    }

    [Fact]
    public void IsSegmentFullyUncontrolled_false_when_a_spanning_zone_is_still_controlled()
    {
        // A custom zone spanning both keys and underglow segments; only the
        // spanning zone's own id gates either segment.
        var spanning = new ResolvedZone
        {
            Id = "keeb:SER1:z0",
            Slices = new List<ZoneSlice>
            {
                new() { Segment = 0, Start = 0, Count = 1 },
                new() { Segment = 1, Start = 0, Count = 1 },
            },
        };

        Assert.False(ZoneResolution.IsSegmentFullyUncontrolled(new[] { spanning }, segment: 0, new List<string>()));
        Assert.True(ZoneResolution.IsSegmentFullyUncontrolled(new[] { spanning }, segment: 0, new List<string> { "keeb:SER1:z0" }));
        Assert.True(ZoneResolution.IsSegmentFullyUncontrolled(new[] { spanning }, segment: 1, new List<string> { "keeb:SER1:z0" }));
    }
}
