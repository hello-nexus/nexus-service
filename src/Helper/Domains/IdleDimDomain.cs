#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>lighting.idleDimWatch</c>. Service-to-helper, one-way. Arms
/// the helper's idle-time poll with a threshold (0 stops it) and its display
/// power-state notification.
/// </summary>
public sealed class IdleDimWatchPayload
{
    public int ThresholdSeconds { get; set; }
    public bool DisplayWatch { get; set; }
}

/// <summary>
/// Payload for <c>lighting.idleState</c>. Helper-to-service. Idle means the
/// session's input idle time passed the armed threshold. It says nothing about
/// what the input was.
/// </summary>
public sealed class IdleStatePayload
{
    public bool Idle { get; set; }
}

/// <summary>Payload for <c>lighting.screenOffTimeoutChanged</c>. Helper-to-service: a power setting that decides the screen-off timeout may have changed.</summary>
public sealed class ScreenOffTimeoutChangedPayload
{
}

/// <summary>Payload for <c>lighting.displayState</c>. Helper-to-service. Off is true only when the console display is off; dimmed counts as on.</summary>
public sealed class DisplayStatePayload
{
    public bool Off { get; set; }
}

[SupportedOSPlatform("windows")]
public static class IdleDimCommands
{
    public const string WatchType = "lighting.idleDimWatch";
    public const string IdleStateType = "lighting.idleState";
    public const string DisplayStateType = "lighting.displayState";
    public const string ScreenOffTimeoutChangedType = "lighting.screenOffTimeoutChanged";

    public static Task SetWatchAsync(HelperRegistry registry, int thresholdSeconds, bool displayWatch, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: WatchType,
            payload: new IdleDimWatchPayload { ThresholdSeconds = thresholdSeconds, DisplayWatch = displayWatch },
            payloadType: AppJsonContext.Default.IdleDimWatchPayload,
            ct: ct);
    }
}

/// <summary>Helper-side handler binding <c>lighting.idleDimWatch</c> to the idle poller and the display-state watch.</summary>
[SupportedOSPlatform("windows")]
public sealed class IdleDimHandler
{
    private readonly Action<int, bool> _onWatchChanged;

    public IdleDimHandler(Action<int, bool> onWatchChanged) => _onWatchChanged = onWatchChanged;

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(IdleDimCommands.WatchType, (env, _) =>
        {
            try
            {
                var p = env.Payload is null
                    ? null
                    : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.IdleDimWatchPayload);
                _onWatchChanged(Math.Max(0, p?.ThresholdSeconds ?? 0), p?.DisplayWatch == true);
            }
            catch { }
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
