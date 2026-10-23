#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>Payload for <c>astral.countdownNotice</c>. Service-to-helper: a ROG Astral shutdown countdown with a Cancel button.</summary>
public sealed class AstralNoticePayload
{
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string CancelLabel { get; set; } = "";
    /// <summary>The countdown the Cancel button applies to.</summary>
    public long CancelId { get; set; }
    /// <summary>SPA path the toast body, and the balloon fallback, opens.</summary>
    public string WindowPath { get; set; } = "";
}

/// <summary>Payload for <c>astral.cancel</c>. Helper-to-service: the user pressed Cancel on the countdown notice.</summary>
public sealed class AstralCancelPayload
{
    public long Id { get; set; }
}

// JSON source-gen registration lives in src/Serialization/AppJsonContext.cs.

[SupportedOSPlatform("windows")]
public static class AstralNoticeCommands
{
    public const string NoticeType = "astral.countdownNotice";
    public const string CancelType = "astral.cancel";

    /// <summary>Never held by the notification gate: a shutdown countdown must show.</summary>
    public static Task NoticeAsync(HelperRegistry registry, AstralNoticePayload payload, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: NoticeType,
            payload: payload,
            payloadType: AppJsonContext.Default.AstralNoticePayload,
            ct: ct);
    }
}

[SupportedOSPlatform("windows")]
public sealed class AstralNoticeHandler
{
    private readonly Action<AstralNoticePayload> _show;

    public AstralNoticeHandler(Action<AstralNoticePayload> show)
    {
        _show = show;
    }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(AstralNoticeCommands.NoticeType, (env, _) =>
        {
            if (env.Payload is null) return Task.FromResult(env.Ok());
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.AstralNoticePayload);
            if (p is not null) _show(p);
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
