using System.Text.Json.Nodes;
using System.Threading.Tasks;
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
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task Only_a_first_install_without_the_panel_is_refused(bool panel, bool installed, bool refused)
    {
        Assert.Equal(refused, await Hardware(panel).RefusesFirstInstallAsync(HardwareAppCatalog.InaAppId, installed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ibuypower_and_other_apps_are_never_gated(bool ibuypower)
    {
        Assert.False(await Hardware(false, ibuypower).RefusesFirstInstallAsync(HardwareAppCatalog.IbuypowerAppId, installed: false));
        Assert.False(await Hardware(false).RefusesFirstInstallAsync("com.x.plain", installed: false));
        Assert.Null(HardwareAppCatalog.ProductFor(HardwareAppCatalog.IbuypowerAppId));
    }

    private const string List = """{"apps":[{"id":"com.hellonexus.ina","name":"Ina"},{"id":"com.ibuypower.control"},{"id":"com.x.plain"}]}""";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_list_entry_for_ina_carries_the_hardware_object(bool panel)
    {
        var apps = JsonNode.Parse((await HardwareAnnotation.ApplyAsync(List, null, Hardware(panel)))!)!["apps"]!.AsArray();

        var hw = apps[0]!["hardware"]!;
        Assert.Equal(panel, hw["matched"]!.GetValue<bool>());
        Assert.Equal("HYTE Y70 Touch Infinite (Ina edition)", hw["name"]!.GetValue<string>());
        Assert.StartsWith("https://hyte.com/store/", hw["url"]!.GetValue<string>());
        Assert.Null(apps[1]!["hardware"]);
        Assert.Null(apps[2]!["hardware"]);
    }

    [Fact]
    public async Task The_detail_reply_is_annotated_on_its_root_only_for_a_bound_app()
    {
        var ina = JsonNode.Parse((await HardwareAnnotation.ApplyAsync("""{"name":"Ina"}""", HardwareAppCatalog.InaAppId, Hardware(true)))!)!;
        Assert.True(ina["hardware"]!["matched"]!.GetValue<bool>());

        const string plain = """{"name":"X"}""";
        Assert.Equal(plain, await HardwareAnnotation.ApplyAsync(plain, "com.x.plain", Hardware(true)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public async Task A_non_catalog_body_passes_through(string? body)
    {
        Assert.Equal(body, await HardwareAnnotation.ApplyAsync(body, null, Hardware(true)));
    }

    [Fact]
    public async Task Duplicate_keys_return_the_input_untouched()
    {
        const string dup = """{"apps":[{"id":"com.hellonexus.ina","id":"com.hellonexus.ina"}]}""";

        Assert.Equal(dup, await HardwareAnnotation.ApplyAsync(dup, null, Hardware(true)));
    }

    [Fact]
    public async Task Non_ascii_listing_copy_stays_readable()
    {
        const string list = """{"apps":[{"id":"com.hellonexus.ina","name":"イナ"}]}""";

        var json = (await HardwareAnnotation.ApplyAsync(list, null, Hardware(true)))!;

        Assert.Contains("イナ", json);
        Assert.DoesNotContain("\\u30a4", json);
    }
}
