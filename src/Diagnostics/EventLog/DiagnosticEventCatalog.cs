using System.Collections.Generic;

namespace Nexus.Service.Diagnostics.EventLog;

/// <summary>Dispatch tag for EventXmlParser; every catalog entry maps to exactly one strategy.</summary>
public enum DiagnosticParseStrategy
{
    Whea,
    Bugcheck,
    DirtyShutdown,
    Disk,
    Tdr,
    GpuDriver,
    AppCrash,
    LiveKernel,
    MemDiag,
}

/// <summary>
/// One watched Windows Event Log source. <see cref="XPath"/> is the inner
/// predicate only (e.g. "Provider[@Name='...']"), not a complete query: the
/// backfill query ANDs it with a time bound, and the live subscription ORs
/// every entry sharing a channel into one combined query.
/// </summary>
public sealed record DiagnosticEventCatalogEntry
{
    /// <summary>Contract `source` value (whea, bugcheck, dirtyShutdown, ...).</summary>
    public string Source { get; init; } = "";
    /// <summary>Event Viewer channel name (System, Application).</summary>
    public string Channel { get; init; } = "";
    /// <summary>Inner XPath predicate selecting this source's events within its channel.</summary>
    public string XPath { get; init; } = "";
    public DiagnosticParseStrategy ParseStrategy { get; init; }
}

/// <summary>The watched-source table for the diagnostics event monitor (EventLogMonitor) and parser (EventXmlParser).</summary>
public static class DiagnosticEventCatalog
{
    public const string ChannelSystem = "System";
    public const string ChannelApplication = "Application";

    public const string SourceWhea = "whea";
    public const string SourceBugcheck = "bugcheck";
    public const string SourceDirtyShutdown = "dirtyShutdown";
    public const string SourceDisk = "disk";
    public const string SourceTdr = "tdr";
    public const string SourceGpuDriver = "gpuDriver";
    public const string SourceAppCrash = "appCrash";
    /// <summary>Nexus itself faulting in nvml.dll: an NVIDIA driver reload pulled the library out from under it, and the service restarts.</summary>
    public const string SourceDriverRestart = "driverRestart";
    public const string SourceLiveKernel = "liveKernel";
    public const string SourceMemDiag = "memDiag";

    public static readonly IReadOnlyList<DiagnosticEventCatalogEntry> Entries = new List<DiagnosticEventCatalogEntry>
    {
        new()
        {
            Source = SourceWhea, Channel = ChannelSystem,
            XPath = "Provider[@Name='Microsoft-Windows-WHEA-Logger']",
            ParseStrategy = DiagnosticParseStrategy.Whea,
        },
        new()
        {
            Source = SourceBugcheck, Channel = ChannelSystem,
            XPath = "Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001",
            ParseStrategy = DiagnosticParseStrategy.Bugcheck,
        },
        new()
        {
            Source = SourceDirtyShutdown, Channel = ChannelSystem,
            XPath = "Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41",
            ParseStrategy = DiagnosticParseStrategy.DirtyShutdown,
        },
        new()
        {
            Source = SourceDisk, Channel = ChannelSystem,
            XPath = "(Provider[@Name='disk'] and (EventID=7 or EventID=51 or EventID=153)) or "
                + "(Provider[@Name='storahci' or @Name='stornvme'] and EventID=129)",
            ParseStrategy = DiagnosticParseStrategy.Disk,
        },
        new()
        {
            Source = SourceTdr, Channel = ChannelSystem,
            XPath = "Provider[@Name='Display'] and (EventID=4101 or Level<=3)",
            ParseStrategy = DiagnosticParseStrategy.Tdr,
        },
        new()
        {
            Source = SourceGpuDriver, Channel = ChannelSystem,
            XPath = "(Provider[@Name='nvlddmkm' or @Name='amdkmdag']) and Level<=3",
            ParseStrategy = DiagnosticParseStrategy.GpuDriver,
        },
        new()
        {
            Source = SourceMemDiag, Channel = ChannelSystem,
            XPath = "Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and (EventID=1201 or EventID=1202)",
            ParseStrategy = DiagnosticParseStrategy.MemDiag,
        },
        new()
        {
            Source = SourceAppCrash, Channel = ChannelApplication,
            XPath = "Provider[@Name='Application Error'] and EventID=1000",
            ParseStrategy = DiagnosticParseStrategy.AppCrash,
        },
        new()
        {
            Source = SourceLiveKernel, Channel = ChannelApplication,
            XPath = "Provider[@Name='Windows Error Reporting'] and EventID=1001",
            ParseStrategy = DiagnosticParseStrategy.LiveKernel,
        },
    };

    /// <summary>
    /// Finds the catalog entry whose channel/provider/id match a rendered event's
    /// System block. Null if the event is not one of the watched sources. A
    /// second, in-strategy filter (see EventXmlParser's LiveKernel and Tdr
    /// handling) may still discard an entry-matching event based on its
    /// EventData or Level.
    /// </summary>
    public static DiagnosticEventCatalogEntry? Match(string channel, string provider, int eventId)
    {
        foreach (var entry in Entries)
        {
            if (entry.Channel != channel)
            {
                continue;
            }
            if (Matches(entry.ParseStrategy, provider, eventId))
            {
                return entry;
            }
        }
        return null;
    }

    private static bool Matches(DiagnosticParseStrategy strategy, string provider, int eventId) => strategy switch
    {
        DiagnosticParseStrategy.Whea => provider == "Microsoft-Windows-WHEA-Logger",
        DiagnosticParseStrategy.Bugcheck => provider == "Microsoft-Windows-WER-SystemErrorReporting" && eventId == 1001,
        DiagnosticParseStrategy.DirtyShutdown => provider == "Microsoft-Windows-Kernel-Power" && eventId == 41,
        DiagnosticParseStrategy.Disk => (provider == "disk" && eventId is 7 or 51 or 153)
            || ((provider == "storahci" || provider == "stornvme") && eventId == 129),
        DiagnosticParseStrategy.Tdr => provider == "Display",
        DiagnosticParseStrategy.GpuDriver => provider == "nvlddmkm" || provider == "amdkmdag",
        DiagnosticParseStrategy.AppCrash => provider == "Application Error" && eventId == 1000,
        DiagnosticParseStrategy.LiveKernel => provider == "Windows Error Reporting" && eventId == 1001,
        DiagnosticParseStrategy.MemDiag => provider == "Microsoft-Windows-MemoryDiagnostics-Results" && eventId is 1201 or 1202,
        _ => false,
    };
}
