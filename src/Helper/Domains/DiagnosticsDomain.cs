#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>diagnostics.openLogs</c>. Service-to-helper, one-way. The
/// service handler runs as LocalSystem in Session 0, where an explorer.exe it
/// spawns lands in the non-interactive session and never appears. The helper
/// runs in the user session, so it opens the Nexus data folder on the user's
/// desktop. No fields - the envelope's existence is the signal.
/// </summary>
public sealed class OpenLogsPayload { }

/// <summary>
/// Payload for <c>diagnostics.openEventViewer</c>. Service-to-helper, one-way,
/// same shape and reasoning as <see cref="OpenLogsPayload"/>: eventvwr.msc
/// must launch in the user session, not Session 0.
/// </summary>
public sealed class OpenEventViewerPayload { }

/// <summary>
/// Payload for <c>diagnostics.openDeviceManager</c>. Service-to-helper, one-way,
/// same shape and reasoning as <see cref="OpenLogsPayload"/>: devmgmt.msc
/// must launch in the user session, not Session 0.
/// </summary>
public sealed class OpenDeviceManagerPayload { }

// JSON source-gen registration is centralised in
// src/Serialization/AppJsonContext.cs - append a matching
// [JsonSerializable(typeof(OpenLogsPayload))] line there.

/// <summary>
/// Service-side outbound facade. Service code calls this to ask the
/// user-session helper to reveal the logs folder or open Event Viewer.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DiagnosticsCommands
{
    public static Task OpenLogsAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "diagnostics.openLogs",
            payload: new OpenLogsPayload(),
            payloadType: AppJsonContext.Default.OpenLogsPayload,
            ct: ct);
    }

    public static Task OpenEventViewerAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "diagnostics.openEventViewer",
            payload: new OpenEventViewerPayload(),
            payloadType: AppJsonContext.Default.OpenEventViewerPayload,
            ct: ct);
    }

    public static Task OpenDeviceManagerAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "diagnostics.openDeviceManager",
            payload: new OpenDeviceManagerPayload(),
            payloadType: AppJsonContext.Default.OpenDeviceManagerPayload,
            ct: ct);
    }
}

/// <summary>
/// Helper-side handler. The helper bootstrap constructs this with the open
/// actions and calls <see cref="Register"/> to bind them to the dispatch
/// registry.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiagnosticsHandler
{
    private readonly Action _onOpenLogs;
    private readonly Action _onOpenEventViewer;
    private readonly Action _onOpenDeviceManager;

    public DiagnosticsHandler(Action onOpenLogs, Action onOpenEventViewer, Action onOpenDeviceManager)
    {
        _onOpenLogs = onOpenLogs;
        _onOpenEventViewer = onOpenEventViewer;
        _onOpenDeviceManager = onOpenDeviceManager;
    }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register("diagnostics.openLogs", (env, _) =>
        {
            try { _onOpenLogs(); } catch { }
            return Task.FromResult(env.Ok());
        });
        registry.Register("diagnostics.openEventViewer", (env, _) =>
        {
            try { _onOpenEventViewer(); } catch { }
            return Task.FromResult(env.Ok());
        });
        registry.Register("diagnostics.openDeviceManager", (env, _) =>
        {
            try { _onOpenDeviceManager(); } catch { }
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
