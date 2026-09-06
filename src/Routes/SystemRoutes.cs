using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Fps;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Panel;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;
using Microsoft.AspNetCore.Mvc;

namespace Nexus.Service.Routes;

public static class SystemRoutes
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        // No REST sensor endpoints - all hardware sensor / model data is
        // delivered via the `/monitoring` topic over the multiplex WebSocket.
        // RAM capacity ships as `theoreticalMaximum` on the Memory Used sensor.

        // Compact, shareable rig identity for the Devices → System Specs tab.
        // Cached for the lifetime of the service (hardware specs don't change
        // at runtime); `SystemSpecsPrewarmService` populates the cache off
        // the boot critical path so the first request is in-memory.
        // Async so the first post-boot request waits for LHM's background open
        // to finish (~1-3 s) and returns fully-populated CPU/motherboard/GPU
        // names. Subsequent calls hit the cache and return in microseconds.
        app.MapGet("/system/specs", (SystemSpecsCollector collector, HttpContext ctx) =>
            collector.GetAsync(ctx.RequestAborted)).AllowPanel();

        // OS accent for the web's "system" accent source. Windows/macOS push it
        // from their native shell; Linux has no shell (the dashboard is a
        // browser) so the service reads the XDG portal and serves it here.
        app.MapGet("/system/accent", (ISystemAccentProvider accent) =>
            new SystemAccentResponse { Accent = accent.GetAccentHex() ?? "" }).AllowPanel();

        // Boot id for the web UI once-per-boot greeting check. Derived from OS
        // uptime (Environment.TickCount64), not process start, so it survives a
        // service restart within the same boot. Quantized so tick jitter does not
        // shift it; still wall-clock-derived, so a clock step mid-boot can change
        // it (at worst a repeat greeting).
        app.MapGet("/system/boot", () =>
        {
            var bootTime = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            var quantizedSeconds = (bootTime.ToUnixTimeSeconds() / 60) * 60;
            return new SystemBootResponse { BootId = quantizedSeconds.ToString() };
        }).AllowPanel();

        // Volume (default render endpoint)
        app.MapGet("/system/volume", (IVolumeProvider v) => v.GetState()).AllowPanel();
        app.MapPost("/system/volume", (SetVolumeBody body, IVolumeProvider v, MultiplexHub hub) =>
        {
            v.SetVolume(body.Volume);
            PanelTopics.BroadcastVolume(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapPost("/system/volume/mute", (SetMutedBody body, IVolumeProvider v, MultiplexHub hub) =>
        {
            v.SetMuted(body.Muted);
            PanelTopics.BroadcastVolume(hub);
            return ApiResponse.Ok();
        }).AllowPanel();

        // ── Keyboard / text injection (deck hotkey + type-text actions) ──
        // Desktop-token only: a free-form virtual keyboard into the console
        // session is code execution as the user. A paired panel triggers the
        // hotkey / text actions saved in its deck layout through
        // POST /panel/deck/dispatch instead (PanelDeckRoutes), which executes
        // the stored action server-side and never takes keys or text from the
        // panel request.
        app.MapPost("/system/input/keys", (SendKeysBody body, Nexus.Service.Actions.SystemActions actions) =>
            actions.SendKeysAsync(body));

        app.MapPost("/system/input/text", (SendTextBody body, Nexus.Service.Actions.SystemActions actions) =>
            actions.SendTextAsync(body.Text ?? ""));

        // ── Open URL / file / folder / OS settings / task manager (deck launch actions) ──
        app.MapPost("/system/open-settings", (Nexus.Service.Actions.SystemActions actions) =>
            actions.OpenSettingsAsync()).AllowPanel();

        app.MapPost("/system/open-url", (OpenUrlRequest body, Nexus.Service.Actions.SystemActions actions) =>
            actions.OpenUrlAsync(body.Url ?? "")).AllowPanel();

        // open-path opens any existing local file with its default handler in
        // the user's session, so it is desktop-token only (and relay-denied in
        // RelayHttpAllowlist.cs). A paired panel's deck "open file / folder"
        // keys go through POST /panel/deck/dispatch, which opens the path saved
        // in the layout rather than one named by the panel request.
        app.MapPost("/system/open-path", (OpenPathBody body, Nexus.Service.Actions.SystemActions actions) =>
            actions.OpenPathAsync(body.Path ?? ""));

        // Native OS file/folder picker for the deck action Browse button
        // (openFile/openFolder ActionFields). Desktop-tier only - no
        // AllowPanel, and deliberately absent from RelayHttpAllowlist.cs -
        // the dialog opens on the host's screen, so it must stay unreachable
        // from a paired panel session or a relayed remote connection.
        app.MapPost("/system/pick-path", async (PickPathBody body, IFileDialogPicker picker, HttpContext ctx) =>
        {
            var mode = body.Folder ? FileDialogPickMode.Folder : FileDialogPickMode.AnyFileSingle;
            var result = await picker.PickAsync(mode, ctx.RequestAborted);
            var path = result.Cancelled || result.Error || result.Paths.Count == 0 ? null : result.Paths[0];
            return Results.Ok(new PickPathResponse { Path = path });
        });

        app.MapPost("/system/open-task-manager", (Nexus.Service.Actions.SystemActions actions) =>
            actions.OpenTaskManager() ? ApiResponse.Ok() : ApiResponse.Fail("failed to open task manager")).AllowPanel();

        // ── Power / session. All five are panel-reachable: the deck widget's
        // power keys run on a paired panel session. ──
        app.MapPost("/system/power/lock", (Nexus.Service.Actions.SystemActions actions) => actions.Lock() ? ApiResponse.Ok() : ApiResponse.Fail("lock failed")).AllowPanel();
        app.MapPost("/system/power/sleep", (Nexus.Service.Actions.SystemActions actions) => actions.Sleep() ? ApiResponse.Ok() : ApiResponse.Fail("sleep failed")).AllowPanel();
        app.MapPost("/system/power/shutdown", (Nexus.Service.Actions.SystemActions actions) => actions.Shutdown() ? ApiResponse.Ok() : ApiResponse.Fail("shutdown failed")).AllowPanel();
        app.MapPost("/system/power/restart", (Nexus.Service.Actions.SystemActions actions) => actions.Restart() ? ApiResponse.Ok() : ApiResponse.Fail("restart failed")).AllowPanel();
        app.MapPost("/system/power/logout", (Nexus.Service.Actions.SystemActions actions) => actions.Logout() ? ApiResponse.Ok() : ApiResponse.Fail("logout failed")).AllowPanel();

        // ── Audio device enumeration + default switching ──
        app.MapGet("/system/audio/devices", (IAudioDeviceProvider a) => a.ListDevices()).AllowPanel();
        // The bump is what tells other open mixers to re-read the endpoints; the
        // caller's own view updates off its POST resolving.
        app.MapPost("/system/audio/default-output", (SetAudioDefaultBody body,
            Nexus.Service.Actions.SystemActions actions, Nexus.Service.Audio.AudioMixerService mixer) =>
        {
            if (!actions.SetDefaultOutput(body.DeviceId)) return ApiResponse.Fail("failed to set output device");
            mixer.NotifyEndpointsChanged();
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapPost("/system/audio/default-input", (SetAudioDefaultBody body,
            Nexus.Service.Actions.SystemActions actions, Nexus.Service.Audio.AudioMixerService mixer) =>
        {
            if (!actions.SetDefaultInput(body.DeviceId)) return ApiResponse.Fail("failed to set input device");
            mixer.NotifyEndpointsChanged();
            return ApiResponse.Ok();
        }).AllowPanel();

        // Desktop-token only: the path is caller-named. A panel's playAudio deck
        // key plays the layout's saved path through POST /panel/deck/dispatch,
        // and the physical deck goes through DeckActionExecutor directly.
        app.MapPost("/system/audio/play", (PlayAudioBody body, Nexus.Service.Audio.AudioFilePlayer player) =>
        {
            player.Play(body.Path, body.Volume);
            return ApiResponse.Ok();
        });

        // ── Per-app volume mixer ──
        // Strips are pushed on the audio/mixer topic while a mixer is open; these
        // cover the first paint and every write.
        app.MapGet("/system/audio/mixer", (Nexus.Service.Audio.AudioMixerService mixer) =>
            mixer.GetState()).AllowPanel();

        app.MapPost("/system/audio/mixer/volume", (SetSessionVolumeBody body, Nexus.Service.Audio.AudioMixerService mixer) =>
        {
            mixer.SetVolume(body.Id, body.Volume, body.Commit ?? true);
            return ApiResponse.Ok();
        }).AllowPanel();

        app.MapPost("/system/audio/mixer/mute", (SetSessionMutedBody body, Nexus.Service.Audio.AudioMixerService mixer) =>
        {
            mixer.SetMuted(body.Id, body.Muted);
            return ApiResponse.Ok();
        }).AllowPanel();

        app.MapPost("/system/audio/mixer/sticky", (AudioMixerStickyBody body, Nexus.Service.Audio.AudioMixerService mixer) =>
        {
            mixer.SetSticky(body.Enabled);
            return ApiResponse.Ok();
        }).AllowPanel();

        app.MapPost("/system/audio/mixer/levels/clear", (Nexus.Service.Audio.AudioMixerService mixer) =>
        {
            mixer.ClearLevels();
            return ApiResponse.Ok();
        }).AllowPanel();

        app.MapPost("/system/audio/mixer/presets", (SaveAudioMixerPresetBody body, Nexus.Service.Audio.AudioMixerService mixer) =>
        {
            var result = mixer.SavePreset(body);
            return result.Preset is null
                ? Results.BadRequest(ApiResponse.Fail(result.Error))
                : Results.Ok(result.Preset);
        }).AllowPanel();

        app.MapPost("/system/audio/mixer/presets/rename", (RenameAudioMixerPresetBody body, Nexus.Service.Audio.AudioMixerService mixer) =>
        {
            var error = mixer.RenamePreset(body.Id, body.Name);
            return error.Length == 0 ? Results.Ok(ApiResponse.Ok()) : Results.BadRequest(ApiResponse.Fail(error));
        }).AllowPanel();

        app.MapPost("/system/audio/mixer/presets/delete", (AudioMixerPresetIdBody body, Nexus.Service.Audio.AudioMixerService mixer) =>
            mixer.DeletePreset(body.Id) ? ApiResponse.Ok() : ApiResponse.Fail("no such preset")).AllowPanel();

        app.MapPost("/system/audio/mixer/presets/apply", (AudioMixerPresetIdBody body, Nexus.Service.Audio.AudioMixerService mixer) =>
            mixer.ApplyPreset(body.Id) ? ApiResponse.Ok() : ApiResponse.Fail("no such preset")).AllowPanel();
    }
}
