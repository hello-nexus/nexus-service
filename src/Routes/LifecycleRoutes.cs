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
            Nexus.Service.Devices.Firmware.FirmwareFlasher flasher, CancellationToken ct) =>
        {
            var status = await gate.FixAsync(ct).ConfigureAwait(false);
            // LHM enumerates SuperIO and SPD once per process, so a fixed driver needs a service restart to surface those devices.
            if (OperatingSystem.IsWindows() && gate.TakeRedetectRequest() && !flasher.IsFlashing
                && Nexus.Service.Lifecycle.FactoryReset.Begin(wipe: false))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    lifetime.StopApplication();
                });
            }
            return status;
        }).LocalhostOnly();

        app.MapPost("/pawnio/restart-pc", (PawnIoGate gate) =>
        {
            if (!gate.RestartPc())
            {
                return Results.Json(new ApiResponse { Error = true, Msg = "Restart is only available on Windows." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse, statusCode: 501);
            }
            return Results.Text("{}", "application/json", System.Text.Encoding.UTF8, 202);
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
