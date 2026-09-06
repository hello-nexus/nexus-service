using Nexus.Service.Auth;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Models.Profiles;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

public static class ProfileRoutes
{
    public static void MapProfileEndpoints(this WebApplication app)
    {
        app.MapGet("/profiles", (ProfileManager pm) =>
        {
            var manifest = pm.GetManifest();
            return new ListProfilesResponse
            {
                Profiles = manifest.Profiles,
                ActiveId = manifest.ActiveProfileId,
            };
        });

        app.MapPost("/profiles/create", (CreateProfileBody body, ProfileManager pm) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return Results.BadRequest(ApiResponse.Fail("Name is required."));
            }

            try
            {
                var entry = pm.CreateProfile(body.Name);
                return Results.Ok(new ProfileResponse { Profile = entry });
            }
            catch (ProfileNameConflictException)
            {
                return Results.Conflict(ApiResponse.Fail("profile_name_taken"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
        });

        app.MapPost("/profiles/{id}/switch", (string id, ProfileManager pm, IConfigStore store, MultiplexHub hub) =>
        {
            try
            {
                pm.SwitchProfile(id);
                var s = store.Load();
                var prefs = new Preferences
                {
                    Theme = s.Theme,
                    Panel = s.Panel,
                    Overlay = s.Overlay,
                    Monitoring = s.Monitoring,
                    Cooling = new CoolingPrefs
                    {
                        FanChannelOrder = s.Cooling.FanChannelOrder,
                        PreferredCpuTempSensorId = s.Cooling.PreferredCpuTempSensorId,
                        PreferredGpuTempSensorId = s.Cooling.PreferredGpuTempSensorId,
                        PreferredGpuId = s.Cooling.PreferredGpuId,
                    },
                    Ui = s.Ui,
                    Units = s.Units,
                    Update = new UpdatePrefs
                    {
                        UpdateMode = s.Update.UpdateMode,
                        UpdateChannel = s.Update.UpdateChannel,
                        LastDismissedUpdateVersion = s.Update.LastDismissedUpdateVersion,
                    },
                    Diagnostics = s.Diagnostics,
                };
                // Profile switches swap the entire prefs block - everyone refetches via the broadcast.
                PanelTopics.BroadcastPrefs(hub);
                PanelTopics.BroadcastLighting(hub);
                PanelTopics.BroadcastCooling(hub);
                return Results.Ok(new SwitchProfileResponse { Switched = id, Prefs = prefs });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }
        });

        app.MapPost("/profiles/{id}/rename", (string id, RenameProfileBody body, ProfileManager pm) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return Results.BadRequest(ApiResponse.Fail("Name is required."));
            }

            try
            {
                pm.RenameProfile(id, body.Name);
                var manifest = pm.GetManifest();
                var entry = manifest.Profiles.Find(p => p.Id == id);
                return Results.Ok(new ProfileResponse { Profile = entry });
            }
            catch (ProfileNameConflictException)
            {
                return Results.Conflict(ApiResponse.Fail("profile_name_taken"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }
        });

        app.MapPost("/profiles/{id}/save", (string id, ProfileManager pm) =>
        {
            pm.SaveActiveProfile();
            return ApiResponse.Ok();
        });

        app.MapDelete("/profiles/{id}", (string id, ProfileManager pm) =>
        {
            try
            {
                pm.DeleteProfile(id);
                return Results.Ok(ApiResponse.Ok());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }
        });

        app.MapGet("/profiles/{id}/export", (string id, ProfileManager pm) =>
        {
            var json = pm.ExportProfileJson(id);
            if (json == null)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }

            return Results.Text(json, "application/json");
        });

        app.MapPost("/profiles/import", async (HttpRequest req, ProfileManager pm, MultiplexHub hub, bool? replace) =>
        {
            var json = await new System.IO.StreamReader(req.Body).ReadToEndAsync();
            try
            {
                var entry = pm.ImportProfileJson(json, replace == true);
                if (replace == true)
                {
                    // A replace can overwrite the active profile; refetch the
                    // same way /profiles/{id}/switch does.
                    PanelTopics.BroadcastPrefs(hub);
                    PanelTopics.BroadcastLighting(hub);
                    PanelTopics.BroadcastCooling(hub);
                }
                return Results.Ok(new ProfileResponse { Profile = entry });
            }
            catch (ProfileNameConflictException)
            {
                return Results.Conflict(ApiResponse.Fail("profile_name_taken"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest(ApiResponse.Fail("Invalid profile JSON."));
            }
        });

        app.MapGet("/preferences", (IConfigStore store) =>
        {
            var s = store.Load();
            return new Preferences
            {
                Theme = s.Theme,
                Panel = s.Panel,
                Overlay = s.Overlay,
                Monitoring = s.Monitoring,
                Cooling = new CoolingPrefs
                {
                    FanChannelOrder = s.Cooling.FanChannelOrder,
                    PreferredCpuTempSensorId = s.Cooling.PreferredCpuTempSensorId,
                    PreferredGpuTempSensorId = s.Cooling.PreferredGpuTempSensorId,
                    PreferredGpuId = s.Cooling.PreferredGpuId,
                },
                Ui = s.Ui,
                Units = s.Units,
                Update = new UpdatePrefs
                {
                    UpdateMode = s.Update.UpdateMode,
                    UpdateChannel = s.Update.UpdateChannel,
                    LastDismissedUpdateVersion = s.Update.LastDismissedUpdateVersion,
                },
                Diagnostics = s.Diagnostics,
                StartupDelaySeconds = s.StartupDelaySeconds,
                DisableGpuMonitoring = s.DisableGpuMonitoring,
                Features = s.Features,
            };
        }).AllowPanel();

        // Read current sharing config: which profile is the Primary, which
        // categories are Shared, and the full list of category ids the UI
        // can render.
        app.MapGet("/profiles/sharing", (IConfigStore store) =>
        {
            var s = store.Load();
            var counts = new Dictionary<string, int>();
            foreach (var category in ProfileSharing.All)
            {
                counts[category] = category switch
                {
                    ProfileSharing.Lighting => s.Lighting.LayoutPresets.Count,
                    ProfileSharing.Device => CountDeckPresets(s.StreamDeck),
                    _ => 0,
                };
            }
            return new SharingResponse
            {
                PrimaryProfileId = s.PrimaryProfileId,
                SharedCategories = new List<string>(s.SharedCategories),
                AllCategories = new List<string>(ProfileSharing.All),
                Counts = counts,
            };

            // Decks is a plain Dictionary mutated under the config-store lock on
            // deck hotplug; this handler reads it lock-free, so a structural
            // change during enumeration throws InvalidOperationException. Retry
            // the rare mid-enumeration race instead of 500ing the sharing page.
            // Enumerate the values directly rather than via ToArray: the
            // enumerator throws InvalidOperationException on a concurrent
            // add/remove, whereas ValueCollection.CopyTo (ToArray's fast path)
            // throws ArgumentException / leaves null slots the catch would miss.
            static int CountDeckPresets(StreamDeckSettings streamDeck)
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        var total = 0;
                        foreach (var deck in streamDeck.Decks.Values)
                        {
                            total += deck.Presets.Count;
                        }
                        return total;
                    }
                    catch (InvalidOperationException) when (attempt < 3)
                    {
                    }
                }
            }
        });

        app.MapPut("/profiles/sharing/primary", (SetPrimaryBody body, ProfileManager pm, MultiplexHub hub) =>
        {
            if (string.IsNullOrWhiteSpace(body.ProfileId))
            {
                return Results.BadRequest(ApiResponse.Fail("profileId is required."));
            }
            try
            {
                pm.SetPrimary(body.ProfileId);
                PanelTopics.BroadcastPrefs(hub);
                PanelTopics.BroadcastLighting(hub);
                PanelTopics.BroadcastCooling(hub);
                return Results.Ok(ApiResponse.Ok());
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }
        });

        app.MapPut("/profiles/sharing/categories", (SetCategorySharedBody body, ProfileManager pm, MultiplexHub hub) =>
        {
            if (string.IsNullOrWhiteSpace(body.Category))
            {
                return Results.BadRequest(ApiResponse.Fail("category is required."));
            }
            try
            {
                var changed = pm.SetCategoryShared(body.Category, body.Shared);
                if (changed)
                {
                    // Toggling a category preserves its data (we never wipe to
                    // defaults here), so the lighting engine MUST keep running
                    // - calling StopAll without a follow-up start request kills
                    // the live effect. Profile switch uses the same machinery:
                    // just broadcast, let the client refetch and resync.
                    PanelTopics.BroadcastPrefs(hub);
                    PanelTopics.BroadcastLighting(hub);
                    PanelTopics.BroadcastCooling(hub);
                }
                return Results.Ok(ApiResponse.Ok());
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
        });

        // Reset every per-profile (non-shared) category on the named profile.
        // Shared categories are untouched. If the named profile is the active
        // one, in-memory state is updated and a broadcast fires; otherwise the
        // reset only touches that profile's JSON on disk.
        app.MapPost("/profiles/{id}/reset", (string id, ProfileManager pm, ILightingProvider lp, IFanControlProvider fans, MultiplexHub hub, IConfigStore store, Nexus.Service.Lifecycle.FeatureGates gates) =>
        {
            try
            {
                // Halt the live lighting engine when the reset clears in-memory
                // lighting state: the named profile is the active one (reset
                // touches ALL categories on active), or the named profile is
                // the Primary and lighting is shared (the active profile's
                // in-memory shared lighting also clears to reflect the reset).
                var settings = store.Load();
                var isActive = id == pm.GetManifest().ActiveProfileId;
                var isPrimary = settings.PrimaryProfileId == id;
                var lightingShared = settings.SharedCategories.Contains(ProfileSharing.Lighting);
                if (isActive || (isPrimary && lightingShared))
                {
                    try
                    { lp.StopAll(); }
                    catch { }
                }
                pm.ResetProfile(id);
                // Re-engage engines from the freshly-defaulted settings so the
                // "on by default" cooling preset + lighting sync mode run
                // instead of leaving the engines idle.
                if (isActive)
                {
                    LiveEngineSync.Apply(store, fans, lp, gates);
                }
                PanelTopics.BroadcastPrefs(hub);
                PanelTopics.BroadcastLighting(hub);
                PanelTopics.BroadcastCooling(hub);
                return Results.Ok(ApiResponse.Ok());
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }
        });

        // Reset a single category. If the category is shared, the reset is
        // redirected to the Primary's data and affects every profile. If the
        // category is per-profile, only the named profile's JSON is touched
        // (and in-memory state if the named profile is active).
        app.MapPost("/profiles/{id}/reset/{category}", (string id, string category, ProfileManager pm, ILightingProvider lp, IFanControlProvider fans, MultiplexHub hub, IConfigStore store, Nexus.Service.Lifecycle.FeatureGates gates) =>
        {
            try
            {
                var normalized = ProfileSharing.Normalize(category);
                var settings = store.Load();
                var isActive = id == pm.GetManifest().ActiveProfileId;
                // Capture the shared flag for the reset category BEFORE the
                // reset runs, so the post-reset re-engage decision reads the
                // value that was in force at request time.
                var categoryIsShared = normalized != null
                    && settings.SharedCategories.Contains(normalized);
                if (normalized == ProfileSharing.Lighting)
                {
                    // StopAll only when the reset flips the live engine: shared
                    // category writes through to in-memory active state (always
                    // changes), or per-profile reset on the active profile.
                    // Per-profile reset on a different profile only touches a
                    // stored JSON, so leave the live engine alone.
                    if (categoryIsShared || isActive)
                    {
                        try
                        { lp.StopAll(); }
                        catch { }
                    }
                }
                pm.ResetCategory(id, category);
                // Re-engage engines from the freshly-defaulted settings when
                // the live state changed (active profile, or shared category
                // that writes through to active).
                if (isActive || categoryIsShared)
                {
                    LiveEngineSync.Apply(store, fans, lp, gates);
                }
                PanelTopics.BroadcastPrefs(hub);
                PanelTopics.BroadcastLighting(hub);
                PanelTopics.BroadcastCooling(hub);
                return Results.Ok(ApiResponse.Ok());
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
        });

        app.MapPost("/preferences", (PreferencesPatch body, ProfileManager pm, MultiplexHub hub, Nexus.Service.Lifecycle.FeatureReconciler reconciler, Nexus.Service.Telemetry.ITelemetry telemetry) =>
        {
            // Captured inside the patch, emitted after it: the current mode
            // also rides the telemetry person profile, but only a recorded
            // transition separates a deliberate switch from the v15 migration
            // that seeded every pre-existing install to "advanced".
            (string From, string To)? lightingModeChange = null;
            (string From, string To)? coolingModeChange = null;
            // ApplyPatch runs the mutation and the Features before/after
            // transition under the reconciler's own lock, so a concurrent
            // PATCH /preferences cannot interleave its own before-snapshot
            // or mutation with this one.
            reconciler.ApplyPatch(s =>
            {
                if (body.Theme is { } theme)
                {
                    if (theme.Language is not null)    s.Theme.Language    = theme.Language;
                    if (theme.ThemeMode is not null)   s.Theme.ThemeMode   = theme.ThemeMode;
                    if (theme.AccentColor is not null) s.Theme.AccentColor = theme.AccentColor;
                    if (theme.ResolvedThemeMode is not null) s.Theme.ResolvedThemeMode = theme.ResolvedThemeMode;
                    if (theme.BackgroundMode is not null) s.Theme.BackgroundMode = theme.BackgroundMode;
                    if (theme.AccentSource is not null) s.Theme.AccentSource = theme.AccentSource;
                    if (theme.CustomAccentColor is not null) s.Theme.CustomAccentColor = theme.CustomAccentColor;
                }
                if (body.Panel is { } panel)
                {
                    if (panel.AutoLaunch.HasValue)               s.Panel.AutoLaunch            = panel.AutoLaunch.Value;
                    if (panel.ReserveMonitor.HasValue)           s.Panel.ReserveMonitor        = panel.ReserveMonitor.Value;
                    if (panel.ThemeSyncWithDesktop.HasValue)     s.Panel.ThemeSyncWithDesktop  = panel.ThemeSyncWithDesktop.Value;
                    if (panel.ThemeMode is not null)             s.Panel.ThemeMode             = panel.ThemeMode;
                    if (panel.AccentSyncWithDesktop.HasValue)    s.Panel.AccentSyncWithDesktop = panel.AccentSyncWithDesktop.Value;
                    if (panel.AccentColor is not null)           s.Panel.AccentColor           = panel.AccentColor;
                    if (panel.BackgroundColor is not null)       s.Panel.BackgroundColor       = panel.BackgroundColor;
                    if (panel.BackgroundColorLight is not null)  s.Panel.BackgroundColorLight  = panel.BackgroundColorLight;
                    if (panel.BackgroundMode is not null)        s.Panel.BackgroundMode        = panel.BackgroundMode;
                    if (panel.BackgroundEffect is not null)      s.Panel.BackgroundEffect      = panel.BackgroundEffect;
                    if (panel.BackgroundTemplate.HasValue)       s.Panel.BackgroundTemplate    = panel.BackgroundTemplate.Value;
                    if (panel.BackgroundOpacity.HasValue)        s.Panel.BackgroundOpacity     = panel.BackgroundOpacity.Value;
                    if (panel.PanelOpacity.HasValue)             s.Panel.PanelOpacity          = Math.Clamp(panel.PanelOpacity.Value, 0.0, 1.0);
                    if (panel.WidgetOpacity.HasValue)            s.Panel.WidgetOpacity         = panel.WidgetOpacity.Value;
                    if (panel.WidgetLabels.HasValue)             s.Panel.WidgetLabels          = panel.WidgetLabels.Value;
                    if (panel.DashboardLayout is not null)       s.Panel.DashboardLayout       = panel.DashboardLayout;
                }
                if (body.Overlay is { } overlay)
                {
                    if (overlay.Enabled.HasValue)      s.Overlay.Enabled     = overlay.Enabled.Value;
                    if (overlay.AlwaysOnTop.HasValue)  s.Overlay.AlwaysOnTop = overlay.AlwaysOnTop.Value;
                    if (overlay.Scale.HasValue)
                    {
                        var newScale = Math.Clamp(overlay.Scale.Value, 50, 200);
                        var oldScale = s.Overlay.Scale > 0 ? s.Overlay.Scale : 100;
                        // Rescale every pinned widget's (col, row) so the *visual*
                        // position stays put when the cell-size scale changes.
                        // ratio = old / new because at 100→200% a widget at col 4
                        // should land at col 2 (half as many cells cover the same
                        // pixels). Snap to the 0.25 drag grid and clamp ≥0; max
                        // clamp happens client-side where monitor size is known.
                        if (oldScale != newScale && s.Overlay.Layout is { } layout)
                        {
                            var ratio = (double)oldScale / newScale;
                            foreach (var w in layout)
                            {
                                w.Col = Math.Max(0, Math.Round(w.Col * ratio * 4) / 4);
                                w.Row = Math.Max(0, Math.Round(w.Row * ratio * 4) / 4);
                            }
                        }
                        s.Overlay.Scale = newScale;
                    }
                    if (overlay.Opacity.HasValue)
                    {
                        s.Overlay.Opacity = Math.Clamp(overlay.Opacity.Value, 0, 1);
                    }
                    if (overlay.Monitor.HasValue)
                    {
                        // -1 (primary) or any non-negative index. Don't clamp to a
                        // max here - the overlay host validates against the
                        // enumerated monitor count and falls back to primary if
                        // the index is out of range.
                        var v = overlay.Monitor.Value;
                        s.Overlay.Monitor = v < -1 ? -1 : v;
                    }
                    if (overlay.Layout is not null) s.Overlay.Layout = overlay.Layout;
                }
                if (body.Monitoring is { } monitoring)
                {
                    if (monitoring.ShowMacStatusBarIcon.HasValue) s.Monitoring.ShowMacStatusBarIcon = monitoring.ShowMacStatusBarIcon.Value;
                    if (monitoring.ShowWindowsTrayIcon.HasValue)  s.Monitoring.ShowWindowsTrayIcon  = monitoring.ShowWindowsTrayIcon.Value;
                    if (monitoring.DetailedCollapsed is not null) s.Monitoring.DetailedCollapsed   = monitoring.DetailedCollapsed;
                    if (monitoring.EventsEnabled.HasValue)        s.Monitoring.EventsEnabled        = monitoring.EventsEnabled.Value;
                    if (monitoring.FpsOverlayEnabled.HasValue)    s.Monitoring.FpsOverlayEnabled    = monitoring.FpsOverlayEnabled.Value;
                    if (monitoring.EventKindsHidden is not null)  s.Monitoring.EventKindsHidden     = monitoring.EventKindsHidden;
                    if (monitoring.SmartPollSeconds is not null)  s.Monitoring.SmartPollSeconds     = SmartPollPolicy.Sanitize(monitoring.SmartPollSeconds);
                    if (monitoring.SmartPollDefaultSeconds.HasValue)
                        s.Monitoring.SmartPollDefaultSeconds = SmartPollPolicy.ClampSeconds(monitoring.SmartPollDefaultSeconds.Value);
                    if (monitoring.SmartPollPerDrive.HasValue) s.Monitoring.SmartPollPerDrive = monitoring.SmartPollPerDrive.Value;
                }
                if (body.Cooling is { } cooling)
                {
                    if (cooling.FanChannelOrder is not null) s.Cooling.FanChannelOrder = cooling.FanChannelOrder;
                    // Empty string is a meaningful "clear back to auto" value, distinct
                    // from null which means "client didn't send this field".
                    if (cooling.PreferredCpuTempSensorId is not null)
                        s.Cooling.PreferredCpuTempSensorId = cooling.PreferredCpuTempSensorId.Length == 0 ? null : cooling.PreferredCpuTempSensorId;
                    if (cooling.PreferredGpuTempSensorId is not null)
                        s.Cooling.PreferredGpuTempSensorId = cooling.PreferredGpuTempSensorId.Length == 0 ? null : cooling.PreferredGpuTempSensorId;
                    if (cooling.PreferredGpuId is not null)
                        s.Cooling.PreferredGpuId = cooling.PreferredGpuId.Length == 0 ? null : cooling.PreferredGpuId;
                }
                if (body.Ui is { } ui)
                {
                    if (ui.ShowConflictAlerts.HasValue) s.Ui.ShowConflictAlerts = ui.ShowConflictAlerts.Value;
                    if (ui.AutoKillConflictsAtStartup.HasValue) s.Ui.AutoKillConflictsAtStartup = ui.AutoKillConflictsAtStartup.Value;
                    if (ui.ConflictAutoKillExclusions is not null) s.Ui.ConflictAutoKillExclusions = ui.ConflictAutoKillExclusions;
                    if (ui.OemAppSeeded.HasValue) s.Ui.OemAppSeeded = ui.OemAppSeeded.Value;
                    if (ui.PinnedSidebarApps is not null) s.Ui.PinnedSidebarApps = ui.PinnedSidebarApps;
                    if (ui.LightingDashboardMode is "simple" or "advanced")
                    {
                        if (!string.Equals(s.Ui.LightingDashboardMode, ui.LightingDashboardMode, StringComparison.Ordinal))
                            lightingModeChange = (s.Ui.LightingDashboardMode, ui.LightingDashboardMode);
                        s.Ui.LightingDashboardMode = ui.LightingDashboardMode;
                    }
                    if (ui.CoolingDashboardMode is "simple" or "advanced")
                    {
                        if (!string.Equals(s.Ui.CoolingDashboardMode, ui.CoolingDashboardMode, StringComparison.Ordinal))
                            coolingModeChange = (s.Ui.CoolingDashboardMode, ui.CoolingDashboardMode);
                        s.Ui.CoolingDashboardMode = ui.CoolingDashboardMode;
                    }
                }
                if (body.Units is { } units)
                {
                    if (units.MonitoringTempUnit is { } v1) s.Units.MonitoringTempUnit = v1;
                    if (units.TimeFormat is { } v2) s.Units.TimeFormat = v2;
                    if (units.NumberFormat is { } v3) s.Units.NumberFormat = v3;
                }
                if (body.Update is { } update)
                {
                    if (update.UpdateMode is "notify" or "download" or "always") s.Update.UpdateMode = update.UpdateMode;
                    if (update.UpdateChannel is not null) s.Update.UpdateChannel = update.UpdateChannel;
                    if (update.LastDismissedUpdateVersion is not null) s.Update.LastDismissedUpdateVersion = update.LastDismissedUpdateVersion;
                }
                // Read once at service start; a change takes effect next boot.
                if (body.StartupDelaySeconds is { } startupDelay)
                {
                    s.StartupDelaySeconds = Math.Clamp(startupDelay, 0, Nexus.Service.Lifecycle.StartupDelayGate.MaxSeconds);
                }
                // Read once at service start; a change takes effect next boot.
                if (body.DisableGpuMonitoring is { } disableGpu)
                {
                    s.DisableGpuMonitoring = disableGpu;
                }
                if (body.Features is { } features)
                {
                    if (features.Lighting.HasValue) s.Features.Lighting = features.Lighting.Value;
                    if (features.Cooling.HasValue) s.Features.Cooling = features.Cooling.Value;
                    if (features.Monitoring.HasValue) s.Features.Monitoring = features.Monitoring.Value;
                    if (features.Diagnostics.HasValue) s.Features.Diagnostics = features.Diagnostics.Value;
                }
                if (body.Diagnostics is { } diagnostics)
                {
                    if (diagnostics.Thresholds is { } thresholds)
                    {
                        if (thresholds.CpuC.HasValue)     s.Diagnostics.Thresholds.CpuC     = thresholds.CpuC.Value;
                        if (thresholds.GpuC.HasValue)     s.Diagnostics.Thresholds.GpuC     = thresholds.GpuC.Value;
                        if (thresholds.StorageC.HasValue) s.Diagnostics.Thresholds.StorageC = thresholds.StorageC.Value;
                        if (thresholds.RamC.HasValue)     s.Diagnostics.Thresholds.RamC     = thresholds.RamC.Value;
                    }
                    if (diagnostics.WarningLingerMinutes.HasValue)
                        s.Diagnostics.WarningLingerMinutes = Math.Max(0, diagnostics.WarningLingerMinutes.Value);
                    if (diagnostics.Notifications is { } notifications)
                    {
                        if (notifications.Enabled.HasValue)        s.Diagnostics.Notifications.Enabled        = notifications.Enabled.Value;
                        if (notifications.HighTemp.HasValue)       s.Diagnostics.Notifications.HighTemp       = notifications.HighTemp.Value;
                        if (notifications.StorageHealth.HasValue)  s.Diagnostics.Notifications.StorageHealth  = notifications.StorageHealth.Value;
                        if (notifications.Cooling.HasValue)        s.Diagnostics.Notifications.Cooling        = notifications.Cooling.Value;
                        if (notifications.MemoryTest.HasValue)     s.Diagnostics.Notifications.MemoryTest     = notifications.MemoryTest.Value;
                        if (notifications.SystemDevices.HasValue)  s.Diagnostics.Notifications.SystemDevices  = notifications.SystemDevices.Value;
                        if (notifications.GpuThrottle.HasValue)    s.Diagnostics.Notifications.GpuThrottle    = notifications.GpuThrottle.Value;
                        if (notifications.CooldownMinutes.HasValue)
                            s.Diagnostics.Notifications.CooldownMinutes = Math.Max(0, notifications.CooldownMinutes.Value);
                    }
                    if (diagnostics.Components is { } components)
                    {
                        if (components.Cpu.HasValue)     s.Diagnostics.Components.Cpu     = components.Cpu.Value;
                        if (components.Gpu.HasValue)     s.Diagnostics.Components.Gpu     = components.Gpu.Value;
                        if (components.Storage.HasValue) s.Diagnostics.Components.Storage = components.Storage.Value;
                        if (components.Ram.HasValue)     s.Diagnostics.Components.Ram     = components.Ram.Value;
                        if (components.Cooling.HasValue) s.Diagnostics.Components.Cooling = components.Cooling.Value;
                        if (components.System.HasValue)  s.Diagnostics.Components.System  = components.System.Value;
                    }
                }
            }, lightingPatchValue: body.Features?.Lighting);
            if (lightingModeChange is { } lm)
            {
                telemetry.Capture(Nexus.Service.Telemetry.TelemetryEvents.DashboardModeChanged,
                    ("surface", "lighting"), ("from", lm.From), ("to", lm.To));
            }
            if (coolingModeChange is { } cm)
            {
                telemetry.Capture(Nexus.Service.Telemetry.TelemetryEvents.DashboardModeChanged,
                    ("surface", "cooling"), ("from", cm.From), ("to", cm.To));
            }
            pm.MarkDirty();
            PanelTopics.BroadcastPrefs(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
    }
}
