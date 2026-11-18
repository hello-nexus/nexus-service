using System.IO.Compression;
using Nexus.Service.Calendar;
using Nexus.Service.Diagnostics;

namespace Nexus.Service.Tests.Calendar;

public sealed class CalendarPrivacyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-test-calendar-priv-" + Guid.NewGuid().ToString("N")[..8]);

    public CalendarPrivacyTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task A_support_bundle_carries_nothing_from_a_configured_calendar()
    {
        var fetcher = new FakeCalendarFetcher { Handler = (_, _) => FakeCalendarFetcher.Ok(IcsFixtures.Google) };
        var svc = new CalendarService(_root, fetcher);
        const string url = "https://calendar.example.com/ical/private-token-9f3a/basic.ics";
        await svc.AddAsync(url, "Private Dentist Calendar", "#7b5cff", CancellationToken.None);
        File.WriteAllText(Path.Combine(_root, "logs", "nexus-service.log"), "[calendar] source cal_x sync status=200 bytes=10 error=none\n");
        Assert.True(File.Exists(Path.Combine(_root, "calendar", "sources.json")));

        var bytes = SupportBundleBuilder.Build(new SupportBundleBuilder.Sources { LogsDirectory = Path.Combine(_root, "logs") });

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.NotEmpty(zip.Entries);
        foreach (var entry in zip.Entries)
        {
            Assert.DoesNotContain("calendar/", entry.FullName.Replace('\\', '/') + "/", StringComparison.Ordinal);
            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();
            foreach (var secret in new[] { "private-token", "basic.ics", "Team standup", "Room 4", "Blockers", "Offsite", "Private Dentist Calendar", "standup-1@google.com" })
            {
                Assert.DoesNotContain(secret, text);
            }
        }
    }

    [Fact]
    public void No_support_bundle_source_resolves_under_the_calendar_directory()
    {
        // The production collector in DiagnosticsHealthRoutes lists every directory it ships from;
        // none of them may be, or sit under, <NexusRoot>/calendar.
        var routes = File.ReadAllText(FindFile("src/Routes/DiagnosticsHealthRoutes.cs"));
        var builder = File.ReadAllText(FindFile("src/Diagnostics/SupportBundleBuilder.cs"));

        Assert.DoesNotContain("\"calendar\"", routes + builder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nexus.Service.Calendar", routes + builder, StringComparison.Ordinal);
    }

    private static string FindFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void The_calendar_files_sit_outside_nexus_settings()
    {
        // NexusSettings is what support bundles and settings exports serialize; a calendar
        // field on it would need its own redaction line, so the types must not appear there.
        var names = typeof(Nexus.Service.Persistence.NexusSettings).GetProperties().Select(p => p.PropertyType.FullName ?? "");

        Assert.DoesNotContain(names, n => n.Contains("Calendar", StringComparison.Ordinal));
    }
}
