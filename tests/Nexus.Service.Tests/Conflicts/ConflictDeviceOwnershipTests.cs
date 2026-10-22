using Nexus.Service.Conflicts;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// The vendor join the conflict UI relies on to list, per running app, the
/// OpenRGB devices it competes with Nexus for.
/// </summary>
public class ConflictDeviceOwnershipTests
{
    [Theory]
    [InlineData("Corsair", "icue")]
    [InlineData("corsair", "icue")]
    [InlineData("Lian Li", "lian-li-l-connect")]
    [InlineData("ASUS", "armoury-crate")]
    [InlineData("ASUS", "aura-sync")]
    [InlineData("MSI", "msi-mystic-light")]
    [InlineData("NZXT", "nzxt-cam")]
    [InlineData("Gigabyte", "msi-gaming-center")]
    public void Vendor_resolves_to_its_apps(string vendor, string expectedId)
    {
        Assert.Contains(expectedId, ConflictDeviceOwnership.AppIdsForVendor(vendor));
    }

    [Fact]
    public void Gigabyte_control_center_does_not_claim_msi_devices()
    {
        Assert.DoesNotContain("msi-gaming-center", ConflictDeviceOwnership.AppIdsForVendor("MSI"));
    }

    [Fact]
    public void Universal_rgb_apps_claim_every_vendor()
    {
        var ids = ConflictDeviceOwnership.AppIdsForVendor("Some Unknown Brand");
        Assert.Contains("signalrgb", ids);
        Assert.Contains("openrgb", ids);
        Assert.Contains("hyte-nexus-2", ids);
        // But no vendor-specific app matches a brand it does not name.
        Assert.DoesNotContain("icue", ids);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Empty_vendor_maps_to_nothing(string? vendor)
    {
        Assert.Empty(ConflictDeviceOwnership.AppIdsForVendor(vendor));
    }

    [Fact]
    public void Every_vendor_in_the_catalog_names_a_real_string()
    {
        foreach (var def in ConflictAppCatalog.All)
        {
            Assert.All(def.Vendors, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        }
    }

    [Theory]
    [InlineData("openrgb")]
    [InlineData("hyte-nexus-2")]
    [InlineData("armoury-crate")]
    [InlineData("icue")]
    [InlineData("nzxt-cam")]
    [InlineData("tryx-kanali")]
    [InlineData("zmatrices")]
    [InlineData("deepcool")]
    public void Overlap_is_known_for_apps_with_a_claim_vendor_or_handler(string id)
    {
        Assert.True(ConflictDeviceOwnership.DeviceOverlapKnown(ConflictWatcher.FindById(id)!));
    }

    [Theory]
    [InlineData("msi-afterburner")]
    [InlineData("asus-fan-xpert")]
    [InlineData("corsair-link4")]
    public void Overlap_is_unknown_for_monitoring_and_fan_apps_without_a_mapping(string id)
    {
        Assert.False(ConflictDeviceOwnership.DeviceOverlapKnown(ConflictWatcher.FindById(id)!));
    }
}
