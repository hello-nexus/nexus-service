using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
#if WINDOWS
using Microsoft.Extensions.DependencyInjection;
#endif
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Platform;
#if WINDOWS
using Nexus.Service.Helper;
#endif

namespace Nexus.Service.Routes;

/// <summary>
/// Tester-facing diagnostics surface. <c>/diagnostics/open-logs</c> reveals the
/// Nexus logs folder (nexus-service.log plus the other nexus-*.log files) in the OS
/// file manager so a tester can attach them to a bug report. <c>/diagnostics/client-mem</c>
/// ingests a renderer/host memory sample and writes it through <see cref="ServiceLog"/>,
/// so a WebView2 renderer's growth is visible in the same log a tester submits
/// (the service log otherwise has zero renderer telemetry). Loopback-only: both
/// act on the local machine, so a paired phone or LAN caller has no business
/// reaching them.
/// </summary>
internal static class DiagnosticsRoutes
{
    // Flag a sample loud (WRN) once it crosses these. JS heap and total working
    // set are different axes, so each has its own ceiling.
    private const int WarnHeapMB = 500;
    private const int WarnWorkingSetMB = 800;

    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        app.MapPost("/diagnostics/open-logs", async (IServiceProvider sp) =>
        {
            try
            {
#if WINDOWS
                // The service is LocalSystem in Session 0; an explorer.exe it
                // spawns lands in the non-interactive session and never shows.
                // The user-session helper opens the folder on the user's desktop
                // and acks, so a missing helper surfaces as 503 instead of a
                // silent 200.
                var registry = sp.GetService<Nexus.Service.Helper.HelperRegistry>();
                if (registry is null ||
                    !await Nexus.Service.Helper.Domains.FileDialogCommands.OpenFolderAsync(
                        registry, Nexus.Service.Persistence.NexusDataPaths.NexusRoot()))
                {
                    return Results.Problem("no interactive user session", statusCode: StatusCodes.Status503ServiceUnavailable);
                }
#else
                // macOS (LaunchAgent) / Linux run in the user session already,
                // so opening directly reaches the user's file manager.
                await Task.CompletedTask;
                Nexus.Service.Diagnostics.LogsFolder.Open();
#endif
                return Results.Ok(ApiResponse.Ok());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[diagnostics] open-logs failed: {ex.Message}");
                return Results.Problem(ex.Message);
            }
        }).LocalhostOnly();

        app.MapPost("/diagnostics/client-mem", (ClientMemBody body) =>
        {
            var line = FormatClientMem(body);
            if (IsWarn(body)) ServiceLog.Warn(line); else ServiceLog.Info(line);
            return Results.Ok(ApiResponse.Ok());
        }).LocalhostOnly();
    }

    private static bool IsWarn(ClientMemBody b) =>
        (b.JsHeapMB ?? 0) >= WarnHeapMB
        || (b.TotalWsMB ?? b.LargestWsMB ?? 0) >= WarnWorkingSetMB;

    private static string FormatClientMem(ClientMemBody b)
    {
        // The overlay host sampler reports working set; the renderer reports JS
        // heap + DOM. One endpoint, one log tag, two shapes keyed off Source.
        if (string.Equals(b.Source, "host", StringComparison.Ordinal))
        {
            // `largest` is the heaviest WebView2 child (the renderer); `total`
            // sums every WebView2 child of this host.
            return $"[client-mem] host total={b.TotalWsMB?.ToString() ?? "?"}MB "
                + $"largest={b.LargestWsMB?.ToString() ?? "?"}MB(pid {b.LargestPid?.ToString() ?? "?"}) "
                + $"children={b.Children?.ToString() ?? "?"}";
        }
        var heap = b.JsHeapMB.HasValue ? $"{b.JsHeapMB}/{b.JsHeapLimitMB}MB" : "n/a";
        return $"[client-mem] renderer surface={San(b.Surface)} age={b.AgeMin ?? 0}m "
            + $"jsHeap={heap} dom={b.DomNodes ?? 0} reconnects={b.Reconnects ?? 0} "
            + $"transport={San(b.Transport)} connected={((b.Connected ?? false) ? 1 : 0)} "
            + $"topics={b.Topics ?? 0} listeners={b.Listeners ?? 0}";
    }

    // Loopback-only, but a log line is still untrusted text: cap length and
    // neutralize control chars so a stray newline can't forge a second line.
    private static string San(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "?";
        if (s.Length > 64) s = s[..64];
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++) span[i] = src[i] < ' ' ? '.' : src[i];
        });
    }
}

/// <summary>
/// One renderer (<c>Source=renderer</c>) or overlay-host (<c>Source=host</c>)
/// memory sample, POSTed sparsely to <c>/diagnostics/client-mem</c>. Every field
/// is optional so a single shape carries both the JS-heap/DOM side and the
/// working-set side.
/// </summary>
public sealed class ClientMemBody
{
    public string? Source { get; set; }
    // Renderer (nexus-web memoryProbe).
    public string? Surface { get; set; }
    public int? AgeMin { get; set; }
    public int? JsHeapMB { get; set; }
    public int? JsHeapLimitMB { get; set; }
    public int? DomNodes { get; set; }
    public int? Reconnects { get; set; }
    public string? Transport { get; set; }
    public bool? Connected { get; set; }
    public int? Topics { get; set; }
    public int? Listeners { get; set; }
    // Overlay host (WebView2 child working sets).
    public int? TotalWsMB { get; set; }
    public int? LargestWsMB { get; set; }
    public int? LargestPid { get; set; }
    public int? Children { get; set; }
}
