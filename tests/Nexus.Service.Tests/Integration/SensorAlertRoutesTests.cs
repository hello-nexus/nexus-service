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
        Assert.Single(list.GetProperty("rules").EnumerateArray());
        Assert.Equal(JsonValueKind.Array, list.GetProperty("states").ValueKind);
        Assert.Equal(JsonValueKind.Array, list.GetProperty("history").ValueKind);
        Assert.Equal(JsonValueKind.Null, list.GetProperty("rules")[0].GetProperty("low").ValueKind);

        var updated = await client.PutAsJsonAsync($"/diagnostics/alerts/{id}", Rule(low: 10, high: 90, severity: "act", repeat: 15));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, after.GetProperty("id").GetString());
        Assert.Equal("act", after.GetProperty("severity").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/diagnostics/alerts/{id}/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/diagnostics/alerts/history/clear", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/diagnostics/alerts/{id}")).StatusCode);

        var empty = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");
        Assert.Equal(0, empty.GetProperty("rules").GetArrayLength());
    }

    [Fact]
    public async Task The_eleventh_rule_is_a_409()
    {
        var client = Client();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/diagnostics/alerts", Rule())).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/diagnostics/alerts", Rule())).StatusCode);
    }

    [Theory]
    [InlineData(null, null)]
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
}
