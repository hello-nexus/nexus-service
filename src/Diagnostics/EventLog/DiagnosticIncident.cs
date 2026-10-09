using System;
using System.Collections.Generic;

namespace Nexus.Service.Diagnostics.EventLog;

/// <summary>Contract severity values for DiagnosticIncident.Severity.</summary>
public static class DiagnosticSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

/// <summary>
/// A single classified Windows Event Log incident. Plain record, no JSON
/// attributes here; the integrator registers this type in AppJsonContext,
/// whose camelCase naming policy maps these PascalCase properties onto the
/// REST contract's id/timeUtc/source/severity/title/detail/app/data fields.
/// </summary>
public sealed record DiagnosticIncident
{
    private static readonly IReadOnlyDictionary<string, string> EmptyData = new Dictionary<string, string>();

    /// <summary>Channel + "/" + EventRecordID.</summary>
    public string Id { get; init; } = "";
    public DateTime TimeUtc { get; init; }
    /// <summary>Windows: whea | bugcheck | dirtyShutdown | disk | tdr | gpuDriver |
    /// appCrash | driverRestart | liveKernel | memDiag. Linux: kernel | oomKill | segfault |
    /// unitFailed, plus the shared disk/appCrash values.</summary>
    public string Source { get; init; } = "";
    /// <summary>info | warning | critical; see DiagnosticSeverity.</summary>
    public string Severity { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>Non-null only when Source is "appCrash" or "driverRestart". IsGame is always false here; the integrator fills it via Steam library match.</summary>
    public DiagnosticAppInfo? App { get; init; }
    public IReadOnlyDictionary<string, string> Data { get; init; } = EmptyData;
    /// <summary>Count of occurrences collapsed into this row by the route-layer repeat grouping. 1 when not grouped.</summary>
    public int RepeatCount { get; init; } = 1;
    /// <summary>Oldest grouped occurrence's time. Null when RepeatCount == 1.</summary>
    public DateTime? FirstUtc { get; init; }
}

/// <summary>The crashing application, populated only for Source == "appCrash" or "driverRestart".</summary>
public sealed record DiagnosticAppInfo
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string ExceptionCode { get; init; } = "";
    public string FaultingModule { get; init; } = "";
    public bool IsGame { get; init; }
}
