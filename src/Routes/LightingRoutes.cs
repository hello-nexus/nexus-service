using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Models;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

public static class LightingRoutes
{
    // Presence alone made ?frozen=0 and ?frozen=false freeze the render.
    private static bool IsTruthy(Microsoft.Extensions.Primitives.StringValues v)
    {
        if (v.Count == 0) return false;
        var s = v.ToString();
        return s.Length == 0 || !(s is "0" or "false" or "False" or "FALSE");
    }

    private const int MaxSunLabelLength = 80;

    private static Models.Lighting.BrightnessScheduleBody BuildBrightnessScheduleBody(
        Nexus.Service.Persistence.BrightnessSchedule schedule, Nexus.Service.Lighting.Sun.SunLocationResolver resolver, bool forceResolve = false)
    {
        // The lookup runs in the background; this response reports no-location until it lands.
        if (Nexus.Service.Lighting.Sun.SunShift.NeedsAutoLocation(schedule))
        {
            resolver.Request(forceResolve);
        }
        var location = Nexus.Service.Lighting.Sun.SunShift.Effective(schedule);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var plan = Nexus.Service.Lighting.Sun.SunShift.PlanFor(location, today, schedule.SunNightLevel, schedule.SunDayLevel);
        var sun = new Models.Lighting.BrightnessScheduleSun
        {
            Date = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Status = plan is null ? "no-location" : plan.Day.Status switch
            {
                Nexus.Service.Lighting.Sun.SunStatus.Ok => "ok",
                Nexus.Service.Lighting.Sun.SunStatus.PolarDay => "polar-day",
                Nexus.Service.Lighting.Sun.SunStatus.PolarNight => "polar-night",
                _ => "no-location",
            },
            SunriseMin = plan?.Day.SunriseMin,
            SunsetMin = plan?.Day.SunsetMin,
            ResolvedLocation = location is null ? null : ToDto(location),
            AutoLocation = Nexus.Service.Lighting.Sun.SunShift.AutoLocation is { } auto ? ToDto(auto) : null,
        };
        if (plan is not null)
        {
            for (var i = 0; i < plan.Minutes.Length; i++)
            {
                sun.SunPoints.Add(new() { Min = plan.Minutes[i], Brightness = plan.Levels[i] });
            }
        }
        return new Models.Lighting.BrightnessScheduleBody
        {
            Enabled = schedule.Enabled,
            Points = schedule.Points ?? new(),
            Defaults = Nexus.Service.Lighting.MasterBrightness.DefaultSchedule(),
            SunShift = schedule.SunShiftOn,
            SunNight = schedule.SunNightLevel,
            SunDay = schedule.SunDayLevel,
            Location = schedule.SunLocation is null ? null : ToDto(schedule.SunLocation),
            Sun = sun,
        };
    }

    private static Models.Lighting.SunLocationDto ToDto(Nexus.Service.Persistence.WeatherSavedLocation l) =>
        new() { Lat = l.Lat, Lon = l.Lon, Label = l.Label, Cc = l.Cc };

    public static void MapLightingEndpoints(this WebApplication app)
    {
        app.MapPost("/lighting/stop", (ILightingProvider l, MultiplexHub hub) => { l.StopAll(); ResetEffectSignature(); PanelTopics.BroadcastLighting(hub); return ApiResponse.Ok(); }).AllowPanel();
        app.MapGet("/lighting/current", (ILightingProvider l, Nexus.Service.Persistence.IConfigStore store) => new CurrentSyncResponse { Sync = l.GetSync(), Paused = l.IsPaused, StudioLive = l.StudioLiveActive, GameSyncRemovedNotice = store.Load().GameSyncRemovedNotice }).AllowPanel();
        // Freeze/resume the active effect's rendered frame. No-op with no active
        // effect (engine not running); returns the resulting paused state either way.
        app.MapPost("/lighting/pause", (Models.Lighting.LightingPauseBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.SetPaused(body.Paused);
            PanelTopics.BroadcastLighting(hub);
            return Results.Json(new Models.Lighting.LightingPauseResponse { Paused = l.IsPaused }, AppJsonContext.Default.LightingPauseResponse);
        }).AllowPanel();
        app.MapGet("/lighting/animate/settings", (Nexus.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.Animate).AllowPanel();
        app.MapGet("/lighting/static/settings", (Nexus.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.Static).AllowPanel();
        // Canonical default template bundles. The web keeps no copy of these
        // tables; it merges this over the sparse user deltas from
        // /lighting/animate/settings. Static per binary, so clients revalidate
        // against the content ETag and get cheap 304s.
        app.MapGet("/lighting/animate/defaults", (HttpRequest req, HttpResponse res) =>
        {
            var etag = AnimateTemplateDefaults.ETag;
            res.Headers.ETag = etag;
            res.Headers.CacheControl = "no-cache";
            if (string.Equals(req.Headers.IfNoneMatch.ToString(), etag, StringComparison.Ordinal))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            return Results.Bytes(AnimateTemplateDefaults.SerializedJson, "application/json");
        }).AllowPanel();
        app.MapPost("/lighting/animate/templates", (SetAnimateTemplatesBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            // Persist + reconcile: if the edited slot is the one driving the LEDs,
            // the provider pushes the new look to the running shader in place.
            l.SaveAnimateTemplates(body.Templates ?? new());
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapGet("/lighting/effects/{key}/thumbnail.bmp", (string key, ILightingProvider l, HttpRequest req, HttpResponse res) =>
        {
            var fresh = req.Query.ContainsKey("fresh");
            // Preset slot to render (universal: 4 per effect). ?v is the client's
            // content-bust token for the browser cache; the ETag below is the
            // service's own freshness check.
            var slot = int.TryParse(req.Query["slot"], out var sv) ? sv : 0;
            var result = l.CaptureAnimateThumbnail(key, slot, skipCache: fresh, frozen: IsTruthy(req.Query["frozen"]));
            if (result is null) return Results.NotFound();
            var (bytes, tag) = result.Value;
            var etag = $"\"{tag}\"";
            // ETag is the saved look's content hash + must-revalidate, so the
            // browser may store the BMP but always rechecks: an edit is never
            // pinned behind the old image, even when a surface keeps requesting
            // the same ?v token. Unchanged looks come back as a cheap 304.
            res.Headers.ETag = etag;
            if (!fresh)
            {
                res.Headers.CacheControl = "no-cache";
                if (string.Equals(req.Headers.IfNoneMatch.ToString(), etag, StringComparison.Ordinal))
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }
            }
            else
            {
                res.Headers.CacheControl = "no-store";
            }
            return Results.File(bytes, "image/bmp");
        }).AllowPanel();
        // Music reactive toggle: persists the flag and starts/stops audio capture
        // only when the active effect is audio-reactive.
        app.MapPost("/lighting/music-reactive", (Models.Lighting.MusicReactiveBody body,
            ILightingProvider l,
            MultiplexHub hub) =>
        {
            l.SetMusicReactive(body.Enabled);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapGet("/lighting/music-reactive", (Nexus.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.MusicReactiveBody { Enabled = store.Load().Lighting.MusicReactive }).AllowPanel();
        // Which output audio-reactive capture listens to. The Windows capture
        // loop re-reads it periodically, so no restart is needed here.
        app.MapGet("/lighting/audio-output", (Nexus.Service.Persistence.IConfigStore store) =>
        {
            var l = store.Load().Lighting;
            return new Models.Lighting.AudioOutputBody
            {
                DeviceId = l.AudioOutputDeviceId,
                DeviceName = l.AudioOutputDeviceName,
                Direction = l.AudioOutputDeviceDirection == "input" ? "input" : "output",
            };
        }).AllowPanel();
        app.MapPost("/lighting/audio-output", (Models.Lighting.AudioOutputBody body,
            Nexus.Service.Persistence.IConfigStore store,
            MultiplexHub hub) =>
        {
            var direction = string.IsNullOrEmpty(body.Direction) ? "output" : body.Direction;
            if (direction != "output" && direction != "input")
            {
                return Results.Json(ApiResponse.Fail("direction must be output or input"), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status400BadRequest);
            }
            store.Update(s =>
            {
                s.Lighting.AudioOutputDeviceId = body.DeviceId ?? "";
                s.Lighting.AudioOutputDeviceName = body.DeviceName ?? "";
                s.Lighting.AudioOutputDeviceDirection = direction;
            });
            // Settings and the lighting canvas each show the pick; the bump makes the other re-read it.
            PanelTopics.BroadcastLighting(hub);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).AllowPanel();
        // Blank lighting while the host sleeps. Host-only: this is a property of
        // the machine going to sleep, not something a paired phone should flip.
        app.MapGet("/lighting/sleep-blackout", (Nexus.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.SleepBlackoutBody { Enabled = store.Load().Lighting.SleepBlackout }).LocalhostOnly();
        app.MapPost("/lighting/sleep-blackout", (Models.Lighting.SleepBlackoutBody body,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.SleepBlackoutCoordinator blackout) =>
        {
            store.Update(s => s.Lighting.SleepBlackout = body.Enabled);
            // Turning it off mid-blackout (only reachable if a resume event was
            // missed) must give the user their lighting back, not leave them dark.
            if (!body.Enabled)
            {
                blackout.OnResumed();
            }
            return ApiResponse.Ok();
        }).LocalhostOnly();
        // Blank lighting while the session is locked. Host-only for the same
        // reason as sleep-blackout: it is a property of this machine.
        app.MapGet("/lighting/lock-blackout", (Nexus.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.LockBlackoutBody { Enabled = store.Load().Lighting.LockBlackout }).LocalhostOnly();
        app.MapPost("/lighting/lock-blackout", (Models.Lighting.LockBlackoutBody body,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.SleepBlackoutCoordinator blackout) =>
        {
            store.Update(s => s.Lighting.LockBlackout = body.Enabled);
            // Turning it off while a lock blackout is engaged (a missed unlock
            // event) must give the lighting back, not leave the user dark.
            if (!body.Enabled)
            {
                blackout.OnSessionUnlocked();
            }
            return ApiResponse.Ok();
        }).LocalhostOnly();
        app.MapGet("/lighting/shaders/{name}", (string name, Nexus.Service.Lighting.CustomEffects.CustomEffectStore custom) =>
        {
            name = name.ToLowerInvariant();
            if (ShaderLibrary.IsCustomKey(name))
            {
                // Read through the store so the first request after a boot sees the persisted source.
                var entry = custom.Get(name[ShaderLibrary.CustomPrefix.Length..]);
                return entry is null
                    ? Results.NotFound()
                    : Results.Json(ToShaderSource(entry.Frag, Nexus.Service.Lighting.Engine.Gpu.SensorUniformSpec.Parse(entry.Frag)), AppJsonContext.Default.ShaderSourceResponse);
            }
            if (!ShaderLibrary.AllEffectKeys.Contains(name))
                return Results.NotFound();
            return Results.Json(ToShaderSource(ShaderLibrary.Get(name), ShaderLibrary.SensorUniforms(name)), AppJsonContext.Default.ShaderSourceResponse);
        }).AllowPanel();
        MapCustomEffects(app);
        MapStudioLive(app);
#if DEV_TOOLS
        EffectMarketRoutes.Map(app);
#endif
        app.MapGet("/lighting/screen/monitors", (ILightingProvider l) => l.GetScreenSyncOptions()).AllowPanel();
        app.MapGet("/lighting/status", (Nexus.Service.Lighting.Engine.LightingEngine engine, ILightingDeviceProvider devices, IServiceProvider sp) =>
        {
            // OpenRgbProcessManager only exists on Windows/macOS; resolve optionally so Linux returns false.
            var pm = sp.GetService(typeof(Nexus.Service.Lighting.Rgb.OpenRgbProcessManager)) as Nexus.Service.Lighting.Rgb.OpenRgbProcessManager;
            var gpu = sp.GetService(typeof(Nexus.Service.Lighting.Engine.Gpu.GpuContext)) as Nexus.Service.Lighting.Engine.Gpu.GpuContext;
            var bridge = sp.GetService(typeof(Nexus.Service.Lighting.Rgb.RgbBridge)) as Nexus.Service.Lighting.Rgb.RgbBridge;
            var rescanning = bridge?.IsRescanning ?? false;
            return new Nexus.Service.Models.Cooling.LightingStatusResponse
            {
                Effect = engine.CurrentEffectName,
                Running = engine.CurrentEffectName != "none",
                // Scanning covers both first-connect and subprocess bounces so the
                // UI spinner tracks every phase where the device list is in flux.
                // Gated on the daemon actually running: with no live subprocess
                // (never started, launch-failed, or between backoff respawns)
                // nothing is being scanned and the spinner would never resolve -
                // the perpetual-scanning symptom on daemon-less machines.
                Scanning = rescanning || (engine.CurrentEffectName != "none" && pm?.IsRunning == true && !devices.IsConnected),
                RgbRunning = pm?.IsRunning ?? false,
                GpuAvailable = gpu?.Available ?? false,
                // Only a thrown init is "unavailable". Before the deferred
                // warmup fires there is no attempt yet, but one is always
                // scheduled, so reporting "unavailable" for that half second
                // flashes "no usable GPU" at the user for a card that is fine.
                GpuState = gpu is null ? "unavailable"
                    : gpu.Available ? "ready"
                    : gpu.Failed ? "unavailable"
                    : "initializing",
                // Same enumeration the render-GPU picker gates on, so the UI never
                // offers a shortcut to a control that will not be there. Cached:
                // GetGpus() runs a full LHM refresh and this is polled.
                GpuCanSwitch = MultiGpuCache.Value(sp)
                    || GpuBackend.SoftwareRenderingAvailable(
                        sp.GetService<Nexus.Service.Persistence.IConfigStore>()?.Load().Lighting.RenderBackend,
                        GpuBackend.AngleAvailable()),
                GpuRenderer = gpu is { Available: true } ? gpu.Renderer ?? "" : "",
                GpuSoftware = gpu is { Available: true, IsWarp: true },
                GpuSoftwareForced = gpu is { Available: true, IsWarp: true } && GpuBackend.SoftwareForced,
            };
        }).AllowPanel();
        // Master brightness slider: caps every LED channel before it leaves the
        // RGB bridge (a device never renders brighter than master). Read live by
        // RgbBridge.OnFrame, so a POST takes effect on the next frame push
        // without restarting any effect.
        app.MapGet("/lighting/global-brightness", (Nexus.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.GlobalBrightnessBody { Value = store.Load().Lighting.GlobalBrightness }).AllowPanel();
        app.MapPost("/lighting/global-brightness", (Models.Lighting.GlobalBrightnessBody body,
            Nexus.Service.Persistence.IConfigStore store, MultiplexHub hub) =>
        {
            // Math.Clamp(NaN, ...) returns NaN, which would propagate through
            // RgbBridge.OnFrame and zero every LED. Treat a non-finite payload
            // as "no change requested" - fall back to the documented default.
            var safe = float.IsFinite(body.Value) ? body.Value : 1.0f;
            var clamped = Math.Clamp(safe, 0f, 1f);
            store.Update(s =>
            {
                s.Lighting.GlobalBrightness = clamped;
                Nexus.Service.Lighting.LightingPresetLooks.CaptureIntoActive(s);
            });
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        // Time-of-day cap on master brightness. Read live by every frame writer
        // through MasterBrightness.Effective, so a POST lands on the next push.
        // AllowPanel like the slider: a paired phone toggles it from bed.
        app.MapGet("/lighting/brightness-schedule", (Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.Sun.SunLocationResolver sunResolver) =>
            BuildBrightnessScheduleBody(store.Load().Lighting.BrightnessSchedule ?? new(), sunResolver)).AllowPanel();
        app.MapPost("/lighting/brightness-schedule", (Models.Lighting.BrightnessScheduleBody body,
            Nexus.Service.Persistence.IConfigStore store, MultiplexHub hub,
            Nexus.Service.Lighting.Sun.SunLocationResolver sunResolver) =>
        {
            // Below the editor's floor there is no curve to interpolate. Values
            // are clamped rather than refused so an out-of-range hand edit still
            // saves. One point per hour, the last sent winning, so the writers
            // and the web readout never disagree on a shared hour.
            if (body.Points is null || body.Points.Count < 2)
            {
                return Results.BadRequest(ApiResponse.Fail("at least two points"));
            }
            Nexus.Service.Persistence.WeatherSavedLocation? location = null;
            if (body.LocationSpecified && body.Location is { } l)
            {
                if (l.Lat is not { } lat || l.Lon is not { } lon)
                {
                    return Results.BadRequest(ApiResponse.Fail("location needs lat and lon"));
                }
                if (!double.IsFinite(lat) || !double.IsFinite(lon) || lat < -90 || lat > 90 || lon < -180 || lon > 180)
                {
                    return Results.BadRequest(ApiResponse.Fail("location out of range"));
                }
                var label = (l.Label ?? "").Trim();
                if (label.Length > MaxSunLabelLength)
                {
                    label = label.Substring(0, MaxSunLabelLength);
                }
                var cc = (l.Cc ?? "").Trim().ToUpperInvariant();
                if (cc.Length > 3)
                {
                    cc = cc.Substring(0, 3);
                }
                location = new() { Lat = lat, Lon = lon, Label = label, Cc = cc };
            }
            if (body.SunNight is < 0 or > 100)
            {
                return Results.BadRequest(ApiResponse.Fail("sunNight must be within 0..100"));
            }
            if (body.SunDay is < 0 or > 100)
            {
                return Results.BadRequest(ApiResponse.Fail("sunDay must be within 0..100"));
            }
            var byHour = new SortedDictionary<int, Nexus.Service.Persistence.BrightnessSchedulePoint>();
            foreach (var p in body.Points)
            {
                var hour = Math.Clamp(p.Hour, 0, 23);
                byHour[hour] = new Nexus.Service.Persistence.BrightnessSchedulePoint
                {
                    Hour = hour,
                    Brightness = Math.Clamp(p.Brightness, 0, 100),
                };
            }
            var points = new List<Nexus.Service.Persistence.BrightnessSchedulePoint>(byHour.Values);
            // Replace the object, never mutate the list: frame writers walk the
            // reference they read, unlocked.
            var changed = false;
            string? levelError = null;
            store.Update(s =>
            {
                var current = s.Lighting.BrightnessSchedule;
                // Checked against the stored pair under the store's update, and both
                // resolved values saved, so the saved pair is the checked pair.
                int? savedNight = current?.SunNight;
                int? savedDay = current?.SunDay;
                if (body.SunNight is not null || body.SunDay is not null)
                {
                    var night = body.SunNight ?? current?.SunNightLevel ?? Nexus.Service.Persistence.BrightnessSchedule.DefaultSunNight;
                    var day = body.SunDay ?? current?.SunDayLevel ?? Nexus.Service.Persistence.BrightnessSchedule.DefaultSunDay;
                    if (night > day)
                    {
                        levelError = "sunNight must not be above sunDay";
                        return;
                    }
                    savedNight = night;
                    savedDay = day;
                }
                // Pin the resolved choice on first save so editing the curve
                // cannot flip an unchosen sun shift from on to off.
                var sunShift = body.SunShift ?? current?.SunShiftOn ?? true;
                var newLocation = body.LocationSpecified ? location : current?.SunLocation;
                var old = current?.SunLocation;
                changed = body.Enabled != (current?.Enabled ?? false)
                    || sunShift != (current?.SunShiftOn ?? true)
                    || old?.Lat != newLocation?.Lat
                    || old?.Lon != newLocation?.Lon;
                s.Lighting.BrightnessSchedule = new()
                {
                    Enabled = body.Enabled,
                    Points = points,
                    SunShift = sunShift,
                    SunNight = savedNight,
                    SunDay = savedDay,
                    SunLocation = newLocation,
                };
            });
            if (levelError is not null)
            {
                return Results.BadRequest(ApiResponse.Fail(levelError));
            }
            var saved = store.Load().Lighting.BrightnessSchedule;
            PanelTopics.BroadcastLighting(hub);
            return Results.Ok(BuildBrightnessScheduleBody(saved, sunResolver, forceResolve: changed));
        }).AllowPanel();
        // Dim to a user-set level while the PC is idle. The controller picks the
        // change up from the store, so a POST applies at once (a dimmed room
        // releases on the unlock ramp when it is turned off).
        app.MapGet("/lighting/idle-dim", (Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.IdleDim.IdleDimController idleDim) =>
        {
            var s = store.Load().Lighting.IdleDim ?? new();
            if (!idleDim.Supported)
            {
                idleDim.RequestReprobe();
            }
            return new Models.Lighting.IdleDimStatus
            {
                Enabled = s.Enabled,
                TimeoutSeconds = s.TimeoutSeconds,
                Level = Math.Clamp(s.Level, 0, 100),
                Supported = idleDim.Supported,
                ScreenOffSupported = idleDim.ScreenOffSupported,
                OsScreenOffSeconds = idleDim.ScreenOffSupported ? Nexus.Service.Lighting.IdleDim.OsScreenOffTimeout.ReadSeconds() : null,
            };
        }).AllowPanel();
        app.MapPost("/lighting/idle-dim", (Models.Lighting.IdleDimBody body,
            Nexus.Service.Persistence.IConfigStore store, MultiplexHub hub) =>
        {
            var timeout = body.TimeoutSeconds;
            if (timeout != 0
                && (timeout < Nexus.Service.Lighting.IdleDim.IdleDimController.MinTimeoutSeconds
                    || timeout > Nexus.Service.Lighting.IdleDim.IdleDimController.MaxTimeoutSeconds))
            {
                return Results.BadRequest(ApiResponse.Fail($"timeoutSeconds must be 0 or {Nexus.Service.Lighting.IdleDim.IdleDimController.MinTimeoutSeconds}..{Nexus.Service.Lighting.IdleDim.IdleDimController.MaxTimeoutSeconds}"));
            }
            var level = Math.Clamp(body.Level, 0, 100);
            // Replace the object, never mutate it: the controller reads a snapshot.
            store.Update(s => s.Lighting.IdleDim = new() { Enabled = body.Enabled, TimeoutSeconds = timeout, Level = level });
            PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        // Render-GPU selection (which card runs the lighting shaders). Host-only
        // (LocalhostOnly) -- a paired phone must not flip the host's GPU. The POST
        // persists + writes the OS preference; applying it needs a service restart
        // (POST /service/restart), since the GL context is created once at boot.
        app.MapGet("/lighting/render-gpu", (Nexus.Service.Persistence.IConfigStore store) =>
        {
            var lighting = store.Load().Lighting;
            return new Models.Lighting.RenderGpuInfo
            {
                Value = lighting.RenderGpu,
                SoftwareRenderingAvailable = GpuBackend.SoftwareRenderingAvailable(
                    lighting.RenderBackend, GpuBackend.AngleAvailable()),
            };
        }).LocalhostOnly();
        app.MapPost("/lighting/render-gpu", (Models.Lighting.RenderGpuBody body,
            Nexus.Service.Persistence.IConfigStore store, Nexus.Service.Sensors.ISensorProvider sensors) =>
        {
            var value = string.IsNullOrWhiteSpace(body.Value) ? "auto" : body.Value.Trim();
            if (GpuBackend.IsSoftwareChoice(value))
            {
                if (!GpuBackend.SoftwareRenderingAvailable(store.Load().Lighting.RenderBackend, GpuBackend.AngleAvailable()))
                {
                    return Results.BadRequest(ApiResponse.Fail("software rendering needs the ANGLE backend"));
                }
                value = GpuBackend.SoftwareChoice;
            }
            store.Update(s => s.Lighting.RenderGpu = value);
            GpuRenderPreference.Apply(value, sensors.GetGpus());
#if WINDOWS
            // Forget any auto-select decision so switching back to "auto"
            // re-probes (recovers from a persisted "off" where no card worked).
            GpuRenderSelect.ClearState();
#endif
            return Results.Ok(ApiResponse.Ok());
        }).LocalhostOnly();
        // The GL backend Windows creates the lighting context with. No UI: it is
        // the escape (wgl) if a driver ever refuses ANGLE, and support
        // needs to be able to flip it without hand-editing settings.json as
        // SYSTEM. Restart-to-apply.
        app.MapGet("/lighting/render-backend", (Nexus.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.RenderGpuBody { Value = store.Load().Lighting.RenderBackend }).LocalhostOnly();
        app.MapPost("/lighting/render-backend", (Models.Lighting.RenderGpuBody body,
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            var requested = body.Value?.Trim();
            var value = string.Equals(requested, "wgl", StringComparison.OrdinalIgnoreCase) ? "wgl" : "angle";
            store.Update(s => s.Lighting.RenderBackend = value);
            return ApiResponse.Ok();
        }).LocalhostOnly();
        // Headless start endpoints
        app.MapPost("/lighting/animate/headless-start", (AnimateHeadlessStart body, ILightingProvider l, MultiplexHub hub, FeatureGates gates, Nexus.Service.Telemetry.ITelemetry telemetry) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            l.StartAnimate(body);
            CaptureEffect(telemetry, "animate", body.Effect);
            PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        app.MapPost("/lighting/static/headless-start", (StaticHeadlessStart body, ILightingProvider l, MultiplexHub hub, FeatureGates gates, Nexus.Service.Telemetry.ITelemetry telemetry) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            try { l.StartStatic(body); }
            catch (System.ArgumentException ex) { return Results.Ok(ApiResponse.Fail(ex.Message)); }
            CaptureEffect(telemetry, "static", body.Effect);
            PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        app.MapPost("/lighting/screen/headless-start", (ScreenHeadlessStart body, ILightingProvider l, MultiplexHub hub, FeatureGates gates, Nexus.Service.Telemetry.ITelemetry telemetry) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            l.StartScreen(body);
            if (!body.KeepMode)
            {
                CaptureEffect(telemetry, "screen", null);
            }
            PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        // Re-open the OS screen picker to change the mirrored screen (Wayland).
        app.MapPost("/lighting/screen/reselect", (ILightingProvider l, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            l.ReselectScreen();
            PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        // Screen Mirror + Media post-process (hue / colorize / saturation / contrast).
        // Same shape for both modes so the right-pane Effect tab can drive either
        // with one slider set.
        app.MapGet("/lighting/screen/effect", (Nexus.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.ScreenEffect).AllowPanel();
        app.MapPost("/lighting/screen/effect", (Nexus.Service.Models.Lighting.PostProcessBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.UpdateScreenEffect(body.Hue, body.Colorize, body.Saturation, body.Contrast, body.FlipX, body.FlipY, body.Persist, body.Reactive, body.Reactivity, body.Intensity);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapGet("/lighting/media/effect", (Nexus.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.MediaEffect).AllowPanel();
        app.MapPost("/lighting/media/effect", (Nexus.Service.Models.Lighting.PostProcessBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.UpdateMediaEffect(body.Hue, body.Colorize, body.Saturation, body.Contrast, body.FlipX, body.FlipY, body.Persist);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
    }

    private static ShaderSourceResponse ToShaderSource(string frag, IReadOnlyList<Nexus.Service.Lighting.Engine.Gpu.SensorUniformSpec> sensors)
    {
        var response = new ShaderSourceResponse { Frag = frag };
        foreach (var spec in sensors)
        {
            response.Sensors.Add(new SensorUniformInfo { Uniform = spec.Name, Binding = spec.Default.Copy() });
        }
        return response;
    }

    private static Nexus.Service.Models.Lighting.CustomEffectResponse ToResponse(Nexus.Service.Lighting.CustomEffects.CustomEffectEntry e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Recipe = e.Recipe,
        UpdatedAt = e.UpdatedAt,
        Audio = ShaderLibrary.ReadsAudio(e.Frag),
        MarketId = e.MarketId,
        MarketAuthor = e.MarketAuthor,
        Downloaded = e.Downloaded,
    };

    private static IResult CustomEffectError(string message) =>
        Results.Json(ApiResponse.Fail(message), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status400BadRequest);

    private static IResult CustomEffectUnavailable() =>
        Results.Json(ApiResponse.Fail(Nexus.Service.Lighting.CustomEffects.CustomEffectStore.UnavailableMessage),
            AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static void MapCustomEffects(WebApplication app)
    {
        app.MapGet("/lighting/custom-effects", (Nexus.Service.Lighting.CustomEffects.CustomEffectStore custom) =>
            Results.Json(custom.List().Select(ToResponse).ToList(), AppJsonContext.Default.ListCustomEffectResponse)).AllowPanel();
        app.MapPut("/lighting/custom-effects/{id}", (string id, Nexus.Service.Models.Lighting.CustomEffectPutBody? body,
            Nexus.Service.Lighting.CustomEffects.CustomEffectStore custom,
            Nexus.Service.Lighting.CustomEffects.CustomFragCompiler compile,
            ILightingProvider l, MultiplexHub hub) =>
        {
            if (!custom.EnsureAvailable())
            {
                return CustomEffectUnavailable();
            }
            var problem = Nexus.Service.Lighting.CustomEffects.CustomEffectStore.Validate(id, body?.Name, body?.Recipe, body?.Frag);
            if (problem is not null)
            {
                return CustomEffectError(problem);
            }
            var linkProblem = Nexus.Service.Lighting.CustomEffects.CustomEffectStore.ValidateMarketLink(body!.MarketId, body.MarketAuthor);
            if (linkProblem is not null)
            {
                return CustomEffectError(linkProblem);
            }
            var compileError = compile(body.Frag!);
            if (compileError is not null)
            {
                return CustomEffectError(compileError);
            }
            var (entry, error) = custom.Put(id, body.Name!, body.Recipe ?? "", body.Frag!, body.MarketId, body.MarketAuthor, body.Downloaded);
            if (entry is null)
            {
                return CustomEffectError(error ?? "rejected");
            }
            l.ReloadCustomEffect(ShaderLibrary.CustomPrefix + id);
            PanelTopics.BroadcastLighting(hub);
            return Results.Json(ToResponse(entry), AppJsonContext.Default.CustomEffectResponse);
        });
        app.MapDelete("/lighting/custom-effects/{id}", (string id,
            Nexus.Service.Lighting.CustomEffects.CustomEffectStore custom,
            ILightingProvider l, MultiplexHub hub) =>
        {
            if (!custom.EnsureAvailable())
            {
                return CustomEffectUnavailable();
            }
            if (!custom.Delete(id))
            {
                return Results.NotFound();
            }
            l.DropCustomEffect(ShaderLibrary.CustomPrefix + id);
            PanelTopics.BroadcastLighting(hub);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    // Shader Studio live preview. Dashboard-only: no AllowPanel, so a panel token is refused on both verbs.
    // The session holds nothing in settings: it is an engine overlay over the running mode (LightingProvider.StudioLive.cs).
    private static void MapStudioLive(WebApplication app)
    {
        app.MapPost("/lighting/studio-live", (Nexus.Service.Models.Lighting.StudioLiveBody? body,
            Nexus.Service.Lighting.CustomEffects.CustomFragCompiler compile,
            ILightingProvider l, FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            // The store's own rules, under a fixed id and name that are always valid, then the GPU compile.
            var problem = Nexus.Service.Lighting.CustomEffects.CustomEffectStore.Validate("studiolive", "Studio live", null, body?.Frag);
            if (problem is not null)
            {
                return CustomEffectError(problem);
            }
            var compileError = compile(body!.Frag!);
            if (compileError is not null)
            {
                return CustomEffectError(compileError);
            }
            var result = l.StartStudioLive(body);
            if (!result.Ok)
            {
                return Results.Json(ApiResponse.Fail(result.Message), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Json(new Nexus.Service.Models.Lighting.StudioLiveResponse
            {
                Active = true,
                LeaseMs = Nexus.Service.Lighting.LightingProvider.StudioLeaseMs,
                SessionId = result.SessionId,
                LightingOff = result.LightingOff,
            }, AppJsonContext.Default.StudioLiveResponse);
        });
        app.MapDelete("/lighting/studio-live", (string? sessionId, ILightingProvider l, MultiplexHub hub) =>
        {
            if (l.StopStudioLive(sessionId))
            {
                PanelTopics.BroadcastLighting(hub);
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    // The three headless-start routes are re-posted on every slider move, so
    // only a changed mode/effect pair is worth an event. One slot, not one per
    // mode: it tracks what is currently applied, so switching away and back is
    // two real transitions.
    private static string _lastEffectSignature = "";

    internal static void ResetEffectSignature() => Interlocked.Exchange(ref _lastEffectSignature, "");

    internal static void CaptureEffect(Nexus.Service.Telemetry.ITelemetry telemetry, string routeMode, string? effect)
    {
        var (mode, slug) = ResolveEffect(routeMode, effect);
        var signature = mode + ":" + slug;
        // Both operands are typed string, so this is ordinal value equality;
        // a change to ReferenceEquals would silently stop deduping.
        if (Interlocked.Exchange(ref _lastEffectSignature, signature) == signature) return;
        telemetry.Capture(Nexus.Service.Telemetry.TelemetryEvents.LightingEffectApplied,
            ("mode", mode), ("effect", slug));
    }

    /// <summary>
    /// The mode the provider actually ran, with the effect key it actually used.
    /// StartAnimate reroutes any static-catalog key into Static, and an absent
    /// key falls back to rainbow, so reporting the route and the raw body would
    /// mislabel both. Screen mode ignores Effect entirely.
    /// </summary>
    internal static (string Mode, string Effect) ResolveEffect(string routeMode, string? effect)
    {
        if (routeMode == "screen") return ("screen", "screen");
        var name = (effect ?? "").Trim().ToLowerInvariant();
        // The id is user content; only the fact that a custom effect ran is reported.
        if (ShaderLibrary.IsCustomKey(name)) return (routeMode, "custom");
        if (routeMode == "animate")
        {
            if (name.Length == 0) name = "rainbow";
            if (Nexus.Service.Lighting.StaticEffectCatalog.Contains(name)) return ("static", SafeSlug(name));
        }
        else if (name.Length == 0)
        {
            name = Nexus.Service.Lighting.StaticEffectCatalog.DefaultEffect;
        }
        return (routeMode, SafeSlug(name));
    }

    internal static string EffectSignature(string mode, string? effect)
    {
        var (m, e) = ResolveEffect(mode, effect);
        return m + ":" + e;
    }

    /// <summary>Effect names arrive in a client POST, so the send boundary keeps
    /// them to a lowercase ASCII slug; anything else reports as "other".</summary>
    internal static string SafeSlug(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64) return "other";
        foreach (var c in value)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok) return "other";
        }
        return value;
    }
}

/// <summary>Whether the box has a second GPU to move lighting rendering to.
/// The source call refreshes every LHM sensor, so a positive is memoized and a
/// negative is re-checked at most every 30s: hardware enumeration is not ready
/// at the first status poll, and caching that answer strands the UI on
/// "no second card" for the life of the process.</summary>
internal static class MultiGpuCache
{
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(30);
    private static int _multi;
    private static long _lastCheckTicks;

    public static bool Value(IServiceProvider sp)
    {
        if (Volatile.Read(ref _multi) == 1)
        {
            return true;
        }
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastCheckTicks);
        if (last != 0 && new TimeSpan(now - last) < RecheckAfter)
        {
            return false;
        }
        Interlocked.Exchange(ref _lastCheckTicks, now);
        try
        {
            if ((sp.GetService(typeof(Nexus.Service.Sensors.ISensorProvider))
                as Nexus.Service.Sensors.ISensorProvider)?.GetGpus().Count > 1)
            {
                Volatile.Write(ref _multi, 1);
                return true;
            }
        }
        catch { }
        return false;
    }

}
