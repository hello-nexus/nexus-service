using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Mcp.Assistant;
using Nexus.Service.Models;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// Local AI assistant surface: managed Ollama runtime install/remove, model
/// pull/remove/select, and the natural-language query loop. Main service host,
/// desktop bearer auth like every other dashboard-only route - not
/// AllowPanel(), so a paired phone session gets 403, not access. Every runtime/
/// model mutation runs against CancellationToken.None, not the request's own
/// token: install and pull are fire-and-forget (a client navigating away must
/// not abort a multi-GB download - progress rides the "aiAssistant" WS topic
/// and GET /ai/assistant/status instead), and remove is still awaited inline
/// but must not be left half-deleted by a disconnecting client either.
/// </summary>
public static class AiAssistantRoutes
{
    public static void MapAiAssistantEndpoints(this WebApplication app)
    {
        app.MapGet("/ai/assistant/status", async (OllamaRuntimeManager runtime, IConfigStore store, CancellationToken ct) =>
        {
            await runtime.RefreshAsync(ct);
            return Results.Json(await BuildStatusAsync(runtime, store, ct), AppJsonContext.Default.AssistantStatusResponse);
        });

        app.MapPost("/ai/assistant/runtime/install", async (OllamaRuntimeManager runtime, IConfigStore store, CancellationToken ct) =>
        {
            _ = runtime.InstallRuntimeAsync(CancellationToken.None);
            return Results.Json(await BuildStatusAsync(runtime, store, ct), AppJsonContext.Default.AssistantStatusResponse);
        });

        app.MapPost("/ai/assistant/runtime/remove", async (OllamaRuntimeManager runtime, IConfigStore store, CancellationToken ct) =>
        {
            await runtime.RemoveRuntimeAsync(deleteModels: true, CancellationToken.None);
            return Results.Json(await BuildStatusAsync(runtime, store, ct), AppJsonContext.Default.AssistantStatusResponse);
        });

        // Opt-in to adopt an Ollama already listening on the default port. Off,
        // the service only ever talks to the runtime it downloaded and launched
        // itself; a foreign listener would otherwise receive every prompt and
        // drive the tool calls.
        app.MapPost("/ai/assistant/runtime/use-system", async (AssistantUseSystemRequest body, OllamaRuntimeManager runtime, IConfigStore store, CancellationToken ct) =>
        {
            store.Update(s => s.AiIntegration.UseSystemOllama = body.Enabled);
            runtime.ApplyUseSystemOllama(body.Enabled);
            if (body.Enabled)
            {
                await runtime.RefreshAsync(ct);
            }
            return Results.Json(await BuildStatusAsync(runtime, store, ct), AppJsonContext.Default.AssistantStatusResponse);
        });

        app.MapPost("/ai/assistant/model/pull", async (AssistantModelPullRequest body, OllamaRuntimeManager runtime, IConfigStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Model))
            {
                return Results.Json(ApiResponse.Fail("'model' is required."), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status400BadRequest);
            }
            _ = runtime.PullModelAsync(body.Model, CancellationToken.None);
            return Results.Json(await BuildStatusAsync(runtime, store, ct), AppJsonContext.Default.AssistantStatusResponse);
        });

        app.MapPost("/ai/assistant/model/remove", async (AssistantModelRemoveRequest body, OllamaRuntimeManager runtime, IConfigStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Model))
            {
                return Results.Json(ApiResponse.Fail("'model' is required."), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status400BadRequest);
            }
            var removed = await runtime.RemoveModelAsync(body.Model, CancellationToken.None);
            if (removed && string.Equals(store.Load().AiIntegration.AssistantActiveModel, body.Model, System.StringComparison.Ordinal))
            {
                store.Update(s => s.AiIntegration.AssistantActiveModel = "");
            }
            return Results.Json(await BuildStatusAsync(runtime, store, ct), AppJsonContext.Default.AssistantStatusResponse);
        });

        app.MapPost("/ai/assistant/model/select", async (AssistantModelSelectRequest body, OllamaRuntimeManager runtime, IConfigStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Model) || !AssistantModelCatalog.IsKnownModel(body.Model))
            {
                return Results.Json(ApiResponse.Fail("Unknown model."), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status400BadRequest);
            }
            store.Update(s => s.AiIntegration.AssistantActiveModel = body.Model);
            return Results.Json(await BuildStatusAsync(runtime, store, ct), AppJsonContext.Default.AssistantStatusResponse);
        });

        app.MapPost("/ai/assistant/query", async (AssistantQueryRequest body, LocalAssistant assistant, CancellationToken ct) =>
        {
            var outcome = await assistant.RunAsync(body.Prompt, ct);
            if (outcome.IsRefused)
            {
                return Results.Json(ApiResponse.Fail(outcome.RefusalReason ?? "Refused."), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status400BadRequest);
            }
            return Results.Json(outcome.Response, AppJsonContext.Default.AssistantQueryResponse);
        });
    }

    private static async Task<AssistantStatusResponse> BuildStatusAsync(OllamaRuntimeManager runtime, IConfigStore store, CancellationToken ct)
    {
        var installed = await runtime.ListInstalledModelsAsync(ct);
        var installedDtos = new List<AssistantInstalledModelDto>(installed.Count);
        foreach (var m in installed)
        {
            installedDtos.Add(new AssistantInstalledModelDto { Id = m.Model.Length > 0 ? m.Model : m.Name, SizeBytes = m.Size });
        }

        var catalog = new List<AssistantCatalogModelDto>(AssistantModelCatalog.Models.Count);
        foreach (var m in AssistantModelCatalog.Models)
        {
            catalog.Add(new AssistantCatalogModelDto
            {
                Id = m.Id,
                Label = m.Label,
                DownloadBytes = m.DownloadBytes,
                RamHint = m.RamHint,
                Recommended = m.Recommended,
            });
        }

        var snap = runtime.GetSnapshot();
        return new AssistantStatusResponse
        {
            RuntimeState = OllamaRuntimeManager.StateWireName(snap.State),
            SystemOllamaDetected = snap.SystemDetected,
            UseSystemOllama = store.Load().AiIntegration.UseSystemOllama,
            DownloadProgress = snap.DownloadTotal is { } total && snap.DownloadReceived is { } received
                ? new AssistantDownloadProgressDto { Received = received, Total = total }
                : null,
            InstalledModels = installedDtos,
            ActiveModel = store.Load().AiIntegration.AssistantActiveModel,
            Catalog = catalog,
            Busy = snap.BusyKind is { } kind ? new AssistantBusyDto { Kind = kind, Model = snap.BusyModel } : null,
            LastError = snap.LastError,
        };
    }
}
