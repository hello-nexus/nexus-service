using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Mcp;
using Nexus.Service.Mcp.Tools;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Monitoring.Events;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// Unit coverage for the two write tools (add_monitoring_event,
/// calibrate_fans), plus registry/tools-list and input-schema sanity checks
/// shared across all seven history and action tools.
/// </summary>
public sealed class HistoryActionToolsTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "nexus-mcp-history-action-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TestableConfigStore NewConfigStore() => new(Path.Combine(_tempDir, "settings.json"));

    private static McpTestHarness.StubFanControlProvider NewFans() => new()
    {
        Channels = new List<FanChannel> { new() { Id = "fan-1", Name = "Fan 1" } },
    };

    private sealed class RecordingAuditSink : IMcpAuditSink
    {
        public List<McpAuditEntry> Entries { get; } = new();
        public void Record(McpAuditEntry entry) => Entries.Add(entry);
    }

    // A CalibrateAsync that only completes once the test releases Gate, so
    // "already running" is deterministic instead of racing the background task.
    private sealed class BlockingFanControlProvider : IFanControlProvider
    {
        public TaskCompletionSource<bool> Gate { get; } = new();
        public List<FanChannel> Channels { get; set; } = new() { new() { Id = "fan-1", Name = "Fan 1" } };

        public IReadOnlyList<FanChannel> GetFanChannels() => Channels;
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }

        public async Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
        {
            await Gate.Task.ConfigureAwait(false);
            return Array.Empty<FanCalibration>();
        }
    }

    // ── add_monitoring_event ────────────────────────────────────────────────

    [Fact]
    public async Task AddMonitoringEvent_happy_path_defaults_kind_to_custom()
    {
        var store = new InMemoryMetricsHistoryStore();
        var tool = new AddMonitoringEventTool(store);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { label = "Applied silent preset" }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("custom", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Applied silent preset", doc.RootElement.GetProperty("label").GetString());
        Assert.True(doc.RootElement.GetProperty("id").GetInt64() > 0);

        var stored = Assert.Single(((IMonitoringEventStore)store).Query(0, long.MaxValue, 10));
        Assert.True(stored.Custom);
    }

    [Fact]
    public async Task AddMonitoringEvent_missing_label_is_error()
    {
        var tool = new AddMonitoringEventTool(new InMemoryMetricsHistoryStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task AddMonitoringEvent_ignores_a_caller_supplied_kind_and_always_records_custom()
    {
        var store = new InMemoryMetricsHistoryStore();
        var tool = new AddMonitoringEventTool(store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { label = "USB attached", kind = MonitoringEventKinds.UsbAttach }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal(MonitoringEventKinds.Custom, doc.RootElement.GetProperty("kind").GetString());
        var stored = Assert.Single(((IMonitoringEventStore)store).Query(0, long.MaxValue, 10));
        Assert.Equal(MonitoringEventKinds.Custom, stored.Kind);
        Assert.True(stored.Custom);
    }

    [Fact]
    public async Task AddMonitoringEvent_detail_beyond_the_cap_is_truncated()
    {
        var store = new InMemoryMetricsHistoryStore();
        var tool = new AddMonitoringEventTool(store);
        var longDetail = new string('x', AddMonitoringEventTool.MaxDetailLength + 50);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { label = "x", detail = longDetail }), CancellationToken.None);

        Assert.False(result.IsError);
        var stored = Assert.Single(((IMonitoringEventStore)store).Query(0, long.MaxValue, 10));
        Assert.Equal(AddMonitoringEventTool.MaxDetailLength, stored.Detail!.Length);
    }

    // ── calibrate_fans ───────────────────────────────────────────────────────

    [Fact]
    public async Task CalibrateFans_happy_path_starts_calibration()
    {
        var fans = NewFans();
        var runner = new CalibrationRunner(new MultiplexHub());
        var tool = new CalibrateFansTool(fans, runner);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.True(doc.RootElement.GetProperty("started").GetBoolean());
    }

    [Fact]
    public async Task CalibrateFans_single_fan_echoes_it()
    {
        var fans = NewFans();
        var runner = new CalibrationRunner(new MultiplexHub());
        var tool = new CalibrateFansTool(fans, runner);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { fan = "fan-1" }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("fan-1", doc.RootElement.GetProperty("fan").GetString());
    }

    [Fact]
    public async Task CalibrateFans_unknown_fan_id_is_error_listing_known_ids()
    {
        var fans = NewFans();
        var runner = new CalibrationRunner(new MultiplexHub());
        var tool = new CalibrateFansTool(fans, runner);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { fan = "not-a-fan" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("fan-1", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CalibrateFans_gate_off_is_error()
    {
        var store = NewConfigStore();
        store.Update(s => s.Features.Cooling = false);
        var tool = new CalibrateFansTool(NewFans(), new CalibrationRunner(new MultiplexHub()), new FeatureGates(store));

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task CalibrateFans_already_running_is_error()
    {
        var fans = new BlockingFanControlProvider();
        var runner = new CalibrationRunner(new MultiplexHub());
        var tool = new CalibrateFansTool(fans, runner);

        var first = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.False(first.IsError);

        var second = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.True(second.IsError);
        Assert.Contains("already running", second.Text, StringComparison.OrdinalIgnoreCase);

        fans.Gate.SetResult(true);
    }

    // ── consent refusal ──────────────────────────────────────────────────────

    [Fact]
    public async Task Registry_refuses_a_read_tool_when_history_is_disallowed()
    {
        var store = NewConfigStore();
        store.Update(s => s.AiIntegration.AllowHistory = false);
        var tool = new AddMonitoringEventTool(new InMemoryMetricsHistoryStore());
        // add_monitoring_event is itself History-gated; use it purely to prove
        // the registry blocks on the capability flag before executing.
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, new RecordingAuditSink());

        var result = await registry.CallAsync(tool, JsonSerializer.SerializeToElement(new { label = "x" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Allow history access", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registry_refuses_calibrate_fans_when_cooling_is_disallowed()
    {
        var store = NewConfigStore();
        store.Update(s => s.AiIntegration.AllowCooling = false);
        var tool = new CalibrateFansTool(NewFans(), new CalibrationRunner(new MultiplexHub()));
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, new RecordingAuditSink());

        var result = await registry.CallAsync(tool, JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Allow cooling control", result.Text, StringComparison.Ordinal);
    }

    // ── audit ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registry_audits_a_successful_add_monitoring_event_call()
    {
        var store = NewConfigStore();
        var tool = new AddMonitoringEventTool(new InMemoryMetricsHistoryStore());
        var audit = new RecordingAuditSink();
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, audit);

        await registry.CallAsync(tool, JsonSerializer.SerializeToElement(new { label = "marker" }), CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("add_monitoring_event", entry.ToolName);
        Assert.True(entry.Success);
    }

    [Fact]
    public async Task Registry_audits_a_successful_calibrate_fans_call()
    {
        var store = NewConfigStore();
        var tool = new CalibrateFansTool(NewFans(), new CalibrationRunner(new MultiplexHub()));
        var audit = new RecordingAuditSink();
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, audit);

        await registry.CallAsync(tool, JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("calibrate_fans", entry.ToolName);
        Assert.True(entry.Success);
    }

    // ── registry / tools-list ────────────────────────────────────────────────

    private sealed class StubScreenTimeStore : Nexus.Service.Activity.Storage.IScreenTimeStore
    {
        public void RecordSession(string appName, string? appPath, long startedUtcMs, long endedUtcMs) { }
        public Nexus.Service.Models.Activity.DayBreakdown GetDay(DateOnly localDate) => new();
        public IReadOnlyList<Nexus.Service.Models.Activity.DayTotal> GetRange(DateOnly fromInclusive, DateOnly toInclusive) => Array.Empty<Nexus.Service.Models.Activity.DayTotal>();
        public IReadOnlyList<Nexus.Service.Models.Activity.FocusSessionRow> QuerySessions(long fromUtcMs, long toUtcMs) => Array.Empty<Nexus.Service.Models.Activity.FocusSessionRow>();
        public Nexus.Service.Models.Activity.AppHistory GetAppHistory(string appName, DateOnly fromInclusive, DateOnly toInclusive) => new();
        public IReadOnlyList<Nexus.Service.Models.Activity.AppUsage> GetTodayUsage(DateOnly today) => Array.Empty<Nexus.Service.Models.Activity.AppUsage>();
        public IReadOnlyList<Nexus.Service.Models.Activity.AppUsage> GetHourUsage(DateOnly localDate, int hourLocal) => Array.Empty<Nexus.Service.Models.Activity.AppUsage>();
        public int DeleteDay(DateOnly localDate) => 0;
        public int DeleteRange(DateOnly fromInclusive, DateOnly toInclusive) => 0;
        public int DeleteApp(string appName) => 0;
        public int DeleteAll() => 0;
        public void Dispose() { }
    }

    private sealed class StubProcessDetailProvider : Nexus.Service.Activity.IProcessDetailProvider
    {
        public Nexus.Service.Activity.ProcessFileDetail GetFileDetail(string path) => new(null, null, null, "unknown", null, null, null);
        public Task<string?> ComputeSha256Async(string path, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    [Fact]
    public void Registry_lists_all_seven_tools_with_correct_readOnlyHint()
    {
        var store = NewConfigStore();
        var tools = new IMcpTool[]
        {
            new GetIncidentsTool(new Nexus.Service.Diagnostics.EventLog.EventLogMonitor(), new Nexus.Service.Routes.SteamGameLibraryCache()),
            new GetTemperatureHistoryTool(new InMemoryMetricsHistoryStore(), new StubScreenTimeStore()),
            new GetGameSessionsTool(new Nexus.Service.Games.BinaryFpsSessionStore(Path.Combine(_tempDir, "fps-registry")), store),
            new GetScreenTimeTool(new StubScreenTimeStore(), store),
            new GetProcessInfoTool(
                new Nexus.Service.Activity.ProcessMonitor(new MultiplexHub()),
                new StubProcessDetailProvider(),
                new Nexus.Service.Monitoring.History.ProcessFirstSeenCache(new InMemoryMetricsHistoryStore())),
            new AddMonitoringEventTool(new InMemoryMetricsHistoryStore()),
            new CalibrateFansTool(NewFans(), new CalibrationRunner(new MultiplexHub())),
        };
        var registry = new McpToolRegistry(tools, store, new RecordingAuditSink());

        var byName = new Dictionary<string, IMcpTool>(StringComparer.Ordinal);
        foreach (var t in registry.Tools) byName[t.Name] = t;

        Assert.True(byName["get_incidents"].ReadOnly);
        Assert.True(byName["get_temperature_history"].ReadOnly);
        Assert.True(byName["get_game_sessions"].ReadOnly);
        Assert.True(byName["get_screen_time"].ReadOnly);
        Assert.True(byName["get_process_info"].ReadOnly);
        Assert.False(byName["add_monitoring_event"].ReadOnly);
        Assert.False(byName["calibrate_fans"].ReadOnly);
        Assert.Equal(7, byName.Count);

        // Every schema must be valid, self-describing JSON - a malformed
        // literal here would otherwise only surface as a wire-level 500.
        foreach (var t in tools)
        {
            using var doc = JsonDocument.Parse(t.InputSchemaJson);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
        }
    }
}
