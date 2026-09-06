using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Activity;
using Nexus.Service.Models;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// The monitoring sidebar's per-process detail panel: file/version/signature
/// info for a live process, and the kill / reveal-in-file-manager actions.
/// Kept separate from MonitoringHistoryRoutes (history/decimation reads) since
/// these are a distinct concern - live process detail plus two helper-driven
/// mutations with their own guard/auth posture - and would otherwise bloat an
/// already large file.
///
/// Default token auth only: none of these three endpoints calls
/// .AllowPanel(), so a paired phone/panel session cannot reach them - only
/// the LAN bearer token can. Kill and open-location additionally refuse a
/// caller-supplied name against <see cref="ProcessActionGuards"/> before ever
/// touching a process, and route the actual OS call through
/// <see cref="IProcessActionsProvider"/> rather than acting on the
/// LocalSystem service's own privileges (see ConflictRoutes for the sibling
/// vetted-catalog-id kill path, which stays direct because it never accepts
/// an arbitrary caller-supplied name).
/// </summary>
public static class ProcessDetailRoutes
{
    public static void MapProcessDetailEndpoints(this WebApplication app)
    {
        app.MapGet("/monitoring/process-info", async (
            string? name, HttpContext ctx, ProcessMonitor processes, IProcessDetailProvider detail,
            ProcessFirstSeenCache firstSeen) =>
        {
            if (string.IsNullOrEmpty(name))
            {
                return Results.BadRequest();
            }

            var response = await ResolveProcessInfoAsync(name, processes, detail, firstSeen, ctx.RequestAborted);
            return response is null ? Results.NotFound() : Results.Ok(response);
        });

        app.MapPost("/monitoring/process-kill", async (ProcessActionBody? body, IProcessActionsProvider actions) =>
        {
            var name = body?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(ApiResponse.Fail("name is required"));
            }
            if (ProcessActionGuards.IsDenylisted(name))
            {
                return Results.BadRequest(ApiResponse.Fail("refusing to kill a critical process"));
            }
            if (!actions.IsAvailable)
            {
                return NoHelperResponse();
            }

            var (killed, failed) = await actions.KillAsync(name);
            ServiceLog.Info($"[process-actions] kill requested name={name} killed={killed} failed={failed}");
            return Results.Ok(new ProcessKillResponse { Error = false, Msg = "Ok", Killed = killed, Failed = failed });
        });

        app.MapPost("/monitoring/process-open-location", async (
            ProcessActionBody? body, ProcessMonitor processes, IProcessActionsProvider actions) =>
        {
            var name = body?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(ApiResponse.Fail("name is required"));
            }
            if (ProcessActionGuards.IsDenylisted(name))
            {
                return Results.BadRequest(ApiResponse.Fail("refusing to act on a critical process"));
            }

            var path = processes.ResolveExecutablePath(name);
            if (path is null)
            {
                return Results.NotFound();
            }
            if (!actions.IsAvailable)
            {
                return NoHelperResponse();
            }

            var ok = await actions.OpenLocationAsync(path);
            if (!ok)
            {
                return Results.Json(ApiResponse.Fail("could not open the file location"),
                    AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status502BadGateway);
            }
            return Results.Ok(ApiResponse.Ok());
        });
    }

    private static IResult NoHelperResponse() =>
        Results.Json(ApiResponse.Fail("no active user session to perform this action"),
            AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status503ServiceUnavailable);

    // internal: also called by the get_process_info MCP tool, so both serve
    // identical resolution and fallback behavior. Null means path resolution
    // failed (no running or recently seen process by that name); every other
    // outcome, including the caught-exception fallback, returns a response.
    internal static async Task<ProcessInfoResponse?> ResolveProcessInfoAsync(
        string name, ProcessMonitor processes, IProcessDetailProvider detail,
        ProcessFirstSeenCache firstSeen, CancellationToken ct)
    {
        var path = processes.ResolveExecutablePath(name);
        if (path is null)
        {
            return null;
        }

        try
        {
            var procs = processes.GetProcesses();
            var live = procs.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var instanceCount = live.Count;
            var canonicalName = live.Count > 0 ? live[0].Name : name;
            var startedAtMs = ProcessAggregation.GroupByName(procs).GetValueOrDefault(canonicalName)?.StartedAtMs;

            var fileDetail = detail.GetFileDetail(path);
            var sha256 = await detail.ComputeSha256Async(path, ct).ConfigureAwait(false);
            var firstSeenMs = firstSeen.Resolve(canonicalName);

            return BuildProcessInfoResponse(canonicalName, path, instanceCount, startedAtMs, fileDetail, sha256, firstSeenMs);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[process-info] resolve failed for {name}: {ex.Message}");
            return new ProcessInfoResponse { Supported = false, Name = name };
        }
    }

    // Pure and directly unit-tested: every input is plain data the caller
    // already resolved (live snapshot, file detail, hash, first-seen), so
    // this has no I/O of its own.
    internal static ProcessInfoResponse BuildProcessInfoResponse(
        string name, string path, int instanceCount, long? startedAtMs,
        ProcessFileDetail detail, string? sha256, long? firstSeenMs) => new()
    {
        Supported = true,
        Name = name,
        Path = path,
        InstanceCount = instanceCount,
        StartedAtMs = startedAtMs,
        Description = detail.Description,
        Version = detail.Version,
        Company = detail.Company,
        Publisher = detail.Publisher,
        Signed = detail.Signed,
        Sha256 = sha256,
        CreatedAtMs = detail.CreatedAtMs,
        ModifiedAtMs = detail.ModifiedAtMs,
        FirstSeenMs = firstSeenMs,
    };
}

// ----- Wire request/response types -----

public sealed class ProcessActionBody
{
    public string Name { get; set; } = "";
}

public sealed record ProcessInfoResponse
{
    public bool Supported { get; init; } = true;
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public int InstanceCount { get; init; }
    public long? StartedAtMs { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    public string? Company { get; init; }
    public string? Publisher { get; init; }
    public string Signed { get; init; } = "unknown";
    public string? Sha256 { get; init; }
    public long? CreatedAtMs { get; init; }
    public long? ModifiedAtMs { get; init; }
    public long? FirstSeenMs { get; init; }
}

public sealed class ProcessKillResponse : ApiResponse
{
    public int Killed { get; set; }
    public int Failed { get; set; }
}
