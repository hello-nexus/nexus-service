using System.Text.Json;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Widgets;

public class AppManifestFreeformTests
{
    private const string Head = """{"schema":"nexus.app/1","id":"com.example.fish","name":"Fish","version":"1.0.0","sizes":["2x2"]""";

    [Fact]
    public void FreeformTrueReachesTheInstalledListingPayload()
    {
        var manifest = JsonSerializer.Deserialize(Head + ""","freeform":true}""", AppJsonContext.Default.AppManifest)!;
        Assert.True(manifest.Freeform);

        var json = JsonSerializer.Serialize(new AppInstalledListing { Freeform = manifest.Freeform }, AppJsonContext.Default.AppInstalledListing);
        Assert.Contains("\"freeform\":true", json);
    }

    [Fact]
    public void AbsentFreeformMeansFalse()
    {
        var manifest = JsonSerializer.Deserialize(Head + "}", AppJsonContext.Default.AppManifest)!;
        Assert.False(manifest.Freeform);

        var json = JsonSerializer.Serialize(new AppInstalledListing { Freeform = manifest.Freeform }, AppJsonContext.Default.AppInstalledListing);
        Assert.Contains("\"freeform\":false", json);
    }
}
