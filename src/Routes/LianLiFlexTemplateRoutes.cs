using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class LianLiFlexTemplateCreateRequest
{
    public string Name { get; set; } = "";
    /// <summary>Unit whose lighting is saved; empty saves none.</summary>
    public string Mac { get; set; } = "";
    /// <summary>Screen whose content is saved; empty saves none.</summary>
    public string Serial { get; set; } = "";
}

public sealed class LianLiFlexTemplateApplyRequest
{
    public string Mac { get; set; } = "";
    public string Serial { get; set; } = "";
}

public sealed class LianLiFlexTemplatesResponse
{
    public FlexTemplate[] Templates { get; set; } = Array.Empty<FlexTemplate>();
}

public static partial class LianLiFlexRoutes
{
    internal static (FlexTemplate? Template, string? Error) BuildTemplate(LianLiFlexTemplateCreateRequest body, DevicesSettings devices, FlexTemplateStore store)
    {
        var template = new FlexTemplate { Name = body.Name };
        if (!string.IsNullOrEmpty(body.Mac))
        {
            if (!devices.FlexUnits.TryGetValue(body.Mac, out var unit))
            {
                return (null, "unknown unit");
            }
            template.Lighting = unit;
        }
        if (!string.IsNullOrEmpty(body.Serial))
        {
            if (!devices.FlexLcdScreens.TryGetValue(body.Serial, out var screen))
            {
                return (null, "unknown screen");
            }
            template.Screen = screen;
        }
        return store.Save(template);
    }

    /// <summary>Writes the template's parts onto the named targets, keeping the unit's list position and merge choice.</summary>
    internal static string? ApplyTemplate(FlexTemplate template, LianLiFlexTemplateApplyRequest body, DevicesSettings devices)
    {
        var lighting = FlexTemplateStore.LightingOf(template);
        var screen = FlexTemplateStore.ScreenOf(template);
        if (lighting is not null && string.IsNullOrEmpty(body.Mac))
        {
            return "mac required";
        }
        if (screen is not null && string.IsNullOrEmpty(body.Serial))
        {
            return "serial required";
        }
        if (lighting is not null)
        {
            if (devices.FlexUnits.TryGetValue(body.Mac, out var current))
            {
                lighting.SortIndex = current.SortIndex;
                lighting.Merge = current.Merge;
                lighting.FanProfile = current.FanProfile;
            }
            devices.FlexUnits[body.Mac] = lighting;
        }
        if (screen is not null)
        {
            if (devices.FlexLcdScreens.TryGetValue(body.Serial, out var existing))
            {
                screen.Order = existing.Order;
            }
            devices.FlexLcdScreens[body.Serial] = screen;
        }
        return null;
    }

    private static void MapTemplateEndpoints(WebApplication app)
    {
        app.MapGet("/devices/lianli-flex/templates", (FlexTemplateStore store) =>
            Results.Json(new LianLiFlexTemplatesResponse { Templates = store.List().ToArray() }, AppJsonContext.Default.LianLiFlexTemplatesResponse));

        app.MapPost("/devices/lianli-flex/templates", (LianLiFlexTemplateCreateRequest body, IConfigStore config, FlexTemplateStore store) =>
        {
            var (template, error) = BuildTemplate(body, config.Load().Devices, store);
            return template is null
                ? Results.Json(ApiResponse.Fail(error ?? "failed"), AppJsonContext.Default.ApiResponse, statusCode: 400)
                : Results.Json(template, AppJsonContext.Default.FlexTemplate);
        });

        app.MapPost("/devices/lianli-flex/templates/{id}/apply", (string id, LianLiFlexTemplateApplyRequest body, IConfigStore config, FlexTemplateStore store) =>
        {
            var template = store.Get(id);
            if (template is null)
            {
                return Results.Json(ApiResponse.Fail("unknown template"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }
            string? error = null;
            config.Update(s => error = ApplyTemplate(template, body, s.Devices));
            return error is null
                ? Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse)
                : Results.Json(ApiResponse.Fail(error), AppJsonContext.Default.ApiResponse, statusCode: 400);
        });

        app.MapGet("/devices/lianli-flex/templates/{id}/export", (string id, FlexTemplateStore store) =>
            store.Export(id) is { } zip ? Results.File(zip, "application/zip", $"nexus-flex-template-{id}.zip") : Results.NotFound());

        app.MapPost("/devices/lianli-flex/templates/import", async (HttpContext ctx, FlexTemplateStore store) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.Json(ApiResponse.Fail("Expected multipart/form-data"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0 || file.Length > FlexTemplateStore.MaxJsonBytes)
            {
                return Results.Json(ApiResponse.Fail("No usable file"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var (template, error) = store.Import(ms.ToArray());
            return template is null
                ? Results.Json(ApiResponse.Fail(error ?? "failed"), AppJsonContext.Default.ApiResponse, statusCode: 400)
                : Results.Json(template, AppJsonContext.Default.FlexTemplate);
        });

        app.MapDelete("/devices/lianli-flex/templates/{id}", (string id, FlexTemplateStore store) =>
            store.Delete(id)
                ? Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse)
                : Results.Json(ApiResponse.Fail("unknown template"), AppJsonContext.Default.ApiResponse, statusCode: 404));
    }
}
