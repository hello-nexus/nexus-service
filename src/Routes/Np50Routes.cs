using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Routes;

/// <summary>
/// HYTE NP50 device-specific endpoints. The singleton <see cref="Np50Hub"/>
/// holds live state; routes are thin read-throughs so the UI doesn't have
/// to subscribe to the full cooling broadcast to see hub-only fields like
/// firmware version or AmpScale warnings.
///
/// v1 ships read endpoints only. Lighting POST + firmware update endpoints
/// land with phases 3 and 4 respectively.
/// </summary>
public static partial class DevicesRoutes
{
    private static void MapNp50Endpoints(WebApplication app)
    {
        // Singleton-style endpoint - nexus currently supports at most one NP50.
        // Per-serial routes get added once multi-hub support arrives; the
        // shape is intentionally compatible (a single NP50 means /devices/np50
        // and /devices/np50/{any-serial} both return the same state).
        app.MapGet("/devices/np50", (Np50Hub hub) =>
        {
            return Results.Ok(new Np50StateResponse
            {
                Connected = hub.IsConnected,
                DeviceId = hub.DeviceId,
                State = hub.State,
            });
        });

        app.MapGet("/devices/np50/{serial}", (string serial, Np50Hub hub) =>
        {
            // Until multi-hub support lands, accept any serial and return the
            // singleton. Doesn't 404 - the UI uses this to refetch on event.
            return Results.Ok(new Np50StateResponse
            {
                Connected = hub.IsConnected,
                DeviceId = hub.DeviceId,
                State = hub.State,
            });
        });

        // Active cooling warnings as a flat list (parsed from the hub's
        // last warning-detail poll). Returns an empty list when nothing is
        // wrong. The "cooling/warnings" WebSocket topic notifies on change;
        // subscribers fetch this to repaint.
        // Read the EEPROM-persisted firmware defaults (default cooling mode +
        // static fan%). What the hub does when nexus isn't streaming.
        app.MapGet("/devices/np50/firmware-defaults", (Np50Hub hub) =>
        {
            if (!hub.IsConnected)
                return DeviceFail(StatusCodes.Status409Conflict, "NP50 not connected");
            var d = hub.GetFirmwareDefaults();
            if (d is null)
                return FirmwareReadBusy("Failed to read firmware defaults from NP50.");
            return Results.Ok(new Np50FirmwareDefaultsResponse
            {
                DefaultMode = d.Value.DefaultMode,
                StaticFanPercent = d.Value.StaticFanPercent,
                IsStartAnimationOff = d.Value.IsStartAnimationOff,
                IsFirmwareLightingOff = d.Value.IsFirmwareLightingOff,
            });
        });

        // Persist new firmware defaults to EEPROM. Read-before-write inside
        // SetFirmwareDefaults - if the hub already reports the same values
        // we do not issue a write (EEPROM endurance is ~10 000 cycles).
        app.MapPut("/devices/np50/firmware-defaults", (Np50FirmwareDefaultsRequest body, Np50Hub hub) =>
        {
            if (!hub.IsConnected)
                return DeviceFail(StatusCodes.Status409Conflict, "NP50 not connected");
            if (body.DefaultMode != Np50Protocol.DefaultModeStatic
                && body.DefaultMode != Np50Protocol.DefaultModeMotherboard)
            {
                return DeviceFail(StatusCodes.Status400BadRequest, "defaultMode must be 0 (Static) or 1 (Motherboard)");
            }
            var ok = hub.SetFirmwareDefaults((byte)body.DefaultMode, (byte)Math.Clamp(body.StaticFanPercent, 0, 100));
            if (!ok)
                return Results.Problem("Failed to write firmware defaults to NP50.");
            return Results.Ok(ApiResponse.Ok());
        });

        // Read the firmware-side LED animation (effect + RGB + brightness).
        // What the strips show when the firmware is driving them - i.e. PC
        // off, service down, or hub in Software mode while nexus isn't
        // streaming frames.
        app.MapGet("/devices/np50/firmware-animation", (Np50Hub hub) =>
        {
            if (!hub.IsConnected)
                return DeviceFail(StatusCodes.Status409Conflict, "NP50 not connected");
            var a = hub.GetFirmwareAnimation();
            if (a is null)
                return FirmwareReadBusy("Failed to read firmware animation from NP50.");
            return Results.Ok(new Np50FirmwareAnimationResponse
            {
                Animation = a.Value.Animation,
                R = a.Value.R,
                G = a.Value.G,
                B = a.Value.B,
                Brightness = a.Value.Brightness,
            });
        });

        // Write firmware-side LED animation. Read-before-write inside
        // SetFirmwareAnimation; opcode 0x0C carries the SAVE byte and updates
        // both EEPROM and the live MCU animation in one shot.
        app.MapPut("/devices/np50/firmware-animation", (Np50FirmwareAnimationRequest body, Np50Hub hub) =>
        {
            if (!hub.IsConnected)
                return DeviceFail(StatusCodes.Status409Conflict, "NP50 not connected");
            if (body.Animation < Np50Protocol.FwAnimationColor
                || body.Animation > Np50Protocol.FwAnimationRainbowGradient)
            {
                return DeviceFail(StatusCodes.Status400BadRequest, "animation must be 1 (Color), 2 (Rainbow), 3 (Breathe), or 4 (Rainbow Gradient)");
            }
            var ok = hub.SetFirmwareAnimation(
                (byte)body.Animation,
                (byte)Math.Clamp(body.R, 0, 255),
                (byte)Math.Clamp(body.G, 0, 255),
                (byte)Math.Clamp(body.B, 0, 255),
                (byte)Math.Clamp(body.Brightness, 0, 100));
            if (!ok)
                return Results.Problem("Failed to write firmware animation to NP50.");
            return Results.Ok(ApiResponse.Ok());
        });

        // Switch the LIVE cooling mode (opcode #3). Distinct from the
        // EEPROM-persisted default above - this is what's actively driving
        // the fans right now. SetDesiredCoolingMode also stores the value
        // so the heartbeat re-asserts it if the firmware drifts back.
        app.MapPut("/devices/np50/cooling-mode", (Np50CoolingModeRequest body, Np50Hub hub) =>
        {
            if (!hub.IsConnected)
                return DeviceFail(StatusCodes.Status409Conflict, "NP50 not connected");
            if (body.Mode != Np50Protocol.ModeSoftware
                && body.Mode != Np50Protocol.ModeMotherboard
                && body.Mode != Np50Protocol.ModeStatic)
            {
                return DeviceFail(StatusCodes.Status400BadRequest, "mode must be 1 (Software), 2 (Motherboard), or 3 (Static/Firmware)");
            }
            hub.SetDesiredCoolingMode((byte)body.Mode);
            return Results.Ok(ApiResponse.Ok());
        });

        // "FW Control" - hand the fans back to the firmware's standalone
        // behaviour configured on the device page (Static @ stored % or
        // Motherboard PWM). Replaces "BIOS control" for the NP50 in the
        // cooling-page dropdown: a USB hub has no motherboard hand-off of its
        // own, so firmware control IS the off / hand-back setting. Reads the
        // EEPROM defaults to pick the matching live mode + setpoint.
        app.MapPut("/devices/np50/firmware-control", (Np50Hub hub) =>
        {
            if (!hub.IsConnected)
                return DeviceFail(StatusCodes.Status409Conflict, "NP50 not connected");
            if (!hub.ApplyFirmwareStandaloneMode())
                return Results.Problem("Failed to apply firmware control mode to NP50.");
            return Results.Ok(ApiResponse.Ok());
        });

        // Push an LED frame at one of the three ports. Body shape is a flat list
    }

}

public sealed class Np50LightingRequest
{
    public List<Np50LedColor> Leds { get; set; } = new();
}

/// <summary>Shape returned by /devices/np50/{serial}/fw.</summary>
public sealed class Np50FirmwareResponse
{
    public string Current { get; set; } = "";
    public string Available { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string Changelog { get; set; } = "";
    /// <summary>"external" until the in-app flasher lands.</summary>
    public string V1Action { get; set; } = "external";
}

public sealed class Np50LedColor
{
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
}

/// <summary>Shape returned by GET /devices/np50/firmware-defaults.</summary>
public sealed class Np50FirmwareDefaultsResponse
{
    /// <summary>0 = Static (uses StaticFanPercent), 1 = Motherboard PWM passthrough.</summary>
    public byte DefaultMode { get; set; }
    public byte StaticFanPercent { get; set; }
    public bool IsStartAnimationOff { get; set; }
    public bool IsFirmwareLightingOff { get; set; }
}

/// <summary>Body shape for PUT /devices/np50/firmware-defaults.</summary>
public sealed class Np50FirmwareDefaultsRequest
{
    public int DefaultMode { get; set; }
    public int StaticFanPercent { get; set; }
}

/// <summary>Shape returned by GET /devices/np50/firmware-animation.</summary>
public sealed class Np50FirmwareAnimationResponse
{
    /// <summary>1=Color, 2=Rainbow, 3=Breathe, 4=Rainbow Gradient.</summary>
    public byte Animation { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public byte Brightness { get; set; }
}

/// <summary>Body shape for PUT /devices/np50/firmware-animation.</summary>
public sealed class Np50FirmwareAnimationRequest
{
    public int Animation { get; set; }
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
    public int Brightness { get; set; }
}

/// <summary>Body shape for PUT /devices/np50/cooling-mode.</summary>
public sealed class Np50CoolingModeRequest
{
    /// <summary>1=Software, 2=Motherboard, 3=Static (firmware fallback).</summary>
    public int Mode { get; set; }
}

/// <summary>Shape returned by /devices/np50[/{serial}]. AOT-registered in <see cref="Serialization.AppJsonContext"/>.</summary>
public sealed class Np50StateResponse
{
    public bool Connected { get; set; }
    public string DeviceId { get; set; } = "";
    public Np50State State { get; set; } = new();
}
