using System;
using System.Collections.Generic;

namespace Nexus.Service.Telemetry;

/// <summary>Error batch body for nexus-api's /telemetry/errors; a cross-language contract, serialized camelCase - do not rename fields without updating that side.</summary>
public sealed class ErrorReportPayload
{
    public string InstallId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Os { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public bool DevTools { get; set; }
    public List<ErrorReportItem> Errors { get; set; } = new();
}

/// <summary>One aggregated error. Also the on-disk shape of the crash file.</summary>
public sealed class ErrorReportItem
{
    public string Source { get; set; } = "service";
    public string Kind { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public string Stack { get; set; } = "";
    public string Context { get; set; } = "";

    /// <summary>Recent service log lines; set only on a crash, null (omitted) otherwise.</summary>
    public string? Log { get; set; }
    public int Count { get; set; } = 1;
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>Body of POST /telemetry/client-errors, relayed from nexus-web.</summary>
public sealed class ClientErrorsBody
{
    public List<ClientErrorBody>? Errors { get; set; }
}

public sealed class ClientErrorBody
{
    public string? Kind { get; set; }
    public string? Fingerprint { get; set; }
    public string? Type { get; set; }
    public string? Message { get; set; }
    public string? Stack { get; set; }
    public string? Context { get; set; }
    public int Count { get; set; } = 1;
}
