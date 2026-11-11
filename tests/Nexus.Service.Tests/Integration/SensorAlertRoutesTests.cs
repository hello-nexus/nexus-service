using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;

namespace Nexus.Service.Tests.Integration;

public sealed class SensorAlertRoutesTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public SensorAlertRoutesTests(NexusAppFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static object Rule(double? low = null, double? high = 80, string severity = "watch", int repeat = 0) => new
    {
        enabled = true,
        sensor = new { category = "cpu", id = "cpu/temp", name = "CPU Package", type = "Temperature", parentName = "CPU" },
        low,
        high,
        raiseAfterSeconds = 5,
        clearAfterSeconds = 5,
        severity,
        notifyDesktop = true,
        showInHealth = true,
        repeatMinutes = repeat,
    };

    [Fact]
    public async Task Create_list_update_acknowledge_and_delete()
    {
        var client = Client();

        var created = await client.PostAsJsonAsync("/diagnostics/alerts", Rule());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rule = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = rule.GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrEmpty(id));

        var list = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");
        Assert.Single(list.GetProperty("rules").EnumerateArray(), r => r.GetProperty("id").GetString() == id);
        Assert.Equal(JsonValueKind.Array, list.GetProperty("states").ValueKind);
        Assert.Equal(JsonValueKind.Array, list.GetProperty("history").ValueKind);
        Assert.Equal(JsonValueKind.Null, list.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id).GetProperty("low").ValueKind);

        var updated = await client.PutAsJsonAsync($"/diagnostics/alerts/{id}", Rule(low: 10, high: 90, severity: "act", repeat: 15));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, after.GetProperty("id").GetString());
        Assert.Equal("act", after.GetProperty("severity").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/diagnostics/alerts/{id}/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/diagnostics/alerts/history/clear", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/diagnostics/alerts/{id}")).StatusCode);

        var empty = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");
        Assert.DoesNotContain(empty.GetProperty("rules").EnumerateArray(), r => r.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task A_rule_without_bounds_is_a_valid_tracked_sensor()
    {
        var client = Client();

        var res = await client.PostAsJsonAsync("/diagnostics/alerts", Rule(low: null, high: null));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var rule = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("low").ValueKind);
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("high").ValueKind);
    }

    [Fact]
    public async Task The_cap_counts_the_built_ins()
    {
        var client = Client();
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/diagnostics/alerts", Rule())).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/diagnostics/alerts", Rule())).StatusCode);
    }

    [Theory]
    [InlineData(90.0, 80.0)]
    [InlineData(80.0, 80.0)]
    public async Task Bad_bounds_are_a_400(double? low, double? high)
    {
        var res = await Client().PostAsJsonAsync("/diagnostics/alerts", Rule(low, high));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Bad_severity_and_repeat_are_a_400()
    {
        var client = Client();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/diagnostics/alerts", Rule(severity: "loud"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/diagnostics/alerts", Rule(repeat: 7))).StatusCode);
    }

    [Fact]
    public async Task Unknown_rule_is_a_404_on_put_and_acknowledge()
    {
        var client = Client();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/diagnostics/alerts/nope", Rule())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/diagnostics/alerts/nope/acknowledge", null)).StatusCode);
    }

    private static object BuiltInBody(double? high = 88, double? low = null, string sensorId = "elsewhere") => new
    {
        enabled = true,
        sensor = new { category = "gpu", id = sensorId, name = "Hijack", type = "Fan", parentName = "" },
        low,
        high,
        raiseAfterSeconds = 30,
        clearAfterSeconds = 10,
        severity = "act",
        notifyDesktop = false,
        showInHealth = true,
        repeatMinutes = 5,
        builtIn = "gpuTemp",
    };

    [Fact]
    public async Task Built_ins_are_listed_first_with_their_marker_and_synthetic_sensor()
    {
        var client = Client();
        await client.PostAsJsonAsync("/diagnostics/alerts", Rule());

        var list = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");

        var rules = list.GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(new[] { "builtin-cpu", "builtin-gpu", "builtin-storage", "builtin-ram" }, rules.Take(4).Select(r => r.GetProperty("id").GetString()).ToArray());
        Assert.Equal(new[] { "cpuTemp", "gpuTemp", "storageTemp", "ramTemp" }, rules.Take(4).Select(r => r.GetProperty("builtIn").GetString()).ToArray());
        Assert.All(rules.Take(4), r => Assert.Equal("builtin", r.GetProperty("sensor").GetProperty("category").GetString()));
        Assert.Equal(JsonValueKind.Null, rules[4].GetProperty("builtIn").ValueKind);
    }

    [Fact]
    public async Task A_post_cannot_create_a_built_in()
    {
        var client = Client();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/diagnostics/alerts", BuiltInBody())).StatusCode);
        var asCategory = new
        {
            enabled = true, sensor = new { category = "builtin", id = "cpu", name = "CPU", type = "Temperature", parentName = "" },
            low = (double?)null, high = (double?)80, raiseAfterSeconds = 5, clearAfterSeconds = 5, severity = "watch", notifyDesktop = true, showInHealth = true, repeatMinutes = 0,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/diagnostics/alerts", asCategory)).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_built_in_is_a_409_and_the_rule_stays()
    {
        var client = Client();

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync("/diagnostics/alerts/builtin-cpu")).StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");
        Assert.Contains(list.GetProperty("rules").EnumerateArray(), r => r.GetProperty("id").GetString() == "builtin-cpu");
    }

    [Fact]
    public async Task A_put_on_a_built_in_keeps_its_fixed_sensor_drops_the_lower_bound_and_takes_the_edits()
    {
        var client = Client();

        var res = await client.PutAsJsonAsync("/diagnostics/alerts/builtin-gpu", BuiltInBody(high: 88, low: 20));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rule = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("builtin-gpu", rule.GetProperty("id").GetString());
        Assert.Equal("gpuTemp", rule.GetProperty("builtIn").GetString());
        Assert.Equal("builtin", rule.GetProperty("sensor").GetProperty("category").GetString());
        Assert.Equal("gpu", rule.GetProperty("sensor").GetProperty("id").GetString());
        Assert.Equal("Temperature", rule.GetProperty("sensor").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("low").ValueKind);
        Assert.Equal(88, rule.GetProperty("high").GetDouble());
        Assert.Equal(30, rule.GetProperty("raiseAfterSeconds").GetInt32());
        Assert.Equal("act", rule.GetProperty("severity").GetString());
        Assert.False(rule.GetProperty("notifyDesktop").GetBoolean());
    }

    [Fact]
    public async Task A_put_clamps_a_built_in_limit_and_needs_one()
    {
        var client = Client();

        var clamped = await (await client.PutAsJsonAsync("/diagnostics/alerts/builtin-cpu", BuiltInBody(high: 300))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(105, clamped.GetProperty("high").GetDouble());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/diagnostics/alerts/builtin-cpu", BuiltInBody(high: null))).StatusCode);
    }

    [Fact]
    public async Task A_put_cannot_turn_a_user_rule_into_a_built_in()
    {
        var client = Client();
        var created = await (await client.PostAsJsonAsync("/diagnostics/alerts", Rule())).Content.ReadFromJsonAsync<JsonElement>();

        var res = await client.PutAsJsonAsync($"/diagnostics/alerts/{created.GetProperty("id").GetString()}", BuiltInBody());

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Reset_writes_the_defaults_back_for_one_or_all_built_ins_and_leaves_user_rules()
    {
        var client = Client();
        await client.PostAsJsonAsync("/diagnostics/alerts", Rule());
        await client.PutAsJsonAsync("/diagnostics/alerts/builtin-gpu", BuiltInBody(high: 70));
        await client.PutAsJsonAsync("/diagnostics/alerts/builtin-cpu", BuiltInBody(high: 70));

        var one = await client.PostAsync("/diagnostics/alerts/builtins/reset?id=builtin-gpu", null);

        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        var afterOne = (await one.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(90, afterOne.Single(r => r.GetProperty("id").GetString() == "builtin-gpu").GetProperty("high").GetDouble());
        Assert.Equal(70, afterOne.Single(r => r.GetProperty("id").GetString() == "builtin-cpu").GetProperty("high").GetDouble());

        var all = await (await client.PostAsync("/diagnostics/alerts/builtins/reset", null)).Content.ReadFromJsonAsync<JsonElement>();
        var rules = all.GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(100, rules.Single(r => r.GetProperty("id").GetString() == "builtin-cpu").GetProperty("high").GetDouble());
        Assert.Equal(5, rules.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/diagnostics/alerts/builtins/reset?id=nope", null)).StatusCode);
    }
}
