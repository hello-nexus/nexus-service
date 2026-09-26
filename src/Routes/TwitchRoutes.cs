using Microsoft.Net.Http.Headers;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Twitch;

namespace Nexus.Service.Routes;

public static class TwitchRoutes
{
    public static void MapTwitchEndpoints(this WebApplication app)
    {
        // Chat itself rides the twitch/chat/{channel} multiplex topic; only the
        // emote images need an HTTP route. Serving them here is what lets a
        // Q-series panel (no internet route of its own) render them, and keeps
        // the panel CSP free of a third-party image host.
        app.MapGet("/api/twitch/emote/{emoteId}", async (string emoteId, ITwitchEmoteCache cache) =>
        {
            var bytes = await cache.GetEmoteAsync(emoteId);
            if (bytes.Length == 0)
            {
                return Results.NotFound();
            }
            // Emote bytes are immutable for a given id.
            return Results.File(bytes, "image/png", entityTag: new EntityTagHeaderValue($"\"{emoteId}\""));
        }).AllowPanel();

        app.MapPost("/api/twitch/chat/{channel}/clear", (string channel, TwitchChatHub hub) =>
        {
            if (!TwitchChatHub.IsValidChannel(channel))
            {
                return Results.BadRequest(ApiResponse.Fail("Invalid channel"));
            }
            hub.Clear(channel.ToLowerInvariant());
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
    }
}
