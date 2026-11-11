using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Monitoring.History;

namespace Nexus.Service.Tests.Integration;

/// <summary>GET /diagnostics/alerts/history-series and the history lifecycle on rule edits, over the real route pipeline with an in-memory history store (the ring-file store is covered in SensorSeriesStoreTests).</summary>
public sealed class SensorAlertHistoryRoutesTests : IClassFixture<SensorAlertHistoryRoutesTests.InMemoryHistoryFactory>
{
    public sealed class InMemoryHistoryFactory : NexusAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<IMetricsHistoryStore>();
                s.AddSingleton<IMetricsHistoryStore>(new InMemoryMetricsHistoryStore());
                // No temperature source, so no built-in rule adds its monitoring series to these user-rule assertions.
                s.RemoveAll<Nexus.Service.Sensors.ISensorProvider>();
                s.AddSingleton<Nexus.Service.Sensors.ISensorProvider>(new StubSensorProvider());
            });
        }
    }

    private readonly InMemoryHistoryFactory _factory;

    public SensorAlertHistoryRoutesTests(InMemoryHistoryFactory factory)
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

    private static object Rule(string id = "cpu/temp", double? high = 80) => new
    {
        enabled = true,
        sensor = new { category = "cpu", id, name = "CPU Package", type = "Temperature", parentName = "CPU" },
        low = (double?)null,
        high,
        raiseAfterSeconds = 5,
        clearAfterSeconds = 5,
        severity = "watch",
        notifyDesktop = true,
        showInHealth = true,
        repeatMinutes = 0,
    };

    private static MetricSample Sample(long ts, params (string Id, double Value)[] readings) =>
        new(ts, 1, 1, 0, 0, 1, Array.Empty<GpuReading>(), Array.Empty<FanReading>())
        {
            SensorReadings = readings.Select(r => new SensorSeriesReading(r.Id, r.Value)).ToList(),
        };

    private static string Url(long fromSec, long toSec, string extra = "") =>
        $"/diagnostics/alerts/history-series?from={fromSec * 1000}&to={toSec * 1000}{extra}";

    private async Task<string> Create(HttpClient client, object rule)
    {
        var res = await client.PostAsJsonAsync("/diagnostics/alerts", rule);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private IMetricsHistoryStore Store => _factory.Services.GetRequiredService<IMetricsHistoryStore>();
    private MetricsSampleBuffer Buffer => _factory.Services.GetRequiredService<MetricsSampleBuffer>();

    [Theory]
    [InlineData("")]
    [InlineData("?from=1000")]
    [InlineData("?to=1000")]
    [InlineData("?from=2000&to=1000")]
    public async Task Missing_or_inverted_range_is_a_400(string query)
    {
        var res = await Client().GetAsync("/diagnostics/alerts/history-series" + query);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("from and to are required and to must be >= from", body.GetProperty("msg").GetString());
    }

    [Fact]
    public async Task Returns_one_series_per_rule_with_data_in_the_history_response_shape()
    {
        var client = Client();
        var id = await Create(client, Rule());
        await Create(client, Rule("cpu/other"));
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;
        Store.Append(Enumerable.Range(0, 10).Select(i => Sample(t0 + i, (id, 50 + i))).ToList(), null);

        var body = await client.GetFromJsonAsync<JsonElement>(Url(t0, t0 + 20, "&maxPoints=100"));

        Assert.True(body.GetProperty("supported").GetBoolean());
        Assert.Equal(MetricsHistory.TempRetentionDays, body.GetProperty("retentionDays").GetInt32());
        Assert.Equal(1, body.GetProperty("stepSeconds").GetInt32());
        var series = Assert.Single(body.GetProperty("series").EnumerateArray());
        Assert.Equal(id, series.GetProperty("id").GetString());
        Assert.Equal("sensorAlert", series.GetProperty("kind").GetString());
        Assert.Equal("CPU Package", series.GetProperty("name").GetString());
        var points = series.GetProperty("points");
        Assert.Equal(10, points.GetArrayLength());
        Assert.Equal(t0 * 1000, points[0].GetProperty("t").GetInt64());
        Assert.Equal(50, points[0].GetProperty("avg").GetDouble());
        Assert.Equal(50, points[0].GetProperty("max").GetDouble());
    }

    [Fact]
    public async Task Rule_id_filters_the_series()
    {
        var client = Client();
        var a = await Create(client, Rule());
        var b = await Create(client, Rule("cpu/other"));
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;
        Store.Append(new[] { Sample(t0, (a, 1), (b, 2)) }, null);

        var all = await client.GetFromJsonAsync<JsonElement>(Url(t0, t0 + 5));
        var only = await client.GetFromJsonAsync<JsonElement>(Url(t0, t0 + 5, $"&ruleId={b}"));
        var none = await client.GetFromJsonAsync<JsonElement>(Url(t0, t0 + 5, "&ruleId=missing"));

        Assert.Equal(2, all.GetProperty("series").GetArrayLength());
        Assert.Equal(b, Assert.Single(only.GetProperty("series").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(0, none.GetProperty("series").GetArrayLength());
    }

    [Fact]
    public async Task Unflushed_buffer_tail_is_merged_and_wins_on_overlap()
    {
        var client = Client();
        var id = await Create(client, Rule());
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;
        Store.Append(new[] { Sample(t0, (id, 10)), Sample(t0 + 1, (id, 10)) }, null);
        Buffer.Append(Sample(t0 + 1, (id, 99)));
        Buffer.Append(Sample(t0 + 2, (id, 20)));

        var body = await client.GetFromJsonAsync<JsonElement>(Url(t0, t0 + 5));

        var points = body.GetProperty("series")[0].GetProperty("points");
        Assert.Equal(new[] { 10.0, 99.0, 20.0 }, points.EnumerateArray().Select(p => p.GetProperty("avg").GetDouble()).ToArray());
    }

    [Fact]
    public async Task Deleting_a_rule_deletes_its_history_including_the_buffer_tail()
    {
        var client = Client();
        var id = await Create(client, Rule());
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;
        Store.Append(new[] { Sample(t0, (id, 10)) }, null);
        Buffer.Append(Sample(t0 + 1, (id, 20)));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/diagnostics/alerts/{id}")).StatusCode);

        Assert.DoesNotContain(Store.QuerySensorSeriesDecimated(t0, t0 + 5, 1), s => s.SeriesId == id);
        Assert.DoesNotContain(Buffer.SnapshotRange(t0, t0 + 5).SelectMany(s => s.SensorReadings), r => r.Id == id);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("id")]
    [InlineData("type")]
    public async Task Changing_the_sensor_clears_the_history(string field)
    {
        var client = Client();
        var id = await Create(client, Rule());
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;
        Store.Append(new[] { Sample(t0, (id, 10)) }, null);
        var sensor = field switch
        {
            "category" => new { category = "gpu", id = "cpu/temp", name = "CPU Package", type = "Temperature", parentName = "CPU" },
            "id" => new { category = "cpu", id = "cpu/temp2", name = "Other Sensor", type = "Temperature", parentName = "CPU" },
            _ => new { category = "cpu", id = "cpu/temp", name = "CPU Package", type = "Power", parentName = "CPU" },
        };

        var res = await client.PutAsJsonAsync($"/diagnostics/alerts/{id}", new
        {
            enabled = true, sensor, low = (double?)null, high = 80.0, raiseAfterSeconds = 5, clearAfterSeconds = 5,
            severity = "watch", notifyDesktop = true, showInHealth = true, repeatMinutes = 0,
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain(Store.QuerySensorSeriesDecimated(t0, t0 + 5, 1), s => s.SeriesId == id);
    }

    [Fact]
    public async Task Editing_bounds_severity_or_enabled_keeps_the_history()
    {
        var client = Client();
        var id = await Create(client, Rule());
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;
        Store.Append(new[] { Sample(t0, (id, 10)) }, null);

        var res = await client.PutAsJsonAsync($"/diagnostics/alerts/{id}", new
        {
            enabled = false,
            sensor = new { category = "cpu", id = "cpu/temp", name = "Renamed", type = "temperature", parentName = "CPU" },
            low = 5.0, high = 90.0, raiseAfterSeconds = 9, clearAfterSeconds = 9,
            severity = "act", notifyDesktop = false, showInHealth = false, repeatMinutes = 5,
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Single(Store.QuerySensorSeriesDecimated(t0, t0 + 5, 1), s => s.SeriesId == id);
    }

    [Fact]
    public async Task A_put_with_the_pre_heal_id_keeps_the_history()
    {
        var client = Client();
        var id = await Create(client, Rule());
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;
        Store.Append(new[] { Sample(t0, (id, 10)) }, null);
        // The engine healed the stored id; a client still holding the old one sends it back with the same name and parent.
        _factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>()
            .Update(s => s.Diagnostics.SensorAlerts!.Single(r => r.Id == id).Sensor.Id = "cpu/temp-healed");

        var res = await client.PutAsJsonAsync($"/diagnostics/alerts/{id}", Rule(id: "cpu/stale-old", high: 85));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Single(Store.QuerySensorSeriesDecimated(t0, t0 + 5, 1), s => s.SeriesId == id);
    }

    [Fact]
    public async Task Deleting_an_unknown_rule_leaves_no_history_tombstone()
    {
        var client = Client();
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10;

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/diagnostics/alerts/ghost")).StatusCode);
        Store.Append(new[] { Sample(t0, ("ghost", 1)) }, null);

        Assert.Contains(Store.QuerySensorSeriesDecimated(t0, t0 + 5, 1), s => s.SeriesId == "ghost");
    }
}
