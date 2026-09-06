using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp.History;
using Nexus.Service.Mcp.History.Binary;
using Nexus.Service.Mcp.Tools;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// Unit coverage for the three history MCP tools. query_sensor_history and
/// get_history_summary run against a MonitoringSensorHistoryReader wrapping
/// an InMemoryMetricsHistoryStore - the same fallback store production uses
/// when BinaryMetricsHistoryStore can't open, and a lightweight stand-in for
/// it in tests (see MonitoringSensorHistoryReaderTests for the id-resolution
/// coverage). query_events runs against a real BinaryAiEventLog for the
/// happy paths and UnavailableAiEventLog for the log-unavailable path it
/// must report as isError rather than throw or return empty data.
/// </summary>
public sealed class HistoryToolsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "nexus-mcp-history-tools-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly BinaryAiEventLog _events;

    public HistoryToolsTests()
    {
        Directory.CreateDirectory(_dir);
        _events = new BinaryAiEventLog(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static long ToMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static MetricSample CpuTempSample(DateTime utc, double value) =>
        new(new DateTimeOffset(utc).ToUnixTimeSeconds(), null, null, null, null, value, Array.Empty<GpuReading>(), Array.Empty<FanReading>());

    private static McpTestHarness.StubSensorProvider EmptySensors() => new();

    private static QuerySensorHistoryTool Tool(MonitoringSensorHistoryReader reader, McpTestHarness.StubSensorProvider? sensors = null) =>
        new(reader, sensors ?? EmptySensors());

    // ── query_sensor_history ─────────────────────────────────────────────────

    [Fact]
    public async Task QuerySensorHistory_missing_sensorId_is_error()
    {
        var tool = Tool(new MonitoringSensorHistoryReader(new InMemoryMetricsHistoryStore()));

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task QuerySensorHistory_missing_minutes_is_error()
    {
        var tool = Tool(new MonitoringSensorHistoryReader(new InMemoryMetricsHistoryStore()));

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { sensorId = "cpu.temp" }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task QuerySensorHistory_unknown_sensor_is_error_listing_known_sensors()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[] { CpuTempSample(DateTime.UtcNow, 55) }, null);
        var tool = Tool(new MonitoringSensorHistoryReader(store));

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { sensorId = "does.not.exist", minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("cpu.temp", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuerySensorHistory_unknown_sensor_with_nothing_recorded_says_so()
    {
        var tool = Tool(new MonitoringSensorHistoryReader(new InMemoryMetricsHistoryStore()));

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { sensorId = "does.not.exist", minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("none recorded", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuerySensorHistory_happy_path_returns_points_tier_and_unit()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[] { CpuTempSample(DateTime.UtcNow, 55) }, null);
        var tool = Tool(new MonitoringSensorHistoryReader(store));

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { sensorId = "cpu.temp", minutes = 5 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("cpu.temp", doc.RootElement.GetProperty("sensorId").GetString());
        Assert.Equal("CPU Temperature", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("C", doc.RootElement.GetProperty("unit").GetString());
        Assert.Equal("raw", doc.RootElement.GetProperty("tier").GetString());
        var points = doc.RootElement.GetProperty("points").EnumerateArray();
        Assert.Single(points);
    }

    [Fact]
    public async Task QuerySensorHistory_accepts_a_raw_cpu_sensor_id_from_get_sensors()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[] { CpuTempSample(DateTime.UtcNow, 55) }, null);
        var sensors = EmptySensors();
        sensors.Cpu = new List<HardwareSensor>
        {
            new() { Id = "/amdcpu/0/temperature/2", Name = "Core (Tctl/Tdie)", Type = "Temperature" },
        };
        var tool = Tool(new MonitoringSensorHistoryReader(store), sensors);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { sensorId = "/amdcpu/0/temperature/2", minutes = 5 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("cpu.temp", doc.RootElement.GetProperty("sensorId").GetString());
    }

    [Fact]
    public async Task QuerySensorHistory_accepts_a_raw_gpu_sensor_id_from_get_sensors()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[]
        {
            new MetricSample(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds(), null, null, null, null, null,
                new[] { new GpuReading("gpu-0", "RTX 5080", "", 55, 65) }, Array.Empty<FanReading>()),
        }, null);
        var sensors = EmptySensors();
        sensors.Gpus = new List<GpuReadout>
        {
            new()
            {
                Id = "gpu-0",
                Name = "RTX 5080",
                Sensors = new List<HardwareSensor>
                {
                    new() { Id = "/nvidiagpu/0/load/0", Name = "GPU Core", Type = "Load" },
                },
            },
        };
        var tool = Tool(new MonitoringSensorHistoryReader(store), sensors);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { sensorId = "/nvidiagpu/0/load/0", minutes = 5 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("gpu.gpu-0.load", doc.RootElement.GetProperty("sensorId").GetString());
    }

    [Fact]
    public async Task QuerySensorHistory_unresolvable_raw_id_still_reports_the_original_id_in_the_error()
    {
        var sensors = EmptySensors();
        sensors.Motherboard = new List<HardwareSensor>
        {
            new() { Id = "/lpc/nct6798d/0/fan/2", Name = "Fan 3", Type = "Fan" },
        };
        var tool = Tool(new MonitoringSensorHistoryReader(new InMemoryMetricsHistoryStore()), sensors);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { sensorId = "/lpc/nct6798d/0/fan/2", minutes = 5 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("/lpc/nct6798d/0/fan/2", result.Text, StringComparison.Ordinal);
    }

    // ── get_history_summary ──────────────────────────────────────────────────

    [Fact]
    public async Task GetHistorySummary_missing_minutes_is_error()
    {
        var tool = new GetHistorySummaryTool(new MonitoringSensorHistoryReader(new InMemoryMetricsHistoryStore()));

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task GetHistorySummary_happy_path_reports_min_max_avg_latest()
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTime.UtcNow;
        store.Append(new[] { CpuTempSample(now.AddSeconds(-2), 40), CpuTempSample(now.AddSeconds(-1), 60) }, null);
        var tool = new GetHistorySummaryTool(new MonitoringSensorHistoryReader(store));

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { minutes = 5 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal(5, doc.RootElement.GetProperty("minutes").GetInt32());
        var sensor = Assert.Single(
            doc.RootElement.GetProperty("sensors").EnumerateArray(), s => s.GetProperty("sensorId").GetString() == "cpu.temp");
        Assert.Equal(40, sensor.GetProperty("min").GetDouble());
        Assert.Equal(60, sensor.GetProperty("max").GetDouble());
        Assert.Equal(60, sensor.GetProperty("latest").GetDouble());
        Assert.Equal(2, sensor.GetProperty("samples").GetInt32());
    }

    // ── query_events ──────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryEvents_missing_minutes_is_error()
    {
        var tool = new QueryEventsTool(_events);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task QueryEvents_invalid_type_is_error()
    {
        var tool = new QueryEventsTool(_events);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { minutes = 5, type = "not_a_type" }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task QueryEvents_happy_path_returns_newest_first_and_respects_limit()
    {
        // Offsets must stay at or before "now" - the tool windows against the
        // real wall clock, which would exclude a row timestamped later than it
        // has actually reached.
        var now = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            _events.RecordEvent(new AiHistoryEventRow(ToMs(now.AddSeconds(i - 3)), "ai_write", $"tool{i}", "{}", true, null));
        }
        var tool = new QueryEventsTool(_events);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { minutes = 5, limit = 2 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        var events = doc.RootElement.GetProperty("events").EnumerateArray();
        var first = Assert.Single(events, e => e.GetProperty("name").GetString() == "tool2");
        Assert.Equal("ai_write", first.GetProperty("type").GetString());
        Assert.True(first.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task QueryEvents_when_store_unavailable_is_error()
    {
        var tool = new QueryEventsTool(new UnavailableAiEventLog());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { minutes = 5 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("unavailable", result.Text, StringComparison.OrdinalIgnoreCase);
    }
}
