using Nexus.Service.Hotkeys;
using Nexus.Service.Models;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// Global hotkey bindings. Desktop token only: none of these routes is marked
/// AllowPanel, so a panel or phone session gets 403 from the auth middleware.
/// Every successful call returns the same body as GET /hotkeys, which is also
/// what the "hotkeys" WebSocket topic carries.
/// </summary>
public static class HotkeyRoutes
{
    public static void MapHotkeyEndpoints(this WebApplication app)
    {
        app.MapGet("/hotkeys", (HotkeyService hotkeys) =>
            Results.Json(hotkeys.BuildResponse(), AppJsonContext.Default.HotkeysResponse));

        app.MapPut("/hotkeys/{id}", async (string id, HotkeyBinding body, HotkeyService hotkeys) =>
        {
            if (!string.IsNullOrEmpty(body.Id) && body.Id != id)
            {
                return Fail("id in body does not match path", 400);
            }
            body.Id = id;
            if (HotkeyService.Validate(body) is { } error)
            {
                return Fail(error, 400);
            }
            if (!await hotkeys.UpsertAsync(body).ConfigureAwait(false))
            {
                return Fail("too many hotkeys", 409);
            }
            return Results.Json(hotkeys.BuildResponse(), AppJsonContext.Default.HotkeysResponse);
        });

        app.MapDelete("/hotkeys/{id}", async (string id, HotkeyService hotkeys) =>
        {
            if (!await hotkeys.DeleteAsync(id).ConfigureAwait(false))
            {
                return Fail("hotkey not found", 404);
            }
            return Results.Json(hotkeys.BuildResponse(), AppJsonContext.Default.HotkeysResponse);
        });

        app.MapPost("/hotkeys/reset-defaults", async (HotkeyService hotkeys) =>
        {
            await hotkeys.ResetDefaultsAsync().ConfigureAwait(false);
            return Results.Json(hotkeys.BuildResponse(), AppJsonContext.Default.HotkeysResponse);
        });

        app.MapPost("/hotkeys/suspend", async (HotkeySuspendBody body, HotkeyService hotkeys) =>
        {
            await hotkeys.SetSuspendedAsync(body.Suspended).ConfigureAwait(false);
            return Results.Json(hotkeys.BuildResponse(), AppJsonContext.Default.HotkeysResponse);
        });
    }

    private static IResult Fail(string message, int status)
        => Results.Json(ApiResponse.Fail(message), AppJsonContext.Default.ApiResponse, statusCode: status);
}
