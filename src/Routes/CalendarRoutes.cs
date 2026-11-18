using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Auth;
using Nexus.Service.Calendar;
using Nexus.Service.Models.Calendar;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static class CalendarRoutes
{
    public static void MapCalendarEndpoints(this WebApplication app)
    {
        app.MapGet("/api/calendar/calendars", (CalendarService calendar) =>
            Results.Json(new CalendarListResponse { Calendars = calendar.ListSources() }, AppJsonContext.Default.CalendarListResponse))
            .AllowPanel();

        app.MapPost("/api/calendar/calendars", async (HttpRequest req, CalendarService calendar, CancellationToken ct) =>
        {
            var body = await ReadBody(req, AppJsonContext.Default.CalendarCreateRequest, ct);
            if (body is null)
            {
                return BadRequest(CalendarErrors.InvalidUrl);
            }
            var result = await calendar.AddAsync(body.Url, body.Name, body.Color, ct);
            return result.Source is { } source
                ? Results.Json(source, AppJsonContext.Default.CalendarSourceDto)
                : BadRequest(result.Error ?? CalendarErrors.Unreachable);
        }).AllowPanel();

        app.MapPost("/api/calendar/probe", async (HttpRequest req, CalendarService calendar, CancellationToken ct) =>
        {
            var body = await ReadBody(req, AppJsonContext.Default.CalendarProbeRequest, ct);
            if (body is null)
            {
                return BadRequest(CalendarErrors.InvalidUrl);
            }
            var result = await calendar.ProbeAsync(body.Url, ct);
            return result.Response is { } probe
                ? Results.Json(probe, AppJsonContext.Default.CalendarProbeResponse)
                : BadRequest(result.Error ?? CalendarErrors.Unreachable);
        }).AllowPanel();

        app.MapPatch("/api/calendar/calendars/{id}", async (string id, HttpRequest req, CalendarService calendar, CancellationToken ct) =>
        {
            var body = await ReadBody(req, AppJsonContext.Default.CalendarPatchRequest, ct);
            if (body is null)
            {
                return BadRequest(CalendarErrors.InvalidUrl);
            }
            return calendar.Patch(id, body.Name, body.Color) is { } dto
                ? Results.Json(dto, AppJsonContext.Default.CalendarSourceDto)
                : Results.NotFound();
        }).AllowPanel();

        app.MapDelete("/api/calendar/calendars/{id}", (string id, CalendarService calendar) =>
            calendar.Remove(id) ? Results.NoContent() : Results.NotFound())
            .AllowPanel();

        app.MapGet("/api/calendar/settings", (CalendarService calendar) =>
            Results.Json(calendar.GetSettings(), AppJsonContext.Default.CalendarSettingsDto))
            .AllowPanel();

        app.MapPut("/api/calendar/settings", async (HttpRequest req, CalendarService calendar, CancellationToken ct) =>
        {
            var body = await ReadBody(req, AppJsonContext.Default.CalendarSettingsRequest, ct);
            if (body is null || calendar.SetRefreshMinutes(body.RefreshMinutes) is not { } settings)
            {
                return BadRequest(CalendarErrors.InvalidRefresh);
            }
            return Results.Json(settings, AppJsonContext.Default.CalendarSettingsDto);
        }).AllowPanel();

        app.MapPost("/api/calendar/refresh", (CalendarService calendar) =>
            Results.Json(new CalendarRefreshResponse { Started = calendar.TryStartRefreshAll() }, AppJsonContext.Default.CalendarRefreshResponse, statusCode: StatusCodes.Status202Accepted))
            .AllowPanel();

        app.MapPost("/api/calendar/calendars/{id}/sync", (string id, CalendarService calendar) =>
            calendar.TryStartSync(id) is { } started
                ? Results.Json(new CalendarRefreshResponse { Started = started }, AppJsonContext.Default.CalendarRefreshResponse, statusCode: StatusCodes.Status202Accepted)
                : Results.NotFound())
            .AllowPanel();

        app.MapGet("/api/calendar/events", (long? from, long? to, string? calendars, CalendarService calendar) =>
        {
            if (from is not { } f || to is not { } t)
            {
                return BadRequest(CalendarErrors.InvalidRange);
            }
            var response = calendar.Query(f, t, ParseIds(calendars));
            return response is null
                ? BadRequest(CalendarErrors.InvalidRange)
                : Results.Json(response, AppJsonContext.Default.CalendarEventsResponse);
        }).AllowPanel();
    }

    internal static List<string>? ParseIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return null;
        }
        var ids = new List<string>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ids.Add(part);
        }
        return ids;
    }

    private static IResult BadRequest(string code) =>
        Results.Json(new CalendarErrorResponse { Error = code, Msg = code }, AppJsonContext.Default.CalendarErrorResponse, statusCode: StatusCodes.Status400BadRequest);

    private static async Task<T?> ReadBody<T>(HttpRequest req, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken ct) where T : class
    {
        try
        {
            return await req.ReadFromJsonAsync(info, ct);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }
}
