using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.FocusModes;
using Nexus.Service.Models;
using Nexus.Service.Models.Focus;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>
/// Focus modes: status for the top bar chip, the manual on/off, and CRUD over
/// the user's mode list. Stock modes are editable but never deletable - their
/// ids are wired to triggers, and a reset needs a known-good pair to restore.
///
/// Error bodies are ApiResponse, never an anonymous type: source-gen JSON has
/// no metadata for those, so an AOT build answers 500 instead of the 400 the
/// route intended (bench-hit on T1, 2026-08-30).
/// </summary>
public static class FocusRoutes
{
    private const int MaxModes = 12;
    // The name rides the top bar chip and the mode list; longer than this and it truncates everywhere it is shown.
    private const int MaxNameLength = 10;
    private const int MinExitGraceSeconds = 0;
    private const int MaxExitGraceSeconds = 600;

    public static void MapFocusEndpoints(this WebApplication app)
    {
        app.MapGet("/api/focus", (FocusModeState state, IConfigStore config) =>
            BuildStatus(state, config));

        app.MapPost("/api/focus/active", (SetFocusActiveBody body, FocusModeState state, IConfigStore config, MultiplexHub hub) =>
        {
            if (string.IsNullOrWhiteSpace(body.ModeId))
            {
                state.TurnOff();
            }
            else
            {
                // Answering 200 for an id that does not exist reports "nothing
                // active" and reads as the mode silently refusing to turn on.
                if (!config.Load().Focus.Modes.Any(m => m.Id == body.ModeId))
                    return Results.NotFound(new ApiResponse { Error = true, Msg = "unknown mode" });
                state.ActivateManually(body.ModeId);
            }
            PanelTopics.BroadcastFocus(hub);
            return Results.Ok(BuildStatus(state, config));
        });

        app.MapPost("/api/focus/modes", (CreateFocusModeBody body, FocusModeState state, IConfigStore config, MultiplexHub hub) =>
        {
            var name = Clean(body.Name, MaxNameLength);
            if (name.Length == 0) return Results.BadRequest(new ApiResponse { Error = true, Msg = "name required" });

            var created = new FocusModeSettings
            {
                Id = Guid.NewGuid().ToString("n"),
                Name = name,
                Icon = Clean(body.Icon, 32) is { Length: > 0 } icon ? icon : "focus",
                BuiltIn = false,
                Trigger = FocusTriggers.IsKnown(body.Trigger) ? body.Trigger : FocusTriggers.Manual,
            };

            var overflow = false;
            config.Update(s =>
            {
                if (s.Focus.Modes.Count >= MaxModes) { overflow = true; return; }
                s.Focus.Modes.Add(created);
            });
            if (overflow) return Results.BadRequest(new ApiResponse { Error = true, Msg = "too many modes" });

            PanelTopics.BroadcastFocus(hub);
            return Results.Ok(BuildStatus(state, config));
        });

        app.MapPatch("/api/focus/modes/{id}", (string id, UpdateFocusModeBody body, FocusModeState state, IConfigStore config, MultiplexHub hub) =>
        {
            var found = false;
            config.Update(s =>
            {
                var mode = s.Focus.Modes.FirstOrDefault(m => m.Id == id);
                if (mode is null) return;
                found = true;

                if (Clean(body.Name, MaxNameLength) is { Length: > 0 } name) mode.Name = name;
                if (Clean(body.Icon, 32) is { Length: > 0 } icon) mode.Icon = icon;
                if (body.Trigger is { } trigger && FocusTriggers.IsKnown(trigger)) mode.Trigger = trigger;
                if (body.HoldNotifications is bool holdNotifications) mode.HoldNotifications = holdNotifications;
                if (body.HoldBackgroundTraffic is bool holdTraffic) mode.HoldBackgroundTraffic = holdTraffic;
                if (body.TurnPanelDisplaysOff is bool panelsOff) mode.TurnPanelDisplaysOff = panelsOff;
                if (body.StaticPanelBackgrounds is bool staticBackgrounds) mode.StaticPanelBackgrounds = staticBackgrounds;
                if (body.ExitGraceSeconds is int grace)
                    mode.ExitGraceSeconds = Math.Clamp(grace, MinExitGraceSeconds, MaxExitGraceSeconds);
            });
            if (!found) return Results.NotFound(new ApiResponse { Error = true, Msg = "no such mode" });

            // An effect toggled while the mode is already active has to take hold now.
            state.ReapplyEffects();
            PanelTopics.BroadcastFocus(hub);
            return Results.Ok(BuildStatus(state, config));
        });

        app.MapDelete("/api/focus/modes/{id}", (string id, FocusModeState state, IConfigStore config, MultiplexHub hub) =>
        {
            var builtIn = false;
            var found = false;
            config.Update(s =>
            {
                var mode = s.Focus.Modes.FirstOrDefault(m => m.Id == id);
                if (mode is null) return;
                found = true;
                if (mode.BuiltIn) { builtIn = true; return; }
                s.Focus.Modes.Remove(mode);
            });
            if (!found) return Results.NotFound(new ApiResponse { Error = true, Msg = "no such mode" });
            if (builtIn) return Results.BadRequest(new ApiResponse { Error = true, Msg = "built-in modes cannot be deleted" });

            if (state.ActiveModeId == id) state.TurnOff();
            state.ReapplyEffects();
            PanelTopics.BroadcastFocus(hub);
            return Results.Ok(BuildStatus(state, config));
        });

        // Restores the stock pair, dropping user modes: "defaults" means the
        // list a fresh install has, not a merge.
        app.MapPost("/api/focus/reset", (FocusModeState state, IConfigStore config, MultiplexHub hub) =>
        {
            config.Update(s => s.Focus.Modes = FocusModeSettings.StockModes());
            state.TurnOff();
            state.ReapplyEffects();
            PanelTopics.BroadcastFocus(hub);
            return BuildStatus(state, config);
        });

        // Order is precedence: the first eligible mode wins when two triggers fire.
        app.MapPost("/api/focus/modes/order", (ReorderFocusModesBody body, FocusModeState state, IConfigStore config, MultiplexHub hub) =>
        {
            config.Update(s =>
            {
                var byId = s.Focus.Modes.ToDictionary(m => m.Id, StringComparer.Ordinal);
                var ordered = new List<FocusModeSettings>(s.Focus.Modes.Count);
                foreach (var id in body.ModeIds)
                {
                    if (byId.Remove(id, out var mode)) ordered.Add(mode);
                }
                // Anything the client did not list keeps its relative order at the end.
                foreach (var mode in s.Focus.Modes)
                {
                    if (byId.ContainsKey(mode.Id)) ordered.Add(mode);
                }
                s.Focus.Modes = ordered;
            });

            state.ReapplyEffects();
            PanelTopics.BroadcastFocus(hub);
            return BuildStatus(state, config);
        });
    }

    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static FocusStatus BuildStatus(FocusModeState state, IConfigStore config)
    {
        var settings = LoadSettings(config);
        return new FocusStatus
        {
            ActiveModeId = state.ActiveModeId,
            Reason = state.Reason,
            ActivatedUtcMs = state.ActivatedUtcMs,
            Games = state.Games
                .Select(g => new FocusGame { Key = g.GameKey, Name = g.Name, Pid = g.Pid, SinceMs = g.StartedUtcMs })
                .ToList(),
            Modes = settings.Modes.Select(m => new FocusModeDto
            {
                Id = m.Id,
                // Clamped on the way out too: a name stored before the cap
                // existed would otherwise still overflow every surface.
                Name = Clean(m.Name, MaxNameLength),
                Icon = m.Icon,
                BuiltIn = m.BuiltIn,
                Trigger = m.Trigger,
                HoldNotifications = m.HoldNotifications,
                HoldBackgroundTraffic = m.HoldBackgroundTraffic,
                TurnPanelDisplaysOff = m.TurnPanelDisplaysOff,
                StaticPanelBackgrounds = m.StaticPanelBackgrounds,
                ExitGraceSeconds = m.ExitGraceSeconds,
            }).ToList(),
            AvailableTriggers = new List<string> { FocusTriggers.Manual, FocusTriggers.Game, FocusTriggers.Obs },
        };
    }

    private static FocusSettings LoadSettings(IConfigStore config)
    {
        try { return config.Load().Focus ?? new FocusSettings(); }
        catch { return new FocusSettings(); }
    }
}
