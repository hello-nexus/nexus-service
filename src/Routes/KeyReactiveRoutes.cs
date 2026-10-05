using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.KeyReactive;
using Nexus.Service.Models;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>Key reactions: per-keyboard config, simulated presses, and the dashboard's preview loop.</summary>
public static class KeyReactiveRoutes
{
    private const int MaxKeyLength = 32;

    public static void MapKeyReactiveEndpoints(this WebApplication app)
    {
        app.MapGet("/lighting/key-reactive", (KeyReactiveOverlay overlay, LightingEngine engine) =>
        {
            var response = new KeyReactiveStateResponse { InputAvailable = overlay.InputAvailable?.Invoke() == true };
            foreach (var board in KeyReactiveOverlay.Keyboards(engine.Devices))
            {
                response.Devices.Add(new KeyReactiveDeviceDto
                {
                    Id = board.Id,
                    DeviceId = board.DeviceId,
                    FrameIndex = board.FrameIndex,
                    HardwareKeys = overlay.HasHardwareKeySource(board.DeviceId),
                    LedCount = board.LedCount,
                    NamedKeys = board.NamedKeys,
                    Config = overlay.Get(board.DeviceId),
                });
            }
            return Results.Json(response, AppJsonContext.Default.KeyReactiveStateResponse);
        });

        // Takes a card id and stores under its device, so every card of a board shares one config.
        app.MapPut("/lighting/key-reactive/{id}", (string id, KeyReaction body, KeyReactiveOverlay overlay, LightingEngine engine) =>
        {
            if (FindKeyboard(engine, id) is not { } frame) return NotAKeyboard();
            return Results.Json(overlay.Set(KeyReactiveOverlay.ConfigKey(frame), body), AppJsonContext.Default.KeyReaction);
        });

        app.MapPost("/lighting/key-reactive/{id}/press", (string id, KeyReactivePressBody? body, KeyReactiveOverlay overlay, LightingEngine engine) =>
        {
            if (FindKeyboard(engine, id) is null) return NotAKeyboard();
            var key = body?.Key;
            if (key is { Length: > MaxKeyLength }) return Results.BadRequest(ApiResponse.Fail("key too long"));
            overlay.PressOn(id, string.IsNullOrWhiteSpace(key) ? null : key, body?.Led);
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapPost("/lighting/key-reactive/{id}/preview", (string id, KeyReaction body, LightingEngine engine) =>
        {
            if (FindKeyboard(engine, id) is not { } frame || KeyReactiveOverlay.GeometryOf(frame) is not { } geo)
            {
                return NotAKeyboard();
            }
            return Results.Json(KeyReactionPreview.Render(geo, body), AppJsonContext.Default.KeyReactivePreviewResponse);
        });
    }

    private static DeviceFrame? FindKeyboard(LightingEngine engine, string id)
    {
        foreach (var frame in engine.Devices)
        {
            if (frame.Id == id) return KeyReactiveOverlay.GeometryOf(frame) is null ? null : frame;
        }
        return null;
    }

    private static IResult NotAKeyboard() =>
        Results.NotFound(ApiResponse.Fail("not a per-key keyboard"));
}
