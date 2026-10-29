using System;
#if WINDOWS
using System.Diagnostics;
#endif
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Auth;
using Nexus.Service.Diagnostics;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models;
using Nexus.Service.Platform;

namespace Nexus.Service.Routes;

/// <summary>
/// Service-control surface. Seven operations:
///   GET  /service/startup-mode   -> current autostart state: Windows reads
///                                   the SCM start type (auto/demand); mac
///                                   and Linux read IStartupProvider
///                                   (launchd agent / systemd unit)
///   POST /service/startup-mode   -> change autostart state for next boot,
///                                   same per-platform split as GET
///   GET  /service/fast-startup   -> Windows Fast Startup state (HiberbootEnabled,
///                                   policy override, hibernation availability)
///   POST /service/fast-startup   -> set HiberbootEnabled; 409 when policy-managed
///                                   or hibernation is off, 404 off Windows
///   POST /service/stop           -> graceful self-stop
///   POST /service/factory-reset  -> wipe all data dirs and restart fresh
///   POST /service/open-app       -> ensure dashboard is open
///
/// All are protected by two stacked gates:
///   1. <see cref="LocalhostOnlyEndpointExtensions.LocalhostOnly"/> - the
///      auth middleware 404s any non-loopback caller before token checks
///      even fire, so the route's existence isn't leaked to LAN scanners.
///   2. The standard token-auth middleware that already covers every
///      non-SPA-fallback route. Loopback callers without a valid dashboard
///      token still get 401. This neutralizes DNS-rebind attacks too: a
///      malicious cross-origin page can't read the real dashboard's
///      token, so its request hits 401 even after rebinding to 127.0.0.1.
/// None of these routes call <c>.AllowPanel()</c>, so panel-session
/// tokens (phones, Y70 displays) can't reach them either.
/// </summary>
internal static class ServiceControlRoutes
{
    public static void MapServiceControlEndpoints(this WebApplication app)
    {
        app.MapGet("/service/startup-mode", (IStartupProvider startupProvider) => Results.Ok(new StartupModeDto
        {
            AutoStart = ReadAutoStart(startupProvider),
        })).LocalhostOnly();

        app.MapPost("/service/startup-mode", (StartupModeBody body, IStartupProvider startupProvider) =>
        {
            var ok = WriteAutoStart(body.AutoStart, startupProvider);
#if WINDOWS
            const string failureDetail = "sc.exe config failed";
            const int failureStatus = 500;
#else
            // No boot unit this process can toggle (not root, or no systemd/launchd install): an install limit, not a fault.
            const string failureDetail = "the startup provider rejected the change";
            const int failureStatus = 409;
#endif
            return ok ? Results.Ok(new StartupModeDto { AutoStart = ReadAutoStart(startupProvider) })
                      : Results.Problem(failureDetail, statusCode: failureStatus);
        }).LocalhostOnly();

        app.MapGet("/service/fast-startup", () => Results.Ok(FastStartupDto.From(FastStartup.Read()))).LocalhostOnly();

        app.MapPost("/service/fast-startup", (FastStartupBody body) =>
        {
            var state = FastStartup.Read();
            if (!state.Supported)
            {
                return Results.Json(new ApiResponse { Error = true, Msg = "Fast Startup is only available on Windows." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse, statusCode: 404);
            }
            if (state.PolicyManaged || !state.HibernateAvailable)
            {
                var why = state.PolicyManaged ? "Fast Startup is managed by group policy." : "Hibernation is off, so Fast Startup is unavailable.";
                return Results.Json(new ApiResponse { Error = true, Msg = why },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse, statusCode: 409);
            }
            if (!FastStartup.Write(body.Enabled))
            {
                return Results.Json(new ApiResponse { Error = true, Msg = "Could not update the Fast Startup setting." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse, statusCode: 500);
            }
            ServiceLog.Info($"[service-control] fast startup set to {(body.Enabled ? "on" : "off")}");
            return Results.Ok(FastStartupDto.From(FastStartup.Read()));
        }).LocalhostOnly();

        app.MapPost("/service/stop", (IHostApplicationLifetime lifetime,
            Nexus.Service.Devices.Firmware.FirmwareFlasher flasher) =>
        {
            // Never tear the service down mid-flash - that would strand the
            // device in the DFU bootloader. Refuse the stop while a firmware
            // update is running; the UI also blocks its quit affordance.
            if (flasher.IsFlashing)
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = "A firmware update is in progress; cannot stop the service." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 409);
            }

            // Fire-and-forget so the response can flush before the host
            // tears down. Lifetime.StopApplication signals the web host's
            // ApplicationStopping token, which is what our SCM dispatcher
            // (WindowsServiceHost) is waiting on - SCM then sees the
            // service transition to STOPPED.
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                lifetime.StopApplication();
            });
            return Results.Ok(ApiResponse.Ok());
        }).LocalhostOnly();

        app.MapPost("/service/factory-reset", (IHostApplicationLifetime lifetime,
            Nexus.Service.Devices.Firmware.FirmwareFlasher flasher) =>
        {
            // Same flash guard as /service/stop - tearing the service down
            // mid-flash strands the device in the DFU bootloader.
            if (flasher.IsFlashing)
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = "A firmware update is in progress; cannot reset now." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 409);
            }

            // Spawn the detached finalizer, then stop ourselves (same 200ms
            // response-flush delay as /service/stop). The finalizer waits for
            // this process to exit, wipes every Nexus data dir, then restarts
            // the service from a clean slate. Begin refuses a second spawn
            // (double-fired reset, or reset racing restart), so the repeat
            // caller gets a conflict instead of a second finalizer.
            if (!Nexus.Service.Lifecycle.FactoryReset.Begin())
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = "A reset or restart is already in progress." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 409);
            }
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                lifetime.StopApplication();
            });
            return Results.Ok(ApiResponse.Ok());
        }).LocalhostOnly();

        // Plain restart (no data wipe). Applies restart-to-apply settings such as
        // the lighting render-GPU choice. Same detached finalizer as factory-reset
        // minus the wipe: the shutdown and start match the OTA install (helper,
        // tray and overlay included), and lighting restores on the next boot.
        app.MapPost("/service/restart", async (HttpRequest request, IHostApplicationLifetime lifetime,
            Nexus.Service.Devices.Firmware.FirmwareFlasher flasher) =>
        {
            var parsed = await ReadRestartRouteAsync(request).ConfigureAwait(false);
            if (!parsed.Ok)
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = parsed.Error! },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 400);
            }
            if (flasher.IsFlashing)
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = "A firmware update is in progress; cannot restart now." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 409);
            }
            if (!Nexus.Service.Lifecycle.FactoryReset.Begin(wipe: false, parsed.Route))
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = "A reset or restart is already in progress." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 409);
            }
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                lifetime.StopApplication();
            });
            return Results.Ok(ApiResponse.Ok());
        }).LocalhostOnly();

        app.MapPost("/service/open-app", (OpenAppBody? body) =>
        {
            var route = OpenAppRoute.Normalize(body?.Route);
#if WINDOWS
            // The service runs as LocalSystem in Session 0 - spawning Edge
            // --app from here would land in a non-interactive session and
            // never show. Delegate to a one-shot Nexus.exe --open-app in the
            // active console session (same schtasks hop the helper bootstrap
            // uses).
            Nexus.Service.Lifecycle.UserHelperBootstrapper.LaunchOpenApp(route);
#elif MACOS
            // No route: focus only when a window exists (Windows parity, no reload). A route navigates it.
            Nexus.Service.Platform.Mac.MacAppWindow.OpenOrFocus(OpenAppRoute.LocalUrl(0, route), navigateIfOpen: route is not null);
#elif LINUX
            Nexus.Service.Platform.Linux.LinuxBrowsers.OpenUrl(OpenAppRoute.LocalUrl(0, route));
#endif
            return Results.Ok(ApiResponse.Ok());
        }).LocalhostOnly();
    }

#if WINDOWS
    private const string ServiceName = "NexusService";
#endif

    private const int MaxRestartBodyChars = 4096;

    internal readonly record struct RestartRouteResult(bool Ok, string? Route, string? Error);

    /// <summary>
    /// Reads the optional restart body by hand so a request that worked before (no body, an empty body, a
    /// non-JSON content type) is untouched. A JSON body must be well formed and carry a valid reopenRoute.
    /// </summary>
    internal static async Task<RestartRouteResult> ReadRestartRouteAsync(HttpRequest request)
    {
        if (!request.HasJsonContentType() || request.ContentLength == 0)
        {
            return new RestartRouteResult(true, null, null);
        }
        string text;
        try
        {
            using var reader = new System.IO.StreamReader(request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
            var buffer = new char[MaxRestartBodyChars + 1];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read > MaxRestartBodyChars)
            {
                return new RestartRouteResult(false, null, "The request body is too large.");
            }
            text = new string(buffer, 0, read);
        }
        catch (System.IO.IOException)
        {
            return new RestartRouteResult(true, null, null);
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return new RestartRouteResult(true, null, null);
        }
        RestartBody? body;
        try
        {
            body = System.Text.Json.JsonSerializer.Deserialize(text, Nexus.Service.Serialization.AppJsonContext.Default.RestartBody);
        }
        catch (System.Text.Json.JsonException)
        {
            return new RestartRouteResult(false, null, "The request body is not valid JSON.");
        }
        if (!Nexus.Service.Lifecycle.ReopenRoute.TryResolveRequest(body?.ReopenRoute, out var route))
        {
            return new RestartRouteResult(false, null, "reopenRoute must be an in-app path.");
        }
        return new RestartRouteResult(true, route, null);
    }

    internal static bool ReadAutoStart(IStartupProvider startupProvider)
    {
#if WINDOWS
        // Registry Start DWORD is locale-neutral: 2=auto, 3=demand, 4=disabled.
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        return key?.GetValue("Start") is int start && start == 2;
#else
        return startupProvider.IsEnabled();
#endif
    }

    internal static bool WriteAutoStart(bool enable, IStartupProvider startupProvider)
    {
#if WINDOWS
        var startMode = enable ? "auto" : "demand";
        var (code, _) = RunSc("config", ServiceName, $"start=", startMode);
        if (code != 0) return false;
        // The service start type only governs the LocalSystem daemon. The
        // helper (tray) also auto-launches from a per-user HKCU Run key, which
        // this Session 0 handler cannot write. Sync it in the console session so
        // "start on boot" off actually removes every boot launcher, not just the
        // service. Best-effort: if no console user is signed in, the helper
        // re-syncs on its next start.
        if (OperatingSystem.IsWindows())
        {
            var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "Nexus.exe");
            UserHelperBootstrapper.RunInUserSession($"\"{exe}\" --sync-autostart", "sync-autostart", "NexusSyncAutostart");
        }
        return true;
#else
        // No separate SCM concept off Windows: IStartupProvider toggles the
        // daemon's own boot launcher directly (launchd agent on mac, systemd
        // unit on Linux). LinuxStartupProvider ignores path/arguments - its
        // unit file already encodes ExecStart.
        return startupProvider.SetEnabled(enable, Environment.ProcessPath ?? string.Empty, "--service");
#endif
    }

#if WINDOWS
    private static (int code, string output) RunSc(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, stdout);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[service-control] sc.exe failed: {ex.Message}");
            return (-1, string.Empty);
        }
    }
#endif

}

/// <summary>Body of POST /service/restart; the whole body is optional.</summary>
public sealed class RestartBody
{
    /// <summary>In-app path (optional single query) the reopened dashboard opens on; Windows and macOS only.</summary>
    public string? ReopenRoute { get; set; }
}

public sealed class StartupModeBody
{
    public bool AutoStart { get; set; }
}

public sealed class StartupModeDto
{
    public bool AutoStart { get; set; }
}

public sealed class FastStartupBody
{
    public bool Enabled { get; set; }
}

public sealed class FastStartupDto
{
    public bool Supported { get; set; }
    public bool Enabled { get; set; }
    public bool PolicyManaged { get; set; }
    public bool HibernateAvailable { get; set; }

    internal static FastStartupDto From(FastStartupState s) => new()
    {
        Supported = s.Supported,
        Enabled = s.Enabled,
        PolicyManaged = s.PolicyManaged,
        HibernateAvailable = s.HibernateAvailable,
    };
}
