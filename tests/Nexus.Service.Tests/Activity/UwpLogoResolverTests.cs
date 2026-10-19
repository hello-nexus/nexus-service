using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class UwpLogoResolverTests
{
    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
          <Properties><Logo>Assets\StoreLogo.png</Logo></Properties>
          <Applications>
            <Application Id="App">
              <uap:VisualElements DisplayName="Calc" Square150x150Logo="Assets\Medium.png" Square44x44Logo="Assets\Small.png" />
            </Application>
            <Application Id="Second">
              <uap:VisualElements Square44x44Logo="Assets\Other.png" />
            </Application>
          </Applications>
        </Package>
        """;

    [Fact]
    public void ManifestLogo_PrefersTheFirstApplicationsSmallLogo()
    {
        Assert.Equal(@"Assets\Small.png", UwpLogoResolver.ManifestLogo(Manifest));
    }

    [Fact]
    public void ManifestLogo_FallsBackToTheMediumLogo()
    {
        var xml = Manifest.Replace(@" Square44x44Logo=""Assets\Small.png""", "");
        Assert.Equal(@"Assets\Medium.png", UwpLogoResolver.ManifestLogo(xml));
    }

    [Fact]
    public void ManifestLogo_NoVisualElements_ReturnsNull()
    {
        Assert.Null(UwpLogoResolver.ManifestLogo("<Package><Applications><Application Id=\"A\"/></Applications></Package>"));
    }

    [Fact]
    public void BestScaledFile_PicksTheHighestScale()
    {
        var files = new[]
        {
            "Small.scale-100.png", "Small.scale-200.png", "Small.scale-400.png",
            "Small.targetsize-44.png", "Medium.scale-400.png", "Small.scale-400.jpg",
        };

        Assert.Equal("Small.scale-400.png", UwpLogoResolver.BestScaledFile("Small.png", files));
    }

    [Fact]
    public void BestScaledFile_UnscaledOnly_MatchesTheExactName()
    {
        Assert.Equal("Small.png", UwpLogoResolver.BestScaledFile("Small.png", new[] { "Small.png", "Other.png" }));
    }

    [Fact]
    public void BestScaledFile_NoMatch_ReturnsNull()
    {
        Assert.Null(UwpLogoResolver.BestScaledFile("Small.png", new[] { "Medium.png" }));
    }
}
