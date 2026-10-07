using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Sentry;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Sentry;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>Sentry: arm and disarm the lock alert, and the per-phone push target it sends to.</summary>
public static class SentryRoutes
{
    private const int MaxTokenLength = 4096;
    private const int MaxTitleLength = SentryCoordinator.MaxTitleLength;
    private const int MaxBodyLength = SentryCoordinator.MaxBodyLength;

    public static void MapSentryEndpoints(this WebApplication app)
    {
        app.MapGet("/sentry", (SentryCoordinator sentry) =>
            Results.Json(sentry.GetStatus(), AppJsonContext.Default.SentryStatusResponse))
            .AllowPanel();

        // Locking the PC is a desktop-token action: a relayed or LAN phone can
        // only arm a PC that is already locked.
        app.MapPost("/sentry/arm", async (SentryArmBody? body, HttpContext ctx, SentryCoordinator sentry, TokenService tokens) =>
        {
            var lockFirst = body?.Lock ?? false;
            if (lockFirst && !ServiceTokenRequests.HasServiceToken(ctx, tokens))
            {
                // Not 401: the panel reads that as an unpaired session and re-pairs.
                return Error(StatusCodes.Status403Forbidden, "desktop_only");
            }

            return await sentry.ArmAsync(lockFirst) switch
            {
                SentryCoordinator.ArmOutcome.Armed =>
                    Results.Json(sentry.GetStatus(), AppJsonContext.Default.SentryStatusResponse),
                SentryCoordinator.ArmOutcome.NotLocked =>
                    Error(StatusCodes.Status409Conflict, "not_locked"),
                SentryCoordinator.ArmOutcome.Unsupported =>
                    Error(StatusCodes.Status400BadRequest, "unsupported"),
                SentryCoordinator.ArmOutcome.LockNotConfirmed =>
                    Error(StatusCodes.Status504GatewayTimeout, "lock_not_confirmed"),
                _ => Error(StatusCodes.Status500InternalServerError, "lock_failed"),
            };
        }).AllowPanel();

        app.MapPost("/sentry/disarm", (SentryCoordinator sentry) =>
        {
            sentry.Disarm();
            return Results.Json(sentry.GetStatus(), AppJsonContext.Default.SentryStatusResponse);
        }).AllowPanel();

        app.MapPut("/panel/phone/push", (PanelPhonePushBody? body, HttpContext ctx, PanelPhonePairingService pairing) =>
        {
            var sessionId = ctx.Items[PathAuthMiddleware.PhoneSessionIdItem] as string;
            if (string.IsNullOrEmpty(sessionId))
            {
                return Results.BadRequest(ApiResponse.Fail("a paired phone session is required"));
            }
            var target = Validate(body, out var problem);
            if (target is null)
            {
                return Results.BadRequest(ApiResponse.Fail(problem));
            }
            return pairing.SetPushTarget(sessionId, target)
                ? Results.Ok(ApiResponse.Ok("saved"))
                : Results.NotFound(ApiResponse.Fail("session not found"));
        }).AllowPanel();

        app.MapDelete("/panel/phone/push", (HttpContext ctx, PanelPhonePairingService pairing) =>
        {
            var sessionId = ctx.Items[PathAuthMiddleware.PhoneSessionIdItem] as string;
            if (string.IsNullOrEmpty(sessionId))
            {
                return Results.BadRequest(ApiResponse.Fail("a paired phone session is required"));
            }
            pairing.SetPushTarget(sessionId, null);
            return Results.Ok(ApiResponse.Ok("removed"));
        }).AllowPanel();
    }

    private static IResult Error(int status, string code) =>
        Results.Json(new SentryErrorResponse { Error = code }, AppJsonContext.Default.SentryErrorResponse, statusCode: status);

    internal static PanelPhonePushTarget? Validate(PanelPhonePushBody? body, out string problem)
    {
        problem = "";
        if (body is null)
        {
            problem = "body is required";
            return null;
        }
        var platform = body.Platform?.Trim().ToLowerInvariant() ?? "";
        if (platform is not ("ios" or "android"))
        {
            problem = "platform must be ios or android";
            return null;
        }
        var environment = body.Environment?.Trim().ToLowerInvariant() ?? "";
        if (environment is not ("sandbox" or "production"))
        {
            problem = "environment must be sandbox or production";
            return null;
        }
        var token = body.Token?.Trim() ?? "";
        if (token.Length == 0 || token.Length > MaxTokenLength || token.AsSpan().IndexOfAny(" \t\r\n") >= 0)
        {
            problem = "token is invalid";
            return null;
        }
        var title = body.Title?.Trim() ?? "";
        if (title.Length == 0 || title.Length > MaxTitleLength)
        {
            problem = $"title must be 1 to {MaxTitleLength} characters";
            return null;
        }
        var text = body.Body?.Trim() ?? "";
        if (text.Length == 0 || text.Length > MaxBodyLength)
        {
            problem = $"body must be 1 to {MaxBodyLength} characters";
            return null;
        }
        return new PanelPhonePushTarget
        {
            Platform = platform,
            Token = token,
            Environment = environment,
            Title = title,
            Body = text,
        };
    }
}
