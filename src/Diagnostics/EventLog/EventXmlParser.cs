using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Nexus.Service.Diagnostics.EventLog;

/// <summary>
/// Converts EvtRenderEventXml output into a DiagnosticIncident. Pure XML
/// parsing (XDocument), no wevtapi dependency, so it builds and runs on any
/// platform. Never throws: malformed or unrecognized input yields null.
/// </summary>
public static class EventXmlParser
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";
    private static readonly IReadOnlyDictionary<string, string> EmptyData = new Dictionary<string, string>();

    public static DiagnosticIncident? Parse(string xml)
    {
        try
        {
            return ParseCore(xml);
        }
        catch
        {
            return null;
        }
    }

    private static DiagnosticIncident? ParseCore(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        var system = root?.Element(Ns + "System");
        if (system is null)
        {
            return null;
        }

        var provider = system.Element(Ns + "Provider")?.Attribute("Name")?.Value;
        if (string.IsNullOrEmpty(provider))
        {
            return null;
        }
        // EventID text is the id regardless of a legacy Qualifiers attribute
        // some providers (e.g. nvlddmkm) still emit on the element.
        if (!int.TryParse(system.Element(Ns + "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
        {
            return null;
        }
        var channel = system.Element(Ns + "Channel")?.Value;
        if (string.IsNullOrEmpty(channel))
        {
            return null;
        }
        if (!long.TryParse(system.Element(Ns + "EventRecordID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var recordId))
        {
            return null;
        }
        var systemTime = system.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
        if (!DateTime.TryParse(systemTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var timeUtc))
        {
            return null;
        }

        var entry = DiagnosticEventCatalog.Match(channel, provider, eventId);
        if (entry is null)
        {
            return null;
        }

        var eventData = root!.Element(Ns + "EventData");
        var extracted = Extract(entry.ParseStrategy, eventId, system, eventData);
        if (extracted is null)
        {
            return null;
        }

        return new DiagnosticIncident
        {
            Id = $"{channel}/{recordId}",
            TimeUtc = timeUtc,
            Source = extracted.Source ?? entry.Source,
            Severity = extracted.Severity,
            Title = extracted.Title,
            Detail = extracted.Detail,
            App = extracted.App,
            Data = extracted.Data,
        };
    }

    private sealed record ExtractedFields(string Severity, string Title, string Detail, DiagnosticAppInfo? App, IReadOnlyDictionary<string, string> Data, string? Source = null);

    private static ExtractedFields? Extract(DiagnosticParseStrategy strategy, int eventId, XElement system, XElement? eventData) => strategy switch
    {
        DiagnosticParseStrategy.Whea => ExtractWhea(eventId, system),
        DiagnosticParseStrategy.Bugcheck => ExtractBugcheck(eventData),
        DiagnosticParseStrategy.DirtyShutdown => ExtractDirtyShutdown(eventData),
        DiagnosticParseStrategy.Disk => ExtractDisk(eventId),
        DiagnosticParseStrategy.Tdr => ExtractTdr(eventId, system),
        DiagnosticParseStrategy.GpuDriver => ExtractGpuDriver(system, eventData),
        DiagnosticParseStrategy.AppCrash => ExtractAppCrash(eventData),
        DiagnosticParseStrategy.LiveKernel => ExtractLiveKernel(eventData),
        DiagnosticParseStrategy.MemDiag => ExtractMemDiag(eventId),
        _ => null,
    };

    private static ExtractedFields ExtractWhea(int eventId, XElement system)
    {
        var level = int.TryParse(system.Element(Ns + "Level")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv) ? lv : 4;
        var severity = level is 1 or 2 ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning;
        var title = eventId switch
        {
            17 or 19 => "Corrected PCIe/MCE hardware error",
            18 or 46 => "Fatal hardware error",
            47 => "Corrected memory error",
            _ => $"Hardware error event {eventId}",
        };
        return new ExtractedFields(severity, title, $"WHEA-Logger event {eventId}.", null, EmptyData);
    }

    // param1 is "<code> (<p1>, <p2>, <p3>, <p4>)"; only the leading code is
    // kept. param2 is the minidump file path; param3 (the report GUID) is not
    // surfaced in Data.
    private static ExtractedFields ExtractBugcheck(XElement? eventData)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        var param1 = GetNamedData(eventData, "param1");
        if (!string.IsNullOrEmpty(param1))
        {
            var spaceIndex = param1.IndexOf(' ');
            var code = spaceIndex > 0 ? param1[..spaceIndex] : param1;
            data["bugcheckCode"] = NormalizeHex(code);
        }
        var dumpPath = GetNamedData(eventData, "param2");
        if (!string.IsNullOrEmpty(dumpPath))
        {
            data["dumpPath"] = dumpPath;
        }
        var display = data.TryGetValue("bugcheckCode", out var c) ? c : "unknown";
        return new ExtractedFields(DiagnosticSeverity.Critical, "System bugcheck",
            $"Windows reported a bugcheck ({display}) on the previous boot.", null, data);
    }

    private static ExtractedFields ExtractDirtyShutdown(XElement? eventData)
    {
        var codeText = GetNamedData(eventData, "BugcheckCode");
        long.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
        if (code != 0)
        {
            var hex = $"0x{code:x}";
            var data = new Dictionary<string, string>(StringComparer.Ordinal) { ["bugcheckCode"] = hex };
            return new ExtractedFields(DiagnosticSeverity.Critical, "Unexpected shutdown with bugcheck",
                $"Kernel-Power event 41 recorded bugcheck code {hex}.", null, data);
        }
        return new ExtractedFields(DiagnosticSeverity.Warning, "Unexpected power loss or hard lock",
            "Kernel-Power event 41: the system restarted without a clean shutdown.", null, EmptyData);
    }

    // disk/storahci/stornvme events are legacy driver events: usually
    // message-only with no structured EventData, so no per-event detail
    // beyond the id-based title.
    private static ExtractedFields ExtractDisk(int eventId)
    {
        var title = eventId switch
        {
            7 => "Bad block detected on disk",
            51 => "Disk I/O error during a paging operation",
            153 => "Storage device I/O error",
            129 => "Storage controller reset a device",
            _ => $"Disk error event {eventId}",
        };
        return new ExtractedFields(DiagnosticSeverity.Warning, title, $"Disk subsystem event {eventId}.", null, EmptyData);
    }

    // Only EventID 4101 is a confirmed TDR (driver reset). Other Display
    // events at Level<=3 (Warning/Error/Critical) still surface as a generic
    // warning; Level>=4 (Information/Verbose) is routine Display-subsystem
    // logging and is discarded, matching the catalog's XPath scope.
    private static ExtractedFields? ExtractTdr(int eventId, XElement system)
    {
        if (eventId == 4101)
        {
            return new ExtractedFields(DiagnosticSeverity.Critical, "Display driver timeout (TDR)",
                "The display driver stopped responding and was reset (Display event 4101).", null, EmptyData);
        }
        var level = int.TryParse(system.Element(Ns + "Level")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv) ? lv : 4;
        if (level > 3)
        {
            return null;
        }
        return new ExtractedFields(DiagnosticSeverity.Warning, $"Display error event {eventId}",
            $"Display subsystem event {eventId}.", null, EmptyData);
    }

    // Level<=3 (Critical/Error/Warning) is also encoded in the catalog XPath, so
    // a live/backfill event never reaches here above that level; re-checked
    // here as a direct-call safety net (e.g. a future caller feeding raw XML)
    // so info/verbose driver chatter is never counted as an incident.
    private static ExtractedFields? ExtractGpuDriver(XElement system, XElement? eventData)
    {
        var level = int.TryParse(system.Element(Ns + "Level")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv) ? lv : 4;
        if (level > 3)
        {
            return null;
        }
        var parts = GetPositionalData(eventData).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var detail = parts.Count > 0 ? string.Join(" ", parts) : "GPU driver reported an error event.";
        return new ExtractedFields(DiagnosticSeverity.Warning, "GPU driver error", detail, null, EmptyData);
    }

    private static ExtractedFields ExtractAppCrash(XElement? eventData)
    {
        var appName = GetNamedData(eventData, "AppName") ?? "";
        var appVersion = GetNamedData(eventData, "AppVersion") ?? "";
        var moduleName = GetNamedData(eventData, "ModuleName") ?? "";
        var exceptionCode = GetNamedData(eventData, "ExceptionCode") ?? "";
        var appPath = GetNamedData(eventData, "AppPath") ?? "";
        var modulePath = GetNamedData(eventData, "ModulePath") ?? "";

        var app = new DiagnosticAppInfo
        {
            Name = appName,
            Path = appPath,
            ExceptionCode = exceptionCode,
            FaultingModule = moduleName,
            IsGame = false,
        };
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(appVersion))
        {
            data["appVersion"] = appVersion;
        }
        if (!string.IsNullOrEmpty(modulePath))
        {
            data["modulePath"] = modulePath;
        }
        if (string.Equals(appName, "Nexus.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(moduleName, "nvml.dll", StringComparison.OrdinalIgnoreCase))
        {
            return new ExtractedFields(DiagnosticSeverity.Info, "Nexus restarted during an NVIDIA driver update",
                $"Nexus restarted when the NVIDIA driver reloaded (exception {exceptionCode} in {moduleName}).", app, data,
                DiagnosticEventCatalog.SourceDriverRestart);
        }
        var detail = $"{appName} crashed (exception {exceptionCode}) in {moduleName}.";
        return new ExtractedFields(DiagnosticSeverity.Warning, "Application crash", detail, app, data);
    }

    // Windows Error Reporting logs plenty of non-kernel reports under the
    // same provider and id (app hangs, regular crash reports); only an
    // EventName starting with "LiveKernelEvent" is the bugcheck/livedump
    // signal this source cares about. Everything else matches the catalog
    // entry but is discarded here.
    private static ExtractedFields? ExtractLiveKernel(XElement? eventData)
    {
        var eventName = GetNamedData(eventData, "EventName") ?? "";
        if (!eventName.StartsWith("LiveKernelEvent", StringComparison.Ordinal))
        {
            return null;
        }
        var p1 = GetNamedData(eventData, "P1") ?? "";
        var p2 = GetNamedData(eventData, "P2") ?? "";
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(p1))
        {
            data["p1"] = p1;
        }
        if (!string.IsNullOrEmpty(p2))
        {
            data["p2"] = p2;
        }
        var detail = string.IsNullOrEmpty(p1)
            ? "Live kernel event reported by Windows Error Reporting."
            : $"Live kernel event, bugcheck parameter {p1}.";
        return new ExtractedFields(DiagnosticSeverity.Critical, "Live kernel event", detail, null, data);
    }

    private static ExtractedFields ExtractMemDiag(int eventId)
    {
        if (eventId == 1202)
        {
            return new ExtractedFields(DiagnosticSeverity.Critical, "Memory errors detected",
                "Windows Memory Diagnostic found hardware memory errors.", null, EmptyData);
        }
        return new ExtractedFields(DiagnosticSeverity.Info, "Memory diagnostic completed",
            "Windows Memory Diagnostic tested memory and found no errors.", null, EmptyData);
    }

    private static string? GetNamedData(XElement? eventData, string name)
    {
        if (eventData is null)
        {
            return null;
        }
        foreach (var data in eventData.Elements(Ns + "Data"))
        {
            if (data.Attribute("Name")?.Value == name)
            {
                return data.Value;
            }
        }
        return null;
    }

    private static List<string> GetPositionalData(XElement? eventData)
    {
        var result = new List<string>();
        if (eventData is null)
        {
            return result;
        }
        foreach (var data in eventData.Elements(Ns + "Data"))
        {
            if (data.Attribute("Name") is not null)
            {
                continue;
            }
            result.Add(data.Value);
        }
        return result;
    }

    private static string NormalizeHex(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return "0x" + trimmed[2..].ToLowerInvariant();
        }
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asDecimal))
        {
            return $"0x{asDecimal:x}";
        }
        return trimmed;
    }
}
