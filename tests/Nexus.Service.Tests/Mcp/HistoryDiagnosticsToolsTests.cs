using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Activity.Storage;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Games;
using Nexus.Service.Mcp.Tools;
using Nexus.Service.Models.Activity;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Routes;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// Unit coverage for the five read-only history/diagnostics MCP tools:
/// get_incidents, get_temperature_history, get_game_sessions,
/// get_screen_time, get_process_info.
/// </summary>
public sealed class HistoryDiagnosticsToolsTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "nexus-mcp-history-diag-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TestableConfigStore NewConfigStore() => new(Path.Combine(_tempDir, "settings.json"));

    private static MetricSample CpuTempSample(DateTime utc, double value) =>
        new(new DateTimeOffset(utc).ToUnixTimeSeconds(), null, null, null, null, value, Array.Empty<GpuReading>(), Array.Empty<FanReading>());

    private sealed class FakeScreenTimeStore : IScreenTimeStore
    {
        public DayBreakdown Day { get; set; } = new();
        public List<DayTotal> Range { get; set; } = new();
        public AppHistory History { get; set; } = new();
        public List<AppUsage> HourUsage { get; set; } = new();
        public List<FocusSessionRow> Sessions { get; set; } = new();

        public void RecordSession(string appName, string? appPath, long startedUtcMs, long endedUtcMs) { }
        public DayBreakdown GetDay(DateOnly localDate) => Day;
        public IReadOnlyList<DayTotal> GetRange(DateOnly fromInclusive, DateOnly toInclusive) => Range;
        public IReadOnlyList<FocusSessionRow> QuerySessions(long fromUtcMs, long toUtcMs) => Sessions;
        public AppHistory GetAppHistory(string appName, DateOnly fromInclusive, DateOnly toInclusive) => History;
        public IReadOnlyList<AppUsage> GetTodayUsage(DateOnly today) => new List<AppUsage>();
        public IReadOnlyList<AppUsage> GetHourUsage(DateOnly localDate, int hourLocal) => HourUsage;
        public int DeleteDay(DateOnly localDate) => 0;
        public int DeleteRange(DateOnly fromInclusive, DateOnly toInclusive) => 0;
        public int DeleteApp(string appName) => 0;
        public int DeleteAll() => 0;
        public void Dispose() { }
    }

    private sealed class FakeProcessDetailProvider : IProcessDetailProvider
    {
        public ProcessFileDetail GetFileDetail(string path) =>
            new("Test App", "1.2.3", "Test Co", "signed", "Test Publisher", 1_000, 2_000);
        public Task<string?> ComputeSha256Async(string path, CancellationToken ct) => Task.FromResult<string?>("deadbeef");
    }

    // ── get_incidents ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetIncidents_happy_path_reports_window_days_and_supported_flag()
    {
        var tool = new GetIncidentsTool(new EventLogMonitor(), new SteamGameLibraryCache());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { days = 3 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal(3, doc.RootElement.GetProperty("windowDays").GetInt32());
        Assert.Equal(OperatingSystem.IsWindows(), doc.RootElement.GetProperty("supported").GetBoolean());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("incidents").ValueKind);
    }

    [Fact]
    public async Task GetIncidents_days_beyond_cap_clamps_to_max()
    {
        var tool = new GetIncidentsTool(new EventLogMonitor(), new SteamGameLibraryCache());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { days = 9999 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal(GetIncidentsTool.MaxDays, doc.RootElement.GetProperty("windowDays").GetInt32());
    }

    [Fact]
    public async Task GetIncidents_missing_days_defaults()
    {
        var tool = new GetIncidentsTool(new EventLogMonitor(), new SteamGameLibraryCache());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal(GetIncidentsTool.DefaultDays, doc.RootElement.GetProperty("windowDays").GetInt32());
    }

    // ── get_temperature_history ─────────────────────────────────────────────

    [Fact]
    public async Task GetTemperatureHistory_happy_path_returns_cpu_series_with_app_overlay()
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTime.UtcNow;
        store.Append(new[] { CpuTempSample(now.AddSeconds(-2), 55) }, null);

        var bucketStartMs = new DateTimeOffset(now).ToUnixTimeMilliseconds() / 300_000L * 300_000L;
        var screenTime = new FakeScreenTimeStore
        {
            Sessions = new List<FocusSessionRow> { new("chrome.exe", null, bucketStartMs, bucketStartMs + 60_000) },
        };
        var tool = new GetTemperatureHistoryTool(store, screenTime);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { hours = 1 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal(5, doc.RootElement.GetProperty("bucketMinutes").GetInt32());
        var series = Assert.Single(doc.RootElement.GetProperty("series").EnumerateArray());
        Assert.Equal("cpu", series.GetProperty("id").GetString());
        var point = Assert.Single(series.GetProperty("points").EnumerateArray());
        Assert.Equal(55, point.GetProperty("avg").GetDouble());
        Assert.Equal("chrome.exe", point.GetProperty("dominantApp").GetString());
    }

    [Theory]
    [InlineData(24, 5)]
    [InlineData(48, 15)]
    [InlineData(100, 30)]
    [InlineData(200, 60)]
    public async Task GetTemperatureHistory_bucket_width_matches_TierWidthMinutesFor(int hours, int expectedTierMinutes)
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[] { CpuTempSample(DateTime.UtcNow.AddMinutes(-10), 60) }, null);
        var tool = new GetTemperatureHistoryTool(store, new FakeScreenTimeStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { hours }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal(TemperatureInsights.TierWidthMinutesFor(hours), doc.RootElement.GetProperty("bucketMinutes").GetInt32());
        Assert.Equal(expectedTierMinutes, doc.RootElement.GetProperty("bucketMinutes").GetInt32());
    }

    [Fact]
    public void GetTemperatureHistory_MaxHours_matches_the_rest_routes_cap()
    {
        Assert.Equal(DiagnosticsHealthRoutes.MaxTemperatureHours, GetTemperatureHistoryTool.MaxHours);
    }

    [Fact]
    public async Task GetTemperatureHistory_hours_beyond_the_route_cap_excludes_data_outside_it()
    {
        var store = new InMemoryMetricsHistoryStore();
        // Older than MaxHours (336h/14d) but well inside the store's own retention.
        store.Append(new[] { CpuTempSample(DateTime.UtcNow.AddHours(-400), 55) }, null);
        var tool = new GetTemperatureHistoryTool(store, new FakeScreenTimeStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { hours = 1000 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Empty(doc.RootElement.GetProperty("series").EnumerateArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(72)]
    [InlineData(168)]
    [InlineData(GetTemperatureHistoryTool.MaxHours)]
    public void GetTemperatureHistory_tiering_keeps_merged_buckets_within_the_route_point_backstop(int hours)
    {
        var maxPossibleBuckets = hours * 60 / TemperatureInsights.TierWidthMinutesFor(hours);

        Assert.True(maxPossibleBuckets <= DiagnosticsHealthRoutes.MaxPointsPerSeries);
    }

    [Fact]
    public async Task GetTemperatureHistory_unknown_component_id_is_error_listing_known_ids()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[] { CpuTempSample(DateTime.UtcNow, 50) }, null);
        var tool = new GetTemperatureHistoryTool(store, new FakeScreenTimeStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { id = "not-a-component", hours = 1 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("cpu", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTemperatureHistory_invalid_date_is_error()
    {
        var tool = new GetTemperatureHistoryTool(new InMemoryMetricsHistoryStore(), new FakeScreenTimeStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { date = "not-a-date" }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── get_game_sessions ────────────────────────────────────────────────────

    private BinaryFpsSessionStore NewFpsStore() => new(Path.Combine(_tempDir, "fps"));

    private static FpsSessionRecord Session(string gameKey, long startedUtcMs) => new(
        Guid.NewGuid(), gameKey, "Test Game", "steam", startedUtcMs, startedUtcMs + 60_000,
        60, 60, 3600, 55, 65, new uint[FpsHistogram.BucketCount],
        1920, 1080, 144, 1920, 1080, true, false, 0, 0, FpsUploadState.Pending);

    [Fact]
    public async Task GetGameSessions_without_game_lists_summaries_when_supported()
    {
        using var store = NewFpsStore();
        store.Append(Session("game-1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var tool = new GetGameSessionsTool(store, NewConfigStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal(OperatingSystem.IsWindows(), doc.RootElement.GetProperty("supported").GetBoolean());
        if (OperatingSystem.IsWindows())
        {
            var game = Assert.Single(doc.RootElement.GetProperty("games").EnumerateArray());
            Assert.Equal("game-1", game.GetProperty("gameKey").GetString());
            Assert.Equal(3600, game.GetProperty("frames").GetInt64());
        }
        else
        {
            Assert.Empty(doc.RootElement.GetProperty("games").EnumerateArray());
        }
    }

    [Fact]
    public async Task GetGameSessions_with_known_game_returns_its_sessions_regardless_of_os()
    {
        using var store = NewFpsStore();
        var startedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Append(Session("game-1", startedUtcMs));
        var tool = new GetGameSessionsTool(store, NewConfigStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { game = "game-1" }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("game-1", doc.RootElement.GetProperty("game").GetString());
        var session = Assert.Single(doc.RootElement.GetProperty("sessions").EnumerateArray());
        Assert.Equal(startedUtcMs, session.GetProperty("startedUtcMs").GetInt64());
        Assert.Equal(60, session.GetProperty("avgFps").GetDouble());
    }

    [Fact]
    public async Task GetGameSessions_unknown_game_key_is_error()
    {
        using var store = NewFpsStore();
        var tool = new GetGameSessionsTool(store, NewConfigStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { game = "not-a-game" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("no games recorded", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetGameSessions_reports_tracking_disabled()
    {
        using var store = NewFpsStore();
        var config = NewConfigStore();
        config.Update(s => s.Fps.TrackingEnabled = false);
        var tool = new GetGameSessionsTool(store, config);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Text);
        Assert.False(doc.RootElement.GetProperty("trackingEnabled").GetBoolean());
    }

    // ── get_screen_time ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetScreenTime_date_mode_returns_day_breakdown()
    {
        var screenTime = new FakeScreenTimeStore { Day = new DayBreakdown { Date = "2026-09-01", TotalMs = 1000 } };
        var tool = new GetScreenTimeTool(screenTime, NewConfigStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { date = "2026-09-01" }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("day", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1000, doc.RootElement.GetProperty("day").GetProperty("totalMs").GetInt64());
    }

    [Fact]
    public async Task GetScreenTime_date_and_hour_returns_hour_usage()
    {
        var screenTime = new FakeScreenTimeStore { HourUsage = new List<AppUsage> { new() { Name = "chrome.exe", TotalMs = 500 } } };
        var tool = new GetScreenTimeTool(screenTime, NewConfigStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { date = "2026-09-01", hour = 14 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("hour", doc.RootElement.GetProperty("mode").GetString());
        Assert.Single(doc.RootElement.GetProperty("hour").EnumerateArray());
    }

    [Fact]
    public async Task GetScreenTime_range_mode_returns_day_totals()
    {
        var screenTime = new FakeScreenTimeStore { Range = new List<DayTotal> { new() { Date = "2026-09-01", TotalMs = 100 } } };
        var tool = new GetScreenTimeTool(screenTime, NewConfigStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { from = "2026-09-01", to = "2026-09-02" }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("range", doc.RootElement.GetProperty("mode").GetString());
        Assert.Single(doc.RootElement.GetProperty("range").EnumerateArray());
    }

    [Fact]
    public async Task GetScreenTime_app_mode_returns_app_history()
    {
        var screenTime = new FakeScreenTimeStore { History = new AppHistory { AppName = "chrome.exe", TotalMs = 200 } };
        var tool = new GetScreenTimeTool(screenTime, NewConfigStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { app = "chrome.exe", from = "2026-09-01", to = "2026-09-02" }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("app", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("chrome.exe", doc.RootElement.GetProperty("app").GetProperty("appName").GetString());
    }

    [Fact]
    public async Task GetScreenTime_range_beyond_the_cap_is_error()
    {
        var tool = new GetScreenTimeTool(new FakeScreenTimeStore(), NewConfigStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { from = "2020-01-01", to = "2026-09-01" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains(GetScreenTimeTool.MaxRangeDays.ToString(), result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetScreenTime_app_range_beyond_the_cap_is_error()
    {
        var tool = new GetScreenTimeTool(new FakeScreenTimeStore(), NewConfigStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { app = "chrome.exe", from = "2020-01-01", to = "2026-09-01" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains(GetScreenTimeTool.MaxRangeDays.ToString(), result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetScreenTime_no_mode_arguments_is_error()
    {
        var tool = new GetScreenTimeTool(new FakeScreenTimeStore(), NewConfigStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task GetScreenTime_invalid_date_is_error()
    {
        var tool = new GetScreenTimeTool(new FakeScreenTimeStore(), NewConfigStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { date = "not-a-date" }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task GetScreenTime_reports_tracking_disabled()
    {
        var config = NewConfigStore();
        config.Update(s => s.ScreenTime.TrackingEnabled = false);
        var tool = new GetScreenTimeTool(new FakeScreenTimeStore(), config);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { from = "2026-09-01", to = "2026-09-02" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Text);
        Assert.False(doc.RootElement.GetProperty("trackingEnabled").GetBoolean());
    }

    // ── get_process_info ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetProcessInfo_missing_name_is_error()
    {
        var processes = new ProcessMonitor(new MultiplexHub());
        var tool = new GetProcessInfoTool(processes, new FakeProcessDetailProvider(), new ProcessFirstSeenCache(new InMemoryMetricsHistoryStore()));

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task GetProcessInfo_unknown_process_is_error()
    {
        var processes = new ProcessMonitor(new MultiplexHub());
        var tool = new GetProcessInfoTool(processes, new FakeProcessDetailProvider(), new ProcessFirstSeenCache(new InMemoryMetricsHistoryStore()));

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { name = "not-a-real-process.exe" }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task GetProcessInfo_happy_path_resolves_the_current_test_process()
    {
        var processes = new ProcessMonitor(new MultiplexHub());
        var currentProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        processes.SetProcessesForTest(new List<ProcessInfo>
        {
            new() { Pid = Environment.ProcessId, Name = currentProcessName, StartedAtMs = 123_456 },
        });
        var tool = new GetProcessInfoTool(processes, new FakeProcessDetailProvider(), new ProcessFirstSeenCache(new InMemoryMetricsHistoryStore()));

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { name = currentProcessName }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.True(doc.RootElement.GetProperty("supported").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("instanceCount").GetInt32());
        Assert.Equal("Test App", doc.RootElement.GetProperty("description").GetString());
        Assert.Equal("deadbeef", doc.RootElement.GetProperty("sha256").GetString());
    }
}
