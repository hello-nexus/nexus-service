using System;
using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Auth;
using Nexus.Service.Models.Reminders;
using Nexus.Service.Reminders;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static class ReminderRoutes
{
    /// <summary>Every route is panel reachable: phones and panels tick items off and add them.</summary>
    public static void MapReminderEndpoints(this WebApplication app)
    {
        app.MapGet("/api/reminders", (ReminderService reminders) =>
            Results.Json(reminders.GetAll(), AppJsonContext.Default.ReminderStoreResponse))
            .AllowPanel();

        app.MapPost("/api/reminders/lists", async (HttpRequest req, ReminderService reminders, CancellationToken ct) =>
        {
            var body = await ReadBody(req, ct);
            if (body is not { } b)
            {
                return Error(ReminderErrors.Invalid);
            }
            var r = reminders.CreateList(b);
            return r.Value is { } list
                ? Results.Json(list, AppJsonContext.Default.ReminderListDto, statusCode: StatusCodes.Status201Created)
                : Error(r.Error!);
        }).AllowPanel();

        app.MapPut("/api/reminders/lists/{id}", async (string id, HttpRequest req, ReminderService reminders, CancellationToken ct) =>
        {
            var body = await ReadBody(req, ct);
            if (body is not { } b)
            {
                return Error(ReminderErrors.Invalid);
            }
            var r = reminders.PatchList(id, b);
            return r.Value is { } list ? Results.Json(list, AppJsonContext.Default.ReminderListDto) : Error(r.Error!);
        }).AllowPanel();

        app.MapDelete("/api/reminders/lists/{id}", (string id, ReminderService reminders) =>
            reminders.DeleteList(id) is { Error: { } error } ? Error(error) : Results.NoContent())
            .AllowPanel();

        app.MapPost("/api/reminders/items", async (HttpRequest req, ReminderService reminders, CancellationToken ct) =>
        {
            var body = await ReadBody(req, ct);
            if (body is not { } b)
            {
                return Error(ReminderErrors.Invalid);
            }
            var r = reminders.CreateItem(b);
            return r.Value is { } item
                ? Results.Json(item, AppJsonContext.Default.ReminderDto, statusCode: StatusCodes.Status201Created)
                : Error(r.Error!);
        }).AllowPanel();

        app.MapPut("/api/reminders/items/{id}", async (string id, HttpRequest req, ReminderService reminders, CancellationToken ct) =>
        {
            var body = await ReadBody(req, ct);
            if (body is not { } b)
            {
                return Error(ReminderErrors.Invalid);
            }
            var r = reminders.PatchItem(id, b);
            return r.Value is { } item ? Results.Json(item, AppJsonContext.Default.ReminderDto) : Error(r.Error!);
        }).AllowPanel();

        app.MapDelete("/api/reminders/items/{id}", (string id, ReminderService reminders) =>
            reminders.DeleteItem(id) is { Error: { } error } ? Error(error) : Results.NoContent())
            .AllowPanel();

        app.MapPost("/api/reminders/items/clear-completed", async (HttpRequest req, ReminderService reminders, CancellationToken ct) =>
        {
            string? listId = null;
            var body = await ReadBody(req, ct, emptyIsObject: true);
            if (body is not { } b || b.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            {
                return Error(ReminderErrors.Invalid);
            }
            if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("listId", out var el) && el.ValueKind != JsonValueKind.Null)
            {
                if (el.ValueKind != JsonValueKind.String)
                {
                    return Error(ReminderErrors.Invalid);
                }
                listId = el.GetString();
            }
            var r = reminders.ClearCompleted(listId);
            return r.Error is { } error
                ? Error(error)
                : Results.Json(new ReminderClearResponse { Removed = r.Value }, AppJsonContext.Default.ReminderClearResponse);
        }).AllowPanel();

        app.MapPost("/api/reminders/reorder", async (HttpRequest req, ReminderService reminders, CancellationToken ct) =>
        {
            var body = await ReadBody(req, ct);
            if (body is not { } b || b.ValueKind != JsonValueKind.Object
                || !b.TryGetProperty("listId", out var listEl) || listEl.ValueKind != JsonValueKind.String
                || !b.TryGetProperty("itemIds", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array)
            {
                return Error(ReminderErrors.Invalid);
            }
            var ids = new List<string>();
            foreach (var e in idsEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String)
                {
                    return Error(ReminderErrors.Invalid);
                }
                ids.Add(e.GetString()!);
            }
            return reminders.Reorder(listEl.GetString()!, ids) is { Error: { } error } ? Error(error) : Results.NoContent();
        }).AllowPanel();
    }

    private static IResult Error(string code)
    {
        var status = code switch
        {
            ReminderErrors.NotFound => StatusCodes.Status404NotFound,
            ReminderErrors.LastList => StatusCodes.Status409Conflict,
            ReminderErrors.SaveFailed => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Json(new ReminderErrorResponse { Code = code, Error = code, Msg = code }, AppJsonContext.Default.ReminderErrorResponse, statusCode: status);
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    /// Parsed with JsonDocument so an absent field and an explicit null stay distinguishable. An empty
    /// body is <c>{}</c> when <paramref name="emptyIsObject"/>, otherwise invalid. Null means a 400.
    /// </summary>
    private static async Task<JsonElement?> ReadBody(HttpRequest req, CancellationToken ct, bool emptyIsObject = false)
    {
        try
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms, ct);
            if (ms.Length == 0)
            {
                return emptyIsObject ? EmptyObject : null;
            }
            ms.Position = 0;
            using var doc = await JsonDocument.ParseAsync(ms, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        {
            return null;
        }
    }
}
