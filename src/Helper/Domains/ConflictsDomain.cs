#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>Payload for <c>conflicts.launchNotice</c>. Service-to-helper: a catalog conflict app started while the service runs.</summary>
public sealed class ConflictLaunchNoticePayload
{
    public string AppId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string EndLabel { get; set; } = "";
    /// <summary>SPA path the toast body, and the balloon fallback, opens.</summary>
    public string WindowPath { get; set; } = "";
}

/// <summary>Payload for <c>conflicts.end</c>. Helper-to-service: the user pressed End task on a launch notice. The service resolves the id against the catalog, so nothing outside it can be ended.</summary>
public sealed class ConflictEndPayload
{
    public string AppId { get; set; } = "";
}

// JSON source-gen registration lives in src/Serialization/AppJsonContext.cs.

[SupportedOSPlatform("windows")]
public static class ConflictNoticeCommands
{
    public const string LaunchNoticeType = "conflicts.launchNotice";
    public const string EndType = "conflicts.end";

    public static Task LaunchNoticeAsync(HelperRegistry registry, ConflictLaunchNoticePayload payload, CancellationToken ct = default)
    {
        // Dropped, not held, while first-run onboarding owns the screen: its
        // conflict step lists running apps, and a held notice could name an
        // app that has exited by the time onboarding ends.
        if (!Nexus.Service.Notifications.NotificationGate.IsOpen) return Task.CompletedTask;
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: LaunchNoticeType,
            payload: payload,
            payloadType: AppJsonContext.Default.ConflictLaunchNoticePayload,
            ct: ct);
    }
}

[SupportedOSPlatform("windows")]
public sealed class ConflictNoticeHandler
{
    private readonly Action<ConflictLaunchNoticePayload> _showLaunchNotice;

    public ConflictNoticeHandler(Action<ConflictLaunchNoticePayload> showLaunchNotice)
    {
        _showLaunchNotice = showLaunchNotice;
    }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(ConflictNoticeCommands.LaunchNoticeType, (env, _) =>
        {
            if (env.Payload is null) return Task.FromResult(env.Ok());
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ConflictLaunchNoticePayload);
            if (p is not null) _showLaunchNotice(p);
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
