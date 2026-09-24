using System.Collections.Generic;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// A panel deck key press, executed server-side from the action stored in the
/// widget's deck instance/preset. The request names a slot, never an action:
/// a paired panel triggers the file, hotkey, text and audio keys the desktop
/// app configured (DeckLayoutPolicy keeps a panel session from authoring
/// them), while <c>/system/open-path</c>, <c>/system/input/*</c> and
/// <c>/system/audio/play</c> stay desktop-token only.
/// </summary>
public static class PanelDeckRoutes
{
    public static void MapPanelDeckEndpoints(this WebApplication app)
    {
        app.MapPost("/panel/deck/dispatch", async (PanelDeckDispatchBody body, PanelDeviceRegistry registry, IConfigStore store, IDeckActionExecutor executor, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.DeviceId) || string.IsNullOrWhiteSpace(body.WidgetId))
            {
                return Results.BadRequest(ApiResponse.Fail("deviceId and widgetId are required"));
            }
            var action = ResolveAction(registry.Get(body.DeviceId)?.Layout, store.Load().StreamDeck, body);
            if (action is null)
            {
                return Results.NotFound(ApiResponse.Fail("deck slot not found"));
            }
            var folderPath = body.FolderPath ?? new List<int>();
            var latchKey = $"panel:{body.DeviceId}:{body.WidgetId}:{body.Page}:{string.Join('.', folderPath)}:{body.Slot}";
            await executor.ExecuteAsync(action, $"panel:{body.DeviceId}", body.Slot, latchKey, ct);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
    }

    /// <summary>
    /// The stored action the press names, or null when the widget is not a deck
    /// widget in this layout, its instance has no resolvable preset, or the
    /// fitted slot holds no action. A toggle resolves to the requested branch
    /// when the panel supplied one.
    /// </summary>
    internal static DeckAction? ResolveAction(PanelLayoutDto? layout, StreamDeckSettings settings, PanelDeckDispatchBody body)
    {
        var widget = FindWidget(layout, body.WidgetId);
        if (widget is null || !string.Equals(widget.Type, DeckLayoutPolicy.DeckWidgetType, StringComparison.Ordinal))
        {
            return null;
        }
        var grid = DeckConfigNavigation.InnerGridForSize(widget.Size);
        var config = DeckInstanceResolver.ResolveFittedConfig(
            settings, DeckInstanceResolver.WidgetInstanceId(body.WidgetId), grid.Cols, grid.Rows, DeckTargetKind.Widget);

        var indices = new List<int>(body.FolderPath ?? new List<int>()) { body.Slot };
        var action = DeckConfigNavigation.ResolveSlot(config, body.Page, indices)?.Action;
        if (action is null)
        {
            return null;
        }
        if (action.Type == "toggle" && body.Branch is "on" or "off")
        {
            return body.Branch == "on" ? action.On : action.Off;
        }
        return action;
    }

    private static PanelWidgetDto? FindWidget(PanelLayoutDto? layout, string widgetId)
    {
        if (layout is null)
        {
            return null;
        }
        foreach (var page in layout.Pages)
        {
            foreach (var widget in page.Widgets)
            {
                if (string.Equals(widget.Id, widgetId, StringComparison.Ordinal))
                {
                    return widget;
                }
            }
        }
        return null;
    }
}
