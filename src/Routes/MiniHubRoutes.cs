using System;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.Hyte.MiniHub;

namespace Nexus.Service.Routes;

/// <summary>
/// HYTE MiniHub (and the IBP rebrand) device-specific endpoints. The hub
/// supports two cooling modes — Software (Nexus drives) and Motherboard
/// (PWM passthrough). There is no firmware-side standalone setpoint, so
/// no "Firmware Control" mode and no EEPROM-default surface (unlike NP50).
/// </summary>
public static partial class DevicesRoutes
{
    private static void MapMiniHubEndpoints(WebApplication app)
    {
        // Switch the LIVE cooling mode. Body: { mode: 0 (Software) | 1 (Motherboard) }.
        // The hub does not expose a "get current mode" command, so callers
        // that need to know the active mode must cache what they last set —
        // exactly what the cooling page does to render the per-fan dropdown.
        app.MapPut("/devices/minihub/cooling-mode", (MiniHubCoolingModeRequest body, MiniHubHub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(new { error = "MiniHub not connected" });
            if (body.Mode != MiniHubProtocol.FanModeSoftware
                && body.Mode != MiniHubProtocol.FanModeMotherboard)
            {
                return Results.BadRequest(new { error = "mode must be 0 (Software) or 1 (Motherboard)" });
            }
            // SetDesiredFanControlMode both sends the write AND pins the
            // mode so MiniHubCoolingProvider's per-tick guard refuses to
            // flip the hub back to Software on the next curve write.
            hub.SetDesiredFanControlMode((byte)body.Mode);
            return Results.Ok(ApiResponse.Ok());
        });
    }
}

/// <summary>Body shape for PUT /devices/minihub/cooling-mode.</summary>
public sealed class MiniHubCoolingModeRequest
{
    /// <summary>0=Software, 1=Motherboard. See <see cref="MiniHubProtocol"/>.</summary>
    public int Mode { get; set; }
}
