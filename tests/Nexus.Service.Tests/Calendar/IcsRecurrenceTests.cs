using Nexus.Service.Calendar.Ics;
using static Nexus.Service.Tests.Calendar.IcsTestHelpers;

namespace Nexus.Service.Tests.Calendar;

public sealed class IcsRecurrenceTests
{
    private static string Timed(string rrule, string start = "20260105T090000Z", params string[] extra) =>
        IcsFixtures.Wrap(IcsFixtures.Event(new[] { "UID:r1", "SUMMARY:R", "DTSTART:" + start, "DTEND:" + start.Replace("T09", "T10"), "RRULE:" + rrule }.Concat(extra).ToArray()));

    private static List<string> LocalDays(string ics, int y, int mo, int d, int months)
    {
        var from = Ms(y, mo, d);
        var to = new DateTimeOffset(y, mo, d, 0, 0, 0, TimeSpan.Zero).AddMonths(months).ToUnixTimeMilliseconds();
        return Expand(ics, from, to).Select(e => DateTimeOffset.FromUnixTimeMilliseconds(e.Start).ToString("yyyy-MM-dd")).ToList();
    }

    [Theory]
    [InlineData("FREQ=DAILY;INTERVAL=3;COUNT=4", "2026-01-05,2026-01-08,2026-01-11,2026-01-14")]
    [InlineData("FREQ=DAILY;UNTIL=20260108T090000Z", "2026-01-05,2026-01-06,2026-01-07,2026-01-08")]
    [InlineData("FREQ=DAILY;UNTIL=20260107", "2026-01-05,2026-01-06,2026-01-07")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE;COUNT=5", "2026-01-05,2026-01-07,2026-01-12,2026-01-14,2026-01-19")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;COUNT=3", "2026-01-05,2026-01-19,2026-02-02")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=31;COUNT=4", "2026-01-31,2026-03-31,2026-05-31,2026-07-31")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR;COUNT=3", "2026-01-30,2026-02-27,2026-03-27")]
    [InlineData("FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1;COUNT=3", "2026-01-30,2026-02-27,2026-03-31")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-1;COUNT=3", "2026-01-31,2026-02-28,2026-03-31")]
    [InlineData("FREQ=YEARLY;COUNT=3", "2026-01-05,2027-01-05,2028-01-05")]
    [InlineData("FREQ=YEARLY;BYMONTH=11;BYDAY=4TH;COUNT=3", "2026-11-26,2027-11-25,2028-11-23")]
    public void Rule_table(string rule, string expectedDays)
    {
        var days = LocalDays(Timed(rule), 2026, 1, 1, 48);

        Assert.Equal(expectedDays.Split(','), days);
    }

    [Fact]
    public void Leap_day_yearly_skips_years_without_it()
    {
        var days = LocalDays(Timed("FREQ=YEARLY;COUNT=3", "20240229T090000Z"), 2024, 1, 1, 12 * 14);

        Assert.Equal(new[] { "2024-02-29", "2028-02-29", "2032-02-29" }, days);
    }

    [Fact]
    public void Open_ended_rule_is_clamped_to_the_requested_range_far_from_the_start()
    {
        var ics = Timed("FREQ=DAILY", "20150105T090000Z");

        var events = Expand(ics, Ms(2026, 3, 1), Ms(2026, 3, 4));

        Assert.Equal(new[] { Ms(2026, 3, 1, 9), Ms(2026, 3, 2, 9), Ms(2026, 3, 3, 9) }, Starts(events));
    }

    [Fact]
    public void Count_is_measured_from_the_series_start_not_the_range()
    {
        var events = Expand(Timed("FREQ=DAILY;COUNT=10"), Ms(2026, 1, 8), Ms(2026, 2, 1));

        Assert.Equal(new[] { 8, 9, 10, 11, 12, 13, 14 }.Select(d => Ms(2026, 1, d, 9)), Starts(events));
    }

    [Fact]
    public void Event_that_started_before_the_range_but_overlaps_it_is_included()
    {
        var ics = IcsFixtures.Wrap(IcsFixtures.Event("UID:a", "SUMMARY:Long", "DTSTART:20261110T230000Z", "DTEND:20261111T020000Z"));

        Assert.Single(Expand(ics, Ms(2026, 11, 11), Ms(2026, 11, 12)));
        Assert.Empty(Expand(ics, Ms(2026, 11, 11, 2), Ms(2026, 11, 12)));
    }

    [Fact]
    public void Exdate_removes_one_instance()
    {
        var events = Expand(Timed("FREQ=DAILY;COUNT=3", extra: "EXDATE:20260106T090000Z"), Ms(2026, 1, 1), Ms(2026, 2, 1));

        Assert.Equal(new[] { Ms(2026, 1, 5, 9), Ms(2026, 1, 7, 9) }, Starts(events));
    }

    [Fact]
    public void Recurrence_id_override_moves_an_instance_and_keeps_its_id()
    {
        var plain = Expand(Timed("FREQ=DAILY;COUNT=3"), Ms(2026, 1, 1), Ms(2026, 2, 1));
        var ics = IcsFixtures.Wrap(
            IcsFixtures.Event("UID:r1", "SUMMARY:R", "DTSTART:20260105T090000Z", "DTEND:20260105T100000Z", "RRULE:FREQ=DAILY;COUNT=3"),
            IcsFixtures.Event("UID:r1", "SUMMARY:Moved", "RECURRENCE-ID:20260106T090000Z", "DTSTART:20260106T150000Z", "DTEND:20260106T160000Z"));

        var events = Expand(ics, Ms(2026, 1, 1), Ms(2026, 2, 1));

        Assert.Equal(3, events.Count);
        var moved = Assert.Single(events, e => e.Title == "Moved");
        Assert.Equal(Ms(2026, 1, 6, 15), moved.Start);
        Assert.Equal(plain[1].Id, moved.Id);
    }

    [Fact]
    public void Override_moved_into_the_range_from_outside_it_still_shows()
    {
        var ics = IcsFixtures.Wrap(
            IcsFixtures.Event("UID:r1", "SUMMARY:R", "DTSTART:20260105T090000Z", "DTEND:20260105T100000Z", "RRULE:FREQ=WEEKLY;COUNT=3"),
            IcsFixtures.Event("UID:r1", "SUMMARY:Pulled", "RECURRENCE-ID:20260112T090000Z", "DTSTART:20260106T090000Z", "DTEND:20260106T100000Z"));

        var events = Expand(ics, Ms(2026, 1, 6), Ms(2026, 1, 7));

        Assert.Equal(new[] { "Pulled" }, events.Select(e => e.Title));
    }

    [Fact]
    public void Cancelled_override_removes_the_instance()
    {
        var ics = IcsFixtures.Wrap(
            IcsFixtures.Event("UID:r1", "SUMMARY:R", "DTSTART:20260105T090000Z", "DTEND:20260105T100000Z", "RRULE:FREQ=DAILY;COUNT=3"),
            IcsFixtures.Event("UID:r1", "SUMMARY:R", "RECURRENCE-ID:20260106T090000Z", "DTSTART:20260106T090000Z", "STATUS:CANCELLED"));

        Assert.Equal(new[] { Ms(2026, 1, 5, 9), Ms(2026, 1, 7, 9) }, Starts(Expand(ics, Ms(2026, 1, 1), Ms(2026, 2, 1))));
    }

    [Fact]
    public void Rdate_adds_an_instance()
    {
        var ics = IcsFixtures.Wrap(IcsFixtures.Event("UID:a", "SUMMARY:A", "DTSTART:20260105T090000Z", "DTEND:20260105T100000Z", "RDATE:20260120T090000Z"));

        Assert.Equal(new[] { Ms(2026, 1, 5, 9), Ms(2026, 1, 20, 9) }, Starts(Expand(ics, Ms(2026, 1, 1), Ms(2026, 2, 1))));
    }

    [Fact]
    public void Unsupported_rule_degrades_to_the_master_instance_and_is_counted()
    {
        var ics = Timed("FREQ=YEARLY;BYWEEKNO=20;BYDAY=MO");

        var feed = IcsParser.Parse(ics);

        Assert.Equal(1, feed.UnsupportedRules);
        Assert.Single(Expand(ics, Ms(2025, 1, 1), Ms(2030, 1, 1)));
    }

    [Fact]
    public void Sub_daily_rule_is_unsupported_not_a_crash()
    {
        var ics = Timed("FREQ=HOURLY;COUNT=5");

        Assert.Equal(1, IcsParser.Parse(ics).UnsupportedRules);
        Assert.Single(Expand(ics, Ms(2026, 1, 1), Ms(2026, 2, 1)));
    }

    [Fact]
    public void Multi_day_all_day_recurrence_keeps_its_span()
    {
        var ics = IcsFixtures.Wrap(IcsFixtures.Event("UID:a", "SUMMARY:Trip", "DTSTART;VALUE=DATE:20260105", "DTEND;VALUE=DATE:20260108", "RRULE:FREQ=MONTHLY;COUNT=2"));

        var events = Expand(ics, Ms(2026, 1, 1), Ms(2026, 3, 1));

        Assert.Equal(new[] { "2026-01-05", "2026-02-05" }, events.Select(e => e.StartDate));
        Assert.Equal(new[] { "2026-01-08", "2026-02-08" }, events.Select(e => e.EndDate));
    }

    [Fact]
    public void Outlook_monthly_ordinal_weekday_in_a_windows_zone()
    {
        // Third Tuesday of each month at 14:30 Eastern: UTC-5 in winter, UTC-4 once DST starts.
        var events = Expand(IcsFixtures.Outlook, Ms(2026, 11, 1), Ms(2027, 5, 1)).Where(e => e.Title == "Planning").ToList();

        Assert.Equal(
            new[] { Ms(2026, 11, 17, 19, 30), Ms(2026, 12, 15, 19, 30), Ms(2027, 1, 19, 19, 30), Ms(2027, 2, 16, 19, 30), Ms(2027, 3, 16, 18, 30), Ms(2027, 4, 20, 18, 30) },
            Starts(events));
        Assert.Equal("Conference Room", events[0].Location);
    }

    [Fact]
    public void Outlook_yearly_all_day()
    {
        var eve = Expand(IcsFixtures.Outlook, Ms(2026, 12, 1), Ms(2028, 1, 1)).Where(e => e.Title == "Christmas Eve").ToList();

        Assert.Equal(new[] { "2026-12-24", "2027-12-24" }, eve.Select(e => e.StartDate));
    }

    [Fact]
    public void Google_weekly_standup_holds_wall_clock_time_across_the_US_dst_change()
    {
        var events = Expand(IcsFixtures.Google, Ms(2027, 3, 1), Ms(2027, 4, 1)).Where(e => e.Title == "Team standup").ToList();

        // 09:00 Eastern: 14:00Z before 14 March 2027, 13:00Z after.
        Assert.Equal(
            new[] { Ms(2027, 3, 1, 14), Ms(2027, 3, 8, 14), Ms(2027, 3, 15, 13), Ms(2027, 3, 22, 13), Ms(2027, 3, 29, 13) },
            Starts(events));
        Assert.All(events, e => Assert.Equal(30 * 60_000, e.End - e.Start));
    }

    [Fact]
    public void Weekly_event_holds_wall_clock_time_across_the_EU_dst_change_in_a_second_zone()
    {
        var ics = IcsFixtures.Wrap(IcsFixtures.Event("UID:b", "SUMMARY:Berlin", "DTSTART;TZID=Europe/Berlin:20260315T100000", "DTEND;TZID=Europe/Berlin:20260315T110000", "RRULE:FREQ=WEEKLY;BYDAY=SU"));

        var events = Expand(ics, Ms(2026, 3, 15), Ms(2026, 4, 6));

        // 10:00 Berlin: 09:00Z until the 29 March change, 08:00Z after.
        Assert.Equal(new[] { Ms(2026, 3, 15, 9), Ms(2026, 3, 22, 9), Ms(2026, 3, 29, 8), Ms(2026, 4, 5, 8) }, Starts(events));
    }

    [Fact]
    public void A_zone_defined_only_by_the_feed_matches_the_real_zone_with_the_same_rules()
    {
        string Feed(string tzid, bool withZone) => IcsFixtures.Wrap(
            withZone ? IcsFixtures.CustomZone : "",
            IcsFixtures.Event("UID:b", "SUMMARY:Z", $"DTSTART;TZID={tzid}:20260315T100000", $"DTEND;TZID={tzid}:20260315T110000", "RRULE:FREQ=WEEKLY;BYDAY=SU;COUNT=8"));

        var custom = Expand(Feed("Nexus Test Zone", true), Ms(2026, 3, 1), Ms(2026, 6, 1));
        var berlin = Expand(Feed("Europe/Berlin", false), Ms(2026, 3, 1), Ms(2026, 6, 1));

        Assert.Equal(8, custom.Count);
        Assert.Equal(Starts(berlin), Starts(custom));
    }

    [Fact]
    public void Vtimezone_only_zone_converts_the_instant_back_to_local_time_consistently()
    {
        var ics = IcsFixtures.Wrap(IcsFixtures.CustomZone,
            IcsFixtures.Event("UID:b", "SUMMARY:Z", "DTSTART;TZID=Nexus Test Zone:20261101T100000", "DTEND;TZID=Nexus Test Zone:20261101T110000", "RRULE:FREQ=DAILY;UNTIL=20261103T000000Z"));

        // UNTIL in UTC is converted into the zone's wall time (UTC+1 in November): 01:00 local on the 3rd, so only 1 and 2 November qualify.
        Assert.Equal(2, Expand(ics, Ms(2026, 10, 1), Ms(2026, 12, 1)).Count);
    }

    [Fact]
    public void Month_end_rule_across_a_wide_range_stays_bounded()
    {
        var ics = Timed("FREQ=MONTHLY;BYMONTHDAY=31", "20100131T090000Z");

        var events = Expand(ics, Ms(2026, 1, 1), Ms(2027, 1, 1));

        Assert.Equal(7, events.Count);
        Assert.All(events, e => Assert.Equal(31, DateTimeOffset.FromUnixTimeMilliseconds(e.Start).Day));
    }
}
