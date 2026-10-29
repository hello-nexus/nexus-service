using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Round-trips a setting through the real route → source-gen JSON →
/// JsonConfigStore (temp-isolated) → route path. Replaces the unit suite's
/// TestableConfigStore fake with a test of the actual persistence wiring.
/// </summary>
public sealed class ConfigRoundTripIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public ConfigRoundTripIntegrationTests(NexusAppFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static async Task<double> ReadValue(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.NameEquals("value") || p.NameEquals("Value"))
                return p.Value.GetDouble();
        throw new InvalidOperationException("no value property: " + doc.RootElement);
    }

    [Fact]
    public async Task Setting_persists_across_post_then_get()
    {
        var client = AuthedClient();

        var post = await client.PostAsync("/lighting/global-brightness", Json("{\"value\":0.42}"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var get = await client.GetAsync("/lighting/global-brightness");
        Assert.Equal(0.42, await ReadValue(get), 2);
    }

    [Fact]
    public async Task Out_of_range_setting_is_clamped_on_write()
    {
        var client = AuthedClient();

        await client.PostAsync("/lighting/global-brightness", Json("{\"value\":5.0}"));

        var get = await client.GetAsync("/lighting/global-brightness");
        Assert.Equal(1.0, await ReadValue(get), 2);
    }

    [Fact]
    public async Task Audio_output_pick_persists_across_post_then_get()
    {
        var client = AuthedClient();

        var post = await client.PostAsync("/lighting/audio-output",
            Json("{\"deviceId\":\"{0.0.0.00000000}.{abc}\",\"deviceName\":\"Speakers\"}"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/lighting/audio-output")).Content.ReadAsStringAsync());
        Assert.Equal("{0.0.0.00000000}.{abc}", doc.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal("Speakers", doc.RootElement.GetProperty("deviceName").GetString());
        Assert.Equal("output", doc.RootElement.GetProperty("direction").GetString());
    }

    [Fact]
    public async Task Audio_output_direction_input_persists_across_post_then_get()
    {
        var client = AuthedClient();

        var post = await client.PostAsync("/lighting/audio-output",
            Json("{\"deviceId\":\"{0.0.1.00000000}.{mic}\",\"deviceName\":\"Microphone\",\"direction\":\"input\"}"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/lighting/audio-output")).Content.ReadAsStringAsync());
        Assert.Equal("input", doc.RootElement.GetProperty("direction").GetString());
        Assert.Equal("{0.0.1.00000000}.{mic}", doc.RootElement.GetProperty("deviceId").GetString());

        await client.PostAsync("/lighting/audio-output", Json("{\"deviceId\":\"\",\"deviceName\":\"\",\"direction\":\"\"}"));
        using var back = JsonDocument.Parse(await (await client.GetAsync("/lighting/audio-output")).Content.ReadAsStringAsync());
        Assert.Equal("output", back.RootElement.GetProperty("direction").GetString());
    }

    [Fact]
    public async Task Audio_output_bad_direction_is_rejected_and_keeps_the_pick()
    {
        var client = AuthedClient();
        await client.PostAsync("/lighting/audio-output",
            Json("{\"deviceId\":\"keep\",\"deviceName\":\"Keep\",\"direction\":\"input\"}"));

        var bad = await client.PostAsync("/lighting/audio-output",
            Json("{\"deviceId\":\"x\",\"deviceName\":\"X\",\"direction\":\"sideways\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/lighting/audio-output")).Content.ReadAsStringAsync());
        Assert.Equal("keep", doc.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal("input", doc.RootElement.GetProperty("direction").GetString());
    }

    [Fact]
    public async Task Theme_background_and_accent_source_persist_across_post_then_get()
    {
        var client = AuthedClient();

        var post = await client.PostAsync("/preferences",
            Json("{\"theme\":{\"backgroundMode\":\"gradient\",\"accentSource\":\"custom\"}}"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/preferences")).Content.ReadAsStringAsync());
        var theme = doc.RootElement.GetProperty("theme");
        Assert.Equal("gradient", theme.GetProperty("backgroundMode").GetString());
        Assert.Equal("custom", theme.GetProperty("accentSource").GetString());
    }

    [Fact]
    public async Task View_prefs_persist_and_an_unrelated_ui_patch_keeps_them()
    {
        var client = AuthedClient();

        var post = await client.PostAsync("/preferences",
            Json("{\"ui\":{\"lightingView3d\":true,\"coolingView3d\":true,\"coolingFlowMode\":\"airflow\",\"coolingCollisionShapes\":true}}"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        await client.PostAsync("/preferences", Json("{\"ui\":{\"sidebarCollapsed\":true}}"));

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/preferences")).Content.ReadAsStringAsync());
        var ui = doc.RootElement.GetProperty("ui");
        Assert.True(ui.GetProperty("lightingView3d").GetBoolean());
        Assert.True(ui.GetProperty("coolingView3d").GetBoolean());
        Assert.Equal("airflow", ui.GetProperty("coolingFlowMode").GetString());
        Assert.True(ui.GetProperty("coolingCollisionShapes").GetBoolean());
    }

    [Fact]
    public async Task Unknown_cooling_flow_mode_is_dropped_and_keeps_the_stored_value()
    {
        var client = AuthedClient();

        await client.PostAsync("/preferences", Json("{\"ui\":{\"coolingFlowMode\":\"case\"}}"));
        await client.PostAsync("/preferences", Json("{\"ui\":{\"coolingFlowMode\":\"bogus\"}}"));

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/preferences")).Content.ReadAsStringAsync());
        Assert.Equal("case", doc.RootElement.GetProperty("ui").GetProperty("coolingFlowMode").GetString());
    }

    [Fact]
    public async Task Sidebar_app_order_persists_and_survives_a_pinned_apps_patch()
    {
        var client = AuthedClient();

        await client.PostAsync("/preferences", Json("{\"ui\":{\"sidebarAppOrder\":[\"weather\",\"clock\"]}}"));
        await client.PostAsync("/preferences", Json("{\"ui\":{\"pinnedSidebarApps\":[\"monitoring\"]}}"));

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/preferences")).Content.ReadAsStringAsync());
        var order = doc.RootElement.GetProperty("ui").GetProperty("sidebarAppOrder")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "weather", "clock" }, order);
    }

    [Fact]
    public async Task Recent_lighting_lists_round_trip_and_survive_unrelated_patches()
    {
        var client = AuthedClient();

        var post = await client.PostAsync("/preferences",
            Json("{\"ui\":{\"recentStaticColors\":[\"#FF8800\",\"#00aaff\"],\"recentAnimations\":[\"fire\",\"custom:abc\"]}}"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        await client.PostAsync("/preferences", Json("{\"ui\":{\"sidebarAppOrder\":[\"weather\"]}}"));

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/preferences")).Content.ReadAsStringAsync());
        var ui = doc.RootElement.GetProperty("ui");
        Assert.Equal(new[] { "#FF8800", "#00aaff" }, ui.GetProperty("recentStaticColors").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { "fire", "custom:abc" }, ui.GetProperty("recentAnimations").EnumerateArray().Select(e => e.GetString()).ToArray());

        await client.PostAsync("/preferences", Json("{\"ui\":{\"recentAnimations\":[]}}"));
        using var after = JsonDocument.Parse(await (await client.GetAsync("/preferences")).Content.ReadAsStringAsync());
        Assert.Empty(after.RootElement.GetProperty("ui").GetProperty("recentAnimations").EnumerateArray());
        Assert.Equal(2, after.RootElement.GetProperty("ui").GetProperty("recentStaticColors").GetArrayLength());
    }

    [Fact]
    public async Task Recent_lighting_lists_drop_invalid_entries_and_cap_at_32()
    {
        var client = AuthedClient();
        var colors = string.Join(",", Enumerable.Range(0, 40).Select(i => $"\"#{i:X6}\""));
        var keys = string.Join(",", Enumerable.Range(0, 40).Select(i => $"\"fx{i}\""));

        await client.PostAsync("/preferences", Json(
            "{\"ui\":{\"recentStaticColors\":[\"red\",\"#12345\",\"#GGGGGG\",\"#1234567\"," + colors + "],"
            + "\"recentAnimations\":[\"\",\"   \",\"" + new string('x', 129) + "\"," + keys + "]}}"));

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/preferences")).Content.ReadAsStringAsync());
        var ui = doc.RootElement.GetProperty("ui");
        var gotColors = ui.GetProperty("recentStaticColors").EnumerateArray().Select(e => e.GetString()).ToArray();
        var gotKeys = ui.GetProperty("recentAnimations").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(32, gotColors.Length);
        Assert.Equal("#000000", gotColors[0]);
        Assert.Equal(32, gotKeys.Length);
        Assert.Equal("fx0", gotKeys[0]);
    }

    [Fact]
    public async Task Theme_partial_patch_does_not_clobber_sibling_fields()
    {
        var client = AuthedClient();

        await client.PostAsync("/preferences",
            Json("{\"theme\":{\"backgroundMode\":\"flat\",\"accentColor\":\"#123456\"}}"));
        // A later patch that omits backgroundMode must leave it intact.
        await client.PostAsync("/preferences", Json("{\"theme\":{\"accentSource\":\"system\"}}"));

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/preferences")).Content.ReadAsStringAsync());
        var theme = doc.RootElement.GetProperty("theme");
        Assert.Equal("flat", theme.GetProperty("backgroundMode").GetString());
        Assert.Equal("#123456", theme.GetProperty("accentColor").GetString());
        Assert.Equal("system", theme.GetProperty("accentSource").GetString());
    }

    [Fact]
    public async Task Custom_accent_slot_persists_and_survives_an_accent_color_patch()
    {
        var client = AuthedClient();

        var post = await client.PostAsync("/preferences",
            Json("{\"theme\":{\"customAccentColor\":\"#abcdef\"}}"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        // A preset accent patch must not clear the slot.
        await client.PostAsync("/preferences", Json("{\"theme\":{\"accentColor\":\"#2563eb\"}}"));

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/preferences")).Content.ReadAsStringAsync());
        var theme = doc.RootElement.GetProperty("theme");
        Assert.Equal("#abcdef", theme.GetProperty("customAccentColor").GetString());
        Assert.Equal("#2563eb", theme.GetProperty("accentColor").GetString());
    }

    [Fact]
    public async Task Units_persist_across_post_then_get()
    {
        var client = AuthedClient();

        var post = await client.PostAsync("/preferences",
            Json("{\"units\":{\"monitoringTempUnit\":\"f\",\"timeFormat\":\"12h\",\"numberFormat\":\"comma\",\"dateFormat\":\"yyyy-mm-dd\"}}"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/preferences")).Content.ReadAsStringAsync());
        var units = doc.RootElement.GetProperty("units");
        Assert.Equal("f", units.GetProperty("monitoringTempUnit").GetString());
        Assert.Equal("12h", units.GetProperty("timeFormat").GetString());
        Assert.Equal("comma", units.GetProperty("numberFormat").GetString());
        Assert.Equal("yyyy-mm-dd", units.GetProperty("dateFormat").GetString());
    }

    [Fact]
    public async Task Write_is_flushed_to_the_isolated_settings_file()
    {
        var client = AuthedClient();

        await client.PostAsync("/lighting/global-brightness", Json("{\"value\":0.13}"));
        // Force the debounced store to flush, then read the raw file on disk.
        _factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>().FlushNow();

        var onDisk = await File.ReadAllTextAsync(_factory.SettingsPath);
        Assert.Contains("0.13", onDisk);
    }
}
