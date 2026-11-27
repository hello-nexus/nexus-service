using System.Diagnostics;
using Nexus.Service.Auth;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models;
using Nexus.Service.Models.Lifecycle;

namespace Nexus.Service.Routes;

public static class LifecycleRoutes
{
    public static void MapLifecycleEndpoints(this WebApplication app)
    {
        app.MapGet("/start", (IStartupProvider s) => new WillStartResponse { Enabled = s.IsEnabled() });
        app.MapPost("/start", (SetWillStartParams body, IStartupProvider s) =>
        {
            var path = string.IsNullOrWhiteSpace(body.Path) ? ResolveCurrentExecutablePath() : body.Path;
            var ok = s.SetEnabled(body.Enabled, path, body.Arguments);
            var enabled = s.IsEnabled();
            return new WillStartResponse { Enabled = enabled, Error = !ok || enabled != body.Enabled };
        });

        app.MapGet("/pawnio", (PawnIoGate gate) => gate.GetStatus());

        app.MapPost("/pawnio/fix", async (PawnIoGate gate, IHostApplicationLifetime lifetime,
            Nexus.Service.Devices.Firmware.FirmwareFlasher flasher) =>
        {
            if (flasher.IsFlashing)
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = "A firmware update is in progress; cannot fix the driver now." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 409);
            }

            // Not the request token: a closed window must not abort an install or repair that is already running.
            var status = await gate.FixAsync(lifetime.ApplicationStopping).ConfigureAwait(false);
            // LHM enumerates SuperIO and SPD once per process, so a fixed driver needs a service restart to surface those devices.
            if (OperatingSystem.IsWindows() && !flasher.IsFlashing && gate.TakeRedetectRequest())
            {
                if (Nexus.Service.Lifecycle.FactoryReset.Begin(wipe: false))
                {
                    status.Restarting = true;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(200);
                        lifetime.StopApplication();
                    });
                }
                else
                {
                    gate.RestoreRedetectRequest();
                }
            }
            return Results.Json(status, Nexus.Service.Serialization.AppJsonContext.Default.PawnIoStatus);
        }).LocalhostOnly();

        app.MapPost("/pawnio/restart-pc", (PawnIoGate gate, Nexus.Service.Devices.Firmware.FirmwareFlasher flasher) =>
        {
            if (!gate.IsSupported)
            {
                return Results.Json(new ApiResponse { Error = true, Msg = "Restart is only available on Windows." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse, statusCode: 501);
            }

            // A reboot mid USB flash strands the device in its bootloader.
            if (flasher.IsFlashing)
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = "A firmware update is in progress; cannot restart now." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 409);
            }

            if (gate.GetStatus().State is not (PawnIoStates.RestartRequired or PawnIoStates.NotRunning))
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = "No restart is needed for the driver." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 409);
            }

            if (!gate.RestartPc())
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = "Could not start the restart." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 500);
            }
            return Results.Json(ApiResponse.Ok(), Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse, statusCode: 202);
        }).LocalhostOnly();
    }

    private static string ResolveCurrentExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }

        try
        {
            var modulePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(modulePath))
            {
                return modulePath;
            }
        }
        catch { /* best-effort fallback below */ }

        return Path.Combine(AppContext.BaseDirectory, "Nexus.exe");
    }
}
