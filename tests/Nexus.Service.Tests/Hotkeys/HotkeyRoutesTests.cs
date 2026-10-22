using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Hotkeys;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Integration;
using Xunit;

namespace Nexus.Service.Tests.Hotkeys;

/// <summary>
/// /hotkeys through the real pipeline with the OS registrar and the executor
/// replaced by fakes: the contract shapes, the desktop-only auth tier and the
/// validation errors.
/// </summary>
public sealed class HotkeyRoutesTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly FakeHotkeyRegistrar _registrar = new();
    private readonly SpyDeckExecutor _executor = new();

    public HotkeyRoutesTests()
    {
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHotkeyRegistrar>();
                services.AddSingleton<IHotkeyRegistrar>(_registrar);
                services.RemoveAll<IDeckActionExecutor>();
                services.AddSingleton<IDeckActionExecutor>(_executor);
            }));
        // Hosted services are stripped in the test host, so seed what StartAsync would.
        _factory.Services.GetRequiredService<HotkeyService>().SeedDefaults();
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private HttpClient PanelClient()
    {
        var pairing = _factory.Services.GetRequiredService<PanelPhonePairingService>();
        pairing.CreatePairQr();
        var pairToken = pairing.GetOutstandingPairTokens()[0].Token;
        var claim = pairing.ClaimCore(pairToken, "Test Phone", "TestUA", "192.168.1.50", "itest-device",
            overRelay: false, claimedOverHttps: true);
        Assert.True(claim.Ok, claim.Error);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", claim.SessionToken);
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Read(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static JsonElement Binding(JsonElement body, string id)
        => body.GetProperty("bindings").EnumerateArray().First(b => b.GetProperty("id").GetString() == id);

    [Fact]
    public async Task Get_returns_the_seeded_defaults_with_status()
    {
        var body = await Read(await DesktopClient().GetAsync("/hotkeys"));

        Assert.True(body.GetProperty("supported").GetBoolean());
        Assert.Equal("macos", body.GetProperty("platform").GetString());
        Assert.False(body.GetProperty("suspended").GetBoolean());
        Assert.Equal(4, body.GetProperty("bindings").GetArrayLength());
        Assert.Equal("active", Binding(body, HotkeyDefaults.ToggleWindow).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("GET", "/hotkeys", null)]
    [InlineData("PUT", "/hotkeys/x", "{\"enabled\":true,\"target\":{\"kind\":\"action\",\"action\":{\"type\":\"hotkey\",\"keys\":\"ctrl+c\"}}}")]
    [InlineData("DELETE", "/hotkeys/builtin:toggle-window", null)]
    [InlineData("POST", "/hotkeys/reset-defaults", "{}")]
    [InlineData("POST", "/hotkeys/suspend", "{\"suspended\":true}")]
    public async Task Panel_sessions_get_403_on_every_route(string method, string path, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = Json(body);
        }
        var res = await PanelClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        var res = await _factory.CreateClient().GetAsync("/hotkeys");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Put_upserts_and_canonicalizes_the_combo()
    {
        var res = await DesktopClient().PutAsync("/hotkeys/custom:copy", Json(
            "{\"combo\":\"shift+ctrl+alt+c\",\"enabled\":true,\"label\":\"Copy\",\"target\":{\"kind\":\"action\",\"action\":{\"type\":\"hotkey\",\"keys\":\"ctrl+c\"}}}"));
        var body = await Read(res);

        var binding = Binding(body, "custom:copy");
        Assert.Equal("Ctrl+Alt+Shift+c", binding.GetProperty("combo").GetString());
        Assert.Equal("Copy", binding.GetProperty("label").GetString());
        Assert.Equal("active", binding.GetProperty("status").GetString());
        Assert.Contains(_registrar.Last, r => r.Id == "custom:copy");
    }

    [Fact]
    public async Task Put_a_deck_target_round_trips_the_tag_and_orphans_when_the_preset_is_missing()
    {
        var res = await DesktopClient().PutAsync("/hotkeys/custom:deck", Json(
            "{\"combo\":\"ctrl+alt+d\",\"enabled\":true,\"target\":{\"kind\":\"deck\",\"presetId\":\"gone\",\"tag\":\"custom:deck\"}}"));
        var binding = Binding(await Read(res), "custom:deck");

        var target = binding.GetProperty("target");
        Assert.Equal("gone", target.GetProperty("presetId").GetString());
        Assert.Equal("custom:deck", target.GetProperty("tag").GetString());
        Assert.False(target.TryGetProperty("slot", out _));
        Assert.Equal("orphaned", binding.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("{\"combo\":\"n\",\"enabled\":true,\"target\":{\"kind\":\"action\",\"action\":{\"type\":\"hotkey\"}}}", "invalidCombo")]
    [InlineData("{\"combo\":\"ctrl+alt+nope\",\"enabled\":true,\"target\":{\"kind\":\"action\",\"action\":{\"type\":\"hotkey\"}}}", "invalidCombo")]
    [InlineData("{\"combo\":\"ctrl+alt+a\",\"enabled\":true,\"target\":{\"kind\":\"action\"}}", "invalidTarget")]
    [InlineData("{\"combo\":\"ctrl+alt+a\",\"enabled\":true,\"target\":{\"kind\":\"deck\",\"presetId\":\"p\"}}", "invalidTarget")]
    [InlineData("{\"combo\":\"ctrl+alt+a\",\"enabled\":true,\"target\":null}", "invalidTarget")]
    [InlineData("{\"id\":\"other\",\"combo\":\"ctrl+alt+a\",\"enabled\":true,\"target\":{\"kind\":\"action\",\"action\":{\"type\":\"hotkey\"}}}", "idMismatch")]
    public async Task Put_rejects_invalid_bindings_with_400_and_a_stable_code(string body, string code)
    {
        var res = await DesktopClient().PutAsync("/hotkeys/custom:bad", Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.GetProperty("error").GetBoolean());
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("msg").GetString()));
        Assert.DoesNotContain(_factory.Services.GetRequiredService<IConfigStore>().Load().Hotkeys.Bindings, b => b.Id == "custom:bad");
    }

    [Fact]
    public async Task Put_with_a_bad_id_is_invalidId_and_delete_of_an_unknown_one_is_notFound()
    {
        var client = DesktopClient();
        var bad = await client.PutAsync("/hotkeys/has%20space", Json(
            "{\"enabled\":true,\"target\":{\"kind\":\"action\",\"action\":{\"type\":\"hotkey\"}}}"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalidId", JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        var missing = await client.DeleteAsync("/hotkeys/never-existed");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("notFound", JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_with_a_null_target_is_a_400_not_a_500()
    {
        var res = await DesktopClient().PutAsync("/hotkeys/custom:nt", Json("{\"combo\":\"ctrl+alt+a\",\"enabled\":true,\"target\":null}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Duplicate_combos_report_conflict_on_both()
    {
        var client = DesktopClient();
        var res = await client.PutAsync("/hotkeys/custom:dup", Json(
            "{\"combo\":\"alt+meta+n\",\"enabled\":true,\"target\":{\"kind\":\"action\",\"action\":{\"type\":\"hotkey\",\"keys\":\"ctrl+c\"}}}"));
        var body = await Read(res);
        Assert.Equal("conflict", Binding(body, "custom:dup").GetProperty("status").GetString());
        Assert.Equal("conflict", Binding(body, HotkeyDefaults.ToggleWindow).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Delete_removes_a_binding_and_404s_an_unknown_one()
    {
        var client = DesktopClient();
        var body = await Read(await client.DeleteAsync("/hotkeys/" + HotkeyDefaults.LightingToggle));
        Assert.Equal(3, body.GetProperty("bindings").GetArrayLength());

        var res = await client.DeleteAsync("/hotkeys/never-existed");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Reset_defaults_restores_deleted_and_edited_builtins()
    {
        var client = DesktopClient();
        await client.DeleteAsync("/hotkeys/" + HotkeyDefaults.LightingToggle);
        await client.PutAsync("/hotkeys/" + HotkeyDefaults.ToggleWindow, Json(
            "{\"combo\":\"ctrl+alt+z\",\"enabled\":false,\"target\":{\"kind\":\"action\",\"action\":{\"type\":\"hotkey\",\"keys\":\"ctrl+c\"}}}"));

        var body = await Read(await client.PostAsync("/hotkeys/reset-defaults", Json("{}")));

        Assert.Equal(4, body.GetProperty("bindings").GetArrayLength());
        var toggle = Binding(body, HotkeyDefaults.ToggleWindow);
        Assert.Equal("Alt+Meta+n", toggle.GetProperty("combo").GetString());
        Assert.True(toggle.GetProperty("enabled").GetBoolean());
        Assert.Equal("toggleWindow", toggle.GetProperty("target").GetProperty("action").GetProperty("action").GetProperty("op").GetString());
    }

    [Fact]
    public async Task Suspend_unregisters_and_resumes()
    {
        var client = DesktopClient();
        var suspended = await Read(await client.PostAsync("/hotkeys/suspend", Json("{\"suspended\":true}")));
        Assert.True(suspended.GetProperty("suspended").GetBoolean());
        Assert.Empty(_registrar.Last);

        var resumed = await Read(await client.PostAsync("/hotkeys/suspend", Json("{\"suspended\":false}")));
        Assert.False(resumed.GetProperty("suspended").GetBoolean());
        Assert.Equal(4, _registrar.Last.Count);
        _factory.Services.GetRequiredService<HotkeyService>().Dispose();
    }

    [Fact]
    public async Task Linux_style_registrar_reports_unsupported_over_the_wire()
    {
        _registrar.Supported = false;
        _registrar.Platform = "linux";
        var body = await Read(await DesktopClient().GetAsync("/hotkeys"));
        Assert.False(body.GetProperty("supported").GetBoolean());
        Assert.All(body.GetProperty("bindings").EnumerateArray(), b => Assert.Equal("unsupported", b.GetProperty("status").GetString()));
    }
}
