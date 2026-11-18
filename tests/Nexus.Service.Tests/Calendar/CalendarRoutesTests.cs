using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Calendar;
using Nexus.Service.Tests.Integration;

namespace Nexus.Service.Tests.Calendar;

/// <summary>The calendar routes through the real request pipeline, with a fake fetcher and an isolated data root.</summary>
public sealed class CalendarRoutesTests : IClassFixture<NexusAppFactory>
{
    private const string SecretUrl = "https://calendar.example.com/ical/private-token-9f3a/basic.ics";
    private readonly NexusAppFactory _factory;
    private readonly HttpClient _client;
    private readonly FakeCalendarFetcher _fetcher;

    public CalendarRoutesTests(NexusAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        _fetcher = factory.Services.GetRequiredService<FakeCalendarFetcher>();
        _fetcher.Handler = (_, _) => FakeCalendarFetcher.Ok(IcsFixtures.Google);
        _fetcher.Gate = null;
        var svc = factory.Services.GetRequiredService<CalendarService>();
        foreach (var s in svc.ListSources())
        {
            svc.Remove(s.Id);
        }
        svc.SetRefreshMinutes(15);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<string> AddAsync(string url = SecretUrl, string name = "Work")
    {
        var res = await _client.PostAsync("/api/calendar/calendars", Json($$"""{"url":"{{url}}","name":"{{name}}","color":"#7b5cff"}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Only_read_settings_and_refresh_routes_are_panel_reachable()
    {
        var sources = _factory.Services.GetServices<EndpointDataSource>().SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/calendar", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Equal(10, sources.Count);
        var panel = sources.Where(e => e.Metadata.GetMetadata<AllowPanelAccess>() is not null)
            .Select(e => string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods) + " " + e.RoutePattern.RawText)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            "GET /api/calendar/calendars",
            "GET /api/calendar/events",
            "GET /api/calendar/settings",
            "POST /api/calendar/calendars/{id}/sync",
            "POST /api/calendar/refresh",
            "PUT /api/calendar/settings",
        }, panel);
    }

    [Fact]
    public async Task Add_list_and_patch_never_echo_the_url()
    {
        var id = await AddAsync();

        var list = await _client.GetStringAsync("/api/calendar/calendars");
        var patch = await _client.PatchAsync($"/api/calendar/calendars/{id}", Json("""{"name":"Team","color":"#ff8800"}"""));
        var patched = await patch.Content.ReadAsStringAsync();

        Assert.Contains("calendar.example.com", list);
        Assert.DoesNotContain("private-token", list + patched);
        Assert.DoesNotContain("basic.ics", list + patched);
        using var doc = JsonDocument.Parse(patched);
        Assert.Equal("Team", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("#ff8800", doc.RootElement.GetProperty("color").GetString());
    }

    [Theory]
    [InlineData("""{"url":"ftp://calendar.example.com/a.ics","name":"x","color":"#000000"}""", "invalidUrl")]
    [InlineData("""{"url":"http://192.168.1.5/a.ics","name":"x","color":"#000000"}""", "invalidUrl")]
    [InlineData("""{"name":"x"}""", "invalidUrl")]
    [InlineData("not json", "invalidUrl")]
    public async Task Add_with_a_bad_body_is_400_with_a_code(string body, string code)
    {
        var res = await _client.PostAsync("/api/calendar/calendars", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(code, doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(code, doc.RootElement.GetProperty("msg").GetString());
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("notFound")]
    [InlineData("unreachable")]
    public async Task Add_surfaces_the_first_fetch_failure_as_400_and_keeps_nothing(string code)
    {
        _fetcher.Handler = (_, _) => FakeCalendarFetcher.Fail(code);

        var res = await _client.PostAsync("/api/calendar/calendars", Json($$"""{"url":"{{SecretUrl}}","name":"x","color":"#000000"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains(code, body);
        Assert.DoesNotContain("private-token", body);
        Assert.Contains("\"calendars\":[]", await _client.GetStringAsync("/api/calendar/calendars"));
    }

    [Fact]
    public async Task Events_returns_expanded_instances_with_sync_time_and_validates_the_range()
    {
        await AddAsync();
        var from = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var to = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        using var ok = JsonDocument.Parse(await _client.GetStringAsync($"/api/calendar/events?from={from}&to={to}"));

        var events = ok.RootElement.GetProperty("events");
        Assert.Equal(3, events.EnumerateArray().Count(e => e.GetProperty("title").GetString() == "Team standup"));
        Assert.True(ok.RootElement.GetProperty("syncedAt").GetInt64() > 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/calendar/events")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/calendar/events?from={to}&to={from}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/calendar/events?from={from}&to={from + 401L * 86_400_000L}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/calendar/events?from={from}&to={to}&calendars=cal_missing")).StatusCode);
    }

    [Fact]
    public async Task Settings_round_trip_and_reject_other_values()
    {
        Assert.Contains("\"refreshMinutes\":15", await _client.GetStringAsync("/api/calendar/settings"));

        var ok = await _client.PutAsync("/api/calendar/settings", Json("""{"refreshMinutes":30}"""));
        var bad = await _client.PutAsync("/api/calendar/settings", Json("""{"refreshMinutes":20}"""));
        var missing = await _client.PutAsync("/api/calendar/settings", Json("{}"));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/calendar/settings"));
        Assert.Equal(30, doc.RootElement.GetProperty("refreshMinutes").GetInt32());
        Assert.False(doc.RootElement.GetProperty("syncing").GetBoolean());
    }

    [Fact]
    public async Task Refresh_is_202_and_throttled_right_after_a_sync()
    {
        var id = await AddAsync();

        var first = await _client.PostAsync("/api/calendar/refresh", null);
        var second = await _client.PostAsync($"/api/calendar/calendars/{id}/sync", null);
        var unknown = await _client.PostAsync("/api/calendar/calendars/cal_missing/sync", null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Contains("\"started\":false", await first.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Contains("\"started\":false", await second.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Delete_is_204_and_drops_the_source_and_its_cache()
    {
        var id = await AddAsync();

        var res = await _client.DeleteAsync($"/api/calendar/calendars/{id}");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Contains("\"calendars\":[]", await _client.GetStringAsync("/api/calendar/calendars"));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/calendar/calendars/{id}")).StatusCode);
    }

    [Fact]
    public async Task Mutations_raise_the_change_signal_the_topic_ping_hangs_off()
    {
        var svc = _factory.Services.GetRequiredService<CalendarService>();
        var fired = 0;
        void Handler() => Interlocked.Increment(ref fired);
        svc.Changed += Handler;
        try
        {
            var id = await AddAsync();
            var afterAdd = fired;
            await _client.PatchAsync($"/api/calendar/calendars/{id}", Json("""{"name":"Team"}"""));
            await _client.PutAsync("/api/calendar/settings", Json("""{"refreshMinutes":5}"""));
            await _client.DeleteAsync($"/api/calendar/calendars/{id}");

            Assert.True(afterAdd >= 1);
            Assert.Equal(afterAdd + 3, fired);
        }
        finally
        {
            svc.Changed -= Handler;
        }
    }

    [Fact]
    public async Task Probe_route_returns_name_and_host_or_the_error_envelope()
    {
        _fetcher.Handler = (_, _) => FakeCalendarFetcher.Ok("BEGIN:VCALENDAR\r\nX-WR-CALNAME:Probe Me\r\nEND:VCALENDAR\r\n");

        var ok = await _client.PostAsync("/api/calendar/probe", Json($$"""{"url":"{{SecretUrl}}"}"""));
        using var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        var bad = await _client.PostAsync("/api/calendar/probe", Json("""{"url":"ftp://x.example.com/a"}"""));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Probe Me", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("calendar.example.com", doc.RootElement.GetProperty("host").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("invalidUrl", await bad.Content.ReadAsStringAsync());
        Assert.Contains("\"calendars\":[]", await _client.GetStringAsync("/api/calendar/calendars"));
    }
}
