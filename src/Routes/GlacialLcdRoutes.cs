using System;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static readonly string[] GlacialSensorCategories = { "cpu", "gpu", "motherboard" };

    private static void MapGlacialLcdEndpoints(WebApplication app)
    {
        app.MapGet("/devices/asus-glacial-lcd/state", (IConfigStore store, GlacialLcdWorker worker, ISensorProvider sensors) =>
            Results.Json(BuildGlacialState(ReadGlacial(store), worker.IsConnected, sensors, NexusPanelOn(store)), AppJsonContext.Default.GlacialLcdStateResponse));

        app.MapPut("/devices/asus-glacial-lcd/settings", (GlacialLcdSettingsRequest body, IConfigStore store, GlacialLcdWorker worker, GlacialLcdLink link, IServiceProvider services) =>
        {
            if (body.Mode is not null
                && body.Mode is not (GlacialLcdSettings.ModeStatic or GlacialLcdSettings.ModeAnimated or GlacialLcdSettings.ModeMonitor or GlacialLcdSettings.ModePanel))
            {
                return Results.BadRequest(ApiResponse.Fail("unknown mode"));
            }
            if (body.Mode == GlacialLcdSettings.ModePanel && !NexusPanelOn(store))
            {
                return Results.BadRequest(ApiResponse.Fail("nexus panel mode is unavailable"));
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
                    || slots.Any(s => s is null || s.Category is null || s.Sensor is null || s.Label is null
                        || (s.Category.Length > 0 && !GlacialSensorCategories.Contains(s.Category))
                        || (s.Category.Length == 0 && s.Sensor.Length > 0))))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid slots"));
            }
            store.Update(s => ApplyGlacialRequest(s.Devices.GlacialLcd, body));
            if (body.Mode is not null)
            {
                // Leaving the Nexus Panel mode stops a running upload now and closes the session on
                // this pass instead of the coordinator's next poll; choosing it lets the stream start.
                link.StreamWanted = body.Mode == GlacialLcdSettings.ModePanel;
                if (!link.StreamWanted)
                {
                    link.RequestCancel();
                }
                services.GetService<Nexus.Service.Panel.Streams.StreamedPanelCoordinator>()?.Wake();
            }
            worker.RequestApply();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    /// <summary>Applies a settings request to the saved state; separate from the route so it can be tested.</summary>
    internal static void ApplyGlacialRequest(GlacialLcdSettings g, GlacialLcdSettingsRequest body)
    {
        bool wasChosen = g.ModeChosen;
        string keyBefore = GlacialLcdScreen.ModeKeyOf(g.Copy());
        // Any change hands the panel to Nexus; each field the user sets is from then on written.
        g.UserEdited = true;
        if (body.Mode is not null || body.StaticAsset.HasValue || body.AnimatedAsset.HasValue)
        {
            g.ModeChosen = true;
        }
        if (body.DisplayOn.HasValue) { g.DisplayOn = body.DisplayOn.Value; g.DisplayEdited = true; }
        if (body.Mode is not null) { g.Mode = body.Mode; }
        if (body.StaticAsset.HasValue) { g.StaticAsset = body.StaticAsset.Value; }
        if (body.AnimatedAsset.HasValue) { g.AnimatedAsset = body.AnimatedAsset.Value; }
        if (body.SlotCount.HasValue) { g.SlotCount = body.SlotCount.Value; }
        if (body.Brightness.HasValue) { g.Brightness = Math.Clamp(body.Brightness.Value, 0, 100); g.BrightnessEdited = true; }
        if (body.StayOnWhileAsleep.HasValue) { g.StayOnWhileAsleep = body.StayOnWhileAsleep.Value; g.SleepEdited = true; }
        if (body.Slots is { } slots)
        {
            g.Slots = g.Copy().Slots;
            while (g.Slots.Count < GlacialLcdProtocol.MaxMonitorSlots)
            {
                g.Slots.Add(new GlacialLcdSlot());
            }
            for (int i = 0; i < slots.Length; i++)
            {
                g.Slots[i] = new GlacialLcdSlot { Category = slots[i].Category, Sensor = slots[i].Sensor, Label = slots[i].Label };
            }
        }
        // Turning the display on with no mode ever chosen selects the vendor's first-run animation.
        if (body.DisplayOn == true && !g.ModeChosen)
        {
            g.ModeChosen = true;
            g.Mode = GlacialLcdSettings.ModeAnimated;
            g.AnimatedAsset = GlacialLcdSettings.FirstRunAnimation;
        }
        // Pending only when the panel is not already known to hold this content: a mode
        // newly chosen, or a changed key. An unchanged key leaves the flag as it was.
        if (g.ModeChosen && g.Mode == GlacialLcdSettings.ModePanel)
        {
            // The upload owns this mode; there is nothing for the native side to select.
            g.ModePending = false;
        }
        else if (g.ModeChosen && (!wasChosen || GlacialLcdScreen.ModeKeyOf(g.Copy()) != keyBefore))
        {
            g.ModePending = true;
        }
    }

    private static GlacialLcdSettings ReadGlacial(IConfigStore store)
    {
        GlacialLcdSettings copy = new();
        store.Read(s => copy = s.Devices.GlacialLcd.Copy());
        return copy;
    }

    private static bool NexusPanelOn(IConfigStore store)
    {
        bool on = false;
        store.Read(s => on = s.Devices.GlacialLcdNexusPanel);
        return on;
    }

    internal static GlacialLcdStateResponse BuildGlacialState(GlacialLcdSettings g, bool connected, ISensorProvider sensors, bool panelModeAvailable) => new()
    {
        PanelModeAvailable = panelModeAvailable,
        IsConnected = connected,
        ModeChosen = g.ModeChosen,
        DisplayOn = g.DisplayOn,
        Mode = g.Mode,
        StaticAsset = g.StaticAsset,
        AnimatedAsset = g.AnimatedAsset,
        SlotCount = g.SlotCount,
        Slots = Enumerable.Range(0, GlacialLcdProtocol.MaxMonitorSlots)
            .Select(i => (Slot: i < g.Slots.Count ? g.Slots[i] : new GlacialLcdSlot(), Index: i))
            .Select(x => new GlacialLcdSlotDto
            {
                Category = x.Slot.Category,
                Sensor = x.Slot.Sensor,
                Label = x.Slot.Label,
                ResolvedCategory = x.Slot.Sensor.Length > 0 ? x.Slot.Category : GlacialLcdSensors.DefaultCategory(x.Slot, x.Index),
                ResolvedSensor = GlacialLcdSensors.Find(sensors, x.Slot, x.Index)?.Id ?? "",
            })
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

    /// <summary>Response only: the category the widget reads, which for an unpicked widget is its default.</summary>
    public string ResolvedCategory { get; set; } = "";

    /// <summary>Response only: the sensor id the widget reads, empty when none resolves.</summary>
    public string ResolvedSensor { get; set; } = "";
}

public sealed class GlacialLcdStateResponse
{
    public bool IsConnected { get; set; }
    /// <summary>False when the Nexus panel mode is off for this display, so the page offers only the native modes.</summary>
    public bool PanelModeAvailable { get; set; }
    public bool DisplayOn { get; set; }
    /// <summary>False until the user has picked a mode, when the mode, asset and widget fields are placeholders and the panel's actual mode is unknown.</summary>
    public bool ModeChosen { get; set; }
    /// <summary>"static", "animated", "monitor" or "panel" (a Nexus Panel streams to the display).</summary>
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
