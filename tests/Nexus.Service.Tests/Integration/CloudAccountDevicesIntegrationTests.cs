using System.Net;
using System.Net.Http;
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
using Xunit;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// NexusAppFactory variant with ICloudApiClient swapped for FakeCloudApiClient,
/// same pattern as CloudBenchmarkSubmitAppFactory - proves the account/devices
/// raw forwarders' auth wiring and passthrough shape without reaching the real
/// cloud API.
/// </summary>
public sealed class CloudAccountDevicesAppFactory : NexusAppFactory
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
public sealed class CloudAccountDevicesIntegrationTests : IClassFixture<CloudAccountDevicesAppFactory>
{
    private readonly CloudAccountDevicesAppFactory _factory;

    public CloudAccountDevicesIntegrationTests(CloudAccountDevicesAppFactory factory) => _factory = factory;

    private HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private void SignOut() =>
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.ActiveCloudAccountId = null;
        });

    private void SignIn(string accountId, string accessToken)
    {
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = accountId, RefreshToken = "refresh-" + accountId });
            s.Auth.ActiveCloudAccountId = accountId;
        });
        _factory.Api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = accessToken,
            RefreshToken = "refresh-" + accountId,
            Account = new CloudAccountDto { Id = accountId, Email = "x@example.com", Username = "x", EmailVerified = true },
        });
    }

    // ── GET /cloud/account/devices ──────────────────────────────────────

    [Fact]
    public async Task Get_devices_without_desktop_token_is_401()
    {
        var res = await _factory.CreateClient().GetAsync("/cloud/account/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Get_devices_signed_out_forwards_without_bearer_and_relays_body_verbatim()
    {
        SignOut();
        string? capturedToken = "not-called";
        HttpMethod? capturedMethod = null;
        string? capturedPath = null;
        _factory.Api.OnSendRaw = (method, path, _, token) =>
        {
            capturedMethod = method;
            capturedPath = path;
            capturedToken = token;
            return CloudApiResult<CloudRawResponse>.Ok(
                new CloudRawResponse { Body = "[{\"installId\":\"a\"}]", ContentType = "application/json" }, 200);
        };

        var res = await DesktopClient().GetAsync("/cloud/account/devices");

        Assert.Equal(HttpMethod.Get, capturedMethod);
        Assert.Equal("/account/devices", capturedPath);
        Assert.Null(capturedToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("[{\"installId\":\"a\"}]", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_devices_signed_in_forwards_with_bearer_and_relays_upstream_401_verbatim()
    {
        SignIn("acct-dev-get", "access-dev-get");
        string? capturedToken = null;
        _factory.Api.OnSendRaw = (_, _, _, token) =>
        {
            capturedToken = token;
            return CloudApiResult<CloudRawResponse>.Ok(
                new CloudRawResponse { Body = "{\"code\":\"unauthorized\"}", ContentType = "application/json" }, 401);
        };

        var res = await DesktopClient().GetAsync("/cloud/account/devices");

        Assert.Equal("access-dev-get", capturedToken);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("{\"code\":\"unauthorized\"}", await res.Content.ReadAsStringAsync());
    }

    // ── PUT /cloud/account/devices/{installId} ──────────────────────────

    [Fact]
    public async Task Put_device_without_desktop_token_is_401()
    {
        var res = await _factory.CreateClient().PutAsync("/cloud/account/devices/install-1",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Put_device_signed_out_forwards_body_verbatim_without_bearer()
    {
        SignOut();
        string? capturedToken = "not-called";
        string? capturedPath = null;
        string? capturedBody = null;
        _factory.Api.OnSendRaw = (_, path, body, token) =>
        {
            capturedPath = path;
            capturedBody = body;
            capturedToken = token;
            return CloudApiResult<CloudRawResponse>.Ok(CloudVoidRaw(), 200);
        };

        var res = await DesktopClient().PutAsync("/cloud/account/devices/install-1",
            new StringContent("{\"hostname\":\"pc\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal("/account/devices/install-1", capturedPath);
        Assert.Equal("{\"hostname\":\"pc\"}", capturedBody);
        Assert.Null(capturedToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Put_device_percent_encodes_installId_in_upstream_path()
    {
        SignIn("acct-dev-put", "access-dev-put");
        string? capturedPath = null;
        _factory.Api.OnSendRaw = (_, path, _, _) =>
        {
            capturedPath = path;
            return CloudApiResult<CloudRawResponse>.Ok(CloudVoidRaw(), 200);
        };

        // ASP.NET route matching decodes the incoming %20 into a literal space
        // before binding installId, so a round trip through the handler's own
        // Uri.EscapeDataString call is what re-encodes it for the outbound path.
        var res = await DesktopClient().PutAsync("/cloud/account/devices/" + Uri.EscapeDataString("install id 2"),
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal("/account/devices/install%20id%202", capturedPath);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Put_device_signed_in_relays_upstream_404_verbatim()
    {
        SignIn("acct-dev-put-404", "access-dev-put-404");
        string? capturedToken = null;
        _factory.Api.OnSendRaw = (_, _, _, token) =>
        {
            capturedToken = token;
            return CloudApiResult<CloudRawResponse>.Ok(
                new CloudRawResponse { Body = "{\"code\":\"not_found\"}", ContentType = "application/json" }, 404);
        };

        var res = await DesktopClient().PutAsync("/cloud/account/devices/install-1",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal("access-dev-put-404", capturedToken);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("{\"code\":\"not_found\"}", await res.Content.ReadAsStringAsync());
    }

    // ── DELETE /cloud/account/devices/{installId} ───────────────────────

    [Fact]
    public async Task Delete_device_without_desktop_token_is_401()
    {
        var res = await _factory.CreateClient().DeleteAsync("/cloud/account/devices/install-1");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Delete_device_signed_out_forwards_without_bearer()
    {
        SignOut();
        string? capturedToken = "not-called";
        string? capturedPath = null;
        _factory.Api.OnSendRaw = (_, path, _, token) =>
        {
            capturedPath = path;
            capturedToken = token;
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = "", ContentType = "application/json" }, 204);
        };

        var res = await DesktopClient().DeleteAsync("/cloud/account/devices/install-1");

        Assert.Equal("/account/devices/install-1", capturedPath);
        Assert.Null(capturedToken);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task Delete_device_signed_in_forwards_bearer_and_relays_upstream_204_with_empty_body()
    {
        SignIn("acct-dev-del", "access-dev-del");
        string? capturedToken = null;
        HttpMethod? capturedMethod = null;
        string? capturedPath = null;
        _factory.Api.OnSendRaw = (method, path, _, token) =>
        {
            capturedMethod = method;
            capturedPath = path;
            capturedToken = token;
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = "", ContentType = "application/json" }, 204);
        };

        var res = await DesktopClient().DeleteAsync("/cloud/account/devices/install-1");

        Assert.Equal(HttpMethod.Delete, capturedMethod);
        Assert.Equal("/account/devices/install-1", capturedPath);
        Assert.Equal("access-dev-del", capturedToken);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal("", await res.Content.ReadAsStringAsync());
    }

    private static CloudRawResponse CloudVoidRaw() => new() { Body = "{}", ContentType = "application/json" };
}
