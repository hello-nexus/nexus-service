using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Mcp;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// GET /ai/status, POST /ai/config, POST /ai/token/rotate over the real
/// request pipeline. NexusAppFactory strips McpServerHost from IHostedService
/// (like every other hosted service) so it never autostarts, but the
/// McpServerHost singleton itself is still resolvable, and POST /ai/config
/// calls its ApplyConfiguredStateAsync for real - so an "enabled" patch here
/// really does bind a loopback listener. The port is pinned to 0 (ephemeral)
/// before any enable so that bind can never collide with a real MCP listener
/// on this machine, and Dispose stops it explicitly since the stripped
/// hosted-service registration means the factory's own teardown never would.
/// The listener itself is covered end to end by McpServerHostIntegrationTests;
/// this class is scoped to the REST surface and the settings round trip.
/// </summary>
public sealed class AiRoutesIntegrationTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();

    public AiRoutesIntegrationTests()
    {
        _factory.Services.GetRequiredService<IConfigStore>().Update(s => s.AiIntegration.Port = 0);
    }

    public void Dispose()
    {
        try
        {
            _factory.Services.GetService<McpServerHost>()?.StopAsync(default).GetAwaiter().GetResult();
        }
        catch { /* best-effort teardown */ }
        _factory.Dispose();
    }

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Status_without_auth_is_401()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/ai/status");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Status_defaults_to_disabled_with_no_token_minted()
    {
        var client = AuthedClient();
        var res = await client.GetAsync("/ai/status");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.False(body.GetProperty("running").GetBoolean());
        Assert.Equal("", body.GetProperty("token").GetString());
        Assert.True(body.GetProperty("capabilities").GetProperty("telemetry").GetBoolean());
        Assert.True(body.GetProperty("capabilities").GetProperty("cooling").GetBoolean());
        Assert.True(body.GetProperty("capabilities").GetProperty("lighting").GetBoolean());
        Assert.True(body.GetProperty("capabilities").GetProperty("profiles").GetBoolean());
        Assert.True(body.GetProperty("capabilities").GetProperty("history").GetBoolean());
    }

    [Fact]
    public async Task Enabling_mints_a_token_when_none_exists()
    {
        var client = AuthedClient();
        var res = await client.PostAsJsonAsync("/ai/config", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("enabled").GetBoolean());
        var token = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public async Task Allow_remote_defaults_false_and_status_reports_it()
    {
        var client = AuthedClient();
        var body = await (await client.GetAsync("/ai/status")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("allowRemote").GetBoolean());
    }

    [Fact]
    public async Task Allow_remote_patch_persists_and_rebinds_the_running_listener()
    {
        var client = AuthedClient();
        await client.PostAsJsonAsync("/ai/config", new { enabled = true });
        var host = _factory.Services.GetRequiredService<McpServerHost>();
        Assert.False(host.BoundRemote);

        var on = await (await client.PostAsJsonAsync("/ai/config", new { allowRemote = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(on.GetProperty("allowRemote").GetBoolean());
        Assert.True(on.GetProperty("running").GetBoolean());
        Assert.True(host.BoundRemote);
        Assert.True(_factory.Services.GetRequiredService<IConfigStore>().Load().AiIntegration.AllowRemote);

        var off = await (await client.PostAsJsonAsync("/ai/config", new { allowRemote = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(off.GetProperty("allowRemote").GetBoolean());
        Assert.False(host.BoundRemote);
        Assert.False(_factory.Services.GetRequiredService<IConfigStore>().Load().AiIntegration.AllowRemote);
    }

    [Fact]
    public async Task Allow_remote_patch_while_disabled_persists_without_binding()
    {
        var client = AuthedClient();
        var body = await (await client.PostAsJsonAsync("/ai/config", new { allowRemote = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("allowRemote").GetBoolean());
        Assert.False(body.GetProperty("running").GetBoolean());
    }

    private List<Nexus.Service.Transfer.TransferAttentionNotice> CaptureNotices()
    {
        var notices = new List<Nexus.Service.Transfer.TransferAttentionNotice>();
        _factory.Services.GetRequiredService<Nexus.Service.Transfer.TransferInbox>().TransferNeedsAttention += notices.Add;
        return notices;
    }

    [Fact]
    public async Task Notifies_the_user_only_on_false_to_true_transitions()
    {
        var client = AuthedClient();
        var notices = CaptureNotices();

        await client.PostAsJsonAsync("/ai/config", new { enabled = true });
        Assert.Equal(new[] { "AI Integration turned on" }, notices.Select(n => n.Title));

        await client.PostAsJsonAsync("/ai/config", new { enabled = true });
        await client.PostAsJsonAsync("/ai/config", new { capabilities = new { cooling = false } });
        await client.PostAsJsonAsync("/ai/config", new { allowRemote = false });
        Assert.Single(notices);

        await client.PostAsJsonAsync("/ai/config", new { allowRemote = true });
        Assert.Equal("Remote AI connections turned on", notices[^1].Title);
        Assert.Equal(2, notices.Count);

        await client.PostAsJsonAsync("/ai/config", new { allowRemote = true });
        await client.PostAsJsonAsync("/ai/config", new { enabled = false });
        await client.PostAsJsonAsync("/ai/config", new { allowRemote = false });
        Assert.Equal(2, notices.Count);
        Assert.All(notices, n => Assert.Null(n.FolderPath));
    }

    [Fact]
    public async Task Notifies_when_remote_is_turned_on_while_integration_is_disabled()
    {
        var client = AuthedClient();
        var notices = CaptureNotices();

        await client.PostAsJsonAsync("/ai/config", new { allowRemote = true });

        Assert.Equal(new[] { "Remote AI connections turned on" }, notices.Select(n => n.Title));
    }

    [Fact]
    public async Task Enabling_twice_keeps_the_same_token()
    {
        var client = AuthedClient();
        var first = await client.PostAsJsonAsync("/ai/config", new { enabled = true });
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstToken = firstBody.GetProperty("token").GetString();

        var second = await client.PostAsJsonAsync("/ai/config", new { enabled = true });
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(firstToken, secondBody.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Config_patch_updates_only_the_capabilities_it_names()
    {
        var client = AuthedClient();
        await client.PostAsJsonAsync("/ai/config", new { enabled = true });

        var res = await client.PostAsJsonAsync("/ai/config", new { capabilities = new { cooling = false } });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("capabilities").GetProperty("cooling").GetBoolean());
        Assert.True(body.GetProperty("capabilities").GetProperty("telemetry").GetBoolean());
        Assert.True(body.GetProperty("capabilities").GetProperty("lighting").GetBoolean());
    }

    [Fact]
    public async Task Capabilities_only_patch_leaves_the_running_listener_bound_to_the_same_port()
    {
        var client = AuthedClient();
        var enableRes = await client.PostAsJsonAsync("/ai/config", new { enabled = true });
        var enableBody = await enableRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(enableBody.GetProperty("running").GetBoolean());
        var portBefore = enableBody.GetProperty("port").GetInt32();

        var capsRes = await client.PostAsJsonAsync("/ai/config", new { capabilities = new { cooling = false } });
        var capsBody = await capsRes.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(capsBody.GetProperty("running").GetBoolean());
        Assert.Equal(portBefore, capsBody.GetProperty("port").GetInt32());
        var host = _factory.Services.GetRequiredService<McpServerHost>();
        Assert.Equal(portBefore, host.BoundPort);
    }

    [Fact]
    public async Task Rotate_before_any_enable_still_mints_a_token()
    {
        var client = AuthedClient();
        var res = await client.PostAsync("/ai/token/rotate", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task Rotate_replaces_the_previously_minted_token()
    {
        var client = AuthedClient();
        var enableRes = await client.PostAsJsonAsync("/ai/config", new { enabled = true });
        var enableBody = await enableRes.Content.ReadFromJsonAsync<JsonElement>();
        var original = enableBody.GetProperty("token").GetString();

        var rotateRes = await client.PostAsync("/ai/token/rotate", null);
        var rotateBody = await rotateRes.Content.ReadFromJsonAsync<JsonElement>();
        var rotated = rotateBody.GetProperty("token").GetString();

        Assert.False(string.IsNullOrEmpty(rotated));
        Assert.NotEqual(original, rotated);
    }

    [Fact]
    public async Task A_paired_phone_session_cannot_reach_ai_routes()
    {
        var phoneClient = TestPhoneSession.CreateClient(_factory);

        var res = await phoneClient.GetAsync("/ai/status");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
