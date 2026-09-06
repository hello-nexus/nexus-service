namespace Nexus.Service.Telemetry;

/// <summary>
/// Canonical event names. Add one line here and reference it at the call site -
/// never a raw string literal (keeps the taxonomy greppable and typo-proof).
/// snake_case to match PostHog conventions.
/// </summary>
public static class TelemetryEvents
{
    public const string AppStarted = "app_started";
    public const string DeviceConnected = "device_connected";
    public const string DeviceDisconnected = "device_disconnected";
    public const string FanSpeedSet = "fan_speed_set";
    public const string FanCurveApplied = "fan_curve_applied";
    public const string LightingEffectApplied = "lighting_effect_applied";
    public const string WidgetOpened = "widget_opened";
    public const string FirmwareFlashed = "firmware_flashed";
    public const string PairCompleted = "pair_completed";

    // Usage shape. Panel widgets are persistent, not "opened", so what people
    // actually run is a periodic census (LayoutSnapshot) rather than a click
    // count; the add/remove pair carries intent - what was tried and dropped.
    public const string LayoutSnapshot = "layout_snapshot";
    public const string WidgetAdded = "widget_added";
    public const string WidgetRemoved = "widget_removed";

    /// <summary>Lighting/cooling page density switched between "simple" and
    /// "advanced". The current value also rides the person profile; this event
    /// is the only way to tell a deliberate switch from the v15 migration,
    /// which seeded every pre-existing install to "advanced".</summary>
    public const string DashboardModeChanged = "dashboard_mode_changed";

    public const string OnboardingCompleted = "onboarding_completed";

    // Fleet events (dual-sink: PostHog + nexus-api /telemetry/events). The
    // string doubles as the wire "type" field nexus-api expects.
    public const string Install = "install";
    public const string Specs = "specs";
    public const string OptOut = "opt_out";
    public const string OptIn = "opt_in";
}
