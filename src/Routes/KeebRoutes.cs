using Qos.Service.Models;
using Qos.Service.Models.Peripherals.Keeb;
using Qos.Service.Peripherals.Keeb;

namespace Qos.Service.Routes;

public static class KeebRoutes
{
    public static void MapKeebEndpoints(this WebApplication app)
    {
        app.MapGet("/keeb/state", (IKeebProvider k, int? layer) => k.GetState(layer ?? 0));
        app.MapGet("/keeb/layer/{layer:int}", (int layer, IKeebProvider k) =>
            new KeyboardState
            {
                IsConnected = k.GetState(layer).IsConnected,
                Profile = 0,
                Layout = k.GetState(layer).Layout,
                Layer = layer,
                Keys = k.GetLayer(layer),
            });
        app.MapPost("/keeb/layer/{layer:int}/key", (int layer, SetLayerKeyBody body, IKeebProvider k) =>
        {
            var connected = k.SetLayerKey(layer, body);
            return new KeyboardState
            {
                IsConnected = connected,
                Profile = 0,
                Layer = layer,
                Layout = k.GetState(layer).Layout,
                Keys = k.GetLayer(layer),
            };
        });
        app.MapPost("/keeb/layer/{layer:int}/reset", (int layer, IKeebProvider k) =>
        {
            var connected = k.ResetLayer(layer);
            return new KeyboardState
            {
                IsConnected = connected,
                Profile = 0,
                Layer = layer,
                Layout = k.GetState(layer).Layout,
                Keys = k.GetLayer(layer),
            };
        });

        app.MapGet("/keeb/settings", (IKeebProvider k) => k.GetSettings());
        app.MapGet("/keeb/rotary/functions", (IKeebProvider k) =>
            new GetRotaryFunctionsResponse { Functions = k.GetRotaryFunctions() });
        app.MapPost("/keeb/rotary", (SetRotaryWheelsBody body, IKeebProvider k) =>
        {
            k.SetRotary(body);
            return ApiResponse.Ok();
        });
        app.MapPost("/keeb/rotary/sensitivity", (SetRotarySensitivityBody body, IKeebProvider k) =>
        {
            k.SetRotarySensitivity(body.Sensitivity);
            return ApiResponse.Ok();
        });
        // Legacy alias: kept so older nexus clients pointing at /keeb/key-reactive still work.
        // New web should POST /keeb/passive-lighting instead.
        app.MapPost("/keeb/key-reactive", (SetPassiveLightingBody body, IKeebProvider k) =>
        {
            k.SetPassiveLighting(body);
            return ApiResponse.Ok();
        });
        app.MapPost("/keeb/passive-lighting", (SetPassiveLightingBody body, IKeebProvider k) =>
        {
            k.SetPassiveLighting(body);
            return ApiResponse.Ok();
        });
        app.MapPost("/keeb/firmware/lighting", (SetFirmwareLightingBody body, IKeebProvider k) =>
        {
            k.SetFirmwareLighting(body);
            return ApiResponse.Ok();
        });
        app.MapPost("/keeb/game-mode", (SetGameModeBody body, IKeebProvider k) =>
        {
            k.SetGameMode(body);
            return ApiResponse.Ok();
        });
        app.MapGet("/keeb/macro/{index}", (int index, IKeebProvider k) =>
            new GetMacroResponse { Macro = k.GetMacro(index) });
        app.MapPost("/keeb/macro/{index}", (int index, SetMacroBody body, IKeebProvider k) =>
            new GetMacroResponse { Macro = k.SetMacro(index, body) });
        app.MapPost("/inputter", (InputterBody body, IInputterProvider i) =>
        {
            i.Send(body);
            return Results.Ok();
        });
    }
}
