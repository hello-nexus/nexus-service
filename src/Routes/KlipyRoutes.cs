using Microsoft.Net.Http.Headers;
using Nexus.Service.Auth;
using Nexus.Service.Klipy;
using Nexus.Service.Models.Klipy;

namespace Nexus.Service.Routes;

public static class KlipyRoutes
{
    public static void MapKlipyEndpoints(this WebApplication app)
    {
        // Search and trending are one route: an empty q is the trending page,
        // which is what the picker shows before the user types.
        app.MapGet("/api/klipy/search", async (string? q, int? page, IKlipyCatalog catalog, HttpContext ctx) =>
            await catalog.SearchAsync(q, page ?? 1, ctx.RequestAborted)).AllowPanel();

        // Proxied so a Q-series panel (no IP route of its own) can render the
        // grid, and so the panel CSP needs no third-party image host.
        app.MapGet("/api/klipy/thumb/{slug}", async (string slug, IKlipyCatalog catalog) =>
        {
            var bytes = await catalog.GetThumbAsync(slug);
            if (bytes.Length == 0)
            {
                return Results.NotFound();
            }
            // A slug's file is immutable, so the WebView can keep it.
            return Results.File(bytes, "image/webp", entityTag: new EntityTagHeaderValue($"\"{slug}\""));
        }).AllowPanel();
    }
}
