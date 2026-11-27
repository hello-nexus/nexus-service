using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// The Uni Fan TL device's Screens tab: its wired LCD screens on the same content model and
/// request shapes as the SL wireless screens. Media comes from the shared fan-LCD library at
/// /devices/lianli-wireless/media. These screens have no backlight control.
/// </summary>
public static class TlLcdRoutes
{
    /// <summary>Saved order first, then place on the controller (port, then fan), then key; stable for an unordered set.</summary>
    internal static List<TlLcdScreen> OrderScreens(
        IReadOnlyList<TlLcdScreen> screens, IReadOnlyDictionary<string, LianLiWirelessScreenSettings> settings)
    {
        var ordered = new List<TlLcdScreen>(screens);
        ordered.Sort((a, b) =>
        {
            int orderA = settings.TryGetValue(a.Key, out var sa) && sa.Order >= 0 ? sa.Order : int.MaxValue;
            int orderB = settings.TryGetValue(b.Key, out var sb) && sb.Order >= 0 ? sb.Order : int.MaxValue;
            if (orderA != orderB)
            {
                return orderA.CompareTo(orderB);
            }
            int place = Position(a.Handshake.Identity).CompareTo(Position(b.Handshake.Identity));
            return place != 0 ? place : string.CompareOrdinal(a.Key, b.Key);
        });
        return ordered;
    }

    /// <summary>Port-major place on the controller, or int.MaxValue when the screen did not say.</summary>
    private static int Position(TlLcdIdentity identity) =>
        identity.Port >= 0 && identity.Index >= 0 ? (identity.Port * 3) + identity.Index : int.MaxValue;

    public static void MapTlLcdEndpoints(this WebApplication app)
    {
        app.MapGet("/devices/lianli-tl/screens", (TlLcdPanels panels, TlLcdScreens screens, IConfigStore store, StreamedPanelStore panelStore) =>
        {
            var screenSettings = store.Load().Devices.TlLcdScreens;
            var panelRecords = panelStore.Load();
            var response = new Slv3LcdScreensResponse();
            foreach (var screen in OrderScreens(panels.Attached, screenSettings))
            {
                var settings = screenSettings.TryGetValue(screen.Key, out var s) ? s : new LianLiWirelessScreenSettings();
                var identity = screen.Handshake.Identity;
                int position = Position(identity);
                response.Screens.Add(new Slv3LcdScreenDto
                {
                    Serial = screen.Key,
                    Position = position == int.MaxValue ? -1 : position,
                    Label = TlLcdPanels.LabelFor(identity),
                    Width = FanLcdContent.Size,
                    Height = FanLcdContent.Size,
                    Order = settings.Order,
                    FpsCap = FanLcdClipTiming.CapOf(settings.FpsCap),
                    Brightness = 0,
                    SleepWhenLocked = false,
                    Rotation = settings.Rotation,
                    NexusPanel = FanLcdContent.IsPanel(settings),
                    ContentType = FanLcdContent.ContentOf(settings),
                    MediaId = settings.MediaId,
                    SensorSource = settings.SensorSource,
                    SensorStyle = settings.SensorStyle,
                    ClockFace = settings.ClockFace,
                    AnimationId = settings.AnimationId,
                    ColorA = settings.ColorA,
                    ColorB = settings.ColorB,
                    TempUnit = settings.TempUnit,
                    PanelDeviceId = Slv3LcdRoutes.PanelDeviceIdFor(panelRecords, screens, screen.Key, settings),
                });
            }
            return Results.Json(response, AppJsonContext.Default.Slv3LcdScreensResponse);
        });

        // Rotation (sent with the screen's next Nexus frame) and the Nexus panel switch.
        app.MapPost("/devices/lianli-tl/screen/settings", (Slv3LcdSettingsRequest body, IConfigStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Serial))
            {
                return Results.Json(ApiResponse.Fail("missing serial"), AppJsonContext.Default.ApiResponse);
            }
            if (body.Brightness.HasValue || body.SleepWhenLocked.HasValue)
            {
                return Results.Json(ApiResponse.Fail("this screen has no backlight control"), AppJsonContext.Default.ApiResponse);
            }
            if (body.Rotation is > 3)
            {
                return Results.Json(ApiResponse.Fail("rotation must be 0-3"), AppJsonContext.Default.ApiResponse);
            }
            if (body.FpsCap is < 0)
            {
                return Results.Json(ApiResponse.Fail("frame rate must be 0 or more"), AppJsonContext.Default.ApiResponse);
            }
            store.Update(s =>
            {
                var screen = ScreenOf(s.Devices, body.Serial);
                if (body.Rotation is { } rotation)
                {
                    screen.Rotation = rotation;
                }
                if (body.NexusPanel is { } nexusPanel)
                {
                    Slv3LcdRoutes.ApplyNexusPanel(screen, nexusPanel);
                }
                if (body.FpsCap is { } fpsCap)
                {
                    screen.FpsCap = FanLcdClipTiming.ToStored(fpsCap);
                }
            });
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-tl/screens/order", (Slv3LcdOrderRequest body, IConfigStore store) =>
        {
            var serials = body.Serials ?? new List<string>();
            if (Slv3LcdRoutes.ValidateOrder(serials) is { } orderError)
            {
                return Results.Json(ApiResponse.Fail(orderError), AppJsonContext.Default.ApiResponse);
            }
            store.Update(s =>
            {
                for (var i = 0; i < serials.Count; i++)
                {
                    ScreenOf(s.Devices, serials[i]).Order = i;
                }
            });
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-tl/screen/content", (Slv3LcdContentRequest body, IConfigStore store) =>
        {
            if (Slv3LcdRoutes.ValidateContent(body) is { } contentError)
            {
                return Results.Json(ApiResponse.Fail(contentError), AppJsonContext.Default.ApiResponse);
            }
            store.Update(s => Slv3LcdRoutes.ApplyContent(ScreenOf(s.Devices, body.Serial), body));
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    private static LianLiWirelessScreenSettings ScreenOf(DevicesSettings devices, string serial)
    {
        if (!devices.TlLcdScreens.TryGetValue(serial, out var screen))
        {
            screen = new LianLiWirelessScreenSettings();
            devices.TlLcdScreens[serial] = screen;
        }
        return screen;
    }
}
