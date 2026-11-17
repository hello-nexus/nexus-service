using Nexus.Service.Calendar.Ics;
using static Nexus.Service.Tests.Calendar.IcsTestHelpers;

namespace Nexus.Service.Tests.Calendar;

public sealed class IcsParserTests
{
    [Fact]
    public void Google_feed_parses_events_zones_and_skips_alarm_text()
    {
        var feed = IcsParser.Parse(IcsFixtures.Google);

        Assert.Equal(3, feed.Events.Count);
        Assert.Equal("America/New_York", feed.DefaultTz);
        Assert.Single(feed.Zones);
        Assert.Equal(2, feed.Zones[0].Observances.Count);
        var standup = feed.Events[0];
        Assert.Equal("Team standup", standup.Summary);
        Assert.Equal("Room 4, Floor 2", standup.Location);
        Assert.Equal("Agenda:\n- Blockers, risks\n- Plan; with a very long line that is folded across two physical lines", standup.Description);
        Assert.DoesNotContain("alarm", standup.Description);
        Assert.Equal("America/New_York", standup.Start.Tz);
    }

    [Fact]
    public void All_day_dates_keep_an_exclusive_end_and_no_zone()
    {
        var events = Expand(IcsFixtures.Google, Ms(2026, 11, 20), Ms(2026, 12, 1));

        var offsite = Assert.Single(events, e => e.Title == "Offsite");
        Assert.True(offsite.AllDay);
        Assert.Equal("2026-11-25", offsite.StartDate);
        Assert.Equal("2026-11-27", offsite.EndDate);
    }

    [Fact]
    public void Utc_event_and_tentative_status()
    {
        var call = Assert.Single(Expand(IcsFixtures.Google, Ms(2026, 11, 17), Ms(2026, 11, 18)), e => e.Title == "Call");

        Assert.Equal(Ms(2026, 11, 17, 19), call.Start);
        Assert.Equal(Ms(2026, 11, 17, 20), call.End);
        Assert.Equal("tentative", call.Status);
        Assert.Null(call.ProviderId);
        Assert.Null(call.Etag);
    }

    [Fact]
    public void Missing_end_means_one_day_for_all_day_and_zero_length_for_timed()
    {
        var ics = IcsFixtures.Wrap(
            IcsFixtures.Event("UID:a", "SUMMARY:A", "DTSTART;VALUE=DATE:20261105"),
            IcsFixtures.Event("UID:b", "SUMMARY:B", "DTSTART:20261105T100000Z"),
            IcsFixtures.Event("UID:c", "SUMMARY:C", "DTSTART:20261105T100000Z", "DURATION:PT1H30M"),
            IcsFixtures.Event("UID:d", "SUMMARY:D", "DTSTART;VALUE=DATE:20261105", "DURATION:P3D"));

        var byTitle = Expand(ics, Ms(2026, 11, 1), Ms(2026, 12, 1)).ToDictionary(e => e.Title);

        Assert.Equal("2026-11-06", byTitle["A"].EndDate);
        Assert.Equal(byTitle["B"].Start, byTitle["B"].End);
        Assert.Equal(90 * 60_000, byTitle["C"].End - byTitle["C"].Start);
        Assert.Equal("2026-11-08", byTitle["D"].EndDate);
    }

    [Fact]
    public void Cancelled_events_are_dropped_and_lf_only_line_endings_parse()
    {
        var ics = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:x\nSTATUS:CANCELLED\nSUMMARY:Gone\nDTSTART:20261105T100000Z\nEND:VEVENT\nBEGIN:VEVENT\nUID:y\nSUMMARY:Kept\nDTSTART:20261105T110000Z\nEND:VEVENT\nEND:VCALENDAR";

        var events = Expand(ics, Ms(2026, 11, 1), Ms(2026, 12, 1));

        Assert.Equal(new[] { "Kept" }, events.Select(e => e.Title));
    }

    [Fact]
    public void Events_without_a_readable_start_are_skipped_not_fatal()
    {
        var ics = IcsFixtures.Wrap(
            IcsFixtures.Event("UID:a", "SUMMARY:Broken", "DTSTART:garbage"),
            IcsFixtures.Event("UID:b", "SUMMARY:Fine", "DTSTART:20261105T110000Z"));

        Assert.Equal(new[] { "Fine" }, IcsParser.Parse(ics).Events.Select(e => e.Summary));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>secret-token-123 Sign in</body></html>")]
    [InlineData("{\"json\":\"secret-token-123\"}")]
    public void Non_ics_input_throws_without_echoing_the_input(string body)
    {
        var ex = Assert.Throws<IcsFormatException>(() => IcsParser.Parse(body));

        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public void Notes_are_capped()
    {
        var ics = IcsFixtures.Wrap(IcsFixtures.Event("UID:a", "SUMMARY:A", "DTSTART:20261105T100000Z", "DESCRIPTION:" + new string('x', 10_000)));

        Assert.Equal(IcsParser.MaxNotesChars, IcsParser.Parse(ics).Events[0].Description!.Length);
    }

    [Theory]
    [InlineData("PT1H", 3600)]
    [InlineData("P1D", 86400)]
    [InlineData("P1W", 604800)]
    [InlineData("P1DT2H30M15S", 95415)]
    [InlineData("-PT15M", -900)]
    public void Durations_parse(string text, long seconds)
    {
        Assert.True(IcsParser.TryDuration(text, out var parsed));
        Assert.Equal(seconds, parsed);
    }

    [Theory]
    [InlineData("1H")]
    [InlineData("PTH")]
    [InlineData("P1X")]
    public void Bad_durations_are_rejected(string text)
    {
        Assert.False(IcsParser.TryDuration(text, out _));
    }

    [Fact]
    public void Event_ids_are_stable_across_parses_and_differ_per_instance_and_source()
    {
        var a = Expand(IcsFixtures.Google, Ms(2026, 11, 1), Ms(2026, 12, 15)).Where(e => e.Title == "Team standup").ToList();
        var b = Expand(IcsFixtures.Google, Ms(2026, 11, 1), Ms(2026, 12, 15)).Where(e => e.Title == "Team standup").ToList();
        var other = Expand(IcsFixtures.Google, Ms(2026, 11, 1), Ms(2026, 12, 15), "c2").Where(e => e.Title == "Team standup").ToList();

        Assert.Equal(a.Select(e => e.Id), b.Select(e => e.Id));
        Assert.Equal(a.Count, a.Select(e => e.Id).Distinct().Count());
        Assert.Empty(a.Select(e => e.Id).Intersect(other.Select(e => e.Id)));
    }
}
