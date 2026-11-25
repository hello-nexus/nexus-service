namespace Nexus.Service.Tests.Calendar;

/// <summary>Scrubbed, hand-trimmed feeds in the dialects the common providers emit.</summary>
internal static class IcsFixtures
{
    public static string Wrap(params string[] components) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Nexus Test//EN\r\n" + string.Join("\r\n", components) + "\r\nEND:VCALENDAR\r\n";

    public static string Event(params string[] lines) =>
        "BEGIN:VEVENT\r\n" + string.Join("\r\n", lines) + "\r\nEND:VEVENT";

    /// <summary>Google style: IANA TZIDs, a VTIMEZONE for the calendar zone only, folded lines, escaped text.</summary>
    public const string Google =
        "BEGIN:VCALENDAR\r\n" +
        "PRODID:-//Google Inc//Google Calendar 70.9054//EN\r\n" +
        "VERSION:2.0\r\n" +
        "CALSCALE:GREGORIAN\r\n" +
        "METHOD:PUBLISH\r\n" +
        "X-WR-CALNAME:Work\r\n" +
        "X-WR-TIMEZONE:America/New_York\r\n" +
        "BEGIN:VTIMEZONE\r\n" +
        "TZID:America/New_York\r\n" +
        "X-LIC-LOCATION:America/New_York\r\n" +
        "BEGIN:DAYLIGHT\r\nTZOFFSETFROM:-0500\r\nTZOFFSETTO:-0400\r\nTZNAME:EDT\r\nDTSTART:19700308T020000\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU\r\nEND:DAYLIGHT\r\n" +
        "BEGIN:STANDARD\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0500\r\nTZNAME:EST\r\nDTSTART:19701101T020000\r\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU\r\nEND:STANDARD\r\n" +
        "END:VTIMEZONE\r\n" +
        "BEGIN:VEVENT\r\n" +
        "DTSTART;TZID=America/New_York:20261116T090000\r\n" +
        "DTEND;TZID=America/New_York:20261116T093000\r\n" +
        "RRULE:FREQ=WEEKLY;BYDAY=MO\r\n" +
        "DTSTAMP:20261101T000000Z\r\n" +
        "UID:standup-1@google.com\r\n" +
        "SUMMARY:Team standup\r\n" +
        "DESCRIPTION:Agenda:\\n- Blockers\\, risks\\n- Plan; with a very long line th\r\n" +
        " at is folded across two physical lines\r\n" +
        "LOCATION:Room 4\\, Floor 2\r\n" +
        "STATUS:CONFIRMED\r\n" +
        "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT10M\r\nSUMMARY:alarm text must be ignored\r\nEND:VALARM\r\n" +
        "END:VEVENT\r\n" +
        "BEGIN:VEVENT\r\n" +
        "DTSTART;VALUE=DATE:20261125\r\n" +
        "DTEND;VALUE=DATE:20261127\r\n" +
        "UID:offsite-1@google.com\r\n" +
        "SUMMARY:Offsite\r\n" +
        "END:VEVENT\r\n" +
        "BEGIN:VEVENT\r\n" +
        "DTSTART:20261117T190000Z\r\n" +
        "DTEND:20261117T200000Z\r\n" +
        "UID:utc-1@google.com\r\n" +
        "SUMMARY:Call\r\n" +
        "STATUS:TENTATIVE\r\n" +
        "END:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    /// <summary>Outlook style: Windows zone names, a VTIMEZONE with 1601 anchors, DURATION-free DTEND, INTERVAL on every rule.</summary>
    public const string Outlook =
        "BEGIN:VCALENDAR\r\n" +
        "METHOD:PUBLISH\r\n" +
        "PRODID:Microsoft Exchange Server 2010\r\n" +
        "VERSION:2.0\r\n" +
        "X-WR-CALNAME:Calendar\r\n" +
        "BEGIN:VTIMEZONE\r\n" +
        "TZID:Eastern Standard Time\r\n" +
        "BEGIN:STANDARD\r\nDTSTART:16010101T020000\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0500\r\nRRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=1SU;BYMONTH=11\r\nEND:STANDARD\r\n" +
        "BEGIN:DAYLIGHT\r\nDTSTART:16010101T020000\r\nTZOFFSETFROM:-0500\r\nTZOFFSETTO:-0400\r\nRRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=2SU;BYMONTH=3\r\nEND:DAYLIGHT\r\n" +
        "END:VTIMEZONE\r\n" +
        "BEGIN:VEVENT\r\n" +
        "DTSTART;TZID=Eastern Standard Time:20261117T143000\r\n" +
        "DTEND;TZID=Eastern Standard Time:20261117T153000\r\n" +
        "RRULE:FREQ=MONTHLY;INTERVAL=1;BYDAY=3TU\r\n" +
        "UID:040000008200E00074C5B7101A82E008000000001\r\n" +
        "SUMMARY:Planning\r\n" +
        "LOCATION:Conference Room\r\n" +
        "END:VEVENT\r\n" +
        "BEGIN:VEVENT\r\n" +
        "DTSTART;VALUE=DATE:20261224\r\n" +
        "DTEND;VALUE=DATE:20261225\r\n" +
        "RRULE:FREQ=YEARLY;INTERVAL=1;BYMONTH=12;BYMONTHDAY=24\r\n" +
        "UID:holiday-1\r\n" +
        "SUMMARY:Christmas Eve\r\n" +
        "END:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    /// <summary>A zone only the feed defines (Berlin rules under a made-up name), to exercise the VTIMEZONE path.</summary>
    public const string CustomZone =
        "BEGIN:VTIMEZONE\r\n" +
        "TZID:Nexus Test Zone\r\n" +
        "BEGIN:DAYLIGHT\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\nDTSTART:19700329T020000\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU\r\nEND:DAYLIGHT\r\n" +
        "BEGIN:STANDARD\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nDTSTART:19701025T030000\r\nRRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU\r\nEND:STANDARD\r\n" +
        "END:VTIMEZONE";
}
