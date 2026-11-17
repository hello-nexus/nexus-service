using System.Text.Json;
using Nexus.Service.Calendar;
using Nexus.Service.Models.Calendar;
using Nexus.Service.Serialization;
using static Nexus.Service.Tests.Calendar.IcsTestHelpers;

namespace Nexus.Service.Tests.Calendar;

internal sealed class FakeCalendarFetcher : ICalendarFetcher
{
    public Func<Uri, string?, CalendarFetchResult> Handler { get; set; } = (_, _) => Ok(IcsFixtures.Google);
    public List<(Uri Url, string? ETag)> Calls { get; } = new();
    public TaskCompletionSource? Gate { get; set; }

    public async Task<CalendarFetchResult> FetchAsync(Uri url, string? etag, string? lastModified, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add((url, etag));
        }
        if (Gate is { } gate)
        {
            await gate.Task;
        }
        return Handler(url, etag);
    }

    public static CalendarFetchResult Ok(string body, string? etag = "\"v1\"") =>
        new() { Outcome = CalendarFetchOutcome.Ok, Body = body, ETag = etag, Bytes = body.Length, HttpStatus = 200 };

    public static CalendarFetchResult Fail(string code, int status = 500) =>
        new() { Outcome = CalendarFetchOutcome.Error, ErrorCode = code, HttpStatus = status };

    public static CalendarFetchResult NotModified() => new() { Outcome = CalendarFetchOutcome.NotModified, HttpStatus = 304 };
}

internal sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class CalendarServiceTests : IDisposable
{
    private const string SecretUrl = "https://calendar.example.com/ical/private-token-9f3a/basic.ics";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-test-calendar-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeCalendarFetcher _fetcher = new();
    private readonly FakeClock _clock = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private CalendarService New() => new(_root, _fetcher, _clock);

    private static long Day(int y, int m, int d) => Ms(y, m, d);

    [Fact]
    public async Task Add_returns_host_only_and_never_the_url()
    {
        var svc = New();

        var result = await svc.AddAsync(SecretUrl, "Work", "#7B5CFF", CancellationToken.None);

        var dto = Assert.IsType<CalendarSourceDto>(result.Source);
        Assert.Equal("calendar.example.com", dto.Host);
        Assert.Equal("#7b5cff", dto.Color);
        Assert.Equal("ics", dto.Kind);
        Assert.Equal("ok", dto.Status);
        Assert.NotNull(dto.LastSyncedAt);
        var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.CalendarSourceDto)
            + JsonSerializer.Serialize(new CalendarListResponse { Calendars = svc.ListSources() }, AppJsonContext.Default.CalendarListResponse);
        Assert.DoesNotContain("private-token", json);
        Assert.DoesNotContain("basic.ics", json);
    }

    [Fact]
    public async Task Dto_json_carries_explicit_nulls_the_contract_promises()
    {
        var svc = New();
        await svc.AddAsync(SecretUrl, "Work", "#7b5cff", CancellationToken.None);

        var events = svc.Query(Day(2026, 11, 16), Day(2026, 11, 18), null)!;
        var json = JsonSerializer.Serialize(events, AppJsonContext.Default.CalendarEventsResponse);
        using var doc = JsonDocument.Parse(json);
        var call = doc.RootElement.GetProperty("events").EnumerateArray().First(e => e.GetProperty("title").GetString() == "Call");

        Assert.Equal(JsonValueKind.Null, call.GetProperty("location").ValueKind);
        Assert.Equal(JsonValueKind.Null, call.GetProperty("startDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, call.GetProperty("providerId").ValueKind);
        Assert.Equal(JsonValueKind.Null, call.GetProperty("etag").ValueKind);
        Assert.Equal("tentative", call.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://calendar.example.com/a.ics")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://localhost/a.ics")]
    [InlineData("http://127.0.0.1/a.ics")]
    [InlineData("http://192.168.1.20/a.ics")]
    [InlineData("http://10.0.0.5/a.ics")]
    [InlineData("http://169.254.169.254/latest")]
    [InlineData("http://[::1]/a.ics")]
    [InlineData("https://nas.local/a.ics")]
    [InlineData("https://intranet/a.ics")]
    public async Task Add_rejects_bad_schemes_and_non_public_hosts_without_fetching(string url)
    {
        var svc = New();

        var result = await svc.AddAsync(url, "X", "#000000", CancellationToken.None);

        Assert.Equal("invalidUrl", result.Error);
        Assert.Empty(_fetcher.Calls);
        Assert.Empty(svc.ListSources());
    }

    [Fact]
    public async Task Webcal_is_rewritten_to_https()
    {
        var svc = New();

        await svc.AddAsync("webcal://calendar.example.com/a.ics", "X", "#000000", CancellationToken.None);

        Assert.Equal("https://calendar.example.com/a.ics", _fetcher.Calls.Single().Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("notFound")]
    [InlineData("unreachable")]
    [InlineData("tooLarge")]
    public async Task First_fetch_failure_reports_the_code_and_keeps_nothing(string code)
    {
        _fetcher.Handler = (_, _) => FakeCalendarFetcher.Fail(code);
        var svc = New();

        var result = await svc.AddAsync(SecretUrl, "X", "#000000", CancellationToken.None);

        Assert.Equal(code, result.Error);
        Assert.Null(result.Source);
        Assert.Empty(svc.ListSources());
        Assert.False(Directory.Exists(Path.Combine(_root, "calendar", "cache")) && Directory.GetFiles(Path.Combine(_root, "calendar", "cache")).Length > 0);
    }

    [Fact]
    public async Task A_body_that_is_not_ics_reports_notIcs()
    {
        _fetcher.Handler = (_, _) => FakeCalendarFetcher.Ok("<html>Sign in to continue</html>");
        var svc = New();

        var result = await svc.AddAsync(SecretUrl, "X", "#000000", CancellationToken.None);

        Assert.Equal("notIcs", result.Error);
        Assert.Empty(svc.ListSources());
    }

    [Fact]
    public async Task Events_are_served_from_the_cache_after_a_restart_with_no_fetch()
    {
        await New().AddAsync(SecretUrl, "Work", "#7b5cff", CancellationToken.None);
        var calls = _fetcher.Calls.Count;

        var restarted = New();

        Assert.Equal(calls, _fetcher.Calls.Count);
        var events = restarted.Query(Day(2026, 11, 16), Day(2026, 11, 18), null)!;
        Assert.Contains(events.Events, e => e.Title == "Team standup");
        Assert.NotNull(events.SyncedAt);
        Assert.Single(restarted.ListSources());
    }

    [Fact]
    public async Task Sources_file_holds_no_event_text()
    {
        var svc = New();
        await svc.AddAsync(SecretUrl, "Work", "#7b5cff", CancellationToken.None);

        var json = File.ReadAllText(Path.Combine(_root, "calendar", "sources.json"));

        Assert.DoesNotContain("Team standup", json);
        Assert.DoesNotContain("Room 4", json);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("private-token", json);
        }
    }

    [Fact]
    public async Task Delete_drops_the_source_its_cache_and_its_events()
    {
        var svc = New();
        var id = (await svc.AddAsync(SecretUrl, "Work", "#7b5cff", CancellationToken.None)).Source!.Id;
        var cache = Path.Combine(_root, "calendar", "cache", id + ".json");
        Assert.True(File.Exists(cache));

        Assert.True(svc.Remove(id));

        Assert.False(File.Exists(cache));
        Assert.Empty(svc.ListSources());
        Assert.Empty(svc.Query(Day(2026, 11, 16), Day(2026, 12, 18), null)!.Events);
        Assert.False(svc.Remove(id));
        Assert.DoesNotContain(id, File.ReadAllText(Path.Combine(_root, "calendar", "sources.json")));
    }

    [Fact]
    public async Task Patch_renames_and_recolors_and_ignores_a_bad_color()
    {
        var svc = New();
        var id = (await svc.AddAsync(SecretUrl, "Work", "#7b5cff", CancellationToken.None)).Source!.Id;

        var dto = svc.Patch(id, "Team", "not-a-color")!;

        Assert.Equal("Team", dto.Name);
        Assert.Equal("#7b5cff", dto.Color);
        Assert.Equal("#ff0000", svc.Patch(id, null, "#FF0000")!.Color);
        Assert.Null(svc.Patch("nope", "x", null));
    }

    [Fact]
    public async Task Query_filters_by_calendar_and_rejects_bad_ranges()
    {
        var svc = New();
        var a = (await svc.AddAsync(SecretUrl, "A", "#000000", CancellationToken.None)).Source!.Id;
        var b = (await svc.AddAsync("https://calendar.example.com/other.ics", "B", "#111111", CancellationToken.None)).Source!.Id;

        var onlyA = svc.Query(Day(2026, 11, 16), Day(2026, 11, 18), new[] { a })!;

        Assert.All(onlyA.Events, e => Assert.Equal(a, e.CalendarId));
        Assert.Equal(2 * onlyA.Events.Count, svc.Query(Day(2026, 11, 16), Day(2026, 11, 18), null)!.Events.Count);
        Assert.Contains(svc.Query(Day(2026, 11, 16), Day(2026, 11, 18), new[] { b })!.Events, e => e.CalendarId == b);
        Assert.Null(svc.Query(Day(2026, 11, 18), Day(2026, 11, 16), null));
        Assert.Null(svc.Query(Day(2026, 11, 18), Day(2026, 11, 18), null));
        Assert.Null(svc.Query(Day(2026, 1, 1), Day(2027, 3, 1), null));
        Assert.NotNull(svc.Query(Day(2026, 1, 1), Day(2026, 1, 1) + 400L * 86_400_000L, null));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(15, true)]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(0, false)]
    [InlineData(10, false)]
    [InlineData(45, false)]
    [InlineData(-15, false)]
    [InlineData(1440, false)]
    public void Refresh_interval_accepts_only_the_four_allowed_values(int minutes, bool ok)
    {
        var svc = New();

        var result = svc.SetRefreshMinutes(minutes);

        Assert.Equal(ok, result is not null);
        Assert.Equal(ok ? minutes : 15, svc.GetSettings().RefreshMinutes);
    }

    [Fact]
    public void Refresh_interval_persists_across_a_restart_and_defaults_to_fifteen()
    {
        Assert.Equal(15, New().GetSettings().RefreshMinutes);
        New().SetRefreshMinutes(30);

        Assert.Equal(30, New().GetSettings().RefreshMinutes);
    }

    [Fact]
    public async Task Manual_refresh_is_throttled_for_thirty_seconds_after_a_sync_finishes()
    {
        var svc = New();
        await svc.AddAsync(SecretUrl, "Work", "#7b5cff", CancellationToken.None);
        var calls = _fetcher.Calls.Count;

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(svc.TryStartRefreshAll());
        Assert.Equal(false, svc.TryStartSync(svc.ListSources()[0].Id));

        _clock.Advance(TimeSpan.FromSeconds(21));
        Assert.True(svc.TryStartRefreshAll());
        await svc.WhenIdleAsync();
        Assert.Equal(calls + 1, _fetcher.Calls.Count);
    }

    [Fact]
    public async Task Manual_refresh_is_ignored_while_a_sync_is_running()
    {
        var svc = New();
        await svc.AddAsync(SecretUrl, "Work", "#7b5cff", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        _fetcher.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(svc.TryStartRefreshAll());
        Assert.False(svc.TryStartRefreshAll());
        Assert.True(svc.GetSettings().Syncing);
        Assert.Equal("syncing", svc.ListSources()[0].Status);

        _fetcher.Gate.SetResult();
        await svc.WhenIdleAsync();
        Assert.False(svc.GetSettings().Syncing);
        Assert.Equal("ok", svc.ListSources()[0].Status);
    }

    [Fact]
    public void Refresh_with_no_calendars_does_not_start_and_unknown_source_is_null()
    {
        var svc = New();

        Assert.False(svc.TryStartRefreshAll());
        Assert.Null(svc.TryStartSync("nope"));
    }

    [Fact]
    public async Task Later_sync_sends_the_etag_and_a_304_keeps_the_events_and_refreshes_the_time()
    {
        var svc = New();
        await svc.AddAsync(SecretUrl, "Work", "#7b5cff", CancellationToken.None);
        var before = svc.ListSources()[0].LastSyncedAt;
        _fetcher.Handler = (_, _) => FakeCalendarFetcher.NotModified();
        _clock.Advance(TimeSpan.FromMinutes(16));

        await svc.RunDueAsync(CancellationToken.None);

        Assert.Equal("\"v1\"", _fetcher.Calls.Last().ETag);
        Assert.True(svc.ListSources()[0].LastSyncedAt > before);
        Assert.NotEmpty(svc.Query(Day(2026, 11, 16), Day(2026, 11, 18), null)!.Events);
    }

    [Fact]
    public async Task A_failing_sync_keeps_serving_cached_events_and_flags_the_source()
    {
        var svc = New();
        await svc.AddAsync(SecretUrl, "Work", "#7b5cff", CancellationToken.None);
        var syncedAt = svc.ListSources()[0].LastSyncedAt;
        _fetcher.Handler = (_, _) => FakeCalendarFetcher.Fail("unauthorized", 403);
        _clock.Advance(TimeSpan.FromMinutes(16));

        await svc.RunDueAsync(CancellationToken.None);

        var dto = svc.ListSources()[0];
        Assert.Equal("error", dto.Status);
        Assert.Equal("unauthorized", dto.Error);
        Assert.Equal(syncedAt, dto.LastSyncedAt);
        Assert.NotEmpty(svc.Query(Day(2026, 11, 16), Day(2026, 11, 18), null)!.Events);
    }

    [Fact]
    public async Task Scheduled_sync_waits_for_the_interval_and_backs_off_exponentially_to_two_hours()
    {
        var svc = New();
        await svc.AddAsync(SecretUrl, "Work", "#7b5cff", CancellationToken.None);
        var calls = () => _fetcher.Calls.Count;
        var baseline = calls();

        _clock.Advance(TimeSpan.FromMinutes(14));
        await svc.RunDueAsync(CancellationToken.None);
        Assert.Equal(baseline, calls());

        _fetcher.Handler = (_, _) => FakeCalendarFetcher.Fail("unreachable");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await svc.RunDueAsync(CancellationToken.None);
        Assert.Equal(baseline + 1, calls());

        // One failure: the next attempt is 30 min out, not 15.
        _clock.Advance(TimeSpan.FromMinutes(29));
        await svc.RunDueAsync(CancellationToken.None);
        Assert.Equal(baseline + 1, calls());
        _clock.Advance(TimeSpan.FromMinutes(1));
        await svc.RunDueAsync(CancellationToken.None);
        Assert.Equal(baseline + 2, calls());

        // Many failures cap at two hours.
        for (var i = 0; i < 6; i++)
        {
            _clock.Advance(CalendarService.MaxBackoff);
            await svc.RunDueAsync(CancellationToken.None);
        }
        var afterCap = calls();
        _clock.Advance(CalendarService.MaxBackoff - TimeSpan.FromMinutes(1));
        await svc.RunDueAsync(CancellationToken.None);
        Assert.Equal(afterCap, calls());
        _clock.Advance(TimeSpan.FromMinutes(1));
        await svc.RunDueAsync(CancellationToken.None);
        Assert.Equal(afterCap + 1, calls());
    }

    [Fact]
    public async Task Changing_the_interval_takes_effect_without_a_restart()
    {
        var svc = New();
        await svc.AddAsync(SecretUrl, "Work", "#7b5cff", CancellationToken.None);
        var baseline = _fetcher.Calls.Count;
        svc.SetRefreshMinutes(60);

        _clock.Advance(TimeSpan.FromMinutes(30));
        await svc.RunDueAsync(CancellationToken.None);
        Assert.Equal(baseline, _fetcher.Calls.Count);

        svc.SetRefreshMinutes(5);
        await svc.RunDueAsync(CancellationToken.None);
        Assert.Equal(baseline + 1, _fetcher.Calls.Count);
    }

    [Fact]
    public async Task Changed_fires_for_every_mutation_and_each_sync_and_carries_no_data()
    {
        var svc = New();
        var fired = 0;
        svc.Changed += () => Interlocked.Increment(ref fired);

        var id = (await svc.AddAsync(SecretUrl, "Work", "#7b5cff", CancellationToken.None)).Source!.Id;
        var afterAdd = fired;
        Assert.True(afterAdd >= 1);
        svc.Patch(id, "Team", null);
        svc.SetRefreshMinutes(30);
        svc.Remove(id);

        Assert.Equal(afterAdd + 3, fired);
    }

    [Fact]
    public void Sync_log_line_names_only_ids_codes_and_counts()
    {
        var line = CalendarService.FormatSyncLog("cal_abc", 200, 1234, "notIcs");

        Assert.Equal("[calendar] source cal_abc sync status=200 bytes=1234 error=notIcs", line);
    }

    [Fact]
    public void Parse_ids_splits_a_comma_list()
    {
        Assert.Null(Nexus.Service.Routes.CalendarRoutes.ParseIds(null));
        Assert.Null(Nexus.Service.Routes.CalendarRoutes.ParseIds("  "));
        Assert.Equal(new[] { "a", "b" }, Nexus.Service.Routes.CalendarRoutes.ParseIds("a, b,,"));
    }
}
