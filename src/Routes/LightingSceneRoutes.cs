using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting.Scene;
using Nexus.Service.Models;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>The 3D lighting scene. Desktop-token only: it is machine config, edited from the dashboard.</summary>
public static class LightingSceneRoutes
{
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
            var doc = store.Load();
            doc.Objects = body.Objects;
            doc.Bindings = body.Bindings;
            // The model belongs to the imported case; an editor save cannot claim it for another object.
            foreach (var o in doc.Objects)
            {
                o.HasModel = doc.ModelRev is not null && o.Source == "build" && o.Kind == "case";
            }
            store.Save(doc);
            return Results.Json(ToResponse(doc, config.Load().Lighting.SceneView), AppJsonContext.Default.LightingSceneResponse);
        });

        app.MapPut("/lighting/scene/import", (ImportLightingSceneBody body, LightingSceneStore store, IConfigStore config) =>
        {
            if (body.CaseId is not null && !SceneValidation.IsValidId(body.CaseId))
            {
                return Fail("caseId must be a catalog part id");
            }
            byte[]? model = null;
            if (body.ModelBase64 is { } b64)
            {
                if (b64.Length > SceneValidation.MaxModelBytes / 3 * 4 + 4)
                {
                    return Fail("model is over the size cap");
                }
                try
                {
                    model = Convert.FromBase64String(b64);
                }
                catch (FormatException)
                {
                    return Fail("modelBase64 is not base64");
                }
                if (SceneValidation.ValidateModel(model) is { } modelError)
                {
                    return Fail(modelError);
                }
            }

            var doc = store.Load();
            var merged = SceneImport.Merge(doc, body.Objects ?? new List<SceneObject>(), model is not null);
            if (SceneValidation.Validate(merged.Objects, merged.Bindings) is { } error)
            {
                return Fail(error);
            }
            merged.CaseId = body.CaseId;
            merged.ModelRev = store.SaveModel(model);
            store.Save(merged);
            return Results.Json(ToResponse(merged, config.Load().Lighting.SceneView), AppJsonContext.Default.LightingSceneResponse);
        });

        app.MapDelete("/lighting/scene/import", (LightingSceneStore store, IConfigStore config) =>
        {
            var doc = SceneImport.Merge(store.Load(), new List<SceneObject>(), hasModel: false);
            doc.CaseId = null;
            doc.ModelRev = store.SaveModel(null);
            store.Save(doc);
            return Results.Json(ToResponse(doc, config.Load().Lighting.SceneView), AppJsonContext.Default.LightingSceneResponse);
        });

        app.MapGet("/lighting/scene/model", (LightingSceneStore store) =>
        {
            var bytes = store.LoadModel();
            return bytes is null
                ? Results.NotFound()
                : Results.Bytes(bytes, "model/gltf-binary");
        });

        app.MapPut("/lighting/scene/view", (PutSceneViewBody body, IConfigStore config, LightingSceneService scene) =>
        {
            if (body.Camera is not null && SceneValidation.ValidateCamera(body.Camera) is { } error)
            {
                return Fail(error);
            }
            if (body.Draft)
            {
                if (body.Camera is null)
                {
                    return Fail("a draft needs a camera");
                }
                scene.SetDraftCamera(body.Camera);
                return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
            }
            config.Update(s =>
            {
                var current = s.Lighting.SceneView;
                s.Lighting.SceneView = new SceneView
                {
                    Enabled = body.Enabled ?? current.Enabled,
                    Camera = body.Camera ?? current.Camera,
                };
            });
            // A commit that changes nothing still ends the drag that drafted it.
            scene.ClearDraft();
            return Results.Json(config.Load().Lighting.SceneView, AppJsonContext.Default.SceneView);
        });
    }

    private static LightingSceneResponse ToResponse(LightingSceneDoc doc, SceneView view) => new()
    {
        Objects = doc.Objects,
        Bindings = doc.Bindings,
        View = view,
        ModelRev = doc.ModelRev,
        CaseId = doc.CaseId,
    };

    private static IResult Fail(string message) =>
        Results.Json(ApiResponse.Fail(message), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status400BadRequest);
}
