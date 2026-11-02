using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.LogitechAudio;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    // A Logitech speaker's acoustics (G560): G HUB's Acoustics and Surround sound pages.
    // Every change answers with the state after it, so the page redraws from what the
    // speaker effect really holds.
    private static void MapLogitechSpeakerEndpoints(WebApplication app)
    {
        app.MapGet("/devices/logitech-speaker", (IServiceProvider sp) =>
            Results.Json(Speaker(sp)?.GetState() ?? new LogitechSpeakerStateDto(), AppJsonContext.Default.LogitechSpeakerStateDto));

        app.MapPost("/devices/logitech-speaker/eq", (LogitechSpeakerEqRequest body, IServiceProvider sp) =>
            SpeakerChange(sp, s => s.SetEq(body.Enabled, body.Bands, body.Preset)));

        app.MapPost("/devices/logitech-speaker/surround", (LogitechSpeakerSurroundRequest body, IServiceProvider sp) =>
            SpeakerChange(sp, s => s.SetSurround(body.Enabled, body.Room)));

        app.MapPost("/devices/logitech-speaker/channel", (LogitechSpeakerChannelRequest body, IServiceProvider sp) =>
            SpeakerChange(sp, s => body.Channel is not null && body.Gain is null ? SpeakerStatus.Invalid : s.SetChannelGain(body.Channel, body.Gain ?? 1)));

        app.MapPost("/devices/logitech-speaker/volume", (LogitechSpeakerLevelRequest body, IServiceProvider sp) =>
            SpeakerChange(sp, s => body.Level is { } level ? s.SetVolume(level) : SpeakerStatus.Invalid));

        app.MapPost("/devices/logitech-speaker/subwoofer", (LogitechSpeakerLevelRequest body, IServiceProvider sp) =>
            SpeakerChange(sp, s => body.Level is { } level && float.IsFinite(level) ? s.SetSubwoofer((int)Math.Round(level)) : SpeakerStatus.Invalid));

        app.MapPost("/devices/logitech-speaker/presets", (LogitechSpeakerPresetRequest body, IServiceProvider sp) =>
            SpeakerChange(sp, s => body.Name is { } name && body.Bands is { } bands ? s.SavePreset(name, bands) : SpeakerStatus.Invalid));

        app.MapDelete("/devices/logitech-speaker/presets/{name}", (string name, IServiceProvider sp) =>
            SpeakerChange(sp, s => s.DeletePreset(name)));

        app.MapPost("/devices/logitech-speaker/test", (LogitechSpeakerTestRequest body, IServiceProvider sp) =>
        {
            if (Speaker(sp) is not { } speaker)
            {
                return SpeakerError("no Logitech speaker support on this platform", StatusCodes.Status404NotFound);
            }
            var result = body.Channel is { } channel ? speaker.TestChannel(channel) : SpeakerStatus.Invalid;
            return result
                ? Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse)
                : Failure(result.Status);
        });
    }

    // The speaker service runs on Windows only.
    private static LogitechSpeakerService? Speaker(IServiceProvider sp) => sp.GetService<LogitechSpeakerService>();

    private static IResult SpeakerChange(IServiceProvider sp, Func<LogitechSpeakerService, SpeakerResult> change)
    {
        if (Speaker(sp) is not { } speaker)
        {
            return SpeakerError("no Logitech speaker support on this platform", StatusCodes.Status404NotFound);
        }
        var result = change(speaker);
        return result ? Results.Json(speaker.GetState(), AppJsonContext.Default.LogitechSpeakerStateDto) : Failure(result.Status);
    }

    private static IResult Failure(SpeakerStatus status) => status switch
    {
        SpeakerStatus.Invalid => SpeakerError("the request is not valid for the speaker", StatusCodes.Status400BadRequest),
        SpeakerStatus.NotFound => SpeakerError("no such speaker or preset", StatusCodes.Status404NotFound),
        _ => SpeakerError("the speaker did not take the change", StatusCodes.Status409Conflict),
    };

    private static IResult SpeakerError(string message, int status) =>
        Results.Json(ApiResponse.Fail(message), AppJsonContext.Default.ApiResponse, statusCode: status);
}
