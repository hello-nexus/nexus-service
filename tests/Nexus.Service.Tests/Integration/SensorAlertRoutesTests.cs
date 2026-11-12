using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Integration;

/// <summary>A host whose machine has a CPU, an integrated GPU and a discrete GPU, each with a temperature, two fan channels, and no drive or memory temperature.</summary>
public sealed class SensorAlertFactory : NexusAppFactory
{
    private sealed class TwoFans : Nexus.Service.Cooling.IFanControlProvider
    {
        public IReadOnlyList<Nexus.Service.Models.Cooling.FanChannel> GetFanChannels() => new[]
        {
            new Nexus.Service.Models.Cooling.FanChannel { Id = "/lpc/ctl/1", Name = "CPU Fan", Rpm = 1100 },
            new Nexus.Service.Models.Cooling.FanChannel { Id = "/lpc/ctl/2", Name = "Pump", Rpm = 2400, Kind = "pump" },
        };
        public IReadOnlyList<Nexus.Service.Models.Cooling.TemperatureSource> GetTemperatureSources() => Array.Empty<Nexus.Service.Models.Cooling.TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<Nexus.Service.Models.Cooling.FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<Nexus.Service.Models.Cooling.FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Nexus.Service.Models.Cooling.FanCalibration>>(Array.Empty<Nexus.Service.Models.Cooling.FanCalibration>());
    }

    private static HardwareSensor Temp(string id, string name, float value) => new()
    {
        Id = id,
        Name = name,
        Type = "Temperature",
        Value = value,
        Units = "°C",
        Parent = new SensorParent { Id = "p", Name = "p" },
    };

    private static GpuReadout Gpu(string id, string name, bool integrated) => new()
    {
        Id = id,
        Name = name,
        Integrated = integrated,
        Sensors = new List<HardwareSensor> { Temp(id + "/core", "GPU Core", 50) },
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Nexus.Service.Cooling.IFanControlProvider>();
            services.AddSingleton<Nexus.Service.Cooling.IFanControlProvider>(new TwoFans());
            services.RemoveAll<ISensorProvider>();
            services.AddSingleton<ISensorProvider>(new StubSensorProvider
            {
                CpuSensors = new[] { Temp("cpu/pkg", "CPU Package", 50) },
                Gpus = { Gpu("gpu-igpu", "Radeon Graphics", integrated: true), Gpu("gpu-rtx", "RTX 5080", integrated: false) },
            });
        });
    }
}

public sealed class SensorAlertRoutesTests : IClassFixture<SensorAlertFactory>
{
    private const string CpuRule = "builtin:cpu-temp";
    private const string IgpuRule = "builtin:gpu-temp:gpu-igpu";
    private const string GpuRule = "builtin:gpu-temp:gpu-rtx";
    private const string FanRule = "builtin:fan:lpc-ctl-1";
    private const string PumpRule = "builtin:fan:lpc-ctl-2";

    private readonly NexusAppFactory _factory;

    public SensorAlertRoutesTests(SensorAlertFactory factory)
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

    private static object Rule(double? low = null, double? high = 80, string severity = "watch", int repeat = 0, string sensorId = "cpu/temp") => new
    {
        enabled = true,
        sensor = new { category = "cpu", id = sensorId, name = "CPU Package " + sensorId, type = "Temperature", parentName = "CPU" },
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

        var updated = await client.PutAsJsonAsync($"/diagnostics/alerts/{id}", Rule(low: 10, high: 90, severity: "act"));
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
    public async Task A_user_rule_never_stores_a_client_sent_seed()
    {
        var client = Client();
        var body = new
        {
            enabled = true,
            sensor = new { category = "cpu", id = "cpu/seeded", name = "CPU Package", type = "Temperature", parentName = "CPU" },
            high = 80,
            raiseAfterSeconds = 5,
            severity = "watch",
            notifyDesktop = true,
            seededHigh = 80,
        };

        var created = await client.PostAsJsonAsync("/diagnostics/alerts", body);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = createdBody.GetProperty("id").GetString()!;
        var updated = await client.PutAsJsonAsync($"/diagnostics/alerts/{id}", body);
        var updatedBody = await updated.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(createdBody.TryGetProperty("seededHigh", out _));
        Assert.False(updatedBody.TryGetProperty("seededHigh", out _));
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
    public async Task The_cap_is_fifty_entries_counting_the_detected_built_ins()
    {
        var client = Client();
        // Five built-ins (cpu, two gpus, two fans) leave room for 45 user rules.
        for (var i = 0; i < 45; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/diagnostics/alerts", Rule(sensorId: $"cpu/t{i}"))).StatusCode);
        }

        var refused = await client.PostAsJsonAsync("/diagnostics/alerts", Rule(sensorId: "cpu/t45"));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("50", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("msg").GetString());
        // Built-ins are never refused: the list still carries all five.
        var list = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");
        Assert.Equal(5, list.GetProperty("rules").EnumerateArray().Count(r => r.GetProperty("builtIn").ValueKind != JsonValueKind.Null));
    }

    [Fact]
    public async Task A_fan_entry_switch_is_the_stall_monitoring_toggle_and_reset_turns_it_back_on()
    {
        var client = Client();
        var rules = (await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts")).GetProperty("rules").EnumerateArray().ToList();
        var fan = rules.Single(r => r.GetProperty("id").GetString() == FanRule);
        Assert.True(fan.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, fan.GetProperty("high").ValueKind);
        Assert.Equal("Fan", fan.GetProperty("sensor").GetProperty("type").GetString());

        var body = JsonSerializer.Deserialize<JsonElement>(fan.GetRawText());
        var off = await client.PutAsJsonAsync($"/diagnostics/alerts/{FanRule}", new
        {
            enabled = false, sensor = new { category = "builtin", id = "x", name = "x", type = "Fan", parentName = "" },
            low = (double?)null, high = (double?)null, raiseAfterSeconds = 60, clearAfterSeconds = 5, severity = "watch", notifyDesktop = true, showInHealth = true, repeatMinutes = 0,
            builtIn = "fanSpeed",
        });

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var settings = _factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>().Load();
        Assert.Contains("cooling:/lpc/ctl/1", settings.Diagnostics.IgnoredComponents);
        var listed = (await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts")).GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("id").GetString() == FanRule);
        Assert.False(listed.GetProperty("enabled").GetBoolean());
        Assert.Equal("fanSpeed", body.GetProperty("builtIn").GetString());

        await client.PostAsync($"/diagnostics/alerts/builtins/reset?id={FanRule}", null);

        Assert.DoesNotContain("cooling:/lpc/ctl/1", _factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>().Load().Diagnostics.IgnoredComponents);
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
    public async Task Bad_severity_is_a_400()
    {
        var client = Client();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/diagnostics/alerts", Rule(severity: "loud"))).StatusCode);
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
    public async Task Built_ins_are_one_per_detected_device_listed_first_in_chart_order()
    {
        var client = Client();
        await client.PostAsJsonAsync("/diagnostics/alerts", Rule());

        var list = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");

        var rules = list.GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(new[] { CpuRule, IgpuRule, GpuRule }, rules.Take(3).Select(r => r.GetProperty("id").GetString()).ToArray());
        Assert.Equal(new[] { "cpuTemp", "gpuTemp", "gpuTemp" }, rules.Take(3).Select(r => r.GetProperty("builtIn").GetString()).ToArray());
        Assert.Equal(new[] { "test-cpu", "Radeon Graphics", "RTX 5080" }, rules.Take(3).Select(r => r.GetProperty("sensor").GetProperty("name").GetString()).ToArray());
        Assert.Equal(new[] { "cpu-temp", "gpu-temp:gpu-igpu", "gpu-temp:gpu-rtx" }, rules.Take(3).Select(r => r.GetProperty("sensor").GetProperty("id").GetString()).ToArray());
        Assert.All(rules.Take(3), r => Assert.Equal("builtin", r.GetProperty("sensor").GetProperty("category").GetString()));
        Assert.Equal(new double[] { 100, 100, 90 }, rules.Take(3).Select(r => r.GetProperty("high").GetDouble()).ToArray());
        Assert.Equal(new[] { FanRule, PumpRule }, rules.Skip(3).Take(2).Select(r => r.GetProperty("id").GetString()).ToArray());
        Assert.Equal(JsonValueKind.Null, rules[5].GetProperty("builtIn").ValueKind);
        Assert.Equal(6, rules.Count);
    }

    private static object OnSensor(string category, string id, string name, string parent = "p") => new
    {
        enabled = true,
        sensor = new { category, id, name, type = "Temperature", parentName = parent },
        low = (double?)null, high = (double?)80, raiseAfterSeconds = 5, clearAfterSeconds = 5, severity = "watch", notifyDesktop = true, showInHealth = true, repeatMinutes = 0,
    };

    [Fact]
    public async Task Built_ins_expose_the_exact_underlying_sensor_in_the_picker_identity()
    {
        var list = await Client().GetFromJsonAsync<JsonElement>("/diagnostics/alerts");

        var rules = list.GetProperty("rules").EnumerateArray().ToList();
        var cpu = rules.Single(r => r.GetProperty("id").GetString() == CpuRule).GetProperty("source");
        Assert.Equal("sensor", cpu.GetProperty("kind").GetString());
        Assert.Equal("cpu", cpu.GetProperty("sensor").GetProperty("category").GetString());
        Assert.Equal("cpu/pkg", cpu.GetProperty("sensor").GetProperty("id").GetString());
        Assert.Equal("CPU Package", cpu.GetProperty("sensor").GetProperty("name").GetString());
        Assert.Equal("p", cpu.GetProperty("sensor").GetProperty("parentName").GetString());
        var gpu = rules.Single(r => r.GetProperty("id").GetString() == GpuRule).GetProperty("source");
        Assert.Equal("gpu", gpu.GetProperty("sensor").GetProperty("category").GetString());
        Assert.Equal("gpu-rtx/core", gpu.GetProperty("sensor").GetProperty("id").GetString());
        Assert.Equal("RTX 5080", gpu.GetProperty("hardwareName").GetString());
    }

    [Fact]
    public async Task A_user_rule_has_no_source_and_a_built_in_put_returns_one_without_storing_it()
    {
        var client = Client();
        var created = await (await client.PostAsJsonAsync("/diagnostics/alerts", Rule())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(created.TryGetProperty("source", out _));

        var put = await (await client.PutAsJsonAsync($"/diagnostics/alerts/{CpuRule}", BuiltInBody(high: 88))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("cpu/pkg", put.GetProperty("source").GetProperty("sensor").GetProperty("id").GetString());
        var store = _factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>();
        store.Read(st => Assert.All(st.Diagnostics.SensorAlerts, r => Assert.Null(r.Source)));
    }

    [Fact]
    public async Task Creating_a_rule_on_a_sensor_a_built_in_tracks_is_a_409_naming_that_rule()
    {
        var res = await Client().PostAsJsonAsync("/diagnostics/alerts", OnSensor("gpu", "gpu-rtx/core", "GPU Core"));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("error").GetBoolean());
        Assert.Equal("sensor_already_tracked", body.GetProperty("code").GetString());
        Assert.Equal(GpuRule, body.GetProperty("ruleId").GetString());
        Assert.Equal("RTX 5080", body.GetProperty("ruleName").GetString());
        Assert.True(body.GetProperty("builtIn").GetBoolean());
    }

    [Fact]
    public async Task Creating_a_rule_on_a_sensor_another_custom_rule_tracks_is_a_409_and_a_different_sensor_is_fine()
    {
        var client = Client();
        var first = await (await client.PostAsJsonAsync("/diagnostics/alerts", OnSensor("motherboard", "mb/1", "VRM"))).Content.ReadFromJsonAsync<JsonElement>();

        var dup = await client.PostAsJsonAsync("/diagnostics/alerts", OnSensor("motherboard", "mb/1", "VRM"));

        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var body = await dup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sensor_already_tracked", body.GetProperty("code").GetString());
        Assert.Equal(first.GetProperty("id").GetString(), body.GetProperty("ruleId").GetString());
        Assert.False(body.GetProperty("builtIn").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/diagnostics/alerts", OnSensor("motherboard", "mb/2", "VRM B"))).StatusCode);
    }

    [Fact]
    public async Task Re_pointing_a_rule_at_a_tracked_sensor_is_a_409_but_editing_its_limits_is_not()
    {
        var client = Client();
        var a = await (await client.PostAsJsonAsync("/diagnostics/alerts", OnSensor("motherboard", "mb/1", "VRM"))).Content.ReadFromJsonAsync<JsonElement>();
        var b = await (await client.PostAsJsonAsync("/diagnostics/alerts", OnSensor("motherboard", "mb/2", "VRM B"))).Content.ReadFromJsonAsync<JsonElement>();
        var bId = b.GetProperty("id").GetString();

        var toA = await client.PutAsJsonAsync($"/diagnostics/alerts/{bId}", OnSensor("motherboard", "mb/1", "VRM"));
        var toBuiltIn = await client.PutAsJsonAsync($"/diagnostics/alerts/{bId}", OnSensor("cpu", "cpu/pkg", "CPU Package"));
        var limits = await client.PutAsJsonAsync($"/diagnostics/alerts/{bId}", OnSensor("motherboard", "mb/2", "VRM B"));

        Assert.Equal(HttpStatusCode.Conflict, toA.StatusCode);
        Assert.Equal(a.GetProperty("id").GetString(), (await toA.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ruleId").GetString());
        Assert.Equal(HttpStatusCode.Conflict, toBuiltIn.StatusCode);
        Assert.Equal(CpuRule, (await toBuiltIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ruleId").GetString());
        Assert.Equal(HttpStatusCode.OK, limits.StatusCode);
    }

    [Fact]
    public async Task Existing_duplicates_load_and_their_limits_stay_editable()
    {
        var client = Client();
        var store = _factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>();
        store.Update(st =>
        {
            foreach (var id in new[] { "dup1", "dup2" })
            {
                st.Diagnostics.SensorAlerts.Add(new Nexus.Service.Persistence.SensorAlertRule
                {
                    Id = id,
                    Sensor = new Nexus.Service.Persistence.SensorRef { Category = "motherboard", Id = "mb/9", Name = "VRM", Type = "Temperature", ParentName = "p" },
                    High = 80,
                });
            }
        });

        var list = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");
        Assert.Equal(2, list.GetProperty("rules").EnumerateArray().Count(r => r.GetProperty("id").GetString()!.StartsWith("dup")));

        var edit = await client.PutAsJsonAsync("/diagnostics/alerts/dup2", OnSensor("motherboard", "mb/9", "VRM"));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
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

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/diagnostics/alerts/{CpuRule}")).StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");
        Assert.Contains(list.GetProperty("rules").EnumerateArray(), r => r.GetProperty("id").GetString() == CpuRule);
    }

    [Fact]
    public async Task A_put_on_a_built_in_keeps_its_fixed_sensor_and_takes_the_edits()
    {
        var client = Client();

        var res = await client.PutAsJsonAsync($"/diagnostics/alerts/{GpuRule}", BuiltInBody(high: 88, low: 10));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rule = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GpuRule, rule.GetProperty("id").GetString());
        Assert.Equal("gpuTemp", rule.GetProperty("builtIn").GetString());
        Assert.Equal("builtin", rule.GetProperty("sensor").GetProperty("category").GetString());
        Assert.Equal("gpu-temp:gpu-rtx", rule.GetProperty("sensor").GetProperty("id").GetString());
        Assert.Equal("RTX 5080", rule.GetProperty("sensor").GetProperty("name").GetString());
        Assert.Equal("Temperature", rule.GetProperty("sensor").GetProperty("type").GetString());
        Assert.Equal(20, rule.GetProperty("low").GetDouble());
        Assert.Equal(88, rule.GetProperty("high").GetDouble());
        Assert.Equal(30, rule.GetProperty("raiseAfterSeconds").GetInt32());
        Assert.Equal("act", rule.GetProperty("severity").GetString());
        Assert.False(rule.GetProperty("notifyDesktop").GetBoolean());
    }

    [Fact]
    public async Task A_put_clamps_a_built_in_limit_and_accepts_none()
    {
        var client = Client();

        var clamped = await (await client.PutAsJsonAsync($"/diagnostics/alerts/{CpuRule}", BuiltInBody(high: 300))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(100, clamped.GetProperty("high").GetDouble());
        var none = await client.PutAsJsonAsync($"/diagnostics/alerts/{CpuRule}", BuiltInBody(high: null));
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("high").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/diagnostics/alerts/{CpuRule}", BuiltInBody(high: 70, low: 80))).StatusCode);
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
        await client.PutAsJsonAsync($"/diagnostics/alerts/{GpuRule}", BuiltInBody(high: 70, low: 62));
        await client.PutAsJsonAsync($"/diagnostics/alerts/{CpuRule}", BuiltInBody(high: 70));

        var one = await client.PostAsync($"/diagnostics/alerts/builtins/reset?id={GpuRule}", null);

        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        var afterOne = (await one.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rules").EnumerateArray().ToList();
        var gpuAfter = afterOne.Single(r => r.GetProperty("id").GetString() == GpuRule);
        Assert.Equal(90, gpuAfter.GetProperty("high").GetDouble());
        Assert.Equal(JsonValueKind.Null, gpuAfter.GetProperty("low").ValueKind);
        Assert.Equal(70, afterOne.Single(r => r.GetProperty("id").GetString() == CpuRule).GetProperty("high").GetDouble());

        var all = await (await client.PostAsync("/diagnostics/alerts/builtins/reset", null)).Content.ReadFromJsonAsync<JsonElement>();
        var rules = all.GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(100, rules.Single(r => r.GetProperty("id").GetString() == CpuRule).GetProperty("high").GetDouble());
        Assert.Equal(6, rules.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/diagnostics/alerts/builtins/reset?id=nope", null)).StatusCode);
    }

    [Fact]
    public async Task Notify_mobile_defaults_off_round_trips_on_user_and_built_in_rules_and_reset_clears_it()
    {
        var client = Client();
        var plain = await (await client.PostAsJsonAsync("/diagnostics/alerts", Rule())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(plain.GetProperty("notifyMobile").GetBoolean());

        var user = await (await client.PostAsJsonAsync("/diagnostics/alerts", Rule(sensorId: "cpu/other"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(user.GetProperty("notifyMobile").GetBoolean());
        var userId = user.GetProperty("id").GetString()!;
        var put = await client.PutAsJsonAsync($"/diagnostics/alerts/{userId}", new
        {
            enabled = true, sensor = new { category = "cpu", id = "cpu/other", name = "CPU Package cpu/other", type = "Temperature", parentName = "CPU" },
            low = (double?)null, high = (double?)80, raiseAfterSeconds = 5, clearAfterSeconds = 5, severity = "watch", notifyDesktop = false, notifyMobile = true, showInHealth = true, repeatMinutes = 0,
        });
        Assert.True((await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notifyMobile").GetBoolean());

        var builtIn = await client.PutAsJsonAsync($"/diagnostics/alerts/{GpuRule}", new
        {
            enabled = true, sensor = new { category = "gpu", id = "x", name = "x", type = "Temperature", parentName = "" },
            low = (double?)null, high = (double?)88, raiseAfterSeconds = 30, clearAfterSeconds = 5, severity = "act", notifyDesktop = true, notifyMobile = true, showInHealth = true, repeatMinutes = 0,
            builtIn = "gpuTemp",
        });
        Assert.True((await builtIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notifyMobile").GetBoolean());
        var stored = _factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>().Load().Diagnostics.SensorAlerts;
        Assert.True(stored.Single(r => r.Id == GpuRule).NotifyMobile);
        Assert.True(stored.Single(r => r.Id == userId).NotifyMobile);

        var reset = await (await client.PostAsync($"/diagnostics/alerts/builtins/reset?id={GpuRule}", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(reset.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("id").GetString() == GpuRule).GetProperty("notifyMobile").GetBoolean());
    }

    [Fact]
    public async Task A_put_or_reset_on_a_built_in_whose_device_is_not_detected_is_a_404()
    {
        var client = Client();
        const string absent = "builtin:drive-temp:gone";

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/diagnostics/alerts/{absent}", BuiltInBody())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/diagnostics/alerts/builtins/reset?id={absent}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/diagnostics/alerts/builtin-cpu", BuiltInBody())).StatusCode);
    }

    [Fact]
    public async Task The_history_series_of_a_built_in_is_the_monitoring_series_relabeled_with_the_rule_id()
    {
        var client = Client();
        await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");
        var buffer = _factory.Services.GetRequiredService<Nexus.Service.Monitoring.History.MetricsSampleBuffer>();
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30;
        for (var i = 0; i < 5; i++)
        {
            buffer.Append(new Nexus.Service.Monitoring.History.MetricSample(
                t0 + i, 10, 20, 0, 0, 61 + i,
                new[] { new Nexus.Service.Monitoring.History.GpuReading("gpu-rtx", "RTX 5080", "", 10, 70 + i) },
                Array.Empty<Nexus.Service.Monitoring.History.FanReading>(), "test-cpu"));
        }
        var from = (t0 - 5) * 1000;
        var to = (t0 + 20) * 1000;

        var res = await client.GetFromJsonAsync<JsonElement>($"/diagnostics/alerts/history-series?from={from}&to={to}");

        var series = res.GetProperty("series").EnumerateArray().ToList();
        var cpu = series.Single(x => x.GetProperty("id").GetString() == CpuRule);
        Assert.Equal("sensorAlert", cpu.GetProperty("kind").GetString());
        Assert.Equal("test-cpu", cpu.GetProperty("name").GetString());
        Assert.Contains(cpu.GetProperty("points").EnumerateArray(), p => p.GetProperty("avg").ValueKind == JsonValueKind.Number && p.GetProperty("avg").GetDouble() >= 61);
        var gpu = series.Single(x => x.GetProperty("id").GetString() == GpuRule);
        Assert.Equal("RTX 5080", gpu.GetProperty("name").GetString());
        Assert.DoesNotContain(series, x => x.GetProperty("id").GetString() == IgpuRule);
    }

    [Fact]
    public async Task A_fan_entry_plots_the_per_channel_rpm_series_the_sampler_records()
    {
        var client = Client();
        await client.GetFromJsonAsync<JsonElement>("/diagnostics/alerts");
        var buffer = _factory.Services.GetRequiredService<Nexus.Service.Monitoring.History.MetricsSampleBuffer>();
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30;
        for (var i = 0; i < 5; i++)
        {
            buffer.Append(new Nexus.Service.Monitoring.History.MetricSample(
                t0 + i, 10, 20, 0, 0, 61,
                Array.Empty<Nexus.Service.Monitoring.History.GpuReading>(),
                new[] { new Nexus.Service.Monitoring.History.FanReading("lpc-ctl-1", "CPU Fan", 1000 + i, 40) }, "test-cpu"));
        }

        var res = await client.GetFromJsonAsync<JsonElement>($"/diagnostics/alerts/history-series?from={(t0 - 5) * 1000}&to={(t0 + 20) * 1000}&ruleId={FanRule}");

        var fan = Assert.Single(res.GetProperty("series").EnumerateArray());
        Assert.Equal(FanRule, fan.GetProperty("id").GetString());
        Assert.Equal("CPU Fan", fan.GetProperty("name").GetString());
        Assert.Contains(fan.GetProperty("points").EnumerateArray(), p => p.GetProperty("avg").ValueKind == JsonValueKind.Number && p.GetProperty("avg").GetDouble() >= 1000);
    }
}
