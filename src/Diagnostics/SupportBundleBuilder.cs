using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;

namespace Nexus.Service.Diagnostics;

/// <summary>What a bug report needs in one ZIP: every log file, the OTA install logs, the OpenRGB daemon's own logs and config, redacted settings, live state, and the diagnostics JSON the health page shows.</summary>
public static class SupportBundleBuilder
{
    /// <summary>Append-only logs (helper, gpu, tray) never rotate; only their tail ships.</summary>
    internal const long MaxLogBytes = 5 * 1024 * 1024;

    private const string StartupSnapshotStart = "===== Nexus startup snapshot =====";
    private const string StartupSnapshotEnd = "===== end snapshot =====";

    public sealed class Sources
    {
        public string LogsDirectory { get; init; } = "";
        /// <summary>Per-user fallback log dirs (user-session writers land there when the shared dir is not writable for them), keyed by user label.</summary>
        public IReadOnlyList<(string Label, string Directory)> UserLogDirectories { get; init; } = Array.Empty<(string, string)>();
        /// <summary>Per-profile temp dirs searched for a hand-run installer's Setup Log, keyed by user label.</summary>
        public IReadOnlyList<(string Label, string Directory)> InstallerLogDirectories { get; init; } = Array.Empty<(string, string)>();
        /// <summary>Machine-wide temp dirs (not under a user profile) searched for the same Setup Log.</summary>
        public IReadOnlyList<string> InstallerSystemDirectories { get; init; } = Array.Empty<string>();
        /// <summary>Per-profile Nexus\updater dirs, where the update splash writes splash.log.</summary>
        public IReadOnlyList<(string Label, string Directory)> SplashLogDirectories { get; init; } = Array.Empty<(string, string)>();
        /// <summary>macOS launchd stdout and stderr files.</summary>
        public IReadOnlyList<string> LaunchdLogPaths { get; init; } = Array.Empty<string>();
        /// <summary>Linux journal text for the Nexus unit, collected by the caller; counted against the log budget.</summary>
        public string JournalText { get; init; } = "";
        /// <summary>The OTA staging dir: a failed silent install leaves its only trace in the Inno log there.</summary>
        public string UpdatesDirectory { get; init; } = "";
        public string OpenRgbConfigDirectory { get; init; } = "";
        public NexusSettings? Settings { get; init; }
        public SupportInfo Info { get; init; } = new();
        public string StartupSnapshot { get; init; } = "";
        public DiagnosticsHealthResponse? Health { get; init; }
        public IncidentsResponse? Incidents { get; init; }
        public SmartSnapshot? Smart { get; init; }
        public MemoryHealthResponse? Memory { get; init; }
        public GpuHealthResponse? Gpu { get; init; }
        public CoolingStallSnapshot? Cooling { get; init; }
        /// <summary>Every provider's live fan channels: duty, mode and RPM as each provider reports them (the chain's echoed duty for
        /// Lian Li wireless), which cooling.json (target duty only) does not carry. Settings-derived fields (Locked, Controlled,
        /// Role, Offset, renames) stay at their defaults here; settings.json holds them.</summary>
        public List<FanChannel>? Fans { get; init; }
        public SystemDiagnosticsResponse? System { get; init; }
        public UsbTopologyReport? UsbTopology { get; init; }
        /// <summary>OS account names whose home paths are rewritten to a placeholder in every text file.</summary>
        public IReadOnlyList<string> UserNames { get; init; } = Array.Empty<string>();
        public string MachineName { get; init; } = "";
        /// <summary>Further host names the logs carry for this PC, such as the DNS name behind a 15-character NetBIOS name.</summary>
        public IReadOnlyList<string> OtherHostNames { get; init; } = Array.Empty<string>();
        /// <summary>Logon names that logs print as bare words.</summary>
        public IReadOnlyList<string> AccountNames { get; init; } = Array.Empty<string>();
        /// <summary>Total uncompressed log bytes shipped, newest files first.</summary>
        public long LogBudgetBytes { get; init; } = DefaultLogBudgetBytes;
    }

    internal const long DefaultLogBudgetBytes = 30L * 1024 * 1024;

    /// <summary>A log file that may ship; its cost against the budget is its length capped at <see cref="MaxLogBytes"/>.</summary>
    internal sealed record LogCandidate(string EntryName, string Path, DateTime LastWriteUtc, long Length, bool MustBeUnder, string? ExpectedPrefix)
    {
        public long Cost => Math.Min(Length, MaxLogBytes);
    }

    private static bool IsPinned(LogCandidate c) =>
        c.EntryName is "logs/nexus-service.log" or "logs/" + DeviceIncidentLog.FileName or "logs/device-incidents.1.log";

    /// <summary>Newest first, taking each file that still fits in what is left of the budget.</summary>
    internal static List<LogCandidate> SelectWithinBudget(IEnumerable<LogCandidate> candidates, long budget)
    {
        var chosen = new List<LogCandidate>();
        // The current service log and the device incident logs always ship, then the rest fill what is left, newest first.
        var ordered = candidates.OrderByDescending(IsPinned).ThenByDescending(c => c.LastWriteUtc).ThenBy(c => c.EntryName, StringComparer.Ordinal);
        foreach (var c in ordered)
        {
            if (IsPinned(c))
            {
                budget -= c.Cost;
                chosen.Add(c);
            }
            else if (c.Cost <= budget)
            {
                budget -= c.Cost;
                chosen.Add(c);
            }
        }
        return chosen;
    }

    public static byte[] Build(Sources src)
    {
        using var ms = new MemoryStream();
        var r = new BundleRedactor(src.UserNames, src.MachineName, src.OtherHostNames, src.AccountNames);
        var logs = new List<LogCandidate>();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJson(zip, r, "support-info.json", src.Info, AppJsonContext.Default.SupportInfo);
            if (src.Settings is not null)
            {
                WriteJson(zip, r, "settings.json", Redact(src.Settings), PersistenceJsonContext.Default.NexusSettings);
            }
            WriteText(zip, r, "system-profile.txt", src.StartupSnapshot);
            AddLogs(logs, "logs/", src.LogsDirectory, "*.log");
            foreach (var pattern in SupportBundleExtras.ExtraLogPatterns)
            {
                AddLogs(logs, "logs/", src.LogsDirectory, pattern, accept: SupportBundleExtras.IsExtraLogCandidate);
            }
            AddPerUserLogs(logs, "logs/user-", src.UserLogDirectories, Path.Combine("AppData", "Local", "Nexus", "logs"), new[] { "*.log" });
            foreach (var pattern in SupportBundleExtras.ExtraLogPatterns)
            {
                AddPerUserLogs(logs, "logs/user-", src.UserLogDirectories, Path.Combine("AppData", "Local", "Nexus", "logs"), new[] { pattern }, SupportBundleExtras.IsExtraLogCandidate);
            }
            AddPerUserLogs(logs, "splash/user-", src.SplashLogDirectories, Path.Combine("AppData", "Local", "Nexus", "updater"), new[] { "splash.log" });
            var installerLogs = new List<LogCandidate>();
            AddPerUserLogs(installerLogs, "installer/user-", src.InstallerLogDirectories, Path.Combine("AppData", "Local", "Temp"), new[] { SupportBundleExtras.InstallerLogPattern }, SupportBundleExtras.IsNexusInstallerLog);
            var systemIndex = 0;
            foreach (var dir in src.InstallerSystemDirectories)
            {
                AddLogs(installerLogs, $"installer/system-{++systemIndex}/", dir, SupportBundleExtras.InstallerLogPattern, accept: SupportBundleExtras.IsNexusInstallerLog);
            }
            logs.AddRange(installerLogs.OrderByDescending(c => c.LastWriteUtc).ThenBy(c => c.EntryName, StringComparer.Ordinal).Take(SupportBundleExtras.InstallerLogKeep));
            foreach (var path in src.LaunchdLogPaths)
            {
                AddLogs(logs, "launchd/", Path.GetDirectoryName(path) ?? "", Path.GetFileName(path));
            }
            AddLogs(logs, "updates/", src.UpdatesDirectory, Update.UpdateInstaller.InstallLogGlob);
            AddFile(zip, r, "updates/" + Update.StagedInstallMarkerStore.MarkerFileName, Path.Combine(src.UpdatesDirectory, Update.StagedInstallMarkerStore.MarkerFileName));
            AddLogs(logs, "openrgb/logs/", Path.Combine(src.OpenRgbConfigDirectory, "logs"), "OpenRGB_*.log");
            AddFile(zip, r, "openrgb/OpenRGB.json", Path.Combine(src.OpenRgbConfigDirectory, "OpenRGB.json"));
            AddFile(zip, r, "openrgb/detector-map.json", Path.Combine(src.OpenRgbConfigDirectory, "detector-map.json"));
            if (src.Health is not null) WriteJson(zip, r, "diagnostics/health.json", src.Health, AppJsonContext.Default.DiagnosticsHealthResponse);
            if (src.Incidents is not null) WriteJson(zip, r, "diagnostics/incidents.json", src.Incidents, AppJsonContext.Default.IncidentsResponse);
            if (src.Smart is not null) WriteJson(zip, r, "diagnostics/smart.json", src.Smart, AppJsonContext.Default.SmartSnapshot);
            if (src.Memory is not null) WriteJson(zip, r, "diagnostics/memory.json", src.Memory, AppJsonContext.Default.MemoryHealthResponse);
            if (src.Gpu is not null) WriteJson(zip, r, "diagnostics/gpu.json", src.Gpu, AppJsonContext.Default.GpuHealthResponse);
            if (src.Cooling is not null) WriteJson(zip, r, "diagnostics/cooling.json", src.Cooling, AppJsonContext.Default.CoolingStallSnapshot);
            if (src.Fans is not null) WriteJson(zip, r, "diagnostics/fans.json", src.Fans, AppJsonContext.Default.ListFanChannel);
            if (src.System is not null) WriteJson(zip, r, "diagnostics/system.json", src.System, AppJsonContext.Default.SystemDiagnosticsResponse);
            if (src.UsbTopology is not null) WriteJson(zip, r, "diagnostics/usb-topology.json", src.UsbTopology, AppJsonContext.Default.UsbTopologyReport);
            var budget = src.LogBudgetBytes;
            if (src.JournalText.Length > 0)
            {
                WriteText(zip, r, "logs/journal-" + Lifecycle.LinuxStartupProvider.UnitName + ".txt", src.JournalText);
                budget = Math.Max(0, budget - src.JournalText.Length);
            }
            foreach (var c in SelectWithinBudget(logs, budget))
            {
                AddFile(zip, r, c.EntryName, c.Path, c.MustBeUnder, c.ExpectedPrefix);
            }
        }
        return ms.ToArray();
    }

    /// <summary>A copy with every credential blanked; the source-gen round-trip keeps it AOT-safe and shaped like the file on disk.</summary>
    internal static NexusSettings Redact(NexusSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        var copy = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings) ?? new NexusSettings();
        const string blank = "<redacted>";
        if (copy.Auth is not null)
        {
            copy.Auth.Token = blank;
            foreach (var session in copy.Auth.PanelPhoneSessions)
            {
                session.Hash = blank;
                session.RelayKey = blank;
                // A device push token is a credential to alert that phone.
                session.PushTarget = null;
                // The name is whatever the user called the phone, often their own.
                session.Name = blank;
            }
            var accountIndex = 0;
            var accountAliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var account in copy.Auth.CloudAccounts)
            {
                account.RefreshToken = blank;
                account.Email = blank;
                account.Username = blank;
                account.AvatarLarge = "";
                account.AvatarSmall = "";
                // A stable alias keeps "which account is active" readable without naming the cloud id.
                var alias = "account-" + ++accountIndex;
                accountAliases[account.AccountId] = alias;
                account.AccountId = alias;
            }
            if (copy.Auth.ActiveCloudAccountId is { } active)
            {
                copy.Auth.ActiveCloudAccountId = accountAliases.GetValueOrDefault(active, blank);
            }
        }
        copy.HostDisplayName = copy.HostDisplayName.Length == 0 ? "" : blank;
        foreach (var location in copy.Weather.Locations)
        {
            // Saved coordinates are as precise as a street address; the label is kept only when it is a plain place name.
            location.Lat = 0;
            location.Lon = 0;
            if (location.Label.Any(char.IsDigit))
            {
                location.Label = blank;
            }
        }
        // The install id is what the mapping registry keys provenance and caps on.
        copy.Telemetry.InstallId = blank;
        copy.AiIntegration.Token = blank;
        copy.Obs.Password = blank;
        copy.Steam.ApiKey = blank;
        copy.Discord.ClientSecret = blank;
        copy.HomeAssistant.Token = blank;
        foreach (var light in copy.SmartLights.Devices)
        {
            light.Token = blank;
            light.StableKey = blank;
        }
        return copy;
    }

    /// <summary>The per-user fallback log dir of every profile, deduplicated, labeled by profile folder name.</summary>
    internal static List<(string Label, string Directory)> UserLogDirectoriesFor(IEnumerable<string> profiles) =>
        ProfileSubdirectoriesFor(profiles, "AppData", "Local", "Nexus", "logs");

    /// <summary>The same relative folder under every profile, deduplicated, labeled by profile folder name.</summary>
    internal static List<(string Label, string Directory)> ProfileSubdirectoriesFor(IEnumerable<string> profiles, params string[] relative)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, string)>();
        foreach (var profile in profiles)
        {
            var trimmed = profile.TrimEnd('\\', '/');
            if (trimmed.Length == 0 || !seen.Add(trimmed))
            {
                continue;
            }
            var label = Path.GetFileName(trimmed);
            result.Add((label.Length == 0 ? "user" : label, Path.Combine(new[] { trimmed }.Concat(relative).ToArray())));
        }
        return result;
    }

    /// <summary>Per-user dirs sit under a profile the user controls while SYSTEM reads them: one user's folder must never break the export for everyone, so any failure skips that user. Junctioned dirs and linked files are refused, and each opened file must still resolve under the expected dir.</summary>
    private static void AddPerUserLogs(List<LogCandidate> logs, string prefix, IReadOnlyList<(string Label, string Directory)> dirs, string tailUnderProfile, string[] patterns, Func<string, bool>? accept = null)
    {
        var levels = tailUnderProfile.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
        var userIndex = 0;
        foreach (var (_, dir) in dirs)
        {
            try
            {
                if (Lifecycle.UserDataSafety.ChainHasReparsePoint(dir, levels))
                {
                    continue;
                }
                ++userIndex;
                string? profile = dir;
                for (var i = 0; i < levels; i++)
                {
                    profile = Path.GetDirectoryName(profile);
                }
                var expected = profile is null ? null : Lifecycle.UserDataSafety.ExpectedFinalPrefix(profile, tailUnderProfile);
                foreach (var pattern in patterns)
                {
                    AddLogs(logs, $"{prefix}{userIndex}/", dir, pattern, mustBeUnder: true, expectedPrefix: expected, accept: accept);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private static void AddLogs(List<LogCandidate> logs, string prefix, string dir, string pattern, bool mustBeUnder = false, string? expectedPrefix = null, Func<string, bool>? accept = null)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(dir, pattern))
        {
            if (mustBeUnder && Lifecycle.UserDataSafety.IsReparsePoint(path))
            {
                continue;
            }
            if (accept is not null && !accept(path))
            {
                continue;
            }
            try
            {
                var info = new FileInfo(path);
                logs.Add(new LogCandidate(prefix + info.Name, path, info.LastWriteTimeUtc, info.Length, mustBeUnder, expectedPrefix));
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Copies a file that the service may still be writing; a file past <see cref="MaxLogBytes"/> ships as its tail behind one header line.</summary>
    private static void AddFile(ZipArchive zip, BundleRedactor r, string name, string path, bool mustBeUnder = false, string? expectedPrefix = null)
    {
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (mustBeUnder && !Lifecycle.UserDataSafety.HandleIsUnder(stream.SafeFileHandle, expectedPrefix))
            {
                return;
            }
            var header = "";
            if (stream.Length > MaxLogBytes)
            {
                header = $"[support-bundle] last {MaxLogBytes / (1024 * 1024)} MB of {stream.Length} bytes\n";
                stream.Seek(-MaxLogBytes, SeekOrigin.End);
            }
            using var reader = new StreamReader(stream);
            var text = r.Apply(header + reader.ReadToEnd());
            var entry = zip.CreateEntry(name);
            using var output = entry.Open();
            using var writer = new StreamWriter(output);
            writer.Write(text);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // The default encoder writes the placeholders' angle brackets as \u003C and \u003E; both are valid raw inside a JSON string.
    private static void WriteJson<T>(ZipArchive zip, BundleRedactor r, string name, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        WriteText(zip, r, name, JsonSerializer.Serialize(value, typeInfo).Replace("\\u003C", "<", StringComparison.Ordinal).Replace("\\u003E", ">", StringComparison.Ordinal));

    private static void WriteText(ZipArchive zip, BundleRedactor r, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(r.Apply(content));
    }

    /// <summary>The startup-diagnostics block of the current run's log, so the hardware profile is readable without grepping the log.</summary>
    internal static string ReadStartupSnapshot()
    {
        var path = ServiceLog.LogFilePath ?? Path.Combine(ServiceLog.LogsDirectory, "nexus-service.log");
        if (!File.Exists(path))
        {
            return "";
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            var capturing = false;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!capturing && line.Contains(StartupSnapshotStart, StringComparison.Ordinal))
                {
                    capturing = true;
                }
                if (capturing)
                {
                    lines.Add(line);
                }
                if (capturing && line.Contains(StartupSnapshotEnd, StringComparison.Ordinal))
                {
                    break;
                }
            }
            return string.Join(Environment.NewLine, lines);
        }
        catch (IOException)
        {
            return "";
        }
    }
}

/// <summary>Live state captured at export time, next to the logs it explains.</summary>
public sealed class SupportInfo
{
    public string Version { get; set; } = "";
    public bool DevTools { get; set; }
    public string Os { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string ExportedAtUtc { get; set; } = "";
    public double ServiceUptimeSeconds { get; set; }
    public List<string> ConflictsRunning { get; set; } = new();
    public bool ConflictScanReady { get; set; }

    /// <summary>Windows Dynamic Lighting state: available, enabled and LampArray count.</summary>
    public string DynamicLighting { get; set; } = "";
    public bool LightingBridgeActive { get; set; }
    public bool LightingRescanning { get; set; }
    public List<string> LightingDevices { get; set; } = new();
    public bool OpenRgbDaemonRunning { get; set; }
    public double OpenRgbDaemonUptimeSeconds { get; set; }

    /// <summary>Every NEXUS_* environment variable the service sees; values of TOKEN, KEY, SECRET and PASSWORD names are redacted.</summary>
    public Dictionary<string, string> NexusEnvironment { get; set; } = new();

    /// <summary>Override files the service reads that currently exist, keyed by path relative to the Nexus data dir.</summary>
    public Dictionary<string, string> OverrideFiles { get; set; } = new();

    /// <summary>Allowlisted vendor RGB apps by catalog id: install state, version when known, running.</summary>
    public Dictionary<string, string> VendorSoftware { get; set; } = new();
}
