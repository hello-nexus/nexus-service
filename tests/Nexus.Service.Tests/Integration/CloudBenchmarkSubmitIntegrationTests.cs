using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Cloud;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Cloud;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// NexusAppFactory variant with ICloudApiClient swapped for the Cloud unit
/// tests' FakeCloudApiClient, same pattern as
/// CloudUsernameCooldownAppFactory - proves the raw forwarder's actual JSON
/// envelope and auth wiring without reaching the real cloud API.
/// </summary>
public sealed class CloudBenchmarkSubmitAppFactory : NexusAppFactory
{
    public FakeCloudApiClient Api { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICloudApiClient>();
            services.AddSingleton<ICloudApiClient>(Api);
        });
    }
}

[Collection("NexusHost")]
public sealed class CloudBenchmarkSubmitIntegrationTests : IClassFixture<CloudBenchmarkSubmitAppFactory>
{
    private readonly CloudBenchmarkSubmitAppFactory _factory;

    public CloudBenchmarkSubmitIntegrationTests(CloudBenchmarkSubmitAppFactory factory) => _factory = factory;

    private System.Net.Http.HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    [Fact]
    public async Task Submit_without_desktop_token_is_401()
    {
        var res = await _factory.CreateClient().PostAsync("/cloud/benchmarks/submit",
            new System.Net.Http.StringContent("{\"composite\":950}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Submit_signed_out_forwards_without_bearer_and_relays_upstream_body_verbatim()
    {
        // Defensive reset: the factory (and its config store) is shared across
        // every test in this class, so this must not assume no earlier test
        // activated an account.
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.ActiveCloudAccountId = null;
        });

        string? capturedToken = "not-called";
        string? capturedBody = null;
        _factory.Api.OnPostRaw = (path, body, token) =>
        {
            capturedToken = token;
            capturedBody = body;
            Assert.Equal("/benchmarks/submit", path);
            return CloudApiResult<CloudRawResponse>.Ok(
                new CloudRawResponse { Body = "{\"rank\":42}", ContentType = "application/json" }, 201);
        };

        var res = await DesktopClient().PostAsync("/cloud/benchmarks/submit",
            new System.Net.Http.StringContent("{\"composite\":950}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Null(capturedToken);
        Assert.Equal("{\"composite\":950}", capturedBody);
        Assert.Equal((HttpStatusCode)201, res.StatusCode);
        Assert.Equal("{\"rank\":42}", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Submit_signed_in_forwards_with_bearer_and_relays_upstream_status_verbatim()
    {
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-bench", RefreshToken = "refresh-1" });
            s.Auth.ActiveCloudAccountId = "acct-bench";
        });
        _factory.Api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-bench",
            RefreshToken = "refresh-1",
            Account = new CloudAccountDto { Id = "acct-bench", Email = "x@example.com", Username = "x", EmailVerified = true },
        });

        string? capturedToken = null;
        _factory.Api.OnPostRaw = (_, _, token) =>
        {
            capturedToken = token;
            // A non-2xx upstream status (e.g. a validation rejection) must
            // still be relayed as-is, not translated into the local
            // ApiResponse envelope.
            return CloudApiResult<CloudRawResponse>.Ok(
                new CloudRawResponse { Body = "{\"code\":\"invalid_score\"}", ContentType = "application/json" }, 422);
        };

        var res = await DesktopClient().PostAsync("/cloud/benchmarks/submit",
            new System.Net.Http.StringContent("{\"composite\":-1}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal("access-bench", capturedToken);
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.Equal("{\"code\":\"invalid_score\"}", await res.Content.ReadAsStringAsync());
    }
}
