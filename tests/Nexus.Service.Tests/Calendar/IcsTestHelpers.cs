using Nexus.Service.Calendar.Ics;
using Nexus.Service.Models.Calendar;

namespace Nexus.Service.Tests.Calendar;

internal static class IcsTestHelpers
{
    public static long Ms(int y, int mo, int d, int h = 0, int mi = 0) =>
        new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public static List<CalendarEventDto> Expand(string ics, long from, long to, string calendarId = "c1")
    {
        var events = new List<CalendarEventDto>();
        IcsEventExpander.Expand(IcsParser.Parse(ics), calendarId, from, to, events);
        events.Sort((a, b) => a.Start.CompareTo(b.Start));
        return events;
    }

    public static List<long> Starts(IEnumerable<CalendarEventDto> events) => events.Select(e => e.Start).ToList();
}
