using System.Linq;
using Nexus.Service.Telemetry;
using Xunit;

public class AppLinkTelemetryTests
{
    [Fact]
    public void Keeps_host_and_utm_and_drops_the_rest_of_the_query()
    {
        var props = AppLinkTelemetry.Properties("com.ibuypower.control", "1.2.3",
            "https://www.ibuypower.com/login/account/register-warranty?product-key=ABCDE-12345&utm_source=nexus&utm_campaign=nexus_ibp_warranty&utm_content=extend")!
            .ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("com.ibuypower.control", props["app_id"]);
        Assert.Equal("1.2.3", props["app_version"]);
        Assert.Equal("www.ibuypower.com", props["host"]);
        Assert.Equal("nexus", props["utm_source"]);
        Assert.Equal("nexus_ibp_warranty", props["utm_campaign"]);
        Assert.Equal("extend", props["utm_content"]);
        Assert.DoesNotContain(props.Values, v => v is string s && s.Contains("ABCDE"));
        Assert.DoesNotContain("utm_medium", props.Keys);
    }

    [Fact]
    public void Decodes_and_caps_utm_values()
    {
        var props = AppLinkTelemetry.Properties("a.b", "1.0.0", $"https://x.com/?utm_term=a%20b&utm_content={new string('z', 300)}")!
            .ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("a b", props["utm_term"]);
        Assert.Equal(100, ((string)props["utm_content"]!).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("mailto:support@example.com")]
    public void Ignores_anything_but_a_web_link(string url)
    {
        Assert.Null(AppLinkTelemetry.Properties("a.b", "1.0.0", url));
    }
}
