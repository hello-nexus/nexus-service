using System.Text.Json.Nodes;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Store;
using Xunit;

namespace Nexus.Service.Tests.Store;

/// <summary>
/// The hardware-bound app gate and the catalog annotation. Covers service logic
/// over a stubbed hardware read; the route lambda and real display detection are not exercised.
/// </summary>
public sealed class HardwareGateTests
{
    private static HardwareAppCatalog Hardware(bool inaPanel, bool ibuypower = false) =>
        new(() => inaPanel ? Y70DisplayProtocol.VariantIna : "", () => ibuypower);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Ina_installs_only_with_the_panel_attached(bool panel, bool expected)
    {
        Assert.Equal(expected, Hardware(panel).MayInstall(HardwareAppCatalog.InaAppId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ibuypower_and_other_apps_are_never_gated(bool ibuypower)
    {
        Assert.True(Hardware(false, ibuypower).MayInstall(HardwareAppCatalog.IbuypowerAppId));
        Assert.True(Hardware(false).MayInstall("com.x.plain"));
        Assert.Null(HardwareAppCatalog.ProductFor(HardwareAppCatalog.IbuypowerAppId));
    }

    private const string List = """{"apps":[{"id":"com.hellonexus.ina","name":"Ina"},{"id":"com.ibuypower.control"},{"id":"com.x.plain"}]}""";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_list_entry_for_ina_carries_the_hardware_object(bool panel)
    {
        var apps = JsonNode.Parse(HardwareAnnotation.Apply(List, null, Hardware(panel))!)!["apps"]!.AsArray();

        var hw = apps[0]!["hardware"]!;
        Assert.Equal(panel, hw["matched"]!.GetValue<bool>());
        Assert.Equal("HYTE Y70 Touch Infinite (Ina edition)", hw["name"]!.GetValue<string>());
        Assert.StartsWith("https://hyte.com/store/", hw["url"]!.GetValue<string>());
        Assert.Null(apps[1]!["hardware"]);
        Assert.Null(apps[2]!["hardware"]);
    }

    [Fact]
    public void The_detail_reply_is_annotated_on_its_root_only_for_a_bound_app()
    {
        var ina = JsonNode.Parse(HardwareAnnotation.Apply("""{"name":"Ina"}""", HardwareAppCatalog.InaAppId, Hardware(true))!)!;
        Assert.True(ina["hardware"]!["matched"]!.GetValue<bool>());

        const string plain = """{"name":"X"}""";
        Assert.Equal(plain, HardwareAnnotation.Apply(plain, "com.x.plain", Hardware(true)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void A_non_catalog_body_passes_through(string? body)
    {
        Assert.Equal(body, HardwareAnnotation.Apply(body, null, Hardware(true)));
    }
}
