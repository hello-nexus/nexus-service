using Nexus.Service.Calendar.Ics;
using static Nexus.Service.Tests.Calendar.IcsTestHelpers;

namespace Nexus.Service.Tests.Calendar;

/// <summary>
/// The service builds with InvariantGlobalization, which on Windows drops the ICU
/// IANA/Windows id conversion. These assert the resolver reaches a real zone for
/// both id dialects without relying on that conversion. They run on whatever host
/// executes the suite; the Windows-specific answer needs a run on Windows.
/// </summary>
public sealed class IcsZoneTests
{
    [Theory]
    [InlineData("America/New_York", -4.0)]
    [InlineData("Eastern Standard Time", -4.0)]
    [InlineData("Europe/Berlin", 2.0)]
    [InlineData("W. Europe Standard Time", 2.0)]
    [InlineData("Asia/Tokyo", 9.0)]
    [InlineData("Tokyo Standard Time", 9.0)]
    [InlineData("Australia/Sydney", 10.0)]
    [InlineData("AUS Eastern Standard Time", 10.0)]
    [InlineData("Asia/Kolkata", 5.5)]
    [InlineData("India Standard Time", 5.5)]
    [InlineData("America/Phoenix", -7.0)]
    [InlineData("US Mountain Standard Time", -7.0)]
    public void Both_id_dialects_resolve_without_falling_back_to_the_local_zone(string tzid, double julyOffsetHours)
    {
        var resolver = new IcsZoneResolver(new IcsFeed());

        var utc = resolver.Resolve(tzid).LocalToUtcMs(new DateTime(2026, 7, 1, 12, 0, 0));

        Assert.Equal(0, resolver.Fallbacks);
        Assert.Equal(Ms(2026, 7, 1, 12) - (long)(julyOffsetHours * 3_600_000), utc);
    }

    [Fact]
    public void Every_mapped_windows_zone_resolves_on_this_host()
    {
        var failures = new List<string>();
        foreach (var id in WindowsZoneMap.WindowsIds)
        {
            var resolver = new IcsZoneResolver(new IcsFeed());
            resolver.Resolve(id);
            if (resolver.Fallbacks != 0)
            {
                failures.Add(id);
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Primary_iana_ids_round_trip_to_their_windows_id()
    {
        foreach (var id in WindowsZoneMap.WindowsIds)
        {
            Assert.Equal(id, WindowsZoneMap.ToWindows(WindowsZoneMap.ToIana(id)!), ignoreCase: true);
        }
    }

    [Fact]
    public void Unknown_zone_with_no_vtimezone_falls_back_and_is_counted()
    {
        var resolver = new IcsZoneResolver(new IcsFeed());

        resolver.Resolve("Mars/Olympus_Mons");

        Assert.Equal(1, resolver.Fallbacks);
    }

    [Fact]
    public void Floating_time_uses_the_feeds_default_zone()
    {
        var ics = IcsFixtures.Wrap("X-WR-TIMEZONE:Asia/Tokyo", IcsFixtures.Event("UID:a", "SUMMARY:A", "DTSTART:20260701T120000", "DTEND:20260701T130000"));

        var events = Expand(ics, Ms(2026, 6, 30), Ms(2026, 7, 2));

        Assert.Equal(Ms(2026, 7, 1, 3), Assert.Single(events).Start);
    }

    [Fact]
    public void Utc_designator_wins_over_any_zone()
    {
        var resolver = new IcsZoneResolver(new IcsFeed { DefaultTz = "Asia/Tokyo" });

        Assert.Equal(Ms(2026, 7, 1, 12), resolver.Resolve("UTC").LocalToUtcMs(new DateTime(2026, 7, 1, 12, 0, 0)));
    }
}
