using System.Text.Json;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Widgets.AppActions;

public class SystemInfoResponseTests
{
    [Fact]
    public void Serialized_specs_carry_the_trimmed_host_version()
    {
        var expected = BuildInfo.Version.TrimStart('v', 'V');
        var dto = new SystemInfoResponse { NexusVersion = expected };

        var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.SystemInfoResponse);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(expected, doc.RootElement.GetProperty("nexusVersion").GetString());
    }
}
