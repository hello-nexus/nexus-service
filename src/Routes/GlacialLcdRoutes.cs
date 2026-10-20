using System;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static readonly string[] GlacialSensorCategories = { "cpu", "gpu", "motherboard" };

    private static void MapGlacialLcdEndpoints(WebApplication app)
    {
        app.MapGet("/devices/asus-glacial-lcd/state", (IConfigStore store, GlacialLcdWorker worker) =>
            Results.Json(BuildGlacialState(store.Load().Devices.GlacialLcd, worker.IsConnected), AppJsonContext.Default.GlacialLcdStateResponse));

        app.MapPut("/devices/asus-glacial-lcd/settings", (GlacialLcdSettingsRequest body, IConfigStore store, GlacialLcdWorker worker) =>
        {
            if (body.Mode is not null
                && body.Mode is not (GlacialLcdSettings.ModeStatic or GlacialLcdSettings.ModeAnimated or GlacialLcdSettings.ModeMonitor))
            {
                return Results.BadRequest(ApiResponse.Fail("unknown mode"));
            }
            if (body.StaticAsset is int sa && !GlacialLcdProtocol.StaticAssets.Contains(sa))
            {
                return Results.BadRequest(ApiResponse.Fail("unknown static asset"));
            }
            if (body.AnimatedAsset is int aa && !GlacialLcdProtocol.AnimatedAssets.Contains(aa))
            {
                return Results.BadRequest(ApiResponse.Fail("unknown animated asset"));
            }
            if (body.SlotCount is int sc && (sc < 1 || sc > GlacialLcdProtocol.MaxMonitorSlots))
            {
                return Results.BadRequest(ApiResponse.Fail("slot count out of range"));
            }
            if (body.Slots is { } slots
                && (slots.Length > GlacialLcdProtocol.MaxMonitorSlots
                    || slots.Any(s => s.Category.Length > 0 && !GlacialSensorCategories.Contains(s.Category))))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid slots"));
            }
            store.Update(s =>
            {
                var g = s.Devices.GlacialLcd;
                if (body.DisplayOn.HasValue) { g.DisplayOn = body.DisplayOn.Value; }
                if (body.Mode is not null) { g.Mode = body.Mode; }
                if (body.StaticAsset.HasValue) { g.StaticAsset = body.StaticAsset.Value; }
                if (body.AnimatedAsset.HasValue) { g.AnimatedAsset = body.AnimatedAsset.Value; }
                if (body.SlotCount.HasValue) { g.SlotCount = body.SlotCount.Value; }
                if (body.Brightness.HasValue) { g.Brightness = Math.Clamp(body.Brightness.Value, 0, 100); }
                if (body.StayOnWhileAsleep.HasValue) { g.StayOnWhileAsleep = body.StayOnWhileAsleep.Value; }
                if (body.Slots is { } slots)
                {
                    while (g.Slots.Count < GlacialLcdProtocol.MaxMonitorSlots)
                    {
                        g.Slots.Add(new GlacialLcdSlot());
                    }
                    for (int i = 0; i < slots.Length; i++)
                    {
                        g.Slots[i] = new GlacialLcdSlot { Category = slots[i].Category, Sensor = slots[i].Sensor, Label = slots[i].Label };
                    }
                }
            });
            worker.RequestApply();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    private static GlacialLcdStateResponse BuildGlacialState(GlacialLcdSettings g, bool connected) => new()
    {
        IsConnected = connected,
        DisplayOn = g.DisplayOn,
        Mode = g.Mode,
        StaticAsset = g.StaticAsset,
        AnimatedAsset = g.AnimatedAsset,
        SlotCount = g.SlotCount,
        Slots = Enumerable.Range(0, GlacialLcdProtocol.MaxMonitorSlots)
            .Select(i => i < g.Slots.Count ? g.Slots[i] : new GlacialLcdSlot())
            .Select(s => new GlacialLcdSlotDto { Category = s.Category, Sensor = s.Sensor, Label = s.Label })
            .ToArray(),
        Brightness = g.Brightness,
        StayOnWhileAsleep = g.StayOnWhileAsleep,
        StaticAssets = GlacialLcdProtocol.StaticAssets,
        AnimatedAssets = GlacialLcdProtocol.AnimatedAssets,
    };
}

public sealed class GlacialLcdSlotDto
{
    public string Category { get; set; } = "";
    public string Sensor { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class GlacialLcdStateResponse
{
    public bool IsConnected { get; set; }
    public bool DisplayOn { get; set; }
    /// <summary>"static", "animated" or "monitor".</summary>
    public string Mode { get; set; } = "";
    public int StaticAsset { get; set; }
    public int AnimatedAsset { get; set; }
    public int SlotCount { get; set; }
    public GlacialLcdSlotDto[] Slots { get; set; } = Array.Empty<GlacialLcdSlotDto>();
    public int Brightness { get; set; }
    public bool StayOnWhileAsleep { get; set; }
    public int[] StaticAssets { get; set; } = Array.Empty<int>();
    public int[] AnimatedAssets { get; set; } = Array.Empty<int>();
}

public sealed class GlacialLcdSettingsRequest
{
    public bool? DisplayOn { get; set; }
    public string? Mode { get; set; }
    public int? StaticAsset { get; set; }
    public int? AnimatedAsset { get; set; }
    public int? SlotCount { get; set; }
    /// <summary>Replaces widgets by index; fewer entries leave the rest as they were.</summary>
    public GlacialLcdSlotDto[]? Slots { get; set; }
    public int? Brightness { get; set; }
    public bool? StayOnWhileAsleep { get; set; }
}
