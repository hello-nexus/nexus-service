using Nexus.Service.Calendar;
using Nexus.Service.Calendar.Ics;
using static Nexus.Service.Tests.Calendar.IcsTestHelpers;

namespace Nexus.Service.Tests.Calendar;

public sealed class CalendarHardeningTests : IDisposable
{
    private const string Url = "https://calendar.example.com/ical/tok/basic.ics";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-test-calhard-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeCalendarFetcher _fetcher = new();
    private readonly FakeClock _clock = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private CalendarService New() => new(_root, _fetcher, _clock);

    [Theory]
    [InlineData(long.MinValue, long.MaxValue)]
    [InlineData(long.MaxValue - 5, long.MaxValue)]
    [InlineData(long.MinValue, 0L)]
    [InlineData(-1L, long.MaxValue)]
    [InlineData(0L, 253_402_300_800_000L)]
    public void Query_rejects_extreme_ranges_without_overflowing(long from, long to)
    {
        Assert.Null(New().Query(from, to, null));
    }

    [Fact]
    public async Task One_event_with_dates_beyond_year_9999_does_not_take_the_feed_down()
    {
        var feed = IcsFixtures.Wrap(
            IcsFixtures.Event("UID:bad", "SUMMARY:Bad", "DTSTART;VALUE=DATE:99991231", "DURATION:P3000000D"),
            IcsFixtures.Event("UID:bad2", "SUMMARY:Bad2", "DTSTART;VALUE=DATE:99991230", "RRULE:FREQ=DAILY"),
            IcsFixtures.Event("UID:ok", "SUMMARY:Fine", "DTSTART:20261105T110000Z", "DTEND:20261105T120000Z"));
        _fetcher.Handler = (_, _) => FakeCalendarFetcher.Ok(feed);
        var svc = New();
        await svc.AddAsync(Url, "X", "#000000", CancellationToken.None);

        var near = svc.Query(Ms(2026, 11, 1), Ms(2026, 12, 1), null)!;
        var far = svc.Query(Ms(9999, 6, 1), Ms(9999, 12, 31), null);

        Assert.Contains(near.Events, e => e.Title == "Fine");
        Assert.NotNull(far);
    }

    [Fact]
    public async Task Response_is_capped_and_flagged_truncated()
    {
        var events = Enumerable.Range(0, CalendarService.MaxEventsPerResponse + 50)
            .Select(i => IcsFixtures.Event($"UID:e{i}", "SUMMARY:E", "DTSTART:20261105T110000Z", "DTEND:20261105T120000Z"))
            .ToArray();
        _fetcher.Handler = (_, _) => FakeCalendarFetcher.Ok(IcsFixtures.Wrap(events));
        var svc = New();
        await svc.AddAsync(Url, "X", "#000000", CancellationToken.None);

        var all = svc.Query(Ms(2026, 11, 1), Ms(2026, 12, 1), null)!;
        var none = svc.Query(Ms(2027, 11, 1), Ms(2027, 12, 1), null)!;

        Assert.True(all.Truncated);
        Assert.Equal(CalendarService.MaxEventsPerResponse, all.Events.Count);
        Assert.False(none.Truncated);
        var json = System.Text.Json.JsonSerializer.Serialize(none, Nexus.Service.Serialization.AppJsonContext.Default.CalendarEventsResponse);
        Assert.Contains("\"truncated\":false", json);
    }

    [Fact]
    public async Task Deleting_a_source_mid_sync_does_not_recreate_its_cache()
    {
        var svc = New();
        var id = (await svc.AddAsync(Url, "X", "#000000", CancellationToken.None)).Source!.Id;
        _clock.Advance(TimeSpan.FromMinutes(5));
        _fetcher.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(svc.TryStartRefreshAll());

        svc.Remove(id);
        _fetcher.Gate.SetResult();
        await svc.WhenIdleAsync();

        Assert.False(File.Exists(Path.Combine(_root, "calendar", "cache", id + ".json")));
    }

    [Fact]
    public async Task Cache_files_without_a_source_are_deleted_at_load()
    {
        await New().AddAsync(Url, "X", "#000000", CancellationToken.None);
        var stray = Path.Combine(_root, "calendar", "cache", "cal_gone.json");
        File.WriteAllText(stray, "{}");

        var svc = New();

        Assert.False(File.Exists(stray));
        Assert.Single(svc.ListSources());
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "calendar", "cache")));
    }

    [Fact]
    public async Task Corrupt_sources_file_is_moved_aside_not_overwritten()
    {
        await New().AddAsync(Url, "X", "#000000", CancellationToken.None);
        var path = Path.Combine(_root, "calendar", "sources.json");
        File.WriteAllText(path, "{ not json");

        var svc = New();
        Assert.Empty(svc.ListSources());
        svc.SetRefreshMinutes(30);

        var aside = Directory.GetFiles(Path.Combine(_root, "calendar"), "sources.json.corrupt-*");
        Assert.Single(aside);
        Assert.Equal("{ not json", File.ReadAllText(aside[0]));
        Assert.Equal(30, New().GetSettings().RefreshMinutes);
    }

    [Fact]
    public async Task Cancelled_first_fetch_leaves_no_source()
    {
        using var cts = new CancellationTokenSource();
        _fetcher.Handler = (_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };
        var svc = New();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.AddAsync(Url, "X", "#000000", cts.Token));

        Assert.Empty(svc.ListSources());
        Assert.DoesNotContain("cal_", File.Exists(Path.Combine(_root, "calendar", "sources.json")) ? File.ReadAllText(Path.Combine(_root, "calendar", "sources.json")) : "");
    }

    [Fact]
    public async Task Source_cap_reports_limitReached()
    {
        var svc = New();
        for (var i = 0; i < 50; i++)
        {
            Assert.NotNull((await svc.AddAsync($"https://c{i}.example.com/a.ics", "X", "#000000", CancellationToken.None)).Source);
        }

        var over = await svc.AddAsync("https://over.example.com/a.ics", "X", "#000000", CancellationToken.None);

        Assert.Equal("limitReached", over.Error);
    }

    [Fact]
    public async Task The_worker_cannot_start_a_second_fetch_while_the_first_add_is_running()
    {
        _fetcher.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var svc = New();
        var add = svc.AddAsync(Url, "X", "#000000", CancellationToken.None);
        while (_fetcher.Calls.Count == 0)
        {
            await Task.Delay(5);
        }

        _clock.Advance(TimeSpan.FromHours(3));
        await svc.RunDueAsync(CancellationToken.None);
        _fetcher.Gate.SetResult();
        await add;

        Assert.Single(_fetcher.Calls);
    }

    [Fact]
    public async Task Changed_does_not_fire_for_a_start_or_an_unchanged_304()
    {
        var svc = New();
        await svc.AddAsync(Url, "X", "#000000", CancellationToken.None);
        var fired = 0;
        svc.Changed += () => Interlocked.Increment(ref fired);
        _fetcher.Handler = (_, _) => FakeCalendarFetcher.NotModified();

        _clock.Advance(TimeSpan.FromMinutes(16));
        await svc.RunDueAsync(CancellationToken.None);
        Assert.Equal(0, fired);

        _fetcher.Handler = (_, _) => FakeCalendarFetcher.Fail("unreachable");
        _clock.Advance(TimeSpan.FromHours(1));
        await svc.RunDueAsync(CancellationToken.None);
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task A_slow_source_does_not_delay_the_others()
    {
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var svc = New();
        await svc.AddAsync("https://slow.example.com/a.ics", "Slow", "#000000", CancellationToken.None);
        await svc.AddAsync("https://fast.example.com/a.ics", "Fast", "#000000", CancellationToken.None);
        var finished = new List<string>();
        _fetcher.Handler = (u, _) =>
        {
            lock (finished)
            {
                finished.Add(u.Host);
            }
            return FakeCalendarFetcher.Ok(IcsFixtures.Google);
        };
        _fetcher.GateFor = u => u.Host == "slow.example.com" ? slow.Task : null;
        _clock.Advance(TimeSpan.FromMinutes(16));

        var due = svc.RunDueAsync(CancellationToken.None);
        for (var i = 0; i < 200 && !finished.Contains("fast.example.com"); i++)
        {
            await Task.Delay(10);
        }

        Assert.Contains("fast.example.com", finished);
        Assert.False(due.IsCompleted);
        slow.SetResult();
        await due;
    }

    [Fact]
    public async Task Failed_probes_are_cached_briefly_too()
    {
        _fetcher.Handler = (_, _) => FakeCalendarFetcher.Fail("unreachable");
        var svc = New();

        await svc.ProbeAsync(Url, CancellationToken.None);
        await svc.ProbeAsync(Url, CancellationToken.None);
        Assert.Single(_fetcher.Calls);

        _clock.Advance(TimeSpan.FromSeconds(11));
        await svc.ProbeAsync(Url, CancellationToken.None);
        Assert.Equal(2, _fetcher.Calls.Count);
    }

    [Fact]
    public void Vtimezone_spring_forward_gap_matches_the_real_zone()
    {
        var feed = IcsParser.Parse(IcsFixtures.Wrap(IcsFixtures.CustomZone));
        var custom = new IcsZoneResolver(feed).Resolve("Nexus Test Zone");
        var berlin = new IcsZoneResolver(new IcsFeed()).Resolve("Europe/Berlin");

        foreach (var local in new[] { new DateTime(2026, 3, 29, 2, 30, 0), new DateTime(2026, 3, 29, 2, 0, 0), new DateTime(2026, 3, 29, 1, 59, 0), new DateTime(2026, 3, 29, 3, 0, 0), new DateTime(2026, 7, 1, 12, 0, 0) })
        {
            Assert.Equal(berlin.LocalToUtcMs(local), custom.LocalToUtcMs(local));
        }
    }

    [Theory]
    [InlineData("64:ff9b::808:808", true)]
    [InlineData("64:ff9b::7f00:1", false)]
    [InlineData("2002:0808:0808::1", true)]
    [InlineData("2002:c0a8:101::1", false)]
    [InlineData("::808:808", true)]
    [InlineData("::10.0.0.1", false)]
    [InlineData("198.17.255.255", true)]
    [InlineData("198.18.0.0", false)]
    [InlineData("198.19.255.255", false)]
    [InlineData("198.20.0.0", true)]
    [InlineData("192.0.0.1", false)]
    [InlineData("192.0.1.1", true)]
    [InlineData("8.8.8.8", true)]
    public void Public_address_rule(string ip, bool isPublic)
    {
        Assert.Equal(isPublic, CalendarUrl.IsPublicAddress(System.Net.IPAddress.Parse(ip)));
    }
}
