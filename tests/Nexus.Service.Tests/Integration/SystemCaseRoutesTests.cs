using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

/// <summary>Swaps in a throwaway MachineCaseStore path and a fake cloud client so the real data root is never touched.</summary>
public sealed class SystemCaseAppFactory : NexusAppFactory
{
    public FakeCloudApiClient Api { get; } = new();
    public string CasePath { get; } = Path.Combine(Path.GetTempPath(), "nexus-case-itest-" + Guid.NewGuid().ToString("N"), "system-case.json");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICloudApiClient>();
            services.AddSingleton<ICloudApiClient>(Api);
            services.RemoveAll<MachineCaseStore>();
            services.AddSingleton(new MachineCaseStore(CasePath));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Path.GetDirectoryName(CasePath)!, recursive: true); } catch { /* already gone */ }
    }
}

public sealed class SystemCaseRoutesTests : IClassFixture<SystemCaseAppFactory>
{
    private readonly SystemCaseAppFactory _factory;

    public SystemCaseRoutesTests(SystemCaseAppFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _factory.Services.GetRequiredService<MachineCaseStore>().Save(null, pending: false);
    }

    private HttpClient Desktop()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Body(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Both_verbs_require_the_desktop_token()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/system/case")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync("/system/case", Json("""{"caseId":"x"}"""))).StatusCode);
    }

    [Fact]
    public async Task Get_with_no_pick_returns_an_explicit_null()
    {
        var res = await Desktop().GetAsync("/system/case");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await Body(res);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("caseId").ValueKind);
    }

    [Fact]
    public async Task Put_persists_to_the_machine_file_not_settings_and_get_returns_it()
    {
        var client = Desktop();

        var put = await client.PutAsync("/system/case", Json("""{"caseId":"case-abc"}"""));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("case-abc", (await Body(put)).GetProperty("caseId").GetString());
        Assert.Equal("case-abc", new MachineCaseStore(_factory.CasePath).Load().CaseId);
        Assert.DoesNotContain("case-abc", File.ReadAllText(_factory.SettingsPath));
        Assert.Equal("case-abc", (await Body(await client.GetAsync("/system/case"))).GetProperty("caseId").GetString());
    }

    [Fact]
    public async Task Put_null_clears_the_pick()
    {
        var client = Desktop();
        await client.PutAsync("/system/case", Json("""{"caseId":"case-abc"}"""));

        var put = await client.PutAsync("/system/case", Json("""{"caseId":null}"""));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Body(put)).GetProperty("caseId").ValueKind);
        Assert.Null(new MachineCaseStore(_factory.CasePath).Load().CaseId);
    }

    [Theory]
    [InlineData("""{"caseId":""}""")]
    [InlineData("""{"caseId":"has space"}""")]
    [InlineData("""{"caseId":"a/b"}""")]
    [InlineData("""{"caseId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    public async Task Put_rejects_an_invalid_id_and_leaves_the_pick_alone(string body)
    {
        var client = Desktop();
        await client.PutAsync("/system/case", Json("""{"caseId":"keep"}"""));

        var res = await client.PutAsync("/system/case", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True((await Body(res)).GetProperty("error").GetBoolean());
        Assert.Equal("keep", new MachineCaseStore(_factory.CasePath).Load().CaseId);
    }

    [Fact]
    public async Task Put_signed_in_forwards_to_the_account_device()
    {
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-case", RefreshToken = "r" });
            s.Auth.ActiveCloudAccountId = "acct-case";
        });
        _factory.Api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-case",
            RefreshToken = "r",
            Account = new CloudAccountDto { Id = "acct-case", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        string? sentToken = null;
        string? sentCase = null;
        _factory.Api.OnSetDeviceCase = (token, _, id) =>
        {
            sentToken = token;
            sentCase = id;
            return CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance);
        };

        var res = await Desktop().PutAsync("/system/case", Json("""{"caseId":"case-web"}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await _factory.Services.GetRequiredService<SystemCaseService>().IdleAsync();
        Assert.Equal("access-case", sentToken);
        Assert.Equal("case-web", sentCase);
        Assert.False(new MachineCaseStore(_factory.CasePath).Load().Pending);
    }
}
