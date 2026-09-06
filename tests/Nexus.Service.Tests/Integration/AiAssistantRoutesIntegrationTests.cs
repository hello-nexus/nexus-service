using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
#if DEV_TOOLS
using Nexus.Service.Persistence;
#endif

namespace Nexus.Service.Tests.Integration;

#if DEV_TOOLS
/// <summary>
/// GET /ai/assistant/status and POST /ai/assistant/query over the real
/// request pipeline. Compiles under -p:DevTools=true only, like the routes. NexusAppFactory sets NEXUS_TEST_HOST=1, which the
/// DI-wired OllamaRuntimeManager reads to skip its system-Ollama detection
/// probe entirely (see RefreshAsync) - so status stays "notInstalled" and no
/// real loopback connection or download ever happens under this factory.
/// The manager's download/extract/launch pipeline is covered against fakes
/// in OllamaRuntimeManagerTests; this class is scoped to the REST contract.
/// </summary>
public sealed class AiAssistantRoutesIntegrationTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

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
        var res = await client.GetAsync("/ai/assistant/status");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Status_defaults_to_not_installed_with_the_full_catalog_and_no_active_model()
    {
        var client = AuthedClient();
        var res = await client.GetAsync("/ai/assistant/status");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("notInstalled", body.GetProperty("runtimeState").GetString());
        Assert.False(body.GetProperty("systemOllamaDetected").GetBoolean());
        Assert.Equal("", body.GetProperty("activeModel").GetString());
        Assert.False(body.TryGetProperty("busy", out _)); // DefaultIgnoreCondition=WhenWritingNull omits it
        Assert.Equal(4, body.GetProperty("catalog").GetArrayLength());
        Assert.Empty(body.GetProperty("installedModels").EnumerateArray());
    }

    [Fact]
    public async Task Query_is_refused_with_400_when_the_master_toggle_is_off()
    {
        var client = AuthedClient();
        var res = await client.PostAsJsonAsync("/ai/assistant/query", new { prompt = "what's my cpu temp" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("error").GetBoolean());
        Assert.Contains("turned off", body.GetProperty("msg").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Query_is_refused_with_400_when_no_model_is_ready()
    {
        _factory.Services.GetRequiredService<IConfigStore>().Update(s => s.AiIntegration.Enabled = true);
        var client = AuthedClient();

        var res = await client.PostAsJsonAsync("/ai/assistant/query", new { prompt = "what's my cpu temp" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("error").GetBoolean());
    }

    [Fact]
    public async Task Model_select_rejects_an_unknown_model_id()
    {
        var client = AuthedClient();
        var res = await client.PostAsJsonAsync("/ai/assistant/model/select", new { model = "not-a-real-model" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Model_select_persists_a_known_catalog_model_as_active()
    {
        var client = AuthedClient();
        var res = await client.PostAsJsonAsync("/ai/assistant/model/select", new { model = "qwen3.5:4b" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("qwen3.5:4b", body.GetProperty("activeModel").GetString());
    }

    [Fact]
    public async Task Model_remove_that_fails_does_not_clear_the_active_model()
    {
        // No runtime is installed under this factory (NEXUS_TEST_HOST skips
        // detection), so RemoveModelAsync always returns false here - the
        // active-model clear must be conditioned on that result, not fire
        // unconditionally.
        var client = AuthedClient();
        await client.PostAsJsonAsync("/ai/assistant/model/select", new { model = "qwen3.5:4b" });

        var res = await client.PostAsJsonAsync("/ai/assistant/model/remove", new { model = "qwen3.5:4b" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("qwen3.5:4b", body.GetProperty("activeModel").GetString());
    }

    [Fact]
    public async Task A_paired_phone_session_cannot_reach_assistant_routes()
    {
        var phoneClient = TestPhoneSession.CreateClient(_factory);

        var res = await phoneClient.GetAsync("/ai/assistant/status");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
#else
/// <summary>
/// Release contract: a build without DEV_TOOLS maps no /ai/assistant/* route,
/// so the assistant (Ollama download, model pulls, query) is unreachable and
/// the MCP surface at /ai/* is all a public build exposes.
/// </summary>
public sealed class AiAssistantRoutesIntegrationTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Theory]
    [InlineData("GET", "/ai/assistant/status")]
    [InlineData("POST", "/ai/assistant/runtime/install")]
    [InlineData("POST", "/ai/assistant/model/pull")]
    [InlineData("POST", "/ai/assistant/query")]
    public async Task Assistant_routes_are_absent_in_a_release_build(string method, string path)
    {
        var client = AuthedClient();
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            req.Content = JsonContent.Create(new { model = "qwen3.5:4b", prompt = "what's my cpu temp" });
        }

        var res = await client.SendAsync(req);

        // 405 for POST, 404 for GET; either means no handler owns the route.
        Assert.Contains(res.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }

    [Fact]
    public async Task Mcp_status_route_still_answers_in_a_release_build()
    {
        var client = AuthedClient();
        var res = await client.GetAsync("/ai/status");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("enabled").GetBoolean());
    }
}
#endif
