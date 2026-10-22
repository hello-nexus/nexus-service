using Nexus.Service.Hotkeys;
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
                return Fail(HotkeyErrorCodes.IdMismatch, "id in body does not match path", 400);
            }
            body.Id = id;
            if (HotkeyService.Validate(body) is { } error)
            {
                return Fail(error.Code, error.Msg, 400);
            }
            if (!await hotkeys.UpsertAsync(body).ConfigureAwait(false))
            {
                return Fail(HotkeyErrorCodes.TooMany, "too many hotkeys", 409);
            }
            return Results.Json(hotkeys.BuildResponse(), AppJsonContext.Default.HotkeysResponse);
        });

        app.MapDelete("/hotkeys/{id}", async (string id, HotkeyService hotkeys) =>
        {
            if (!await hotkeys.DeleteAsync(id).ConfigureAwait(false))
            {
                return Fail(HotkeyErrorCodes.NotFound, "hotkey not found", 404);
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

    private static IResult Fail(string code, string message, int status)
        => Results.Json(new HotkeyErrorResponse { Msg = message, Code = code }, AppJsonContext.Default.HotkeyErrorResponse, statusCode: status);
}
