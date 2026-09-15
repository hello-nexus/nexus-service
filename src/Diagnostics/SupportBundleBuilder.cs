using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.Storage;
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
        public SystemDiagnosticsResponse? System { get; init; }
    }

    public static byte[] Build(Sources src)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJson(zip, "support-info.json", src.Info, AppJsonContext.Default.SupportInfo);
            if (src.Settings is not null)
            {
                WriteJson(zip, "settings.json", Redact(src.Settings), PersistenceJsonContext.Default.NexusSettings);
            }
            WriteText(zip, "system-profile.txt", src.StartupSnapshot);
            AddLogs(zip, "logs/", src.LogsDirectory, "*.log");
            AddLogs(zip, "updates/", src.UpdatesDirectory, Update.UpdateInstaller.InstallLogGlob);
            AddFile(zip, "updates/" + Update.StagedInstallMarkerStore.MarkerFileName, Path.Combine(src.UpdatesDirectory, Update.StagedInstallMarkerStore.MarkerFileName));
            AddLogs(zip, "openrgb/logs/", Path.Combine(src.OpenRgbConfigDirectory, "logs"), "OpenRGB_*.log");
            AddFile(zip, "openrgb/OpenRGB.json", Path.Combine(src.OpenRgbConfigDirectory, "OpenRGB.json"));
            AddFile(zip, "openrgb/detector-map.json", Path.Combine(src.OpenRgbConfigDirectory, "detector-map.json"));
            if (src.Health is not null) WriteJson(zip, "diagnostics/health.json", src.Health, AppJsonContext.Default.DiagnosticsHealthResponse);
            if (src.Incidents is not null) WriteJson(zip, "diagnostics/incidents.json", src.Incidents, AppJsonContext.Default.IncidentsResponse);
            if (src.Smart is not null) WriteJson(zip, "diagnostics/smart.json", src.Smart, AppJsonContext.Default.SmartSnapshot);
            if (src.Memory is not null) WriteJson(zip, "diagnostics/memory.json", src.Memory, AppJsonContext.Default.MemoryHealthResponse);
            if (src.Gpu is not null) WriteJson(zip, "diagnostics/gpu.json", src.Gpu, AppJsonContext.Default.GpuHealthResponse);
            if (src.Cooling is not null) WriteJson(zip, "diagnostics/cooling.json", src.Cooling, AppJsonContext.Default.CoolingStallSnapshot);
            if (src.System is not null) WriteJson(zip, "diagnostics/system.json", src.System, AppJsonContext.Default.SystemDiagnosticsResponse);
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

    private static void AddLogs(ZipArchive zip, string prefix, string dir, string pattern)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(dir, pattern).OrderBy(p => p, StringComparer.Ordinal))
        {
            AddFile(zip, prefix + Path.GetFileName(path), path);
        }
    }

    /// <summary>Copies a file that the service may still be writing; a file past <see cref="MaxLogBytes"/> ships as its tail behind one header line.</summary>
    private static void AddFile(ZipArchive zip, string name, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = zip.CreateEntry(name);
            using var output = entry.Open();
            if (stream.Length > MaxLogBytes)
            {
                using var writer = new StreamWriter(output, leaveOpen: true);
                writer.WriteLine($"[support-bundle] last {MaxLogBytes / (1024 * 1024)} MB of {stream.Length} bytes");
                writer.Flush();
                stream.Seek(-MaxLogBytes, SeekOrigin.End);
            }
            stream.CopyTo(output);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteJson<T>(ZipArchive zip, string name, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, typeInfo);
    }

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
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
}
