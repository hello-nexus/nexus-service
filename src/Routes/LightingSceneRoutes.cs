using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nexus.Service.Lighting.Scene;
using Nexus.Service.Models;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>The 3D lighting scene. Desktop-token only: it is machine config, edited from the dashboard.</summary>
public static class LightingSceneRoutes
{
    // An import body is the model plus its objects; anything past this is refused while it streams in.
    private const long MaxImportBodyBytes = SceneValidation.MaxModelBytes + 4 * 1024 * 1024;

    // Every read-modify-write of the scene (an editor save racing a Build import) runs under this.
    private static readonly object SceneWriteLock = new();

    public static void MapLightingSceneEndpoints(this WebApplication app)
    {
        app.MapGet("/lighting/scene", (LightingSceneStore store, IConfigStore config) =>
            Results.Json(ToResponse(store.Load(), config.Load().Lighting.SceneView), AppJsonContext.Default.LightingSceneResponse));

        app.MapPut("/lighting/scene", (PutLightingSceneBody body, LightingSceneStore store, IConfigStore config) =>
        {
            if (SceneValidation.Validate(body.Objects, body.Bindings) is { } error)
            {
                return Fail(error);
            }
            LightingSceneDoc doc;
            lock (SceneWriteLock)
            {
                doc = store.Load();
                // Which objects the model draws is the import's to say; an editor save keeps it as stored.
                var drawn = new HashSet<string>(StringComparer.Ordinal);
                foreach (var o in doc.Objects)
                {
                    if (o.Source == "build" && o.HasModel)
                    {
                        drawn.Add(o.Id);
                    }
                }
                doc.Objects = body.Objects;
                doc.Bindings = body.Bindings;
                foreach (var o in doc.Objects)
                {
                    o.HasModel = o.Source == "build" && drawn.Contains(o.Id);
                }
                store.Save(doc);
            }
            return Results.Json(ToResponse(doc, config.Load().Lighting.SceneView), AppJsonContext.Default.LightingSceneResponse);
        });

        app.MapPut("/lighting/scene/import", async (HttpContext ctx, LightingSceneStore store, IConfigStore config) =>
        {
            var bodySize = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
            {
                bodySize.MaxRequestBodySize = MaxImportBodyBytes;
            }
            ImportLightingSceneBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AppJsonContext.Default.ImportLightingSceneBody, ctx.RequestAborted);
            }
            catch (Exception e) when (e is JsonException or BadHttpRequestException)
            {
                return Fail("import body is malformed or over the size cap");
            }
            if (body is null)
            {
                return Fail("import body is required");
            }
            if (body.CaseId is not null && !SceneValidation.IsValidId(body.CaseId))
            {
                return Fail("caseId must be a catalog part id");
            }
            byte[]? model = null;
            var shapeIds = new HashSet<string>(StringComparer.Ordinal);
            if (body.Model is { ValueKind: not JsonValueKind.Null } json)
            {
                model = Encoding.UTF8.GetBytes(json.GetRawText());
                if (SceneValidation.ValidateModel(model, shapeIds) is { } modelError)
                {
                    return Fail(modelError);
                }
            }

            LightingSceneDoc merged;
            lock (SceneWriteLock)
            {
                merged = SceneImport.Merge(store.Load(), body.Objects ?? new List<SceneObject>(), shapeIds);
                if (SceneValidation.Validate(merged.Objects, merged.Bindings) is { } error)
                {
                    return Fail(error);
                }
                merged.CaseId = body.CaseId;
                merged.ModelRev = store.SaveModel(model);
                store.Save(merged);
            }
            return Results.Json(ToResponse(merged, config.Load().Lighting.SceneView), AppJsonContext.Default.LightingSceneResponse);
        });

        app.MapDelete("/lighting/scene/import", (LightingSceneStore store, IConfigStore config) =>
        {
            LightingSceneDoc doc;
            lock (SceneWriteLock)
            {
                doc = SceneImport.Merge(store.Load(), new List<SceneObject>(), new HashSet<string>());
                doc.CaseId = null;
                doc.ModelRev = store.SaveModel(null);
                store.Save(doc);
            }
            return Results.Json(ToResponse(doc, config.Load().Lighting.SceneView), AppJsonContext.Default.LightingSceneResponse);
        });

        app.MapGet("/lighting/scene/model", (LightingSceneStore store) =>
        {
            var bytes = store.LoadModel();
            return bytes is null
                ? Results.NotFound()
                : Results.Bytes(bytes, "application/json");
        });

        app.MapPut("/lighting/scene/view", (PutSceneViewBody body, IConfigStore config, LightingSceneService scene) =>
        {
            if (body.Camera is not null && SceneValidation.ValidateCamera(body.Camera) is { } error)
            {
                return Fail(error);
            }
            if (body.Session is { Length: > 64 })
            {
                return Fail("session must be at most 64 characters");
            }
            if (body.Draft)
            {
                if (body.Camera is null)
                {
                    return Fail("a draft needs a camera");
                }
                scene.SetDraftCamera(body.Camera, body.Session, body.Seq);
                return Results.Json(config.Load().Lighting.SceneView ?? new SceneView(), AppJsonContext.Default.SceneView);
            }
            config.Update(s =>
            {
                var current = s.Lighting.SceneView ?? new SceneView();
                s.Lighting.SceneView = new SceneView
                {
                    Enabled = body.Enabled ?? current.Enabled,
                    Camera = body.Camera ?? current.Camera,
                };
            });
            // A commit that changes nothing still ends the drag that drafted it.
            scene.ClearDraft(body.Session, body.Seq);
            return Results.Json(config.Load().Lighting.SceneView, AppJsonContext.Default.SceneView);
        });
    }

    private static LightingSceneResponse ToResponse(LightingSceneDoc doc, SceneView? view) => new()
    {
        Objects = doc.Objects,
        Bindings = doc.Bindings,
        View = view ?? new SceneView(),
        ModelRev = doc.ModelRev,
        CaseId = doc.CaseId,
    };

    private static IResult Fail(string message) =>
        Results.Json(ApiResponse.Fail(message), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status400BadRequest);
}
