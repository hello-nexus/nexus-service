using System.IO.Compression;
using Nexus.Service.Devices;
using Nexus.Service.Diagnostics;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Tests.Diagnostics;

public class SupportBundleReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-test-review-" + Guid.NewGuid().ToString("N")[..8]);

    public SupportBundleReviewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class StubHandler : IDeviceHandler
    {
        public string Id => "stub";
        public string Name => "Stub";
        public string Category => "hub";
        public IReadOnlyList<UsbId> Identifiers { get; init; } = Array.Empty<UsbId>();
        public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) => true;
        public string GetFirmwareVersion() => "";
    }

    private static Dictionary<string, string> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    // Redactor built the way the route builds it, from the identity collector.
    private static BundleRedactor RedactorFor(BundleIdentity id, string machine) =>
        new(id.UserNames, machine, id.HostNames, id.AccountNames);

    [Fact]
    public void Console_logon_name_is_redacted_as_a_token_and_after_as_even_when_no_profile_matches()
    {
        var id = SupportBundleExtras.CollectIdentity("SYSTEM", new[] { @"C:\Users\alice-old" }, "bobby", null, Array.Empty<string>(), "GAMING-PC", null);
        var r = RedactorFor(id, "GAMING-PC");
        Assert.Equal("[helper-bootstrap] launched --helper as <user> ok", r.Apply("[helper-bootstrap] launched --helper as bobby ok"));
        Assert.Equal("owner <user> logged in", r.Apply("owner Bobby logged in"));
        Assert.Equal(@"C:\Users\<user>\x", r.Apply(@"C:\Users\alice-old\x"));
    }

    [Theory]
    [InlineData(@"D:\Users\JOHNSM~1\AppData", @"D:\Users\<user>\AppData")]
    [InlineData(@"E:/Users/zed/x", @"E:/Users/<user>/x")]
    [InlineData(@"{""p"":""C:\\Users\\zed\\x""}", @"{""p"":""C:\\Users\\<user>\\x""}")]
    [InlineData("/media/zed/disk", "/media/<user>/disk")]
    [InlineData("/run/media/zed/disk", "/run/media/<user>/disk")]
    [InlineData("/home/zed/.config", "/home/<user>/.config")]
    [InlineData("mail zed@example.com now", "mail <email> now")]
    public void Unknown_account_names_are_caught_by_the_generic_path_rule(string input, string expected)
    {
        Assert.Equal(expected, new BundleRedactor(Array.Empty<string>(), "gaming-pc").Apply(input));
    }

    [Fact]
    public void Redaction_leaves_mac_addresses_serials_and_lan_ips_alone()
    {
        const string text = "mac 00:1A:2B:3C:4D:5E serial ABC123XYZ ip 192.168.1.20";
        Assert.Equal(text, new BundleRedactor(new[] { "alice" }, "gaming-pc").Apply(text));
    }

    [Fact]
    public void Linux_root_daemon_redacts_the_session_user_and_keeps_the_word_root()
    {
        var id = SupportBundleExtras.CollectIdentity("root", Array.Empty<string>(), null, "/home/alice", new[] { "/home/alice", "/home/guestuser" }, "box", null);
        Assert.DoesNotContain("root", id.AccountNames);
        var r = RedactorFor(id, "box");
        Assert.Equal("[session] home /home/<user> and /home/<user>/x", r.Apply("[session] home /home/alice and /home/guestuser/x"));
        Assert.Equal("root hub 1-0:1.0", r.Apply("root hub 1-0:1.0"));
        Assert.Equal("/home/<user>/y", r.Apply("/home/someoneelse/y"));
    }

    [Fact]
    public void Identity_collects_process_console_profiles_homes_and_dns_name()
    {
        var id = SupportBundleExtras.CollectIdentity("svc", new[] { @"C:\Users\p1", @"C:\Users\Default" }, "con", "/home/lin", new[] { "/Users/mac1", "/Users/Shared" }, "NETBIOS15", "netbios15.example.lan");
        Assert.Contains("svc", id.AccountNames);
        Assert.Contains("con", id.AccountNames);
        Assert.Contains("lin", id.AccountNames);
        Assert.DoesNotContain("p1", id.AccountNames);
        Assert.Contains("p1", id.UserNames);
        Assert.Contains("mac1", id.UserNames);
        Assert.DoesNotContain("Shared", id.UserNames);
        Assert.Equal(new[] { "netbios15.example.lan" }, id.HostNames);
        Assert.Empty(SupportBundleExtras.CollectIdentity("u", Array.Empty<string>(), null, null, Array.Empty<string>(), "H", "h").HostNames);
    }

    [Fact]
    public void Machine_name_is_not_replaced_inside_device_names_but_is_in_carrier_contexts()
    {
        var r = new BundleRedactor(Array.Empty<string>(), "TITAN");
        Assert.Equal("TITAN 360 pump on KRAKEN", r.Apply("TITAN 360 pump on KRAKEN"));
        Assert.Equal(@"<pc>\alice", r.Apply(@"TITAN\alice"));
        Assert.Equal(@"\\<pc>\share", r.Apply(@"\\TITAN\share"));
        Assert.Equal("<pc>.local", r.Apply("TITAN.local"));
        Assert.Equal("http://<pc>:9400/x", r.Apply("http://TITAN:9400/x"));
        Assert.Equal("host: <pc>", r.Apply("host: TITAN"));
        Assert.Equal(@"{""pcName"":""<pc>""}", r.Apply(@"{""pcName"":""TITAN""}"));
    }

    [Fact]
    public void Distinctive_machine_names_and_the_dns_host_name_are_replaced_anywhere()
    {
        var r = new BundleRedactor(Array.Empty<string>(), "DESKTOP-AB12CDE", new[] { "DESKTOP-AB12CDE-LONGER" });
        Assert.Equal("a <pc> b and <pc> c", r.Apply("a DESKTOP-AB12CDE b and DESKTOP-AB12CDE-LONGER c"));
    }

    [Fact]
    public void Known_usb_ids_match_declared_identifiers_not_live_connection_state()
    {
        var handlers = new[] { new StubHandler { Identifiers = new[] { new UsbId(0x0E8D, 0x201D) } } };
        Assert.True(SupportBundleExtras.IsKnownUsbId(handlers, 0x0E8D, 0x201D));
        Assert.False(SupportBundleExtras.IsKnownUsbId(handlers, 0x046D, 0xC52B));
        Assert.False(SupportBundleExtras.IsKnownUsbId(Array.Empty<IDeviceHandler>(), 0x0E8D, 0x201D));
    }

    [Fact]
    public void Support_info_and_openrgb_json_are_redacted_and_placeholders_stay_readable()
    {
        var rgb = Path.Combine(_root, "openrgb");
        Directory.CreateDirectory(rgb);
        File.WriteAllText(Path.Combine(rgb, "OpenRGB.json"), @"{""path"":""C:\\Users\\alice\\x"",""host"":""host: GAMING-PC""}");
        var zip = SupportBundleBuilder.Build(new SupportBundleBuilder.Sources
        {
            LogsDirectory = Path.Combine(_root, "logs"),
            OpenRgbConfigDirectory = rgb,
            UpdatesDirectory = Path.Combine(_root, "updates"),
            UserNames = new[] { "alice" },
            MachineName = "GAMING-PC",
            Info = new SupportInfo
            {
                MachineName = "<pc>",
                OverrideFiles = { ["logs/helper-disable.txt"] = @"C:\Users\alice\y" },
                LightingDevices = { "[0] GAMING-PC board" },
            },
        });
        var entries = Entries(zip);
        Assert.Contains(@"""machineName"":""<pc>""", entries["support-info.json"]);
        Assert.DoesNotContain("alice", entries["support-info.json"]);
        Assert.DoesNotContain("GAMING-PC", entries["support-info.json"]);
        Assert.DoesNotContain("alice", entries["openrgb/OpenRGB.json"]);
        Assert.DoesNotContain("GAMING-PC", entries["openrgb/OpenRGB.json"]);
    }

    [Fact]
    public void Settings_copy_blanks_cloud_identity_phone_names_weather_coordinates_and_host_name()
    {
        var s = new NexusSettings();
        s.HostDisplayName = "Alice's rig";
        s.Weather.Locations.Add(new WeatherSavedLocation { Lat = 47.6, Lon = -122.3, Label = "Seattle", Cc = "US" });
        s.Weather.Locations.Add(new WeatherSavedLocation { Lat = 1, Lon = 2, Label = "12 Main St", Cc = "US" });
        s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "uuid-1", Email = "a@b.com", Username = "alice", AvatarLarge = "https://x/a.png", AvatarSmall = "https://x/b.png" });
        s.Auth!.ActiveCloudAccountId = "uuid-1";
        s.Auth!.PanelPhoneSessions.Add(new PanelPhoneSessionToken { Name = "Alice's iPhone", DeviceType = "iPhone" });
        var copy = SupportBundleBuilder.Redact(s);
        Assert.Equal("<redacted>", copy.HostDisplayName);
        Assert.Equal(0, copy.Weather.Locations[0].Lat);
        Assert.Equal(0, copy.Weather.Locations[0].Lon);
        Assert.Equal("Seattle", copy.Weather.Locations[0].Label);
        Assert.Equal("<redacted>", copy.Weather.Locations[1].Label);
        var account = Assert.Single(copy.Auth!.CloudAccounts);
        Assert.Equal("<redacted>", account.Email);
        Assert.Equal("<redacted>", account.Username);
        Assert.Equal("", account.AvatarLarge);
        Assert.Equal("account-1", account.AccountId);
        Assert.Equal("account-1", copy.Auth!.ActiveCloudAccountId);
        Assert.Equal("<redacted>", copy.Auth!.PanelPhoneSessions[0].Name);
        Assert.Equal("iPhone", copy.Auth!.PanelPhoneSessions[0].DeviceType);
        Assert.Equal("Alice's rig", s.HostDisplayName);
    }

    private DeviceIncidentLog NewLog(Func<long> now) => new(() => _root, now);

    private string IncidentText(DeviceIncidentLog log)
    {
        log.WaitIdle();
        var path = Path.Combine(_root, DeviceIncidentLog.FileName);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private static int Headers(string text) => text.Split('\n').Count(l => l.StartsWith("=== ", StringComparison.Ordinal));

    [Fact]
    public void A_failing_flip_snapshots_the_ring_with_a_failing_entry()
    {
        long ms = 0;
        var clock = TimeSpan.Zero;
        var log = NewLog(() => ms);
        var health = new LightingWriteHealth(() => clock) { Incidents = log };
        for (var i = 0; i < 17; i++)
        {
            health.NoteFailure("hub-1", "keeb");
            clock += TimeSpan.FromSeconds(1);
            ms += 1000;
        }
        var text = IncidentText(log);
        Assert.Contains("write failures sustained", text);
        Assert.Contains("Failing", text);
    }

    [Fact]
    public void A_drop_after_a_failure_snapshot_is_still_recorded()
    {
        long ms = 100_000;
        var log = NewLog(() => ms);
        log.Note("d", "keeb", DeviceEventKind.WriteFail);
        Assert.True(log.Snapshot("d", "write failures sustained"));
        ms += 10_000;
        log.Note("d", "keeb", DeviceEventKind.WriteFail);
        log.Disconnect("d", "chain dropped");
        var text = IncidentText(log);
        Assert.Contains("write failures sustained", text);
        Assert.Contains("chain dropped", text);
    }

    [Fact]
    public void A_flapping_device_backs_off_and_keeps_its_first_incident()
    {
        long ms = 0;
        var log = NewLog(() => ms);
        for (var i = 0; i < 40; i++)
        {
            log.Note("d", "keeb", DeviceEventKind.WriteFail, code: i);
            log.Snapshot("d", i == 0 ? "first incident" : "later");
            ms += 30_000;
        }
        var text = IncidentText(log);
        Assert.Contains("first incident", text);
        Assert.InRange(Headers(text), 1, 8);
    }

    [Fact]
    public void Backoff_resets_after_a_quiet_hour()
    {
        long ms = 0;
        var log = NewLog(() => ms);
        log.Note("d", "keeb", DeviceEventKind.WriteFail);
        Assert.True(log.Snapshot("d", "a"));
        ms += 31_000;
        Assert.False(log.Snapshot("d", "too soon, the gap doubled"));
        ms += 30_000;
        Assert.True(log.Snapshot("d", "b"));
        ms += 61_000;
        Assert.False(log.Snapshot("d", "c"));
        ms += (long)DeviceIncidentLog.MaxSnapshotGap.TotalMilliseconds;
        Assert.True(log.Snapshot("d", "d"));
        ms += 31_000;
        Assert.False(log.Snapshot("d", "e"));
        ms += 30_000;
        Assert.True(log.Snapshot("d", "f"));
    }

    [Fact]
    public void Releasing_control_forgets_the_key_without_a_drop_snapshot()
    {
        var log = NewLog(() => 5000);
        var health = new LightingWriteHealth { Incidents = log };
        health.NoteFailure("openrgb-s-1", "openrgb");
        health.Forget("openrgb-s-1", deviceLeft: false);
        Assert.DoesNotContain("device left", IncidentText(log));
        Assert.False(log.Snapshot("openrgb-s-1", "x"));
    }

    [Fact]
    public void Control_release_in_the_poller_does_not_record_a_device_left()
    {
        var log = NewLog(() => 5000);
        var health = new LightingWriteHealth { Incidents = log };
        health.NoteFailure("openrgb-s-off", "openrgb");
        var frames = new[] { new Nexus.Service.Lighting.Engine.DeviceFrame(0, "openrgb-s-off", 4, physicalIndex: 5) };
        OpenRgbWriteHealthPoller.ControlledFrames(frames, new HashSet<string> { "openrgb-s-off" }, new[] { "openrgb-s-off" }, health);
        Assert.DoesNotContain("device left", IncidentText(log));
    }

    [Fact]
    public void A_mixed_window_records_both_counts()
    {
        var log = NewLog(() => 5000);
        var health = new LightingWriteHealth { Incidents = log };
        health.Report("k", failures: 3, successes: 7, handlerId: "keeb");
        log.Snapshot("k", "probe");
        var text = IncidentText(log);
        Assert.Contains("WriteFail n=3", text);
        Assert.Contains("WriteOk n=7", text);
    }

    [Fact]
    public void Rotated_log_count_is_capped_even_when_age_and_bytes_allow_more()
    {
        for (var i = 0; i < 12; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"nexus-service-20260101-0000{i:00}.log"), "x");
        }
        ServiceLog.PruneRotated(_root, DateTime.Now, TimeSpan.FromDays(7), long.MaxValue, minKeep: 3, maxFiles: 5);
        var left = Directory.GetFiles(_root, "nexus-service-*.log").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(5, left.Count);
        Assert.Equal("nexus-service-20260101-000011.log", left[^1]);
    }
}
