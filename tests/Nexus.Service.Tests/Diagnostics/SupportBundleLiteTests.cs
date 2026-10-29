using System.IO.Compression;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Platform;
using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Diagnostics;

public class SupportBundleLiteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-test-lite-" + Guid.NewGuid().ToString("N")[..8]);

    public SupportBundleLiteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private DeviceIncidentLog NewLog(Func<long> now) => new(() => _root, now);

    [Fact]
    public void Ring_wraps_and_snapshot_lists_only_the_newest_entries_oldest_first()
    {
        long t = 1_000_000;
        var log = NewLog(() => t += 2000);
        for (var i = 0; i < DeviceIncidentLog.RingSize + 10; i++)
        {
            log.Note("dev-1", "keeb", DeviceEventKind.WriteFail, code: i);
        }
        Assert.True(log.Snapshot("dev-1", "test"));
        var lines = File.ReadAllLines(Path.Combine(_root, DeviceIncidentLog.FileName));
        Assert.Equal(DeviceIncidentLog.RingSize + 1, lines.Length);
        Assert.Contains($"({DeviceIncidentLog.RingSize} entries)", lines[0]);
        Assert.EndsWith("code=10", lines[1]);
        Assert.EndsWith($"code={DeviceIncidentLog.RingSize + 9}", lines[^1]);
    }

    [Fact]
    public void Snapshot_names_the_device_by_model_and_hash_never_the_raw_id()
    {
        var log = NewLog(() => 5000);
        log.Note("serial-ABC123", "keeb", DeviceEventKind.Disconnect);
        log.Snapshot("serial-ABC123", "device left");
        var text = File.ReadAllText(Path.Combine(_root, DeviceIncidentLog.FileName));
        Assert.DoesNotContain("ABC123", text);
        Assert.Contains(DeviceIncidentLog.Label("keeb", "serial-ABC123"), text);
        Assert.Matches(@"keeb#[0-9a-f]{8}", DeviceIncidentLog.Label("keeb", "serial-ABC123"));
    }

    [Fact]
    public void Steady_successes_fold_into_one_entry_so_they_do_not_evict_failures()
    {
        long t = 10_000;
        var log = NewLog(() => t);
        log.Note("d", "np50", DeviceEventKind.WriteFail);
        for (var i = 0; i < 500; i++)
        {
            t += 1;
            log.Note("d", "np50", DeviceEventKind.WriteOk);
        }
        log.Snapshot("d", "test");
        var lines = File.ReadAllLines(Path.Combine(_root, DeviceIncidentLog.FileName));
        Assert.Equal(3, lines.Length);
        Assert.Contains("WriteOk n=500", lines[2]);
    }

    [Fact]
    public void Snapshots_of_one_device_are_rate_limited_but_other_devices_and_later_times_pass()
    {
        long t = 100_000;
        var log = NewLog(() => t);
        log.Note("a", "x", DeviceEventKind.Stall);
        log.Note("b", "x", DeviceEventKind.Stall);
        Assert.True(log.Snapshot("a", "r"));
        t += 29_000;
        Assert.False(log.Snapshot("a", "r"));
        Assert.True(log.Snapshot("b", "r"));
        t += 2_000;
        Assert.True(log.Snapshot("a", "r"));
    }

    [Fact]
    public void Incident_file_rotates_once_past_its_cap()
    {
        var path = Path.Combine(_root, DeviceIncidentLog.FileName);
        File.WriteAllBytes(path, new byte[DeviceIncidentLog.MaxFileBytes + 1]);
        var log = NewLog(() => 5000);
        log.Note("a", "x", DeviceEventKind.Stall);
        log.Snapshot("a", "r");
        Assert.True(File.Exists(Path.Combine(_root, "device-incidents.1.log")));
        Assert.True(new FileInfo(path).Length < 1000);
    }

    private string RotatedLog(string stamp, int bytes, DateTime written)
    {
        var path = Path.Combine(_root, $"nexus-service-{stamp}.log");
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTime(path, written);
        return path;
    }

    [Fact]
    public void Prune_drops_rotated_logs_older_than_the_age_limit_but_keeps_the_newest_few()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0);
        var old = new[] { RotatedLog("20260101-000000", 10, now.AddDays(-30)), RotatedLog("20260102-000000", 10, now.AddDays(-29)),
            RotatedLog("20260103-000000", 10, now.AddDays(-28)), RotatedLog("20260104-000000", 10, now.AddDays(-27)) };
        var fresh = RotatedLog("20261028-000000", 10, now.AddDays(-1));
        ServiceLog.PruneRotated(_root, now, TimeSpan.FromDays(7), 1_000_000, minKeep: 3);
        Assert.False(File.Exists(old[0]));
        Assert.False(File.Exists(old[1]));
        Assert.True(File.Exists(old[2]));
        Assert.True(File.Exists(old[3]));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Prune_drops_oldest_first_until_the_total_including_the_current_log_fits()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0);
        File.WriteAllBytes(Path.Combine(_root, "nexus-service.log"), new byte[100]);
        var files = Enumerable.Range(1, 5).Select(i => RotatedLog($"2026102{i}-000000", 100, now.AddHours(-i))).ToArray();
        ServiceLog.PruneRotated(_root, now, TimeSpan.FromDays(7), 350, minKeep: 2);
        Assert.False(File.Exists(files[0]));
        Assert.False(File.Exists(files[1]));
        Assert.False(File.Exists(files[2]));
        Assert.True(File.Exists(files[3]));
        Assert.True(File.Exists(files[4]));
    }

    [Fact]
    public void Budget_takes_newest_logs_first_and_skips_what_no_longer_fits()
    {
        var t = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        SupportBundleBuilder.LogCandidate C(string n, int days, long len) => new(n, n, t.AddDays(days), len, false, null);
        var chosen = SupportBundleBuilder.SelectWithinBudget(
            new[] { C("old-small", -5, 10), C("newest", 0, 60), C("mid-big", -1, 60), C("mid-small", -2, 30) }, 100);
        Assert.Equal(new[] { "newest", "mid-small", "old-small" }, chosen.Select(c => c.EntryName));
    }

    [Fact]
    public void Budget_costs_an_oversized_log_at_its_tail_cap()
    {
        var c = new SupportBundleBuilder.LogCandidate("x", "x", DateTime.UtcNow, SupportBundleBuilder.MaxLogBytes * 10, false, null);
        Assert.Equal(SupportBundleBuilder.MaxLogBytes, c.Cost);
    }

    [Fact]
    public void Nexus_environment_lists_only_NEXUS_vars_and_redacts_secret_names()
    {
        var env = new System.Collections.Hashtable
        {
            ["NEXUS_DATA_ROOT"] = "/data", ["NEXUS_KLIPY_KEY"] = "abc", ["NEXUS_API_TOKEN"] = "t",
            ["NEXUS_X_PASSWORD"] = "p", ["NEXUS_MY_SECRET"] = "s", ["PATH"] = "/bin",
        };
        var result = SupportBundleExtras.NexusEnvironment(env);
        Assert.Equal(5, result.Count);
        Assert.Equal("/data", result["NEXUS_DATA_ROOT"]);
        Assert.All(new[] { "NEXUS_KLIPY_KEY", "NEXUS_API_TOKEN", "NEXUS_X_PASSWORD", "NEXUS_MY_SECRET" }, k => Assert.Equal("<redacted>", result[k]));
    }

    [Fact]
    public void Override_files_report_existing_files_relative_to_the_data_dir_and_skip_absent_ones()
    {
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "helper-disable.txt"), " gpu, fans \n");
        var result = SupportBundleExtras.OverrideFiles(new[] { Path.Combine(logs, "helper-disable.txt"), Path.Combine(logs, "missing.txt") }, _root);
        Assert.Single(result);
        Assert.Equal("gpu, fans", result[Path.Combine("logs", "helper-disable.txt")]);
    }

    [Theory]
    [InlineData(@"C:\Users\alice\AppData\Local\Nexus", @"C:\Users\<user>\AppData\Local\Nexus")]
    [InlineData(@"""C:\\Users\\Alice\\x""", @"""C:\\Users\\<user>\\x""")]
    [InlineData("/Users/alice/Library/Logs", "/Users/<user>/Library/Logs")]
    [InlineData("/home/alice/.local/state", "/home/<user>/.local/state")]
    [InlineData("host GAMING-PC crashed", "host <pc> crashed")]
    [InlineData(@"C:\Users\bob\x", @"C:\Users\bob\x")]
    public void Redactor_replaces_the_user_in_home_paths_and_the_machine_name(string input, string expected)
    {
        Assert.Equal(expected, new BundleRedactor(new[] { "alice" }, "gaming-pc").Apply(input));
    }

    private static DiagnosticIncident Crash(string path, string source = DiagnosticEventCatalog.SourceAppCrash) => new()
    {
        Source = source,
        App = new DiagnosticAppInfo { Name = Path.GetFileName(path), Path = path },
    };

    [Fact]
    public void Crash_filter_keeps_nexus_and_allowlisted_apps_and_non_crash_incidents_only()
    {
        var response = new IncidentsResponse
        {
            Incidents = new[]
            {
                Crash(@"C:\Program Files\Nexus\Nexus.Service.exe"),
                Crash(@"C:\Program Files\Corsair\iCUE.exe"),
                Crash(@"C:\Users\alice\Games\secret-game.exe"),
                Crash(@"C:\Windows\explorer.exe", source: DiagnosticEventCatalog.SourceGpuDriver),
            },
        };
        var kept = SupportBundleExtras.FilterCrashes(response, new[] { "iCUE" }).Incidents;
        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain(kept, i => i.App!.Name == "secret-game.exe");
    }

    [Fact]
    public void Build_redacts_paths_in_logs_orders_by_budget_and_names_nothing_after_the_machine()
    {
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "nexus-service.log"), @"opened C:\Users\alice\x on GAMING-PC");
        File.WriteAllBytes(Path.Combine(logs, "big-old.log"), new byte[2000]);
        File.SetLastWriteTimeUtc(Path.Combine(logs, "big-old.log"), DateTime.UtcNow.AddDays(-3));
        var zip = SupportBundleBuilder.Build(new SupportBundleBuilder.Sources
        {
            LogsDirectory = logs,
            OpenRgbConfigDirectory = Path.Combine(_root, "openrgb"),
            UpdatesDirectory = Path.Combine(_root, "updates"),
            UserNames = new[] { "alice" },
            MachineName = "GAMING-PC",
            LogBudgetBytes = 1000,
        });
        using var archive = new ZipArchive(new MemoryStream(zip));
        using var reader = new StreamReader(archive.GetEntry("logs/nexus-service.log")!.Open());
        Assert.Equal(@"opened C:\Users\<user>\x on <pc>", reader.ReadToEnd());
        Assert.Null(archive.GetEntry("logs/big-old.log"));
    }

    [Fact]
    public void Plist_version_is_read_from_an_xml_info_plist()
    {
        var plist = Path.Combine(_root, "Info.plist");
        File.WriteAllText(plist, "<dict><key>CFBundleShortVersionString</key>\n<string>5.2.1</string></dict>");
        Assert.Equal("5.2.1", VendorSoftwareProbe.ReadPlistVersion(plist));
        File.WriteAllText(plist, "bplist00");
        Assert.Null(VendorSoftwareProbe.ReadPlistVersion(plist));
    }

    private void Sys(string name, string vid, string pid, string product, string speed, string? serial = null)
    {
        var dir = Path.Combine(_root, "sys", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "idVendor"), vid + "\n");
        File.WriteAllText(Path.Combine(dir, "idProduct"), pid + "\n");
        File.WriteAllText(Path.Combine(dir, "product"), product + "\n");
        File.WriteAllText(Path.Combine(dir, "speed"), speed + "\n");
        if (serial is not null)
        {
            File.WriteAllText(Path.Combine(dir, "serial"), serial + "\n");
        }
    }

    [Fact]
    public void Sysfs_topology_walks_the_hub_chain_up_to_the_root_hub_and_hashes_serials()
    {
        Sys("usb1", "1d6b", "0002", "xHCI Host Controller", "480");
        Sys("1-2", "05e3", "0610", "USB Hub", "480");
        Sys("1-2.3", "0bda", "5411", "Inner Hub", "480");
        Sys("1-2.3.1", "3402", "0001", "Keeb", "12", serial: "SECRET-SERIAL");
        Sys("1-4", "046d", "c52b", "Receiver", "12");
        Directory.CreateDirectory(Path.Combine(_root, "sys", "1-2.3.1:1.0"));
        var report = UsbTopology.ReadSysfs(Path.Combine(_root, "sys"), (vid, _) => vid == 0x3402);
        var device = Assert.Single(report.Devices);
        Assert.Equal("3402:0001", device.Device.VidPid);
        Assert.Equal("12 Mb/s", device.Device.Speed);
        Assert.Equal(new[] { "1-2.3", "1-2", "usb1" }, device.Parents.Select(p => p.Port));
        Assert.DoesNotContain("SECRET", System.Text.Json.JsonSerializer.Serialize(report, Nexus.Service.Serialization.AppJsonContext.Default.UsbTopologyReport));
        Assert.Matches("^[0-9a-f]{8}$", device.Device.Id);
    }

    [Fact]
    public void Topology_reports_unavailable_instead_of_throwing_when_the_tree_is_missing()
    {
        var report = UsbTopology.ReadSysfs(Path.Combine(_root, "nope"), (_, _) => true);
        Assert.NotNull(report.Unavailable);
        Assert.Empty(report.Devices);
    }

    [Fact]
    public void Topology_collect_never_throws_on_this_os()
    {
        var report = UsbTopology.Collect((_, _) => true);
        Assert.NotNull(report);
    }

    [Fact]
    public void Write_health_feeds_the_incident_ring_and_forgetting_a_device_snapshots_it()
    {
        var log = NewLog(() => 5000);
        var health = new Nexus.Service.Lighting.LightingWriteHealth { Incidents = log };
        health.NoteFailure("dev-9", "keeb");
        health.Forget("dev-9");
        var text = File.ReadAllText(Path.Combine(_root, DeviceIncidentLog.FileName));
        Assert.Contains("WriteFail", text);
        Assert.Contains("Disconnect", text);
        Assert.Contains("device left", text);
    }
}
