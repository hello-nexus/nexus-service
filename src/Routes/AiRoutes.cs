using System;
using System.Security.Cryptography;
using Nexus.Service.Mcp;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;

namespace Nexus.Service.Routes;

/// <summary>
/// AI Integration (MCP) configuration surface for the dashboard, on the main
/// service host. Default bearer auth like every other desktop-only route -
/// not AllowPanel(), so a paired phone session gets 403, not access.
/// </summary>
public static class AiRoutes
{
    public static void MapAiEndpoints(this WebApplication app)
    {
        app.MapGet("/ai/status", (IConfigStore store, McpServerHost host) =>
            BuildStatus(store.Load().AiIntegration, host));

        app.MapPost("/ai/config", async (AiConfigPatch body, IConfigStore store, McpServerHost host, IMcpAuditSink audit) =>
        {
            var before = store.Load().AiIntegration;
            var wasEnabled = before.Enabled;
            var wasRemote = before.AllowRemote;
            store.Update(s =>
            {
                if (body.Enabled.HasValue)
                {
                    s.AiIntegration.Enabled = body.Enabled.Value;
                    if (s.AiIntegration.Enabled && string.IsNullOrEmpty(s.AiIntegration.Token))
                    {
                        s.AiIntegration.Token = MintToken();
                    }
                }
                if (body.AllowRemote.HasValue)
                {
                    s.AiIntegration.AllowRemote = body.AllowRemote.Value;
                }
                if (body.Capabilities is { } caps)
                {
                    if (caps.Telemetry.HasValue) s.AiIntegration.AllowTelemetry = caps.Telemetry.Value;
                    if (caps.Cooling.HasValue) s.AiIntegration.AllowCooling = caps.Cooling.Value;
                    if (caps.Lighting.HasValue) s.AiIntegration.AllowLighting = caps.Lighting.Value;
                    if (caps.Profiles.HasValue) s.AiIntegration.AllowProfiles = caps.Profiles.Value;
                    if (caps.History.HasValue) s.AiIntegration.AllowHistory = caps.History.Value;
                }
            });
            var after = store.Load().AiIntegration;
            var isEnabled = after.Enabled;
            var remoteChanged = wasRemote != after.AllowRemote;
            var enabledChanged = body.Enabled.HasValue && wasEnabled != isEnabled;
            if (enabledChanged)
            {
                RecordLifecycleEvent(audit, isEnabled ? "ai_integration_enabled" : "ai_integration_disabled");
            }
            if (remoteChanged)
            {
                RecordLifecycleEvent(audit, after.AllowRemote ? "ai_remote_enabled" : "ai_remote_disabled");
            }
            // Capability flags are read live on every tool call (see
            // AiIntegrationSettings), so a capabilities-only patch must not
            // bounce the port and abort in-flight MCP requests. Reconcile the
            // listener only when the desired state disagrees with reality: an
            // Enabled flip, an AllowRemote flip while enabled (the bind address
            // changes), or a re-posted enable after a failed bind (port
            // taken) where Enabled is already true but nothing is running.
            if (enabledChanged || (remoteChanged && isEnabled) || isEnabled != host.Running)
            {
                await host.ApplyConfiguredStateAsync();
            }
            return BuildStatus(store.Load().AiIntegration, host);
        });

        app.MapPost("/ai/token/rotate", (IConfigStore store, McpServerHost host, IMcpAuditSink audit) =>
        {
            store.Update(s => s.AiIntegration.Token = MintToken());
            RecordLifecycleEvent(audit, "ai_token_rotated");
            return BuildStatus(store.Load().AiIntegration, host);
        });
    }

    private static void RecordLifecycleEvent(IMcpAuditSink audit, string name) =>
        audit.Record(new McpAuditEntry(name, "{}", true, null, DateTimeOffset.UtcNow, AuditEntryKinds.Lifecycle));

    private static AiStatusResponse BuildStatus(AiIntegrationSettings settings, McpServerHost host)
    {
        var port = host.BoundPort ?? settings.Port;
        return new AiStatusResponse
        {
            Enabled = settings.Enabled,
            Running = host.Running,
            Port = port,
            AllowRemote = settings.AllowRemote,
            Endpoint = $"http://127.0.0.1:{port}/mcp",
            Token = settings.Token,
            LastError = host.LastError,
            Capabilities = new AiCapabilitiesDto
            {
                Telemetry = settings.AllowTelemetry,
                Cooling = settings.AllowCooling,
                Lighting = settings.AllowLighting,
                Profiles = settings.AllowProfiles,
                History = settings.AllowHistory,
            },
        };
    }

    // Same construction as TokenService.EnsureToken: a 32-byte URL-safe token.
    private static string MintToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }
}
