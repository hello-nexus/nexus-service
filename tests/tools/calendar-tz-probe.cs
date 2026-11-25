#:property InvariantGlobalization=true
#:property PublishAot=false

// Reports which time zone ids this host can resolve with the service's runtime
// settings (InvariantGlobalization on). Run on the machine in question:
//   dotnet run --file tests/tools/calendar-tz-probe.cs
// A FAIL on an IANA id (Google feeds) or a Windows id (Outlook feeds) is the case
// the calendar's WindowsZoneMap exists for. JIT run, not AOT: the lookup logic is
// the same runtime code, but this does not prove the AOT binary.
using System;

Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine($"Invariant globalization: {AppContext.TryGetSwitch("System.Globalization.Invariant", out var inv) && inv}");

foreach (var id in new[] { "America/New_York", "Europe/Berlin", "Asia/Kolkata", "Eastern Standard Time", "W. Europe Standard Time", "India Standard Time" })
{
    try
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(id);
        var offset = tz.GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Unspecified));
        Console.WriteLine($"OK   {id,-26} July offset {offset}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {id,-26} {ex.GetType().Name}");
    }
}

foreach (var iana in new[] { "America/New_York", "Europe/Berlin" })
{
    try
    {
        Console.WriteLine(TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var win)
            ? $"OK   IANA->Windows {iana} = {win}"
            : $"FAIL IANA->Windows {iana}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL IANA->Windows {iana} {ex.GetType().Name}");
    }
}
