using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Benchmarks;
using Nexus.Service.Cloud;
using Nexus.Service.Models.Cloud;

namespace Nexus.Service.Tests.Integration;

public sealed class CloudBenchmarkRangesIntegrationTests : IClassFixture<CloudGameScoresAppFactory>
{
    private readonly CloudGameScoresAppFactory _factory;

    public CloudBenchmarkRangesIntegrationTests(CloudGameScoresAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Ranges_forwards_with_this_machines_scoring_version_and_relays_body()
    {
        var version = _factory.Services.GetRequiredService<IBenchmarkProvider>().ScoringVersion;
        string? forwardedPath = null;
        _factory.Api.OnSendRaw = (method, path, _, _) =>
        {
            Assert.Equal(HttpMethod.Get, method);
            forwardedPath = path;
            return CloudApiResult<CloudRawResponse>.Ok(
                new CloudRawResponse { Body = "{\"cpu\":3761.2}", ContentType = "application/json" }, 200);
        };

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        var res = await client.GetAsync("/cloud/benchmarks/ranges?scoringVersion=ignored");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("/benchmarks/ranges?scoringVersion=" + Uri.EscapeDataString(version), forwardedPath);
        Assert.Equal("{\"cpu\":3761.2}", await res.Content.ReadAsStringAsync());
    }
}
