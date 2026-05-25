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
        // Singleton-style endpoint — nexus currently supports at most one NP50.
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
            // singleton. Doesn't 404 — the UI uses this to refetch on event.
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
        app.MapGet("/cooling/warnings", (Np50Hub hub) =>
        {
            var resp = new GetCoolingWarningsResponse();
            if (!hub.IsConnected) return Results.Ok(resp);
            var dev = hub.DeviceId;
            void AddIf(string port, Np50PortWarning w)
            {
                if (w.CurrentOverflow)
                    resp.Warnings.Add(new CoolingWarning
                    {
                        DeviceId = dev, Port = port, Code = "current_overflow", Severity = "error",
                        Message = $"AmpScale is throttling {port}: total current draw exceeds the port's 8A budget.",
                    });
                if (w.LedCountExceeded)
                    resp.Warnings.Add(new CoolingWarning
                    {
                        DeviceId = dev, Port = port, Code = "led_count_exceeded", Severity = "warning",
                        Message = $"{port} has more than 249 LEDs connected.",
                    });
                if (w.PortDeviceCountExceeded)
                    resp.Warnings.Add(new CoolingWarning
                    {
                        DeviceId = dev, Port = port, Code = "port_device_count_exceeded", Severity = "warning",
                        Message = $"{port} has more than 18 daisy-chained fans.",
                    });
                if (w.TotalDeviceCountExceeded)
                    resp.Warnings.Add(new CoolingWarning
                    {
                        DeviceId = dev, Port = port, Code = "total_device_count_exceeded", Severity = "warning",
                        Message = "Total fan count across the hub exceeds the AmpScale budget.",
                    });
            }
            AddIf("Port 1", hub.State.Warnings.Port1);
            AddIf("Port 2", hub.State.Warnings.Port2);
            AddIf("Port 3", hub.State.Warnings.Port3);
            return Results.Ok(resp);
        });

        // Firmware version + "update available" surface. v1 doesn't flash —
        // the FirmwareStore is wired in but Np50FirmwareSource isn't yet, so
        // the available/changelog fields are empty until phase-2 lands.
        app.MapGet("/devices/np50/{serial}/fw", (string serial, Np50Hub hub) =>
        {
            return Results.Ok(new Np50FirmwareResponse
            {
                Current = hub.State.FirmwareVersion,
                Available = "",        // populated once Np50FirmwareSource is wired
                UpdateAvailable = false,
                Changelog = "",
                V1Action = "external",
            });
        });

        // Read the EEPROM-persisted firmware defaults (default cooling mode +
        // static fan%). What the hub does when nexus isn't streaming.
        app.MapGet("/devices/np50/firmware-defaults", (Np50Hub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(new { error = "NP50 not connected" });
            var d = hub.GetFirmwareDefaults();
            if (d is null)
                return Results.Problem("Failed to read firmware defaults from NP50.");
            return Results.Ok(new Np50FirmwareDefaultsResponse
            {
                DefaultMode = d.Value.DefaultMode,
                StaticFanPercent = d.Value.StaticFanPercent,
                IsStartAnimationOff = d.Value.IsStartAnimationOff,
                IsFirmwareLightingOff = d.Value.IsFirmwareLightingOff,
            });
        });

        // Persist new firmware defaults to EEPROM. Read-before-write inside
        // SetFirmwareDefaults — if the hub already reports the same values
        // we do not issue a write (EEPROM endurance is ~10 000 cycles).
        app.MapPut("/devices/np50/firmware-defaults", (Np50FirmwareDefaultsRequest body, Np50Hub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(new { error = "NP50 not connected" });
            if (body.DefaultMode != Np50Protocol.DefaultModeStatic
                && body.DefaultMode != Np50Protocol.DefaultModeMotherboard)
            {
                return Results.BadRequest(new { error = "defaultMode must be 0 (Static) or 1 (Motherboard)" });
            }
            var ok = hub.SetFirmwareDefaults((byte)body.DefaultMode, (byte)Math.Clamp(body.StaticFanPercent, 0, 100));
            if (!ok)
                return Results.Problem("Failed to write firmware defaults to NP50.");
            return Results.Ok(ApiResponse.Ok());
        });

        // Read the firmware-side LED animation (effect + RGB + brightness).
        // What the strips show when the firmware is driving them — i.e. PC
        // off, service down, or hub in Software mode while nexus isn't
        // streaming frames.
        app.MapGet("/devices/np50/firmware-animation", (Np50Hub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(new { error = "NP50 not connected" });
            var a = hub.GetFirmwareAnimation();
            if (a is null)
                return Results.Problem("Failed to read firmware animation from NP50.");
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
                return Results.Conflict(new { error = "NP50 not connected" });
            if (body.Animation < Np50Protocol.FwAnimationColor
                || body.Animation > Np50Protocol.FwAnimationRainbowGradient)
            {
                return Results.BadRequest(new { error = "animation must be 1 (Color), 2 (Rainbow), 3 (Breathe), or 4 (Rainbow Gradient)" });
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
        // EEPROM-persisted default above — this is what's actively driving
        // the fans right now. SetDesiredCoolingMode also stores the value
        // so the heartbeat re-asserts it if the firmware drifts back.
        app.MapPut("/devices/np50/cooling-mode", (Np50CoolingModeRequest body, Np50Hub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(new { error = "NP50 not connected" });
            if (body.Mode != Np50Protocol.ModeSoftware
                && body.Mode != Np50Protocol.ModeMotherboard
                && body.Mode != Np50Protocol.ModeStatic)
            {
                return Results.BadRequest(new { error = "mode must be 1 (Software), 2 (Motherboard), or 3 (Static/Firmware)" });
            }
            hub.SetDesiredCoolingMode((byte)body.Mode);
            return Results.Ok(ApiResponse.Ok());
        });

        // Push an LED frame at one of the three ports. Body shape is a flat list
        // of {r,g,b} triplets in linear order; the protocol handles GRB byte
        // packing. Used by the lighting engine in later phases and by the
        // /identify route below for "which fan is this?" discovery.
        app.MapPost("/devices/np50/lighting/{port:int}", (int port, Np50LightingRequest body, Np50Hub hub) =>
        {
            if (port < 1 || port > Np50Protocol.PortCount)
                return Results.BadRequest(new { error = $"port must be in 1..{Np50Protocol.PortCount}" });
            if (!hub.IsConnected)
                return Results.Conflict(new { error = "NP50 not connected" });
            var leds = new RgbColor[body.Leds.Count];
            for (var i = 0; i < body.Leds.Count; i++)
            {
                var c = body.Leds[i];
                leds[i] = new RgbColor((byte)c.R, (byte)c.G, (byte)c.B);
            }
            hub.WriteLighting(port, leds);
            return Results.Ok();
        });

        // Debug-only: ask the hub to dump the next channel-info response for
        // a port into the service log as hex. Lets us reverse-engineer the
        // actual byte layout when the spec doc table is ambiguous.
        app.MapPost("/devices/np50/debug/dump/{port:int}", (int port, Np50Hub hub) =>
        {
            if (port < 1 || port > Np50Protocol.PortCount)
                return Results.BadRequest(new { error = $"port must be in 1..{Np50Protocol.PortCount}" });
            hub.DumpNextPortResponse(port);
            return Results.Ok(new { armed = true, port });
        });

        // Pulse every connected fan on a port with a unique color so the user
        // can physically identify which port is which. The duration is
        // intentionally short — UI calls this when the user hovers a port in
        // the cooling page; the lighting engine resumes normal output on the
        // next frame it pushes.
        app.MapPost("/devices/np50/identify/{port:int}", (int port, Np50Hub hub) =>
        {
            if (port < 1 || port > Np50Protocol.PortCount)
                return Results.BadRequest(new { error = $"port must be in 1..{Np50Protocol.PortCount}" });
            if (!hub.IsConnected)
                return Results.Conflict(new { error = "NP50 not connected" });

            // Per-port "identify" color. The hub spec caps channel 3 at 250
            // LEDs; bytes beyond that are ignored, so a 250-LED solid fill is
            // safe across all ports.
            var color = port switch
            {
                1 => new RgbColor(0xFF, 0x00, 0x00),
                2 => new RgbColor(0x00, 0xFF, 0x00),
                _ => new RgbColor(0x00, 0x00, 0xFF),
            };
            var leds = new RgbColor[250];
            for (var i = 0; i < leds.Length; i++) leds[i] = color;
            hub.WriteLighting(port, leds);
            return Results.Ok(new { color = $"#{color.R:X2}{color.G:X2}{color.B:X2}" });
        });
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
