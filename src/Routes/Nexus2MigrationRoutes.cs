using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Migration;
using Nexus.Service.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// Nexus 2 (legacy HYTE Nexus) returning-user welcome screen state plus the
/// personalization import (dashboard-only, loopback). Detection runs fresh
/// on every GET; the offered flag latches only on dismiss, so a device that
/// becomes eligible later still triggers the screen on the next load.
/// The screen is gated on <c>detected</c> (Nexus 2 present); the web gates the
/// import on <c>importAvailable</c> alone, which outlives an uninstall.
///   GET  /migration/nexus2                   -> status
///   POST /migration/nexus2/dismiss            -> { dismissed: true }
///   POST /migration/nexus2/disable-autostart  -> ApiResponse
///   POST /migration/nexus2/close-app          -> ApiResponse
///   POST /migration/nexus2/uninstall          -> ApiResponse
///   POST /migration/nexus2/preview            -> Nexus2PreviewResponse
///   POST /migration/nexus2/apply              -> Nexus2ApplyResponse
/// </summary>
internal static class Nexus2MigrationRoutes
{
    public static void MapNexus2MigrationEndpoints(this WebApplication app)
    {
        app.MapGet("/migration/nexus2", (IConfigStore store, INexus2Detector detector) =>
        {
            var settings = store.Load();
            var result = detector.Detect();
            var deviceEligible = IsDeviceEligible(settings);
            var pending = result.Detected && deviceEligible && !settings.Nexus2MigrationOffered;

            return Results.Json(new Nexus2MigrationStatusDto
            {
                Detected = result.Detected,
                ImportAvailable = result.ImportAvailable,
                DeviceEligible = deviceEligible,
                Version = result.Version,
                AutostartTaskPresent = result.AutostartTaskPresent,
                Running = result.Running,
                Pending = pending,
            }, AppJsonContext.Default.Nexus2MigrationStatusDto);
        }).LocalhostOnly();

        app.MapPost("/migration/nexus2/dismiss", (IConfigStore store) =>
        {
            store.Update(s => s.Nexus2MigrationOffered = true);
            return Results.Json(
                new Nexus2MigrationDismissResponse { Dismissed = true },
                AppJsonContext.Default.Nexus2MigrationDismissResponse);
        }).LocalhostOnly();

        app.MapPost("/migration/nexus2/disable-autostart", (INexus2Detector detector) =>
        {
            var response = detector.DisableAutostart()
                ? ApiResponse.Ok("Nexus 2 autostart task disabled")
                : ApiResponse.Fail("Could not disable the Nexus 2 autostart task");
            return Results.Json(response, AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();

        app.MapPost("/migration/nexus2/close-app", async (INexus2Detector detector) =>
        {
            var response = await detector.CloseAppAsync()
                ? ApiResponse.Ok("Nexus 2 closed")
                : ApiResponse.Fail("Could not close Nexus 2");
            return Results.Json(response, AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();

        app.MapPost("/migration/nexus2/uninstall", async (INexus2Detector detector) =>
        {
            var response = await detector.UninstallAsync()
                ? ApiResponse.Ok("Nexus 2 uninstalled")
                : ApiResponse.Fail("Could not uninstall Nexus 2");
            return Results.Json(response, AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();

        app.MapPost("/migration/nexus2/preview", (Nexus2MigrationService migration) =>
            Results.Json(migration.Preview(), AppJsonContext.Default.Nexus2PreviewResponse)
        ).LocalhostOnly();

        app.MapPost("/migration/nexus2/apply", async (Nexus2ApplyRequest body, Nexus2MigrationService migration) =>
        {
            var result = await migration.ApplyAsync(body.Categories ?? new List<string>(), body.ReplaceCustomizedLayout);
            return Results.Json(result, AppJsonContext.Default.Nexus2ApplyResponse);
        }).LocalhostOnly();
    }

    private static bool IsDeviceEligible(NexusSettings settings)
    {
        foreach (var device in settings.PanelDevices.Values)
        {
            var surface = device.Capabilities?.Surface;
            if (surface == PanelSurfaces.Y70 || surface == PanelSurfaces.Q60)
            {
                return true;
            }
        }
        return false;
    }
}

public sealed class Nexus2MigrationStatusDto
{
    public bool Detected { get; set; }
    public bool ImportAvailable { get; set; }
    public bool DeviceEligible { get; set; }
    public string? Version { get; set; }
    public bool AutostartTaskPresent { get; set; }
    public bool Running { get; set; }
    public bool Pending { get; set; }
}

public sealed class Nexus2MigrationDismissResponse
{
    public bool Dismissed { get; set; }
}

/// <summary>One migration category's read-only preview. Fields not relevant
/// to <see cref="Id"/> stay null/default and are omitted from the wire JSON.</summary>
public sealed class Nexus2PreviewCategoryDto
{
    public string Id { get; set; } = "";
    public bool Available { get; set; }
    // appearance
    public string? AccentColor { get; set; }
    public string? Background { get; set; }
    // y70Layout
    public int? Pages { get; set; }
    public int? Widgets { get; set; }
    public int? MappedWidgets { get; set; }
    public List<string>? DroppedTypes { get; set; }
    // q60Face
    public string? Face { get; set; }
    public int? StashedFaces { get; set; }
    // wallpapers / gallerySources
    public int? Count { get; set; }
    public int? Missing { get; set; }
    // rotation / language
    public string? Value { get; set; }
}

public sealed class Nexus2PreviewResponse
{
    public bool Available { get; set; }
    public string? ProfileName { get; set; }
    public List<Nexus2PreviewCategoryDto> Categories { get; set; } = new();
}

public sealed class Nexus2ApplyRequest
{
    public List<string>? Categories { get; set; }
    public bool ReplaceCustomizedLayout { get; set; }
}

public sealed class Nexus2ApplyResultDto
{
    public string Id { get; set; } = "";
    /// <summary>"applied" | "skipped" | "failed" | "needsConfirm".</summary>
    public string Status { get; set; } = "";
    /// <summary>Machine hint (e.g. "no-q60-record", "layout-customized"), not prose - the web localizes it.</summary>
    public string? Detail { get; set; }
}

public sealed class Nexus2ApplyResponse
{
    public List<Nexus2ApplyResultDto> Results { get; set; } = new();
}
