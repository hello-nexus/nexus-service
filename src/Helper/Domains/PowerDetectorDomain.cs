#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>Payload for <c>powerDetector.notice</c>. Service-to-helper: a ROG Astral Power Detector alert or shutdown countdown.</summary>
public sealed class PowerDetectorNoticePayload
{
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>SPA path the toast opens (the card's page); empty opens the dashboard.</summary>
    public string WindowPath { get; set; } = "";
    /// <summary>The button's label. On a countdown it cancels the shutdown; on an alert it opens <see cref="WindowPath"/>.</summary>
    public string ButtonLabel { get; set; } = "";
    public bool CancelsShutdown { get; set; }
}

/// <summary>Payload for <c>powerDetector.cancel</c>. Helper-to-service: the user pressed Cancel shutdown on a countdown notice.</summary>
public sealed class PowerDetectorCancelPayload
{
}

// JSON source-gen registration lives in src/Serialization/AppJsonContext.cs.

[SupportedOSPlatform("windows")]
public static class PowerDetectorCommands
{
    public const string NoticeType = "powerDetector.notice";
    public const string CancelType = "powerDetector.cancel";

    /// <summary>
    /// Sent straight away, never held by <see cref="Nexus.Service.Notifications.NotificationGate"/>:
    /// onboarding or a focus mode must not hide an overcurrent warning or a
    /// shutdown countdown. A no-op with no helper connected.
    /// </summary>
    public static Task NoticeAsync(HelperRegistry registry, PowerDetectorNoticePayload payload, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: NoticeType,
            payload: payload,
            payloadType: AppJsonContext.Default.PowerDetectorNoticePayload,
            ct: ct);
    }
}

[SupportedOSPlatform("windows")]
public sealed class PowerDetectorNoticeHandler
{
    private readonly Action<PowerDetectorNoticePayload> _show;

    public PowerDetectorNoticeHandler(Action<PowerDetectorNoticePayload> show)
    {
        _show = show;
    }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(PowerDetectorCommands.NoticeType, (env, _) =>
        {
            if (env.Payload is null) return Task.FromResult(env.Ok());
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.PowerDetectorNoticePayload);
            if (p is not null) _show(p);
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
