using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nexus.Service.Lighting.Scene;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// The shared PC setup record. Desktop-token only (no panel opt-in), like the
/// scene routes: Build writes the whole record, Lighting only moves items.
/// </summary>
public static class PcSetupRoutes
{
    // The record is capped well under this; anything past it is refused while it streams in.
    private const long MaxBodyBytes = PcSetupValidation.MaxRecordBytes * 2L;

    // Every read-modify-write of the record runs under this.
    private static readonly object SetupWriteLock = new();

    public static void MapPcSetupEndpoints(this WebApplication app)
    {
        app.MapGet("/system/setup", (LightingSceneStore scene) =>
            Results.Json(ToResponse(scene.Setup.Load()), AppJsonContext.Default.PcSetupResponse));

        app.MapPut("/system/setup", async (HttpContext ctx, LightingSceneStore scene) =>
        {
            var body = await ReadBodyAsync(ctx, AppJsonContext.Default.PutPcSetupBody);
            if (body is null)
            {
                return Fail(new("bad_body", "body is malformed or over the size cap"));
            }
            if (body.BaseRev is not { } baseRev)
            {
                return Fail(new("base_rev_required", "baseRev is required"));
            }
            if (PcSetupValidation.Validate(body.Setup) is { } error)
            {
                return Fail(error);
            }
            lock (SetupWriteLock)
            {
                return scene.Setup.TrySave(baseRev, body.Setup!, out var current)
                    ? Results.Json(new PcSetupRevResponse { Rev = current.Rev }, AppJsonContext.Default.PcSetupRevResponse)
                    : Conflict(current);
            }
        });

        app.MapPatch("/system/setup/placements", async (HttpContext ctx, LightingSceneStore scene) =>
        {
            var body = await ReadBodyAsync(ctx, AppJsonContext.Default.PatchPcSetupPlacementsBody);
            if (body is null)
            {
                return Fail(new("bad_body", "body is malformed or over the size cap"));
            }
            if (body.BaseRev is not { } baseRev)
            {
                return Fail(new("base_rev_required", "baseRev is required"));
            }
            var places = body.Places ?? new List<PcSetupPlacement>();
            if (places.Count > PcSetupValidation.MaxItems + 1)
            {
                return Fail(new("too_many_items", $"at most {PcSetupValidation.MaxItems + 1} places"));
            }
            lock (SetupWriteLock)
            {
                var current = scene.Setup.Load();
                if (current.Rev != baseRev)
                {
                    return Conflict(current);
                }
                var next = current.Setup;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in places)
                {
                    if (p is null || !seen.Add(p.Id ?? ""))
                    {
                        return Fail(new("duplicate_id", "each id may appear once in places"));
                    }
                    if (!PcSetupValidation.IsFinitePlace(p.X, p.Z, p.Yaw))
                    {
                        return Fail(new("non_finite_number", $"place {p.Id}: x, z and yaw must be finite"));
                    }
                    if (p.Id == PcSetupValidation.CaseId)
                    {
                        Apply(next.Case.Place ??= new PcSetupPlace(), p);
                        MoveResolved(next.Resolved.Objects.Find(o => o.Kind == "case"), p);
                    }
                    else if (next.Items.Find(i => i.Id == p.Id) is { } item)
                    {
                        Apply(item.Place ??= new PcSetupPlace(), p);
                        var desk = next.Resolved.Objects.Find(o => o.Id == p.Id);
                        var oldX = desk?.Position[0] ?? 0f;
                        var oldZ = desk?.Position[2] ?? 0f;
                        var oldYaw = desk?.Yaw ?? 0f;
                        MoveResolved(desk, p);
                        // An L desk's return arm is its own resolved object, "<id>.return": it keeps its offset and turn relative to the desk.
                        if (desk is not null && next.Resolved.Objects.Find(o => o.Id == p.Id + ".return") is { } ret)
                        {
                            var delta = p.Yaw - oldYaw;
                            var offset = SceneMath.RotateYaw(new System.Numerics.Vector3(ret.Position[0] - oldX, 0f, ret.Position[2] - oldZ), delta);
                            ret.Position = [p.X + offset.X, ret.Position[1], p.Z + offset.Z];
                            ret.Yaw += delta;
                        }
                    }
                    else
                    {
                        return Fail(new("unknown_id", $"no item with id {p.Id}"));
                    }
                }
                if (PcSetupValidation.Size(next) > PcSetupValidation.MaxRecordBytes)
                {
                    return Fail(new("record_too_large", $"the record is over {PcSetupValidation.MaxRecordBytes / 1024} KiB"));
                }
                return scene.Setup.TrySave(baseRev, next, out var saved)
                    ? Results.Json(new PcSetupRevResponse { Rev = saved.Rev }, AppJsonContext.Default.PcSetupRevResponse)
                    : Conflict(saved);
            }
        });

        app.MapPatch("/system/setup/radiator", async (HttpContext ctx, LightingSceneStore scene) =>
        {
            var body = await ReadBodyAsync(ctx, AppJsonContext.Default.PatchPcSetupRadiatorBody);
            if (body is null)
            {
                return Fail(new("bad_body", "body is malformed or over the size cap"));
            }
            if (body.BaseRev is not { } baseRev)
            {
                return Fail(new("base_rev_required", "baseRev is required"));
            }
            if (PcSetupValidation.RadiatorMount(body.Mount) is { } error)
            {
                return Fail(error);
            }
            lock (SetupWriteLock)
            {
                var current = scene.Setup.Load();
                if (current.Rev == 0)
                {
                    return Fail(new("no_record", "no saved setup to change"));
                }
                if (current.Rev != baseRev)
                {
                    return Conflict(current);
                }
                var next = current.Setup;
                next.Case.RadiatorMount = body.Mount;
                return scene.Setup.TrySave(baseRev, next, out var saved)
                    ? Results.Json(new PcSetupRevResponse { Rev = saved.Rev }, AppJsonContext.Default.PcSetupRevResponse)
                    : Conflict(saved);
            }
        });
    }

    /// <summary>Pings the <c>system/setup</c> topic after every save, whichever path made it (a route or a backup restore).</summary>
    public static void WireTopic(PcSetupStore store, Nexus.Service.Sockets.MultiplexHub hub) =>
        store.Changed += rev => Nexus.Service.Sockets.PanelTopics.BroadcastSystemSetup(hub, rev);

    // The service moves the matching resolved object itself, so a client can never add, remove or reshape scene objects here.
    private static void MoveResolved(SceneObject? obj, PcSetupPlacement p)
    {
        if (obj is null)
        {
            return;
        }
        obj.Position = [p.X, obj.Position.Length == 3 ? obj.Position[1] : 0f, p.Z];
        obj.Yaw = p.Yaw;
    }

    private static void Apply(PcSetupPlace place, PcSetupPlacement p)
    {
        place.X = p.X;
        place.Z = p.Z;
        place.Yaw = p.Yaw;
    }

    // The service-wide body limit is sized for media uploads, so this route sets its own before reading.
    private static async Task<T?> ReadBodyAsync<T>(HttpContext ctx, JsonTypeInfo<T> info)
        where T : class
    {
        var size = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false })
        {
            size.MaxRequestBodySize = MaxBodyBytes;
        }
        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, info, ctx.RequestAborted);
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static PcSetupResponse ToResponse(PcSetupFile file) => new() { Rev = file.Rev, Setup = file.Setup };

    private static IResult Conflict(PcSetupFile current) =>
        Results.Json(ToResponse(current), AppJsonContext.Default.PcSetupResponse, statusCode: StatusCodes.Status409Conflict);

    private static IResult Fail(PcSetupError error) =>
        Results.Json(
            new PcSetupErrorResponse { Error = true, Msg = error.Message, Code = error.Code },
            AppJsonContext.Default.PcSetupErrorResponse,
            statusCode: StatusCodes.Status400BadRequest);
}
