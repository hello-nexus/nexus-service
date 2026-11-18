using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Integration;

public sealed class FanNameFactory : NexusAppFactory
{
    private sealed class Fans : IFanControlProvider
    {
        public IReadOnlyList<FanChannel> GetFanChannels() => new[]
        {
            new FanChannel { Id = "/lpc/ctl/1", Name = "Fan #1", Rpm = 1200, RpmSensorId = "/lpc/fan/1" },
            new FanChannel { Id = "np50:1/port1", Name = "Port 1", Rpm = 700, DeviceId = "np50:1", DeviceName = "NP50 Hub" },
        };
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFanControlProvider>();
            services.AddSingleton<IFanControlProvider>(new Fans());
            services.RemoveAll<IMetricsHistoryStore>();
            services.AddSingleton<IMetricsHistoryStore>(new InMemoryMetricsHistoryStore());
            services.RemoveAll<ISensorProvider>();
            services.AddSingleton<ISensorProvider>(new StubSensorProvider
            {
                MotherboardSensors =
                {
                    new HardwareSensor
                    {
                        Id = "/lpc/fan/1", Name = "Fan #1", Type = "Fan", Value = 1200, Units = "RPM",
                        Parent = new SensorParent { Id = "mb", Name = "Board" },
                    },
                },
            });
        });
    }
}

/// <summary>Cooling fan renames reach the Diagnostics and history responses as display labels only: ids, stored rules and stored history do not move.</summary>
public sealed class FanNameDisplayRoutesTests : IClassFixture<FanNameFactory>
{
    private const string BoardRule = "builtin:fan:lpc-ctl-1";
    private const string HubRule = "builtin:fan:np50:1-port1";

    private readonly FanNameFactory _factory;

    public FanNameDisplayRoutesTests(FanNameFactory factory)
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

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();

    private void Rename(string key, string? name) => Store.Update(s =>
    {
        if (name is null)
        {
            s.Cooling.FanNames.Remove(key);
        }
        else
        {
            s.Cooling.FanNames[key] = name;
        }
    });

    private static JsonElement Rule(JsonElement list, string id) =>
        list.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);

    private static object CustomFan(string name) => new
    {
        enabled = true,
        sensor = new { category = "motherboard", id = "/lpc/fan/1", name, type = "Fan", parentName = "Board" },
        raiseAfterSeconds = 5,
        clearAfterSeconds = 5,
        severity = "watch",
        notifyDesktop = true,
        showInHealth = true,
        repeatMinutes = 0,
    };

    [Fact]
    public async Task The_alerts_list_labels_built_in_fans_with_the_cooling_names_and_restores_them_when_cleared()
    {
        var client = Client();
        var raw = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");
        Assert.Equal("Fan #1", Rule(raw, BoardRule).GetProperty("sensor").GetProperty("name").GetString());

        Rename("/lpc/ctl/1", "CPU fan");
        Rename("motherboard", "My board");
        Rename("np50:1/port1", "Front intake");
        Rename("np50:1", "Radiator hub");
        var shown = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");

        var board = Rule(shown, BoardRule);
        Assert.Equal("CPU fan", board.GetProperty("sensor").GetProperty("name").GetString());
        Assert.Equal("CPU fan", board.GetProperty("source").GetProperty("sensor").GetProperty("name").GetString());
        Assert.Equal("My board", board.GetProperty("source").GetProperty("hardwareName").GetString());
        var hub = Rule(shown, HubRule);
        Assert.Equal("Front intake", hub.GetProperty("sensor").GetProperty("name").GetString());
        Assert.Equal("Radiator hub", hub.GetProperty("source").GetProperty("hardwareName").GetString());
        Assert.Equal(BoardRule, board.GetProperty("id").GetString());

        Rename("/lpc/ctl/1", null);
        Rename("motherboard", null);
        Rename("np50:1/port1", null);
        Rename("np50:1", null);
        var cleared = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");

        Assert.Equal("Fan #1", Rule(cleared, BoardRule).GetProperty("sensor").GetProperty("name").GetString());
        Assert.Equal("NP50 Hub", Rule(cleared, HubRule).GetProperty("source").GetProperty("hardwareName").GetString());
    }

    [Fact]
    public async Task A_put_on_a_renamed_built_in_fan_stores_the_hardware_name()
    {
        var client = Client();
        Rename("/lpc/ctl/1", "CPU fan");
        var listed = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");
        var shown = Rule(listed, BoardRule);
        Assert.Equal("CPU fan", shown.GetProperty("sensor").GetProperty("name").GetString());

        var put = await client.PutAsJsonAsync($"/diagnostics/alerts/{BoardRule}", shown);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var stored = Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == BoardRule);
        Assert.Equal("Fan #1", stored.Sensor.Name);
        Assert.Equal(BoardRule, stored.Id);
    }

    [Fact]
    public async Task A_post_from_the_renamed_picker_is_refused_as_a_duplicate_of_the_built_in_and_stores_nothing()
    {
        Rename("/lpc/ctl/1", "CPU fan");

        var created = await Client().PostAsJsonAsync("/diagnostics/alerts", CustomFan("CPU fan"));

        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);
        Assert.DoesNotContain(Store.Load().Diagnostics.SensorAlerts, r => r.BuiltIn is null);
    }

    [Fact]
    public async Task A_put_of_a_rule_stored_under_a_cooling_name_stores_the_hardware_name_and_clearing_keeps_it_resolving()
    {
        var client = Client();
        Rename("/lpc/ctl/1", "CPU fan");
        Store.Update(s => s.Diagnostics.SensorAlerts.Add(new SensorAlertRule
        {
            Id = "legacy",
            Sensor = new SensorRef { Category = "motherboard", Id = "/lpc/fan/1", Name = "CPU fan", Type = "Fan", ParentName = "Board" },
            RaiseAfterSeconds = 5,
            ClearAfterSeconds = 5,
        }));

        var put = await client.PutAsJsonAsync("/diagnostics/alerts/legacy", CustomFan("CPU fan"));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Fan #1", Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == "legacy").Sensor.Name);

        Rename("/lpc/ctl/1", null);
        var again = await client.PutAsJsonAsync("/diagnostics/alerts/legacy", CustomFan("Fan #1"));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("Fan #1", Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == "legacy").Sensor.Name);
    }

    [Fact]
    public async Task The_monitoring_history_names_the_fan_series_while_the_ids_and_stored_samples_stay()
    {
        var store = _factory.Services.GetRequiredService<IMetricsHistoryStore>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        store.Append(new[]
        {
            new MetricSample(now - 30, 1, 1, 0, 0, 1, Array.Empty<GpuReading>(), new[] { new FanReading("lpc-ctl-1", "Fan #1", 1200, 40) }),
        }, null);
        var client = Client();
        var url = $"/monitoring/history?from={(now - 60) * 1000}&to={(now + 60) * 1000}&series=fan:lpc-ctl-1";

        var raw = await client.GetFromJsonAsync<JsonElement>(url);
        Rename("/lpc/ctl/1", "CPU fan");
        var shown = await client.GetFromJsonAsync<JsonElement>(url);

        var rawSeries = raw.GetProperty("series").EnumerateArray().Single();
        var shownSeries = shown.GetProperty("series").EnumerateArray().Single();
        Assert.Equal("Fan #1", rawSeries.GetProperty("name").GetString());
        Assert.Equal("CPU fan", shownSeries.GetProperty("name").GetString());
        Assert.Equal("fan:lpc-ctl-1", shownSeries.GetProperty("id").GetString());
        Assert.Equal(rawSeries.GetProperty("points").GetRawText(), shownSeries.GetProperty("points").GetRawText());
        Rename("/lpc/ctl/1", null);
        var cleared = await client.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal("Fan #1", cleared.GetProperty("series").EnumerateArray().Single().GetProperty("name").GetString());
    }
}
