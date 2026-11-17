using System.IO.Compression;
using Nexus.Service.Diagnostics;

namespace Nexus.Service.Tests.Diagnostics;

public class SupportBundleSourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-test-sources-" + Guid.NewGuid().ToString("N")[..8]);

    public SupportBundleSourcesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relative, string content, DateTime? whenUtc = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (whenUtc is not null)
        {
            File.SetLastWriteTimeUtc(path, whenUtc.Value);
        }
        return path;
    }

    private SupportBundleBuilder.Sources Base() => new()
    {
        LogsDirectory = Path.Combine(_root, "logs"),
        OpenRgbConfigDirectory = Path.Combine(_root, "openrgb"),
        UpdatesDirectory = Path.Combine(_root, "updates"),
        UserNames = new[] { "alice" },
        MachineName = "GAMING-PC",
    };

    private static Dictionary<string, string> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    private const string Stamp = "2026-10-28 23:15:34.136   ";

    // Real Inno logs prefix every line with a timestamp.
    private static string InnoLog(string appLine) => $"{Stamp}Log opened. (Time zone: UTC-07:00)\n{Stamp}Setup version: Inno Setup version 6.7.1\n{Stamp}{appLine}\n{Stamp}Opened as C:\\Users\\alice\\x on GAMING-PC\n";

    // Captured from a hand-run Nexus-Setup on the lab PC (names changed), cancelled at the first prompt.
    private const string RealNexusLog = """
        2026-10-28 23:15:34.136   Log opened. (Time zone: UTC-07:00)
        2026-10-28 23:15:34.136   Setup version: Inno Setup version 6.7.1
        2026-10-28 23:15:34.136   Original Setup EXE: C:\Users\Public\Desktop\Nexus-Setup-3.0.21-alpha.1.exe
        2026-10-28 23:15:34.136   Setup command line: /SL5="$681222,48521675,1008640,C:\Users\Public\Desktop\Nexus-Setup-3.0.21-alpha.1.exe" /SPAWNWND=$17150C /FIRSTWND=$290CDC
        2026-10-28 23:15:34.136   Compatibility mode: Yes (DetectorsAdminProtection Installer)
        2026-10-28 23:15:34.136   Windows version: 10.0.26300
        2026-10-28 23:15:34.140   Created protected temporary directory: C:\Users\alice\AppData\Local\Temp\is-NVLAXVQ54I.tmp
        2026-10-28 23:15:34.453   [nexus] WebView2 runtime installed=1 pv=156.0.4314.50
        2026-10-28 23:15:46.206   Deinitializing Setup.
        2026-10-28 23:15:46.233   Log closed.
        """;

    [Fact]
    public void Inno_filter_accepts_a_real_timestamped_nexus_log_the_web_installer_and_an_nexus_marker_only_log()
    {
        var real = Write("t/real.txt", RealNexusLog);
        var web = Write("t/web.txt", InnoLog(@"Original Setup EXE: C:\Users\alice\Downloads\Nexus-Installer-Beta.exe"));
        var markerOnly = Write("t/marker.txt", $"{Stamp}Log opened.\n{Stamp}[nexus] WebView2 runtime installed=1 pv=1\n");
        var other = Write("t/other.txt", InnoLog(@"Original Setup EXE: C:\Users\alice\Downloads\OtherApp-Setup.exe"));
        Assert.True(SupportBundleExtras.IsNexusInstallerLog(real));
        Assert.True(SupportBundleExtras.IsNexusInstallerLog(web));
        Assert.True(SupportBundleExtras.IsNexusInstallerLog(markerOnly));
        Assert.False(SupportBundleExtras.IsNexusInstallerLog(other));
    }

    [Fact]
    public void Inno_filter_accepts_the_nexus_installer_and_rejects_other_vendors()
    {
        var nexusById = Write("t/a.txt", InnoLog($"Creating new uninstall key: HKLM\\Software\\{{{SupportBundleExtras.InstallerAppId}}}_is1"));
        var nexusByExe = Write("t/b.txt", InnoLog(@"Original Setup EXE: C:\Users\alice\Downloads\Nexus-Setup (1).exe"));
        var other = Write("t/c.txt", InnoLog(@"Original Setup EXE: C:\Users\alice\Downloads\OtherApp-Setup.exe"));
        Assert.True(SupportBundleExtras.IsNexusInstallerLog(nexusById));
        Assert.True(SupportBundleExtras.IsNexusInstallerLog(nexusByExe));
        Assert.False(SupportBundleExtras.IsNexusInstallerLog(other));
        Assert.False(SupportBundleExtras.IsNexusInstallerLog(Path.Combine(_root, "t", "missing.txt")));
    }

    [Fact]
    public void Installer_logs_ship_newest_three_nexus_only_and_are_redacted()
    {
        var now = DateTime.UtcNow;
        var exe = @"Original Setup EXE: C:\Users\alice\Downloads\Nexus-Setup.exe";
        for (var i = 1; i <= 4; i++)
        {
            Write($"temp1/Setup Log 2026-01-0{i} #001.txt", InnoLog(exe), now.AddHours(-i));
        }
        Write("temp1/Setup Log 2026-01-09 #001.txt", InnoLog(@"Original Setup EXE: C:\x\Other.exe"), now);
        Write("systemtemp/Setup Log 2026-01-05 #001.txt", InnoLog(exe), now.AddHours(-10));
        var src = new SupportBundleBuilder.Sources
        {
            LogsDirectory = Base().LogsDirectory,
            OpenRgbConfigDirectory = Base().OpenRgbConfigDirectory,
            UpdatesDirectory = Base().UpdatesDirectory,
            UserNames = new[] { "alice" },
            MachineName = "GAMING-PC",
            InstallerLogDirectories = new[] { ("alice", Path.Combine(_root, "temp1")) },
            InstallerSystemDirectories = new[] { Path.Combine(_root, "systemtemp") },
        };
        var entries = Entries(SupportBundleBuilder.Build(src)).Where(e => e.Key.StartsWith("installer/")).ToList();
        Assert.Equal(3, entries.Count);
        Assert.DoesNotContain(entries, e => e.Key.Contains("01-09"));
        Assert.DoesNotContain(entries, e => e.Key.Contains("system-1"));
        Assert.All(entries, e =>
        {
            Assert.DoesNotContain("alice", e.Value);
            Assert.DoesNotContain("GAMING-PC", e.Value);
        });
    }

    [Fact]
    public void Missing_or_unreadable_source_dirs_do_not_break_the_bundle()
    {
        var src = new SupportBundleBuilder.Sources
        {
            LogsDirectory = Base().LogsDirectory,
            OpenRgbConfigDirectory = Base().OpenRgbConfigDirectory,
            UpdatesDirectory = Base().UpdatesDirectory,
            InstallerLogDirectories = new[] { ("ghost", Path.Combine(_root, "nope", "Temp")) },
            InstallerSystemDirectories = new[] { Path.Combine(_root, "nope2") },
            SplashLogDirectories = new[] { ("ghost", Path.Combine(_root, "nope", "updater")) },
            LaunchdLogPaths = new[] { Path.Combine(_root, "nope3", "nexus-service.log") },
        };
        var entries = Entries(SupportBundleBuilder.Build(src));
        Assert.Contains("support-info.json", entries.Keys);
        Assert.DoesNotContain(entries.Keys, k => k.StartsWith("installer/") || k.StartsWith("splash/") || k.StartsWith("launchd/"));
    }

    [Fact]
    public void Splash_log_ships_from_each_profile_updater_dir_redacted()
    {
        Write("p1/updater/splash.log", @"2026-01-01 exit C:\Users\alice\AppData on GAMING-PC");
        var src = new SupportBundleBuilder.Sources
        {
            LogsDirectory = Base().LogsDirectory,
            OpenRgbConfigDirectory = Base().OpenRgbConfigDirectory,
            UpdatesDirectory = Base().UpdatesDirectory,
            UserNames = new[] { "alice" },
            MachineName = "GAMING-PC",
            SplashLogDirectories = new[] { ("alice", Path.Combine(_root, "p1", "updater")) },
        };
        var entries = Entries(SupportBundleBuilder.Build(src));
        var text = Assert.Single(entries, e => e.Key.StartsWith("splash/")).Value;
        Assert.Equal(@"2026-01-01 exit C:\Users\<user>\AppData on <pc>", text);
    }

    [Fact]
    public void Extra_diagnostic_files_in_the_logs_dir_ship_by_explicit_pattern_only()
    {
        Write("logs/qseries-home-diag-20260101-000000-SER-boot.txt", @"adb at C:\Users\alice\x");
        Write("logs/lighting-bench-20260101-000000.json", "{}");
        Write("logs/lighting-bench-20260101-000000.txt", "table");
        Write("logs/lighting-bench-20260101-000000-wgl-dedicated.child.json", "{}");
        Write("logs/helper-disable.txt", "x");
        Write("logs/random-notes.txt", "x");
        var entries = Entries(SupportBundleBuilder.Build(Base()));
        Assert.Equal(@"adb at C:\Users\<user>\x", entries["logs/qseries-home-diag-20260101-000000-SER-boot.txt"]);
        Assert.Contains("logs/lighting-bench-20260101-000000.json", entries.Keys);
        Assert.Contains("logs/lighting-bench-20260101-000000.txt", entries.Keys);
        Assert.DoesNotContain("logs/lighting-bench-20260101-000000-wgl-dedicated.child.json", entries.Keys);
        Assert.DoesNotContain("logs/helper-disable.txt", entries.Keys);
        Assert.DoesNotContain("logs/random-notes.txt", entries.Keys);
    }

    [Fact]
    public void Launchd_files_ship_tail_capped_and_redacted()
    {
        var big = Write("tmp/nexus-service.log", new string('a', (int)SupportBundleBuilder.MaxLogBytes + 100) + @" /Users/alice/x");
        var err = Write("tmp/nexus-service.err", "boom /Users/alice/y");
        var src = Base();
        var withLaunchd = new SupportBundleBuilder.Sources
        {
            LogsDirectory = src.LogsDirectory,
            OpenRgbConfigDirectory = src.OpenRgbConfigDirectory,
            UpdatesDirectory = src.UpdatesDirectory,
            UserNames = src.UserNames,
            MachineName = src.MachineName,
            LaunchdLogPaths = new[] { big, err },
        };
        var entries = Entries(SupportBundleBuilder.Build(withLaunchd));
        Assert.Equal("boom /Users/<user>/y", entries["launchd/nexus-service.err"]);
        var tail = entries["launchd/nexus-service.log"];
        Assert.StartsWith("[support-bundle] last 5 MB", tail);
        Assert.EndsWith("/Users/<user>/x", tail);
    }

    [Fact]
    public void Journal_runs_the_nexus_system_unit_bounded_and_ships_redacted_against_the_budget()
    {
        string[]? seen = null;
        var text = SupportBundleExtras.ReadJournal(args =>
        {
            seen = args;
            return @"Jan 01 nexus[1]: opened /home/alice/x on GAMING-PC";
        });
        Assert.Equal("nexus.service", seen![2]);
        Assert.Contains("--since", seen);
        Assert.Contains("7 days ago", seen);
        Assert.Contains("--no-pager", seen);

        var src = Base();
        Write("logs/old.log", new string('x', 500), DateTime.UtcNow.AddDays(-1));
        var bundle = new SupportBundleBuilder.Sources
        {
            LogsDirectory = src.LogsDirectory,
            OpenRgbConfigDirectory = src.OpenRgbConfigDirectory,
            UpdatesDirectory = src.UpdatesDirectory,
            UserNames = src.UserNames,
            MachineName = src.MachineName,
            JournalText = text,
            LogBudgetBytes = text.Length + 100,
        };
        var entries = Entries(SupportBundleBuilder.Build(bundle));
        Assert.Equal("Jan 01 nexus[1]: opened /home/<user>/x on <pc>", entries["logs/journal-nexus.service.txt"]);
        Assert.DoesNotContain("logs/old.log", entries.Keys);
    }

    [Fact]
    public void Journal_fails_soft_and_caps_its_size()
    {
        Assert.Equal("", SupportBundleExtras.ReadJournal(_ => ""));
        Assert.Equal("", SupportBundleExtras.ReadJournal(_ => throw new InvalidOperationException("no journalctl")));
        var capped = SupportBundleExtras.ReadJournal(_ => new string('j', SupportBundleExtras.MaxJournalChars + 50));
        Assert.StartsWith("[support-bundle] last 4 MB", capped);
        Assert.True(capped.Length < SupportBundleExtras.MaxJournalChars + 100);
    }

    [Fact]
    public void Profile_subdirectories_are_deduplicated_and_labeled_by_profile()
    {
        var dirs = SupportBundleBuilder.ProfileSubdirectoriesFor(new[] { "/u/alice", "/u/alice/", "/u/bob" }, "AppData", "Local", "Temp");
        Assert.Equal(new[] { "alice", "bob" }, dirs.Select(d => d.Label));
        Assert.Equal(Path.Combine("/u/alice", "AppData", "Local", "Temp"), dirs[0].Directory);
    }
}
