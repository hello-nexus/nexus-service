using System;
using Nexus.Service.Auth;
using Nexus.Service.Conflicts;
using Nexus.Service.Models.Conflicts;

namespace Nexus.Service.Routes;

/// <summary>
/// REST surface for the sidebar conflict warning. The watcher running
/// in the background broadcasts changes over the multiplex hub on topic
/// <c>conflicts</c>; these endpoints exist for clients that prefer a
/// one-shot fetch and for the "End task" button.
/// </summary>
public static class ConflictRoutes
{
    public static void MapConflictEndpoints(this WebApplication app)
    {
        // Current set of detected conflicts. Cheap - backed by the
        // watcher's in-memory snapshot, no rescan.
        app.MapGet("/conflicts", (ConflictWatcher watcher) =>
        {
            var conflicts = watcher.GetConflicts();
            var response = new GetConflictsResponse();
            foreach (var c in conflicts)
                response.Conflicts.Add(c);
            return Results.Ok(response);
        });

        // Every app the catalog knows about, running or not - what the
        // settings modal lists so a user can opt an app out of the startup
        // shutdown before it has ever been detected. Names the app only;
        // the process/service names it resolves to stay server-side.
        app.MapGet("/conflicts/catalog", () =>
        {
            var response = new GetConflictCatalogResponse();
            foreach (var def in ConflictAppCatalog.All)
            {
                response.Apps.Add(new ConflictCatalogApp
                {
                    Id = def.Id,
                    DisplayName = def.DisplayName,
                    Category = def.Category,
                });
            }
            return Results.Ok(response);
        });

        // Read-only: what still launches each detected conflict at boot.
        // Separate from GET /conflicts so the watcher's poll stays cheap, and
        // so nothing is inspected until a user opens a surface that offers the
        // action. Apps with no verified recipe are absent from the response
        // entirely, which is how the SPA knows not to offer the button.
        app.MapGet("/conflicts/autostart", (ConflictWatcher watcher) =>
        {
            var response = new GetConflictAutostartResponse();
            foreach (var c in watcher.GetConflicts())
            {
                var def = ConflictWatcher.FindById(c.Id);
                if (def is null || !ConflictAutostart.IsSupported(def)) continue;
                response.Apps.Add(new ConflictAutostartStatus
                {
                    Id = c.Id,
                    Entries = ConflictAutostart.Find(def),
                });
            }
            return Results.Ok(response);
        });

        // Disables everything the app's own recipe finds enabled. Only ever
        // reached from an explicit per-app user action; nothing here runs on
        // detection or on render. As with the kill route, the caller sends a
        // catalog id and never a registry path or service name.
        app.MapPost("/conflicts/autostart/disable", (DisableConflictAutostartBody body) =>
        {
            var def = ConflictWatcher.FindById(body?.Id ?? "");
            if (def is null)
            {
                return Results.BadRequest(new DisableConflictAutostartResponse { Error = true, Msg = "unknown conflict id" });
            }
            if (!ConflictAutostart.IsSupported(def))
            {
                return Results.BadRequest(new DisableConflictAutostartResponse { Error = true, Msg = "no verified autostart recipe" });
            }

            var entries = ConflictAutostart.Find(def);
            if (entries.Count == 0)
            {
                return Results.Ok(new DisableConflictAutostartResponse { Error = false, Msg = "already disabled" });
            }

            // Anything short of every entry leaves the app still starting with
            // Windows, so a partial result is reported as a failure.
            var disabled = ConflictAutostart.Disable(def, entries);
            return Results.Ok(new DisableConflictAutostartResponse
            {
                Error = disabled < entries.Count,
                Msg = disabled == entries.Count ? "Ok" : $"disabled {disabled} of {entries.Count}",
                Disabled = disabled,
            });
        }).LocalhostOnly();

        // Windows' own Dynamic Lighting - the conflicting "app" that ships with
        // the OS, driving the same LampArray devices. An unsupported platform
        // answers with the default state, whose Available false is what the SPA
        // reads to hide the section.
        // Loopback only, like the POST below: the read walks every HID
        // interface on the box. A LAN or relay browser renders the same modal,
        // so the row is absent there rather than never offered.
        app.MapGet("/conflicts/dynamic-lighting", () => Results.Ok(
            WindowsDynamicLighting.IsSupported() ? WindowsDynamicLighting.Read() : new WindowsDynamicLightingState()))
            .LocalhostOnly();

        // Answers with the state re-read from the registry, so the SPA renders
        // what Windows actually holds rather than what it asked for. Every path
        // answers 200: the client collapses a non-2xx to null, which would
        // reach the user as nothing happening.
        app.MapPost("/conflicts/dynamic-lighting", (SetWindowsDynamicLightingBody? body) =>
        {
            if (!WindowsDynamicLighting.IsSupported()) return Results.Ok(new WindowsDynamicLightingState());
            if (body?.Enabled is null) return Results.Ok(WindowsDynamicLighting.Read());
            return Results.Ok(WindowsDynamicLighting.Write(body.Enabled.Value));
        }).LocalhostOnly();

        // Terminate every running process matching the catalog entry for
        // <c>body.Id</c>, then stop any Windows services it lists (for apps
        // whose background service re-grabs the hardware). We never trust a
        // caller-supplied process/service name - the SPA only sends a catalog
        // id, and we resolve it to the names we have already vetted in
        // ConflictAppCatalog.
        app.MapPost("/conflicts/kill", (KillConflictBody body) =>
        {
            var def = ConflictWatcher.FindById(body?.Id ?? "");
            if (def is null)
            {
                return Results.BadRequest(new KillConflictResponse
                {
                    Error = true,
                    Msg = "unknown conflict id",
                });
            }

            // Measured, not inferred: ProcessKiller.Kill reports that it FOUND
            // processes, its own Kill being swallowed, and StopService counts an
            // already-stopped service as success.
            var outcome = ConflictKiller.Kill(def);

            return Results.Ok(new KillConflictResponse
            {
                Error = false,
                Msg = !outcome.RunningBefore ? "No matching process"
                    : outcome.RunningAfter ? "Still running"
                    : "Killed",
                Killed = outcome.Killed,
            });
        });
    }
}
