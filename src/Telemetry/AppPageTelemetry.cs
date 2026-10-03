namespace Nexus.Service.Telemetry;

public static class AppPageTelemetry
{
    private static readonly AppTelemetryRateLimiter DefaultLimiter = new(30);

    /// <summary>Drops silently beyond the per-app budget; the route still answers 200.</summary>
    public static void CaptureOpened(ITelemetry telemetry, string appId, string appVersion, AppTelemetryRateLimiter? limiter = null)
    {
        if (!(limiter ?? DefaultLimiter).TryAcquire(appId)) return;
        telemetry.Capture(TelemetryEvents.AppPageOpened, ("app_id", appId), ("app_version", appVersion));
    }
}
