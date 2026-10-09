using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapCorsairEndpoints(WebApplication app)
    {
        // GET /devices/corsair/state - every hub's connection, firmware, and
        // auto-detected device topology with live RPM/temperature. The top-level
        // fields mirror hubs[0] for single-hub clients.
        app.MapGet("/devices/corsair/state", (CorsairLinkHubs hubs) =>
        {
            var list = new List<CorsairHubDto>();
            foreach (var hub in hubs.All) list.Add(ToDto(hub));
            var first = list.Count > 0 ? list[0] : null;
            return Results.Json(new CorsairStateResponse
            {
                IsConnected = first?.IsConnected ?? false,
                Firmware = first?.Firmware ?? "",
                Redetecting = first?.Redetecting ?? false,
                Devices = first?.Devices ?? Array.Empty<CorsairDeviceDto>(),
                Hubs = list.ToArray(),
            }, AppJsonContext.Default.CorsairStateResponse);
        });

        // POST /devices/corsair/rescan {hubId?} - re-detect a hub's chain (all hubs
        // when hubId is omitted). 202 queued, 404 unknown hub, 409 not connected or
        // already re-detecting.
        app.MapPost("/devices/corsair/rescan", async (HttpContext ctx, CorsairLinkHubs hubs) =>
        {
            CorsairRescanRequest? body = null;
            if (ctx.Request.ContentLength is > 0)
            {
                try
                {
                    body = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AppJsonContext.Default.CorsairRescanRequest, ctx.RequestAborted);
                }
                catch (Exception e) when (e is JsonException or BadHttpRequestException)
                {
                    return Results.Json(ApiResponse.Fail("body is malformed"), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            var targets = new List<CorsairLinkHub>();
            if (string.IsNullOrEmpty(body?.HubId))
            {
                targets.AddRange(hubs.All);
            }
            else if (hubs.Find(body.HubId) is { } hub)
            {
                targets.Add(hub);
            }
            else
            {
                return Results.Json(ApiResponse.Fail("unknown hub"), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status404NotFound);
            }

            var accepted = false;
            foreach (var hub in targets) accepted |= hub.RequestRedetect();
            return accepted
                ? Results.Json(new CorsairRescanResponse { Accepted = true }, AppJsonContext.Default.CorsairRescanResponse, statusCode: StatusCodes.Status202Accepted)
                : Results.Json(ApiResponse.Fail("hub not connected or already re-detecting"), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status409Conflict);
        });
    }

    private static CorsairHubDto ToDto(CorsairLinkHub hub)
    {
        var devices = new List<CorsairDeviceDto>();
        foreach (var d in hub.State.Devices)
        {
            devices.Add(new CorsairDeviceDto
            {
                Channel = d.Channel,
                Name = d.Name,
                DeviceClass = d.Class.ToString(),
                LedCount = d.LedCount,
                HasSpeed = d.HasSpeed,
                HasTemperature = d.HasTemperature,
                Rpm = d.Rpm,
                TempC = float.IsNaN(d.TempC) ? null : d.TempC,
                Serial = d.Serial,
            });
        }
        return new CorsairHubDto
        {
            Id = hub.HubId,
            Number = hub.Number,
            IsConnected = hub.IsConnected,
            Firmware = hub.State.Firmware,
            Redetecting = hub.Redetecting || hub.RedetectRequested,
            Devices = devices.ToArray(),
        };
    }
}

public sealed class CorsairStateResponse
{
    public bool IsConnected { get; set; }
    public string Firmware { get; set; } = "";
    public bool Redetecting { get; set; }
    public CorsairDeviceDto[] Devices { get; set; } = Array.Empty<CorsairDeviceDto>();
    public CorsairHubDto[] Hubs { get; set; } = Array.Empty<CorsairHubDto>();
}

public sealed class CorsairHubDto
{
    /// <summary>"corsair" for the first hub, "corsair:&lt;serial&gt;" for the others.</summary>
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public bool IsConnected { get; set; }
    public string Firmware { get; set; } = "";
    public bool Redetecting { get; set; }
    public CorsairDeviceDto[] Devices { get; set; } = Array.Empty<CorsairDeviceDto>();
}

public sealed class CorsairRescanRequest
{
    public string? HubId { get; set; }
}

public sealed class CorsairRescanResponse
{
    public bool Accepted { get; set; }
}

public sealed class CorsairDeviceDto
{
    public int Channel { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Fan | Aio | Pump | CpuBlock | GpuBlock | Case | Adapter | Other.</summary>
    public string DeviceClass { get; set; } = "";
    public int LedCount { get; set; }
    public bool HasSpeed { get; set; }
    public bool HasTemperature { get; set; }
    public int Rpm { get; set; }
    public float? TempC { get; set; }
    /// <summary>Hub-assigned device serial; disambiguates otherwise-identical fans.</summary>
    public string Serial { get; set; } = "";
}
