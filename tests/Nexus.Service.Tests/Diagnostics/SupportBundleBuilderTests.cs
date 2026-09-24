using System.IO.Compression;
using System.Text.Json;
using Nexus.Service.Diagnostics;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Diagnostics;

public class SupportBundleBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-test-support-" + Guid.NewGuid().ToString("N")[..8]);

    public SupportBundleBuilderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        Directory.CreateDirectory(Path.Combine(_root, "openrgb", "logs"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Dictionary<string, byte[]> Entries(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var map = new Dictionary<string, byte[]>();
        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            map[e.FullName] = ms.ToArray();
        }
        return map;
    }

    [Fact]
    public void Bundles_every_log_the_install_logs_the_daemon_logs_and_config_and_caps_oversized_logs_to_a_tail()
    {
        var logs = Path.Combine(_root, "logs");
        File.WriteAllText(Path.Combine(logs, "nexus-service.log"), "live\n");
        File.WriteAllText(Path.Combine(logs, "nexus-service-20260913-120000.log"), "rotated\n");
        var big = new byte[SupportBundleBuilder.MaxLogBytes + 4096];
        Array.Fill(big, (byte)'x');
        File.WriteAllBytes(Path.Combine(logs, "nexus-helper.log"), big);
        File.WriteAllText(Path.Combine(logs, "notes.txt"), "not a log");
        var orgb = Path.Combine(_root, "openrgb");
        File.WriteAllText(Path.Combine(orgb, "logs", "OpenRGB_20260913_120005.log"), "daemon\n");
        File.WriteAllText(Path.Combine(orgb, "OpenRGB.json"), "{}");
        File.WriteAllText(Path.Combine(orgb, "detector-map.json"), "{}");
        var updates = Path.Combine(_root, "updates");
        Directory.CreateDirectory(updates);
        File.WriteAllText(Path.Combine(updates, "ota-install-3.0.14-1757800000.log"), "inno\n");
        File.WriteAllText(Path.Combine(updates, "pending-install.json"), "{}");
        File.WriteAllText(Path.Combine(updates, "run-ota-3.0.14.cmd"), "@echo off");
        File.WriteAllText(Path.Combine(updates, "Nexus-Setup.exe"), "MZ");

        var entries = Entries(SupportBundleBuilder.Build(new SupportBundleBuilder.Sources
        {
            LogsDirectory = logs,
            UpdatesDirectory = updates,
            OpenRgbConfigDirectory = orgb,
            Info = new SupportInfo { Version = "v1", MachineName = "box" },
            StartupSnapshot = "snap",
        }));

        Assert.Contains("logs/nexus-service.log", entries.Keys);
        Assert.Contains("logs/nexus-service-20260913-120000.log", entries.Keys);
        Assert.Contains("logs/nexus-helper.log", entries.Keys);
        Assert.DoesNotContain("logs/notes.txt", entries.Keys);
        Assert.Contains("openrgb/logs/OpenRGB_20260913_120005.log", entries.Keys);
        Assert.Contains("openrgb/OpenRGB.json", entries.Keys);
        Assert.Contains("openrgb/detector-map.json", entries.Keys);
        Assert.Contains("updates/ota-install-3.0.14-1757800000.log", entries.Keys);
        Assert.Contains("updates/pending-install.json", entries.Keys);
        Assert.DoesNotContain("updates/run-ota-3.0.14.cmd", entries.Keys);
        Assert.DoesNotContain("updates/Nexus-Setup.exe", entries.Keys);
        Assert.Contains("support-info.json", entries.Keys);
        Assert.Equal("snap", System.Text.Encoding.UTF8.GetString(entries["system-profile.txt"]));

        var helper = entries["logs/nexus-helper.log"];
        Assert.True(helper.Length < big.Length, "oversized log was not capped");
        Assert.StartsWith("[support-bundle] last 5 MB of", System.Text.Encoding.UTF8.GetString(helper[..40]));

        var info = JsonSerializer.Deserialize(entries["support-info.json"], AppJsonContext.Default.SupportInfo)!;
        Assert.Equal("box", info.MachineName);
    }

    [Fact]
    public void Settings_ship_with_every_credential_blanked_and_nothing_else_touched()
    {
        var settings = new NexusSettings();
        settings.Auth!.Token = "session-bearer";
        settings.Auth.PanelPhoneSessions.Add(new PanelPhoneSessionToken { Hash = "s3cr3t-phone", RelayKey = "s3cr3t-relay" });
        settings.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct", RefreshToken = "s3cr3t-refresh" });
        settings.Auth.RelayEnabled = true;
        settings.AiIntegration.Token = "s3cr3t-ai";
        settings.Obs.Password = "s3cr3t-obs";
        settings.Steam.ApiKey = "s3cr3t-steam";
        settings.Discord.ClientSecret = "s3cr3t-discord";
        settings.HomeAssistant.Token = "s3cr3t-ha";
        settings.SmartLights.Devices.Add(new SmartLightConfig { Token = "s3cr3t-hue", StableKey = "s3cr3t-key" });
        settings.HostDisplayName = "Moose";
        settings.Telemetry.InstallId = "s3cr3t-install";

        var entries = Entries(SupportBundleBuilder.Build(new SupportBundleBuilder.Sources
        {
            LogsDirectory = Path.Combine(_root, "logs"),
            OpenRgbConfigDirectory = Path.Combine(_root, "openrgb"),
            Settings = settings,
        }));

        var text = System.Text.Encoding.UTF8.GetString(entries["settings.json"]);
        Assert.DoesNotContain("session-bearer", text);
        Assert.DoesNotContain("s3cr3t", text);
        var shipped = JsonSerializer.Deserialize(text, PersistenceJsonContext.Default.NexusSettings)!;
        Assert.Equal("Moose", shipped.HostDisplayName);
        Assert.True(shipped.Auth!.RelayEnabled);
        Assert.Equal("acct", shipped.Auth.CloudAccounts[0].AccountId);
        Assert.Single(shipped.Auth.PanelPhoneSessions);
        Assert.Equal("session-bearer", settings.Auth.Token);
    }
}
