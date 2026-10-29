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
        /// <summary>Total uncompressed log bytes shipped, newest files first.</summary>
        public long LogBudgetBytes { get; init; } = DefaultLogBudgetBytes;
    }

    internal const long DefaultLogBudgetBytes = 30L * 1024 * 1024;

    /// <summary>A log file that may ship; its cost against the budget is its length capped at <see cref="MaxLogBytes"/>.</summary>
    internal sealed record LogCandidate(string EntryName, string Path, DateTime LastWriteUtc, long Length, bool MustBeUnder, string? ExpectedPrefix)
    {
        public long Cost => Math.Min(Length, MaxLogBytes);
    }

    /// <summary>Newest first, taking each file that still fits in what is left of the budget.</summary>
    internal static List<LogCandidate> SelectWithinBudget(IEnumerable<LogCandidate> candidates, long budget)
    {
        var chosen = new List<LogCandidate>();
        foreach (var c in candidates.OrderByDescending(c => c.LastWriteUtc).ThenBy(c => c.EntryName, StringComparer.Ordinal))
        {
            if (c.Cost <= budget)
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
        var r = new BundleRedactor(src.UserNames, src.MachineName);
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
            foreach (var (label, dir) in src.UserLogDirectories)
            {
                // SYSTEM reads user-controlled folders here: one user's folder must never break the export
                // for everyone, so any failure skips that user. Junctioned dirs and linked files are
                // refused, and each opened file must still resolve under the expected dir.
                try
                {
                    if (Lifecycle.UserDataSafety.ChainHasReparsePoint(dir, 4))
                    {
                        continue;
                    }
                    var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dir))));
                    var prefix = profile is null ? null : Lifecycle.UserDataSafety.ExpectedFinalPrefix(profile, Path.Combine("AppData", "Local", "Nexus", "logs"));
                    AddLogs(logs, $"logs/user-{label}/", dir, "*.log", mustBeUnder: true, expectedPrefix: prefix);
                }
                catch (Exception)
                {
                }
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
            foreach (var c in SelectWithinBudget(logs, src.LogBudgetBytes))
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
            }
            foreach (var account in copy.Auth.CloudAccounts)
            {
                account.RefreshToken = blank;
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
    internal static List<(string Label, string Directory)> UserLogDirectoriesFor(IEnumerable<string> profiles)
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
            result.Add((label.Length == 0 ? "user" : label, Path.Combine(trimmed, "AppData", "Local", "Nexus", "logs")));
        }
        return result;
    }

    private static void AddLogs(List<LogCandidate> logs, string prefix, string dir, string pattern, bool mustBeUnder = false, string? expectedPrefix = null)
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

    private static void WriteJson<T>(ZipArchive zip, BundleRedactor r, string name, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        WriteText(zip, r, name, JsonSerializer.Serialize(value, typeInfo));

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
