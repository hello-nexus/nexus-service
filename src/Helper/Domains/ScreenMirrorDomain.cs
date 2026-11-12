#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>screenMirror.start</c>. Service-to-helper. Tells the
/// helper to begin DXGI desktop duplication on the chosen monitor and
/// push downsampled frames back via <see cref="ScreenMirrorFramePayload"/>.
/// Width/Height are the destination canvas size; helper resamples on its
/// side so the wire payload stays small (160x90 RGB24 = 43 KB).
/// </summary>
public sealed class ScreenMirrorStartPayload
{
    public string MonitorId { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>Payload for <c>screenMirror.stop</c>. Stops the capture thread and releases DXGI resources.</summary>
public sealed class ScreenMirrorStopPayload { }

/// <summary>
/// Payload for <c>screenMirror.frame</c>. Helper-to-service push, one
/// envelope per captured frame. <see cref="Bytes"/> is canvas-resolution
/// RGB24 already downscaled; the service blits straight to the lighting
/// canvas.
/// </summary>
public sealed class ScreenMirrorFramePayload
{
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
}

// JSON source-gen registration lives in src/Serialization/AppJsonContext.cs.

[SupportedOSPlatform("windows")]
public static class ScreenMirrorCommands
{
    public static Task StartAsync(HelperRegistry registry, string monitorId, int width, int height, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "screenMirror.start",
            payload: new ScreenMirrorStartPayload { MonitorId = monitorId, Width = width, Height = height },
            payloadType: AppJsonContext.Default.ScreenMirrorStartPayload,
            ct: ct);
    }

    public static Task StopAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "screenMirror.stop",
            payload: new ScreenMirrorStopPayload(),
            payloadType: AppJsonContext.Default.ScreenMirrorStopPayload,
            ct: ct);
    }
}

[SupportedOSPlatform("windows")]
public sealed class ScreenMirrorHandler
{
    private readonly Action<string, int, int> _start;
    private readonly Action _stop;

    public ScreenMirrorHandler(Action<string, int, int> start, Action stop)
    {
        _start = start;
        _stop = stop;
    }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register("screenMirror.start", (env, _) =>
        {
            if (env.Payload is null)
            {
                Nexus.Service.Platform.HelperLog.Write("[mirror-diag] screenMirror.start without payload");
                return Task.FromResult(env.Ok());
            }
            try
            {
                var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ScreenMirrorStartPayload);
                Nexus.Service.Platform.HelperLog.Write($"[mirror-diag] screenMirror.start received parsed={p is not null}");
                if (p is not null) _start(p.MonitorId, p.Width, p.Height);
            }
            catch (Exception ex)
            {
                Nexus.Service.Platform.HelperLog.Write($"[mirror-diag] screenMirror.start handler failed {ex.GetType().Name}: {ex.Message}");
            }
            return Task.FromResult(env.Ok());
        });
        registry.Register("screenMirror.stop", (env, _) =>
        {
            try { _stop(); } catch (Exception ex) { Nexus.Service.Platform.HelperLog.Write($"[mirror-diag] screenMirror.stop failed {ex.GetType().Name}: {ex.Message}"); }
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
