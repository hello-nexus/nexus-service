using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;

namespace Nexus.Service.Routes;

/// <summary>
/// HYTE Q-series (Q60 / Q80) cooler-controller device endpoints - the firmware
/// options surfaced on the Q60 device page's settings tab. The singleton
/// <see cref="QSeriesCoolerHub"/> holds live state polled from Port-0; pump
/// speed itself is driven through the cooling fan-channel path
/// (<c>POST /cooling/fan/qseries:…:pump/speed</c>), so these routes cover the
/// hub-wide firmware settings: control mode and turbo.
/// </summary>
public static partial class DevicesRoutes
{
    private static void MapQSeriesCoolerEndpoints(WebApplication app)
    {
        app.MapGet("/devices/qseries", (QSeriesCoolerHub hub) =>
            Results.Ok(new QSeriesCoolerStateResponse
            {
                Connected = hub.IsConnected,
                DeviceId = hub.DeviceId,
                ProductName = hub.ProductName,
                Variant = hub.Variant,
                FirmwareVersion = hub.State.FirmwareVersion,
                PumpRpm = hub.State.PumpRpm,
                Pump2Rpm = hub.State.Pump2Rpm,
                HasPump2 = hub.State.HasPump2,
                ControlMode = hub.State.ControlMode,
                TurboOn = hub.State.TurboOn,
                FwAnimationSupported = hub.SupportsFirmwareAnimation,
                FwAnimationBrightnessSupported = hub.SupportsFirmwareAnimationBrightness,
            }));

        // Switch the hub control mode: Software (host drives), Motherboard
        // (mobo PWM), Firmware (onboard temperature curve). Motherboard and
        // Firmware are remembered as the hub's hand-back mode.
        app.MapPut("/devices/qseries/control-mode", (QSeriesControlModeRequest body, QSeriesCoolerHub hub, Nexus.Service.Persistence.IConfigStore store) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(ApiResponse.Fail("Q-series cooler not connected"));
            if (body.Mode < QSeriesCoolerProtocol.ControlModeSoftware
                || body.Mode > QSeriesCoolerProtocol.ControlModeMix)
            {
                return Results.BadRequest(ApiResponse.Fail("mode must be 1 (Software), 2 (Motherboard), 3 (Firmware), or 4 (Mix)"));
            }
            if (!hub.SetControlMode((byte)body.Mode))
                return Results.Problem("Failed to set Q-series control mode.");
            Nexus.Service.Cooling.QSeriesCoolerCoolingProvider.RecordHandBackChoice(store, hub.DeviceId, (byte)body.Mode);
            return Results.Ok(ApiResponse.Ok());
        });

        // Turbo unlocks the pump's full speed range (persisted to the MCU).
        app.MapPut("/devices/qseries/turbo", (QSeriesTurboRequest body, QSeriesCoolerHub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(ApiResponse.Fail("Q-series cooler not connected"));
            if (!hub.SetTurbo(body.On))
                return Results.Problem("Failed to set Q-series turbo.");
            return Results.Ok(ApiResponse.Ok());
        });

        // Read the firmware-driven LED animation (effect + RGB + brightness). What
        // the pump-head/strip LEDs show when the firmware is driving them directly.
        app.MapGet("/devices/qseries/firmware-animation", (QSeriesCoolerHub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(ApiResponse.Fail("Q-series cooler not connected"));
            var a = hub.TryReadFirmwareAnimation();
            if (a is null)
                return Results.Problem("Failed to read firmware animation from Q-series cooler.");
            return Results.Ok(new QSeriesFirmwareAnimationResponse
            {
                Supported = hub.SupportsFirmwareAnimation,
                Animation = a.Value.Animation,
                R = a.Value.R,
                G = a.Value.G,
                B = a.Value.B,
                Brightness = a.Value.Brightness,
            });
        });

        // Write the firmware-driven LED animation. Read-before-write + readback-verify
        // inside SetFirmwareAnimation, which sends the FF CC 02 control frame (live
        // state) before the 0x0C MCU write (stored copy) - 0x0C alone leaves Port-0
        // reporting the old animation.
        app.MapPut("/devices/qseries/firmware-animation", (QSeriesFirmwareAnimationRequest body, QSeriesCoolerHub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(ApiResponse.Fail("Q-series cooler not connected"));
            if (!hub.SupportsFirmwareAnimation)
                return Results.Conflict(ApiResponse.Fail("Firmware animation not supported on this cooler firmware"));
            if (body.Animation < QSeriesCoolerProtocol.FwAnimationColor
                || body.Animation > QSeriesCoolerProtocol.FwAnimationRainbowGradient)
            {
                return Results.BadRequest(ApiResponse.Fail("animation must be 1 (Color), 2 (Rainbow), 3 (Breathe), or 4 (Rainbow Gradient)"));
            }
            if (body.R < 0 || body.R > 255 || body.G < 0 || body.G > 255 || body.B < 0 || body.B > 255)
                return Results.BadRequest(ApiResponse.Fail("r, g, and b must be 0-255"));
            if (body.Brightness < 0 || body.Brightness > 100)
                return Results.BadRequest(ApiResponse.Fail("brightness must be 0-100"));
            var ok = hub.SetFirmwareAnimation(
                (byte)body.Animation, (byte)body.R, (byte)body.G, (byte)body.B, (byte)body.Brightness);
            if (!ok)
                return Results.Problem("Failed to write firmware animation to Q-series cooler.");
            return Results.Ok(ApiResponse.Ok());
        });

        // Read the device's stored firmware temperature curve (pump + fan). Used by
        // the Q60 settings tab to seed the two curve editors. `supported` is false on
        // older firmware that lacks the 5-point format.
        app.MapGet("/devices/qseries/firmware-curve", (QSeriesCoolerHub hub) =>
        {
            var supported = hub.SupportsFirmwareCurve;
            var resp = new QSeriesFirmwareCurveResponse
            {
                Connected = hub.IsConnected,
                Supported = supported,
                Variant = hub.Variant,
                TempMin = QSeriesCoolerProtocol.FirmwareCurveTempMin,
                TempMax = QSeriesCoolerProtocol.FirmwareCurveTempMax,
            };
            if (supported && hub.TryReadFirmwareCurve(out var points))
            {
                foreach (var p in points)
                {
                    resp.Pump.Add(new QSeriesCurvePointDto { TempC = p.PumpTempC, DutyPercent = p.PumpDutyPercent });
                    resp.Fan.Add(new QSeriesCurvePointDto { TempC = p.FanTempC, DutyPercent = p.FanDutyPercent });
                }
            }
            return Results.Ok(resp);
        });

        // Persist a new firmware temperature curve. Pump and fan are independent
        // 5-point curves; each is sorted by temperature before being zipped into the
        // device's combined slot array. Does not change the active control mode.
        app.MapPut("/devices/qseries/firmware-curve", (QSeriesFirmwareCurveRequest body, QSeriesCoolerHub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(ApiResponse.Fail("Q-series cooler not connected"));
            if (!hub.SupportsFirmwareCurve)
                return Results.Conflict(ApiResponse.Fail("Firmware curve not supported on this cooler firmware"));
            var n = QSeriesCoolerProtocol.FirmwareCurvePointCount;
            if (body.Pump.Count != n || body.Fan.Count != n)
                return Results.BadRequest(ApiResponse.Fail($"pump and fan each require exactly {n} points"));

            int ClampTemp(int c) => Math.Clamp(c, QSeriesCoolerProtocol.FirmwareCurveTempMin, QSeriesCoolerProtocol.FirmwareCurveTempMax);
            int ClampDuty(int d) => Math.Clamp(d, 0, 100);
            var pump = body.Pump.OrderBy(p => p.TempC).ToArray();
            var fan = body.Fan.OrderBy(p => p.TempC).ToArray();
            var points = new QSeriesFirmwareCurvePoint[n];
            for (var i = 0; i < n; i++)
            {
                points[i] = new QSeriesFirmwareCurvePoint
                {
                    PumpTempC = ClampTemp(pump[i].TempC),
                    PumpDutyPercent = ClampDuty(pump[i].DutyPercent),
                    FanTempC = ClampTemp(fan[i].TempC),
                    FanDutyPercent = ClampDuty(fan[i].DutyPercent),
                };
            }
            if (!hub.WriteFirmwareCurve(points))
                return Results.Problem("Failed to write Q-series firmware curve.");
            return Results.Ok(ApiResponse.Ok());
        });

        // Reboot the panel's Android side. Returns once queued: the tick loop owns
        // the transport, and the cold qshell bootstrap that follows takes ~2 min over
        // USB-FFS. Clients track completion via the device record's lastSeenAt.
        app.MapPost("/devices/qseries/reboot", (IServiceProvider sp) =>
        {
            var watcher = sp.GetService<Nexus.Service.QSeries.QSeriesPortWatcher>();
            if (watcher is null)
                return Results.Conflict(ApiResponse.Fail("Q-series watcher not running"));
            var registry = sp.GetService<Nexus.Service.Common.ExternalTools.IAdbDeviceRegistry>();
            var device = registry?.TryGet(Nexus.Service.Devices.Firmware.ApkFlasher.QshellPackage);
            if (device is null)
                return Results.Conflict(ApiResponse.Fail("No Q-series panel is connected."));
            // The gate spans the whole flash; InstallInProgress covers only the
            // install, so a reboot queued during the download or the set-home tail
            // would land mid-flash and leave the panel unpinned from HOME.
            if (sp.GetService<Nexus.Service.Devices.Firmware.FlashGate>()?.IsFlashing == true || device.InstallInProgress)
                return Results.Conflict(ApiResponse.Fail("A panel update is in progress; try again once it finishes."));
            watcher.RequestReboot();
            return Results.Accepted(value: ApiResponse.Ok());
        });
    }
}

/// <summary>Shape returned by GET /devices/qseries. AOT-registered in <see cref="Serialization.AppJsonContext"/>.</summary>
public sealed class QSeriesCoolerStateResponse
{
    public bool Connected { get; set; }
    public string DeviceId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Variant { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
    public int PumpRpm { get; set; }
    public int Pump2Rpm { get; set; }
    public bool HasPump2 { get; set; }
    /// <summary>1=Software, 2=Motherboard, 3=Firmware, 4=Mix.</summary>
    public int ControlMode { get; set; }
    public bool TurboOn { get; set; }
    /// <summary>False on firmware too old for the FF CC 0C firmware-animation write.</summary>
    public bool FwAnimationSupported { get; set; }
    /// <summary>False on firmware too old for the firmware-animation brightness field.</summary>
    public bool FwAnimationBrightnessSupported { get; set; }
}

/// <summary>Body for PUT /devices/qseries/control-mode.</summary>
public sealed class QSeriesControlModeRequest
{
    public int Mode { get; set; }
}

/// <summary>Body for PUT /devices/qseries/turbo.</summary>
public sealed class QSeriesTurboRequest
{
    public bool On { get; set; }
}

/// <summary>Shape returned by GET /devices/qseries/firmware-animation.</summary>
public sealed class QSeriesFirmwareAnimationResponse
{
    /// <summary>False on firmware below the animation gate; the bytes are then raw Port-0 state, not a settable animation.</summary>
    public bool Supported { get; set; }
    /// <summary>1=Color, 2=Rainbow, 3=Breathe, 4=Rainbow Gradient.</summary>
    public byte Animation { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public byte Brightness { get; set; }
}

/// <summary>Body for PUT /devices/qseries/firmware-animation.</summary>
public sealed class QSeriesFirmwareAnimationRequest
{
    public int Animation { get; set; }
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
    public int Brightness { get; set; }
}

/// <summary>One firmware-curve point: coolant temperature (°C) → duty (%).</summary>
public sealed class QSeriesCurvePointDto
{
    public int TempC { get; set; }
    public int DutyPercent { get; set; }
}

/// <summary>Shape returned by GET /devices/qseries/firmware-curve.</summary>
public sealed class QSeriesFirmwareCurveResponse
{
    public bool Connected { get; set; }
    /// <summary>False on firmware too old for the 5-point curve format.</summary>
    public bool Supported { get; set; }
    public string Variant { get; set; } = "";
    public int TempMin { get; set; }
    public int TempMax { get; set; }
    public List<QSeriesCurvePointDto> Pump { get; set; } = new();
    public List<QSeriesCurvePointDto> Fan { get; set; } = new();
}

/// <summary>Body for PUT /devices/qseries/firmware-curve.</summary>
public sealed class QSeriesFirmwareCurveRequest
{
    public List<QSeriesCurvePointDto> Pump { get; set; } = new();
    public List<QSeriesCurvePointDto> Fan { get; set; } = new();
}
