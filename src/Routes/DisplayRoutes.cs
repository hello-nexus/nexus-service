using System.Diagnostics;
using System.Threading;
using Nexus.Service.Auth;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Models.Peripherals.QSeries;
using Nexus.Service.Models.Peripherals.Y70;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Corsair.XeneonEdge;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Peripherals.QSeries;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

public static class DisplayRoutes
{
#if WINDOWS
    // Guards the setup-wizard route below: a second POST while MultiDigiMon
    // is already running must not spawn another instance/poll.
    private static int _touchWizardActive;
#endif

    public static void MapDisplayEndpoints(this WebApplication app)
    {
#if DEV_TOOLS
        app.MapGet("/displays/dev/panel-variant", () => Results.Json(
            new DevPanelVariantDto { Variant = DevPanelVariantOverride.Variant, Options = Y70DisplayProtocol.DdcOnlyVariantKeys },
            AppJsonContext.Default.DevPanelVariantDto)).LocalhostOnly();
        app.MapPut("/displays/dev/panel-variant", (DevPanelVariantDto body) =>
        {
            var variant = body.Variant ?? "";
            if (variant.Length > 0 && Array.IndexOf(Y70DisplayProtocol.DdcOnlyVariantKeys, variant) < 0)
                return Results.Json(ApiResponse.Fail("unknown variant"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            DevPanelVariantOverride.Variant = variant;
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();
#endif
        // Y70
        app.MapGet("/y70/rotation", (IY70Provider y) => new Y70RotationParams
        {
            Orientation = y.GetOrientation(),
            ForceOrientation = y.GetForceOrientation(),
        }).AllowPanel();
        app.MapPost("/y70/rotation", (Y70RotationParams body, IY70Provider y) =>
        {
            var changed = false;
            if (body.Orientation is not null) { y.SetOrientation(body.Orientation); changed = true; }
            if (body.ForceOrientation is not null) { y.SetForceOrientation(body.ForceOrientation.Value); changed = true; }
            if (changed) y.ApplyEffectiveOrientation();
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapGet("/y70/brightness", (IY70Provider y) => new Y70BrightnessResponse { Brightness = y.GetBrightness() }).AllowPanel();
        app.MapPost("/y70/brightness", (Y70BrightnessParams body, IY70Provider y) =>
        {
            y.SetBrightness(body.Brightness);
            return new Y70BrightnessResponse { Brightness = body.Brightness };
        }).AllowPanel();
        app.MapGet("/y70/toggle", (IY70Provider y) => new Y70ToggleScreenResponse { Toggle = y.GetToggle() }).AllowPanel();
        app.MapPost("/y70/toggle", (Y70ToggleScreenParams body, IY70Provider y) =>
        {
            y.SetToggle(body.Toggle);
            return new Y70BrightnessResponse { Brightness = 20 };
        }).AllowPanel();
        // Stored only: the store change nudges nexus-overlay, which rebuilds
        // the panel kiosk when /overlay/state reports a different value.
        app.MapGet("/y70/compatibility-rendering", (IConfigStore store) => new Y70CompatibilityRenderingParams
        {
            Enabled = store.Load().Y70.CompatibilityRendering,
            Supported = OperatingSystem.IsWindows(),
        }).AllowPanel();
        app.MapPost("/y70/compatibility-rendering", (Y70CompatibilityRenderingParams body, IConfigStore store) =>
        {
            store.Update(s => s.Y70.CompatibilityRendering = body.Enabled);
            return ApiResponse.Ok();
        }).AllowPanel();

        // Q-series (Q60/Q80) - 180 degree flip only, no landscape.
        app.MapGet("/qseries/rotation", (IConfigStore store) => new QSeriesRotationParams
        {
            Orientation = store.Load().QSeries.Orientation,
        }).AllowPanel();
        app.MapPost("/qseries/rotation", (QSeriesRotationParams body, IConfigStore store, IServiceProvider sp) =>
        {
            if (body.Orientation is null)
                return Results.Ok(ApiResponse.Ok());
            if (body.Orientation != DisplayOrientations.Portrait && body.Orientation != DisplayOrientations.PortraitFlipped)
                return Results.BadRequest(ApiResponse.Fail($"unknown orientation '{body.Orientation}'"));
            store.Update(s => s.QSeries.Orientation = body.Orientation);
            // The watcher is Windows-only (see AddNexusPanel), so GetService is
            // null off Windows; the setting still persists there.
            sp.GetService<Nexus.Service.QSeries.QSeriesPortWatcher>()?.AnnounceDisplayChange();
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        app.MapGet("/qseries/display", (IConfigStore store) =>
        {
            var qseries = store.Load().QSeries;
            return new QSeriesDisplayParams
            {
                Brightness = qseries.Brightness,
                ScreenOff = qseries.ScreenOff,
                SleepWithHost = qseries.SleepWithHost,
                SleepWhenLocked = qseries.SleepWhenLocked,
            };
        }).AllowPanel();
        app.MapPost("/qseries/display", (QSeriesDisplayParams body, IConfigStore store, IServiceProvider sp) =>
        {
            if (body.Brightness is int brightness && (brightness < 0 || brightness > 100))
                return Results.BadRequest(ApiResponse.Fail("brightness must be between 0 and 100"));
            if (body.Brightness is null && body.ScreenOff is null && body.SleepWithHost is null
                && body.SleepWhenLocked is null)
            {
                return Results.Ok(ApiResponse.Ok());
            }

            store.Update(s =>
            {
                if (body.Brightness is int b) s.QSeries.Brightness = b;
                if (body.ScreenOff is bool off) s.QSeries.ScreenOff = off;
                if (body.SleepWithHost is bool sleepWithHost) s.QSeries.SleepWithHost = sleepWithHost;
                if (body.SleepWhenLocked is bool sleepWhenLocked) s.QSeries.SleepWhenLocked = sleepWhenLocked;
            });
            sp.GetService<Nexus.Service.QSeries.QSeriesPortWatcher>()?.AnnounceDisplayChange();
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        // Panel link health: no panel on USB, on USB but its adbd is not
        // answering, or recovery blocked until the host restarts.
        app.MapGet("/qseries/link", (IServiceProvider sp) =>
        {
            var watcher = sp.GetService<Nexus.Service.QSeries.QSeriesPortWatcher>();
            // A host with no watcher registered (macOS) is not the same as a host
            // with no panel attached, and the UI must not read it as one.
            return watcher?.GetLinkStatus()
                ?? new QSeriesLinkStatus { Error = true, Msg = "q-series watcher unavailable" };
        }).AllowPanel();

        // Runs the USB reset now instead of on the recovery cadence. Fire and
        // forget: the reset happens on the tick thread that owns the transport,
        // so the caller polls GET /qseries/link for the outcome. Throttled
        // because each accepted call is a host-level devnode reset and the route
        // is reachable from any panel session.
        app.MapPost("/qseries/link/repair", (IServiceProvider sp) =>
        {
            var watcher = sp.GetService<Nexus.Service.QSeries.QSeriesPortWatcher>();
            if (watcher is null) return Results.Ok(ApiResponse.Fail("q-series watcher unavailable"));
            return watcher.RequestLinkRepair()
                ? Results.Ok(ApiResponse.Ok())
                : Results.Ok(ApiResponse.Fail("a repair is already in flight"));
        }).AllowPanel();

        // System monitors (external DDC/CI + internal panels)
        app.MapGet("/displays", (DisplayBrightnessController d) => d.ListDisplays()).AllowPanel();

        // OS monitor topology (positions, modes, scale) merged with panel state.
        app.MapGet("/displays/topology", (DisplayTopologyService topology) => topology.GetTopology()).AllowPanel();

        // displayId -> panelDeviceId bindings; the overlay reconciles its
        // kiosk windows against this on every prefs poll / push.
        app.MapGet("/displays/assignments", (HttpContext ctx, PanelDeviceRegistry registry, TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            var response = new DisplayAssignmentsResponse { PanelBackdrop = registry.GetY70Backdrop() };
            foreach (var (displayId, panelDeviceId, reserveMonitor, backdrop) in registry.ListAssignments())
            {
                response.Assignments.Add(new DisplayAssignmentDto
                {
                    DisplayId = displayId,
                    PanelDeviceId = panelDeviceId,
                    ReserveMonitor = reserveMonitor,
                    Backdrop = backdrop,
                });
            }
            return Results.Json(response, AppJsonContext.Default.DisplayAssignmentsResponse);
        });

        // Promote a monitor to a Nexus panel: allocates the panel device
        // record that becomes the kiosk's identity (/panel/{recordId}).
        app.MapPost("/displays/{id}/panel", (
            string id,
            PanelPromoteBody? body,
            HttpContext ctx,
            DisplayTopologyService topology,
            PanelDeviceRegistry registry,
            MultiplexHub hub,
            TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            if (!DisplayTopologyService.HostingSupportedOnHost)
                return Results.UnprocessableEntity(ApiResponse.Fail("panel hosting is not supported on this system yet"));

            var display = topology.FindDisplay(id);
            if (display is null)
                return Results.NotFound(ApiResponse.Fail("display not found"));
            if (display.IsY70)
                return Results.Conflict(ApiResponse.Fail("the Y70 panel is managed automatically"));
            if (display.AssignedPanelDeviceId is not null)
                return Results.Conflict(ApiResponse.Fail("display is already a panel"));

            // Stamp viewport hints from the OS facts: the kiosk loads
            // /panel/{id} directly and never runs the self-report path.
            // SyncPromotedPanelCapabilities re-derives the same shape on
            // later topology reads, so the record tracks rotation/rescale.
            var capabilities = DisplayTopologyService.BuildPromotedCapabilities(
                display.Manufacturer, display.Model, display.Name,
                display.Resolution.Width, display.Resolution.Height,
                display.ScaleFactor, display.IsTouch, display.Orientation);
            var (record, activated) = registry.AllocateForDisplay(id, body?.DisplayName ?? display.Name, capabilities);
            if (!activated)
                return Results.Conflict(ApiResponse.Fail("display is already a panel"));
            PanelTopics.BroadcastPanelDevice(hub, record.Id);
            PanelTopics.BroadcastDisplays(hub);
            return Results.Json(record, AppJsonContext.Default.PanelDeviceRecord);
        });

        // Turn the panel OFF: the record (layout/theme/settings) persists so
        // turning it back on restores the panel exactly; assignments stop
        // listing it and the overlay closes the kiosk. Full record deletion
        // stays available via DELETE /panel/devices/{id}.
        app.MapDelete("/displays/{id}/panel", (
            string id,
            HttpContext ctx,
            PanelDeviceRegistry registry,
            MultiplexHub hub,
            TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            var record = registry.FindByDisplayId(id);
            if (record is null || record.Capabilities?.Surface != PanelSurfaces.Monitor || record.Enabled == false)
                return Results.NotFound(ApiResponse.Fail("display is not an active panel"));
            registry.DisablePanelForDisplay(id);
            PanelTopics.BroadcastPanelDevice(hub, record.Id);
            PanelTopics.BroadcastDisplays(hub);
            return Results.Ok(ApiResponse.Ok("panel turned off"));
        });

        // Rotate any monitor by stable display id (promoted-panel settings).
        // Runs through the user-session helper like the Y70 rotation; the
        // resulting WM_DISPLAYCHANGE re-broadcasts the displays topic.
        app.MapPost("/displays/{id}/rotation", (
            string id,
            DisplayRotationBody body,
            HttpContext ctx,
            IDisplayOrientationProvider orientation,
            PanelDeviceRegistry registry,
            MultiplexHub hub,
            TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            if (!DisplayOrientations.IsValid(body.Orientation))
                return Results.BadRequest(ApiResponse.Fail($"unknown orientation '{body.Orientation}'"));
            // Looked up before applying so the cover (if any) can use this
            // panel's own background colour instead of the black fallback.
            var record = registry.FindByDisplayId(id);
            var coverColorHex = PanelDeviceRegistry.ResolveCoverBackgroundHex(record);
            var (ok, error) = orientation.SetDisplayOrientation(id, body.Orientation, coverColorHex);
            if (!ok)
                return Results.BadRequest(ApiResponse.Fail(string.IsNullOrEmpty(error) ? "rotation failed" : error));
            // Settings permanence: remember the applied orientation on the
            // bound record (when this display is a panel), same model as the
            // Y70's persisted orientation.
            if (record is not null)
            {
                registry.UpdateDisplayOrientation(id, body.Orientation);
                PanelTopics.BroadcastPanelDevice(hub, record.Id);
            }
            return Results.Ok(ApiResponse.Ok("rotated"));
        });

        // Per-display DDC/CI opt-out. Off means Nexus sends this monitor no
        // DDC at all - the capability probe included - which is the escape
        // hatch for a panel whose firmware hangs on a transaction (see DdcGate).
        app.MapPost("/displays/{id}/ddc", (
            string id,
            DisplayDdcParams body,
            DisplayBrightnessController d,
            MultiplexHub hub) =>
        {
            if (!d.SetDdcEnabled(id, body.Enabled))
            {
                return Results.UnprocessableEntity(ApiResponse.Fail("could not update the display"));
            }
            PanelTopics.BroadcastDisplays(hub);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapGet("/displays/{id}/brightness", (string id, DisplayBrightnessController d) =>
        {
            var v = d.GetBrightness(id);
            return v.HasValue
                ? Results.Ok(new DisplayBrightnessDto
                {
                    Id = id,
                    Brightness = v.Value,
                    RequestedBrightness = v.Value,
                    AppliedBrightness = v.Value,
                    Status = DisplayBrightnessWriteStatuses.Applied,
                })
                : Results.NotFound();
        }).AllowPanel();

        app.MapPost("/displays/{id}/brightness", async (
            string id,
            DisplayBrightnessParams body,
            DisplayBrightnessController d,
            CancellationToken ct) =>
        {
            var result = await d.SetBrightnessAsync(id, body.Brightness, ct);
            return result.Status == DisplayBrightnessWriteStatuses.Applied
                ? Results.Ok(result)
                : Results.BadRequest(result);
        }).AllowPanel();

        // Corsair Xeneon Edge native settings (brightness/backlight/contrast/
        // RGB) over its vendor HID channel. Replaces the generic DDC path
        // above for this family - see DisplayBrightnessController.IsXeneonEdge.
        app.MapGet("/displays/{id}/xeneon-settings", async (
            string id,
            PanelDeviceRegistry registry,
            XeneonEdgeOrientationWorker xeneon,
            MultiplexHub hub,
            CancellationToken ct) =>
        {
            var record = registry.FindByDisplayId(id);
            if (record is null || record.Capabilities?.Family != KnownPanelDisplays.XeneonEdgeFamily)
                return Results.NotFound(ApiResponse.Fail("not a Xeneon Edge panel"));

            var block = await xeneon.ReadSettingsAsync(ct);
            if (block is null)
                return Results.UnprocessableEntity(ApiResponse.Fail("could not read the panel's settings"));

            var dto = new XeneonEdgeSettingsDto
            {
                Brightness = block.Value.Brightness,
                Backlight = block.Value.Backlight,
                Contrast = block.Value.Contrast,
                Red = block.Value.Red,
                Green = block.Value.Green,
                Blue = block.Value.Blue,
            };
            registry.UpdateXeneonEdgeSettings(id, dto);
            PanelTopics.BroadcastPanelDevice(hub, record.Id);
            return Results.Json(dto, AppJsonContext.Default.XeneonEdgeSettingsDto);
        }).AllowPanel();

        app.MapPost("/displays/{id}/xeneon-settings", async (
            string id,
            XeneonEdgeSettingsDto body,
            PanelDeviceRegistry registry,
            XeneonEdgeOrientationWorker xeneon,
            MultiplexHub hub,
            CancellationToken ct) =>
        {
            var record = registry.FindByDisplayId(id);
            if (record is null || record.Capabilities?.Family != KnownPanelDisplays.XeneonEdgeFamily)
                return Results.NotFound(ApiResponse.Fail("not a Xeneon Edge panel"));

            var applied = new XeneonEdgeSettingsDto();
            var wrote = false;

            // Persists whatever DID apply before reporting a failure: a
            // partial batch (e.g. brightness landed, contrast failed) must
            // not leave the snapshot showing the pre-request brightness too.
            IResult Fail(string message)
            {
                if (wrote)
                {
                    registry.UpdateXeneonEdgeSettings(id, applied);
                    PanelTopics.BroadcastPanelDevice(hub, record.Id);
                }
                return Results.UnprocessableEntity(ApiResponse.Fail(message));
            }

            if (body.Brightness.HasValue)
            {
                var v = await xeneon.SetControlAsync(XeneonEdgeControl.Brightness, body.Brightness.Value, ct);
                if (v is null) return Fail("failed to set brightness");
                applied.Brightness = v;
                wrote = true;
            }
            if (body.Backlight.HasValue)
            {
                var v = await xeneon.SetControlAsync(XeneonEdgeControl.Backlight, body.Backlight.Value, ct);
                if (v is null) return Fail("failed to set backlight");
                applied.Backlight = v;
                wrote = true;
            }
            if (body.Contrast.HasValue)
            {
                var v = await xeneon.SetControlAsync(XeneonEdgeControl.Contrast, body.Contrast.Value, ct);
                if (v is null) return Fail("failed to set contrast");
                applied.Contrast = v;
                wrote = true;
            }
            if (body.Red.HasValue)
            {
                var v = await xeneon.SetControlAsync(XeneonEdgeControl.Red, body.Red.Value, ct);
                if (v is null) return Fail("failed to set red");
                applied.Red = v;
                wrote = true;
            }
            if (body.Green.HasValue)
            {
                var v = await xeneon.SetControlAsync(XeneonEdgeControl.Green, body.Green.Value, ct);
                if (v is null) return Fail("failed to set green");
                applied.Green = v;
                wrote = true;
            }
            if (body.Blue.HasValue)
            {
                var v = await xeneon.SetControlAsync(XeneonEdgeControl.Blue, body.Blue.Value, ct);
                if (v is null) return Fail("failed to set blue");
                applied.Blue = v;
                wrote = true;
            }

            if (!wrote)
                return Results.BadRequest(ApiResponse.Fail("no settings provided"));

            registry.UpdateXeneonEdgeSettings(id, applied);
            PanelTopics.BroadcastPanelDevice(hub, record.Id);
            return Results.Json(applied, AppJsonContext.Default.XeneonEdgeSettingsDto);
        }).AllowPanel();

        // Touch-mapping guard: runs a detect-and-repair pass synchronously.
        // Also the manual entry point the auto-repair guard's background
        // triggers (helper connect, displays-changed) call into.
        app.MapPost("/displays/touch-mapping/repair", async (TouchMappingGuard guard, CancellationToken ct) =>
        {
            var outcome = await guard.RunPassAsync(ct);
            var status = outcome.Result switch
            {
                TouchMappingPassResult.Repaired => "repaired",
                TouchMappingPassResult.AlreadyCorrect => "alreadyCorrect",
                TouchMappingPassResult.NoPanel => "noPanel",
                TouchMappingPassResult.NoDigitizer => "noDigitizer",
                TouchMappingPassResult.NoHelper => "noHelper",
                _ => "failed",
            };
            return Results.Json(
                new TouchMappingRepairResponse { Status = status, Detail = outcome.Detail },
                AppJsonContext.Default.TouchMappingRepairResponse);
        });

        // Manual fallback: launches the OS wizard (Control Panel > Tablet PC
        // Settings > Setup, now only reachable via MultiDigiMon.exe -touch on
        // current Windows) for the rare case the auto-repair guard can't
        // resolve the mapping itself. The kiosk is hidden first because the
        // wizard's identifying-tap prompt renders on the panel.
        app.MapPost("/displays/touch-mapping/setup-wizard", (
            IConfigStore store,
            PanelKioskLauncher kiosk) =>
        {
#if WINDOWS
            if (!OperatingSystem.IsWindows())
                return Results.UnprocessableEntity(ApiResponse.Fail("touch setup is only available on Windows"));

            if (Interlocked.CompareExchange(ref _touchWizardActive, 1, 0) != 0)
                return Results.Conflict(ApiResponse.Fail("touch setup wizard is already running"));

            kiosk.Close();
            var wizardPath = Path.Combine(Environment.SystemDirectory, "MultiDigiMon.exe");
            var launched = UserHelperBootstrapper.RunInUserSession(
                $"\"{wizardPath}\" -touch", "touch-setup-wizard", "NexusTouchSetupWizard");
            if (!launched)
            {
                Interlocked.Exchange(ref _touchWizardActive, 0);
                return Results.UnprocessableEntity(ApiResponse.Fail("no active console user session"));
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
                    // No completion signal for a process launched in a different
                    // session; poll for exit, bounded by the same window the
                    // wizard's own UI would time out a stuck user interaction in.
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(1000);
                        if (Process.GetProcessesByName("MultiDigiMon").Length == 0) break;
                    }
                    if (Process.GetProcessesByName("MultiDigiMon").Length == 0)
                    {
                        if (store.Load().Panel.AutoLaunch) kiosk.Launch();
                    }
                    else
                    {
                        // Still mid-calibration past the deadline: relaunching
                        // the kiosk would paint it over the identify prompt.
                        // The next autoLaunch trigger brings the kiosk back.
                        ServiceLog.Info("[touch-map] setup wizard still running past the poll deadline; leaving kiosk closed");
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _touchWizardActive, 0);
                }
            });
            return Results.Json(ApiResponse.Ok("touch setup wizard launched"), AppJsonContext.Default.ApiResponse, statusCode: 202);
#else
            return Results.UnprocessableEntity(ApiResponse.Fail("touch setup is only available on Windows"));
#endif
        });
    }
}

#if DEV_TOOLS
/// <summary>Dev-tools panel-variant override: <c>Variant</c> is "" or one of <c>Options</c>.</summary>
public sealed class DevPanelVariantDto
{
    public string? Variant { get; set; }
    public string[] Options { get; set; } = [];
}
#endif
