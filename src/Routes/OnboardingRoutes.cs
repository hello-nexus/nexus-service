using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Routes;

/// <summary>
/// First-run onboarding state (dashboard-only, loopback). The desktop
/// dashboard shows a one-time welcome screen on first launch, then the
/// feature-pillars screen, then a one-time lighting device-selection screen,
/// and marks each complete so it never reappears, unless a factory reset
/// wipes settings.json.
/// Dashboard-only by design - a paired phone has no onboarding, so these
/// are <see cref="LocalhostOnlyEndpointExtensions.LocalhostOnly"/> (no
/// .AllowPanel()).
///   GET  /onboarding                    -> { completed, featuresCompleted, lightingCompleted }
///   POST /onboarding/complete           -> set completed, return status
///   POST /onboarding/features-complete  -> set featuresCompleted, return status
///   POST /onboarding/lighting-complete  -> set lightingCompleted, return status
/// </summary>
internal static class OnboardingRoutes
{
    public static void MapOnboardingEndpoints(this WebApplication app)
    {
        // Existing installs start open; a fresh one holds notifications until
        // the sequence ends. featuresCompleted sits before lighting-complete
        // in the sequence, so it does not gate the notification hold - only
        // the last step (lighting) matters for when it releases.
        var initial = app.Services.GetRequiredService<IConfigStore>().Load();
        Nexus.Service.Notifications.NotificationGate.Initialize(
            initial.OnboardingCompleted && initial.LightingOnboardingCompleted);

        app.MapGet("/onboarding", (IConfigStore store) =>
            Results.Ok(Status(store))).LocalhostOnly();

        app.MapPost("/onboarding/complete", (IConfigStore store, Nexus.Service.Telemetry.ITelemetry telemetry) =>
        {
            // Fires on a false->true transition only, so a repeat post is
            // silent. NOT strictly once per install: POST /onboarding/reset
            // clears the flag, so a machine that re-runs onboarding reports
            // again. Count distinct installs, not events.
            if (!store.Load().OnboardingCompleted)
                telemetry.Capture(Nexus.Service.Telemetry.TelemetryEvents.OnboardingCompleted);
            store.Update(s => s.OnboardingCompleted = true);
            // A skip completes both flags at once; releasing here covers it.
            if (store.Load().LightingOnboardingCompleted)
            {
                _ = Nexus.Service.Notifications.NotificationGate.ReleaseAsync(Nexus.Service.Notifications.NotificationGate.ReasonOnboarding);
            }
            return Results.Ok(Status(store));
        }).LocalhostOnly();

        app.MapPost("/onboarding/features-complete", (IConfigStore store) =>
        {
            store.Update(s => s.FeaturesOnboardingCompleted = true);
            return Results.Ok(Status(store));
        }).LocalhostOnly();

        app.MapPost("/onboarding/lighting-complete", (IConfigStore store) =>
        {
            store.Update(s => s.LightingOnboardingCompleted = true);
            // Last server-side step: anything held while the screens owned the
            // display goes out now.
            _ = Nexus.Service.Notifications.NotificationGate.ReleaseAsync(Nexus.Service.Notifications.NotificationGate.ReasonOnboarding);
            return Results.Ok(Status(store));
        }).LocalhostOnly();

        // Replays the whole sequence. The two import latches are cleared as
        // well, or the import step is silently skipped - they are per-app flags
        // the migration routes own, not onboarding ones. An app that is no
        // longer installed still will not be offered: that gate also requires
        // live detection.
        app.MapPost("/onboarding/reset", (IConfigStore store) =>
        {
            store.Update(s =>
            {
                s.OnboardingCompleted = false;
                s.FeaturesOnboardingCompleted = false;
                s.LightingOnboardingCompleted = false;
                s.Nexus2MigrationOffered = false;
                s.FanControlImportOffered = false;
            });
            // Re-close, or notifications keep firing through the replay.
            Nexus.Service.Notifications.NotificationGate.Initialize(onboardingComplete: false);
            return Results.Ok(Status(store));
        }).LocalhostOnly();
    }

    private static OnboardingStatusDto Status(IConfigStore store)
    {
        var s = store.Load();
        return new OnboardingStatusDto
        {
            Completed = s.OnboardingCompleted,
            FeaturesCompleted = s.FeaturesOnboardingCompleted,
            LightingCompleted = s.LightingOnboardingCompleted,
        };
    }
}

public sealed class OnboardingStatusDto
{
    public bool Completed { get; set; }
    public bool FeaturesCompleted { get; set; }
    public bool LightingCompleted { get; set; }
}
