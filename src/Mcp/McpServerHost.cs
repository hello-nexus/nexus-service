using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Mcp;

/// <summary>
/// Owns the MCP (Model Context Protocol) listener: a separate slim Kestrel
/// host bound to loopback by default (all interfaces only when
/// AiIntegrationSettings.AllowRemote is set), serving one endpoint (POST /mcp) that speaks
/// Streamable HTTP per MCP spec revision 2025-11-25. Stateless - every
/// request is a single JSON-RPC message and no Mcp-Session-Id is ever issued.
///
/// Implements plain IHostedService (not BackgroundService): StartAsync
/// completes only after the listener has actually bound or failed, so a test
/// awaiting host.StartAsync/ApplyConfiguredStateAsync observes the real
/// outcome instead of racing a BackgroundService's async ExecuteAsync prefix.
/// Start/stop are serialized under a semaphore so a boot-time autostart can
/// never interleave with a concurrent /ai/config apply.
/// </summary>
public sealed class McpServerHost : IHostedService, IAsyncDisposable
{
    public const string LatestProtocolVersion = "2025-11-25";

    private static readonly HashSet<string> SupportedProtocolVersions = new(StringComparer.Ordinal)
    {
        "2025-03-26", "2025-06-18", LatestProtocolVersion,
    };

    private readonly IConfigStore _store;
    private readonly McpToolRegistry _registry;
    private readonly bool _testHost;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private WebApplication? _app;

    public McpServerHost(IConfigStore store, McpToolRegistry registry)
    {
        _store = store;
        _registry = registry;
        _testHost = Environment.GetEnvironmentVariable("NEXUS_TEST_HOST") == "1";
    }

    public bool Running { get; private set; }
    public int? BoundPort { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>True while the running listener is bound to all interfaces rather than loopback.</summary>
    public bool BoundRemote { get; private set; }

    /// <summary>
    /// NEXUS_TEST_HOST hosts skip autostart entirely - the integration suite
    /// starts the listener explicitly via <see cref="ApplyConfiguredStateAsync"/>
    /// against its own isolated config store.
    /// </summary>
    public Task StartAsync(CancellationToken ct) => _testHost ? Task.CompletedTask : ApplyConfiguredStateAsync(ct);

    public Task StopAsync(CancellationToken ct) => StopListenerAsync();

    /// <summary>
    /// Reconciles the listener with the persisted AiIntegrationSettings: stops
    /// any running listener, then starts a fresh one on the configured port
    /// when enabled. Called on boot and after every /ai/config write.
    /// </summary>
    public async Task ApplyConfiguredStateAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
            var settings = _store.Load().AiIntegration;
            if (!settings.Enabled)
            {
                return;
            }
            await StartInternalAsync(settings.Port, settings.AllowRemote, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StartInternalAsync(int port, bool allowRemote, CancellationToken ct)
    {
        WebApplication? app = null;
        try
        {
            if (allowRemote && port != 0)
            {
                ProbeLoopbackFree(port);
            }
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                if (allowRemote)
                {
                    k.ListenAnyIP(port);
                }
                else
                {
                    k.Listen(IPAddress.Loopback, port);
                }
            });

            app = builder.Build();
            app.MapPost("/mcp", HandlePostAsync);
            app.MapMethods("/mcp", new[] { "GET", "DELETE" },
                () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

            await app.StartAsync(ct).ConfigureAwait(false);

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            var boundAddress = addresses?.FirstOrDefault();
            BoundPort = boundAddress is not null && Uri.TryCreate(boundAddress, UriKind.Absolute, out var boundUri)
                ? boundUri.Port
                : port;

            _app = app;
            BoundRemote = allowRemote;
            Running = true;
            LastError = null;
        }
        catch (OperationCanceledException)
        {
            if (app is not null)
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception ex)
        {
            if (app is not null)
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }
            LastError = ex.Message;
            Running = false;
            BoundPort = null;
            BoundRemote = false;
        }
    }

    /// <summary>
    /// A wildcard bind succeeds even when another process already holds
    /// 127.0.0.1:port (measured on macOS and Windows), and loopback clients
    /// then reach that process instead of this listener, handing it the bearer
    /// token. Fail the start instead.
    /// </summary>
    private static void ProbeLoopbackFree(int port)
    {
        var probe = new TcpListener(IPAddress.Loopback, port);
        try
        {
            probe.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"127.0.0.1:{port} is already in use by another process", ex);
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task StopListenerAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        if (_app is null)
        {
            return;
        }
        var app = _app;
        _app = null;
        Running = false;
        BoundPort = null;
        BoundRemote = false;
        try
        {
            await app.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopListenerAsync().ConfigureAwait(false);

    private async Task HandlePostAsync(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !IsLoopbackOrigin(origin))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var providedToken = ExtractBearerToken(ctx);
        var expectedToken = _store.Load().AiIntegration.Token;
        if (!TokensMatch(providedToken, expectedToken))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var protocolVersionHeader = ctx.Request.Headers["MCP-Protocol-Version"].ToString();
        if (!string.IsNullOrEmpty(protocolVersionHeader) && !SupportedProtocolVersions.Contains(protocolVersionHeader))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string body;
        using (var reader = new StreamReader(ctx.Request.Body))
        {
            body = await reader.ReadToEndAsync(ctx.RequestAborted).ConfigureAwait(false);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "null" : body);
        }
        catch (JsonException)
        {
            await McpJsonRpc.WriteErrorAsync(ctx, null, -32700, "Parse error").ConfigureAwait(false);
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                // Batches (arrays) and any other non-object body are rejected
                // outright - this server only ever accepts one JSON-RPC message.
                await McpJsonRpc.WriteErrorAsync(ctx, null, -32600, "Invalid Request").ConfigureAwait(false);
                return;
            }

            var hasId = root.TryGetProperty("id", out var id);
            var hasMethod = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
            if (!hasId || !hasMethod)
            {
                // Notifications (no id) and response-shaped messages (no method,
                // since this server never sends requests of its own) get no body.
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            root.TryGetProperty("params", out var paramsElement);
            var method = methodElement.GetString() ?? "";
            switch (method)
            {
                case "initialize":
                    await WriteInitializeResultAsync(ctx, id, paramsElement).ConfigureAwait(false);
                    return;
                case "ping":
                    await McpJsonRpc.WriteResultAsync(ctx, id, w =>
                    {
                        w.WriteStartObject();
                        w.WriteEndObject();
                    }).ConfigureAwait(false);
                    return;
                case "tools/list":
                    await WriteToolsListResultAsync(ctx, id).ConfigureAwait(false);
                    return;
                case "tools/call":
                    await HandleToolsCallAsync(ctx, id, paramsElement, ctx.RequestAborted).ConfigureAwait(false);
                    return;
                default:
                    await McpJsonRpc.WriteErrorAsync(ctx, id, -32601, $"Method not found: {method}").ConfigureAwait(false);
                    return;
            }
        }
    }

    private Task WriteInitializeResultAsync(HttpContext ctx, JsonElement id, JsonElement paramsElement) =>
        McpJsonRpc.WriteResultAsync(ctx, id, writer =>
        {
            var requestedVersion = paramsElement.ValueKind == JsonValueKind.Object
                && paramsElement.TryGetProperty("protocolVersion", out var pv)
                && pv.ValueKind == JsonValueKind.String
                ? pv.GetString()
                : null;
            var negotiated = requestedVersion is not null && SupportedProtocolVersions.Contains(requestedVersion)
                ? requestedVersion
                : LatestProtocolVersion;

            writer.WriteStartObject();
            writer.WriteString("protocolVersion", negotiated);
            writer.WritePropertyName("capabilities");
            writer.WriteStartObject();
            writer.WritePropertyName("tools");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("serverInfo");
            writer.WriteStartObject();
            writer.WriteString("name", "nexus-service");
            writer.WriteString("title", "Nexus");
            writer.WriteString("version", Nexus.Service.BuildInfo.Version);
            writer.WriteEndObject();
            writer.WriteString("instructions",
                "Nexus exposes this PC's live hardware telemetry (CPU, GPU, memory, cooling, lighting) as MCP tools. " +
                "Call tools/list to see what is available; each tool description explains when to call it.");
            writer.WriteEndObject();
        });

    private Task WriteToolsListResultAsync(HttpContext ctx, JsonElement id) =>
        McpJsonRpc.WriteResultAsync(ctx, id, writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in _registry.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("title", tool.Title);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("inputSchema");
                writer.WriteRawValue(tool.InputSchemaJson);
                writer.WritePropertyName("annotations");
                writer.WriteStartObject();
                writer.WriteBoolean("readOnlyHint", tool.ReadOnly);
                writer.WriteBoolean("destructiveHint", !tool.ReadOnly);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private async Task HandleToolsCallAsync(HttpContext ctx, JsonElement id, JsonElement paramsElement, CancellationToken ct)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object
            || !paramsElement.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            await McpJsonRpc.WriteErrorAsync(ctx, id, -32602, "Invalid params: 'name' is required").ConfigureAwait(false);
            return;
        }

        var name = nameElement.GetString()!;
        if (!_registry.TryGetTool(name, out var tool))
        {
            await McpJsonRpc.WriteErrorAsync(ctx, id, -32602, $"Unknown tool: {name}").ConfigureAwait(false);
            return;
        }

        JsonElement? args = paramsElement.TryGetProperty("arguments", out var argsElement)
            && argsElement.ValueKind == JsonValueKind.Object
            ? argsElement
            : null;

        McpToolExecutionResult result;
        try
        {
            result = await _registry.CallAsync(tool, args, ct).ConfigureAwait(false);
        }
        catch (McpToolExecutionException ex)
        {
            await McpJsonRpc.WriteErrorAsync(ctx, id, -32603, ex.Message).ConfigureAwait(false);
            return;
        }

        await McpJsonRpc.WriteResultAsync(ctx, id, writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", result.Text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteBoolean("isError", result.IsError);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private static bool IsLoopbackOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }
        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.IsLoopback;
    }

    private static string? ExtractBearerToken(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return header["Bearer ".Length..].Trim();
    }

    /// <summary>
    /// Hashes both sides to a fixed width before the constant-time compare, so
    /// a length mismatch (the common case for a wrong guess) never takes a
    /// different code path than a same-length mismatch - unlike
    /// TokenService.Validate's direct byte compare, which this mirrors otherwise.
    /// </summary>
    private static bool TokensMatch(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided ?? string.Empty));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }
}
