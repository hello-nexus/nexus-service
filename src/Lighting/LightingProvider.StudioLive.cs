using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lighting;

/// <summary>
/// Shader Studio live preview. A session is an engine overlay (<see cref="LightingEngine.SetStudioOverlay"/>)
/// painted over every device above pins and the running effect, like the LED mapper's wipes: no mode,
/// setting or effect is touched and ending the session just clears the overlay. In Off the engine
/// loop is stopped and the overlay shows once a mode starts.
/// </summary>
public sealed partial class LightingProvider
{
    /// <summary>Name the overlay shader runs under.</summary>
    public const string StudioLiveKey = "studio-live";

    /// <summary>No POST for this long and the service clears the overlay on its own.</summary>
    public const int StudioLeaseMs = 15_000;

    private readonly MultiplexHub? _panelHub;
    private readonly object _studioLock = new();
    private volatile StudioSession? _studio;
    private Timer? _studioTimer;
    // The overlay's fragment reads audio uniforms, which counts as audio demand for capture.
    private volatile bool _studioAudio;

    /// <summary>Clock for the lease; tests replace it.</summary>
    internal Func<long> StudioClockMs { get; set; } = () => Environment.TickCount64;

    public bool StudioLiveActive => _studio is not null;

    internal ShaderEffect? StudioLiveEffect => _studio?.Live;

    private sealed class StudioSession
    {
        public required string Id { get; set; }
        public required ShaderEffect Live { get; set; }
        public required string Frag { get; set; }
        public long LeaseDeadline { get; set; }
    }

    public StudioLiveResult StartStudioLive(StudioLiveBody body)
    {
        lock (_studioLock)
        {
            return StartStudioLiveLocked(body);
        }
    }

    private StudioLiveResult StartStudioLiveLocked(StudioLiveBody body)
    {
        if (!_gates.Lighting)
        {
            return Fail("lighting is off");
        }
        var frag = body.Frag ?? "";
        var speed = (float)(body.Speed / 50.0);
        var intensity = body.Intensity > 0 ? body.Intensity : 1f;
        Dictionary<string, float>? extras = body.Params is { Count: > 0 } ? new Dictionary<string, float>(body.Params) : null;

        var session = _studio;
        if (body.SessionId is not null && (session is null || !string.Equals(session.Id, body.SessionId, StringComparison.Ordinal)))
        {
            return new StudioLiveResult(StudioLiveOutcome.Ended, "session_ended", "");
        }

        if (session is not null)
        {
            if (!ApplyLook(session, body, frag, speed, intensity, extras, out var failure))
            {
                return Fail(failure);
            }
            // Another tab opening its own session takes over the overlay; the old id is retired.
            if (body.SessionId is null)
            {
                session.Id = NewSessionId();
            }
        }
        else
        {
            ShaderEffect live;
            try
            {
                live = MakeShader(StudioLiveKey, frag, speed, intensity, body.Hue, body.Colorize, body.Saturation, body.Contrast, extras, Nexus.Service.Persistence.SensorBinding.CopyMap(body.Sensors));
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
            session = new StudioSession { Id = NewSessionId(), Live = live, Frag = frag };
            _studio = session;
            _engine.SetStudioOverlay(live);
            SetStudioAudio(frag);
            _studioTimer ??= new Timer(_ => CheckStudioLease(), null, 1000, 1000);
        }
        session.LeaseDeadline = StudioClockMs() + StudioLeaseMs;
        // In Off the engine loop is stopped, as for the LED mapper's wipes: the overlay shows once any mode starts.
        return new StudioLiveResult(StudioLiveOutcome.Ok, "Ok", session.Id, _engine.CurrentEffect is null);
    }

    private static string NewSessionId() => Guid.NewGuid().ToString("N");

    private bool ApplyLook(StudioSession session, StudioLiveBody body, string frag, float speed, float intensity, Dictionary<string, float>? extras, out string failure)
    {
        failure = "";
        if (string.Equals(frag, session.Frag, StringComparison.Ordinal))
        {
            // Same fragment: uniforms in place, no recompile.
            var cur = session.Live;
            cur.Speed = speed;
            cur.Intensity = intensity;
            cur.Hue = body.Hue;
            cur.Colorize = body.Colorize;
            cur.Saturation = body.Saturation;
            cur.Contrast = body.Contrast;
            cur.ExtraParams = extras;
            cur.SensorOverrides = Nexus.Service.Persistence.SensorBinding.CopyMap(body.Sensors);
            return true;
        }
        ShaderEffect fresh;
        try
        {
            fresh = MakeShader(StudioLiveKey, frag, speed, intensity, body.Hue, body.Colorize, body.Saturation, body.Contrast, extras, Nexus.Service.Persistence.SensorBinding.CopyMap(body.Sensors));
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
        var old = _engine.SetStudioOverlay(fresh);
        session.Live = fresh;
        session.Frag = frag;
        old?.Dispose();
        SetStudioAudio(frag);
        return true;
    }

    private void SetStudioAudio(string? frag)
    {
        _studioAudio = frag is not null && ShaderLibrary.ReadsAudio(frag);
        ReconcileAudioCapture();
    }

    private static StudioLiveResult Fail(string message) => new(StudioLiveOutcome.Failed, message, "");

    public bool StopStudioLive(string? sessionId = null)
    {
        bool was;
        lock (_studioLock)
        {
            if (sessionId is not null && (_studio is not { } s || !string.Equals(s.Id, sessionId, StringComparison.Ordinal)))
            {
                return false;
            }
            was = EndStudioLocked();
        }
        return was;
    }

    /// <summary>Clears the overlay, so the running mode shows again. Caller holds _studioLock.</summary>
    private bool EndStudioLocked()
    {
        var s = _studio;
        if (s is null)
        {
            return false;
        }
        _studio = null;
        _studioTimer?.Dispose();
        _studioTimer = null;
        _engine.SetStudioOverlay(null)?.Dispose();
        SetStudioAudio(null);
        return true;
    }

    internal void CheckStudioLease()
    {
        var ended = false;
        lock (_studioLock)
        {
            if (_studio is { } s && StudioClockMs() > s.LeaseDeadline)
            {
                ended = EndStudioLocked();
            }
        }
        if (ended && _panelHub is not null)
        {
            PanelTopics.BroadcastLighting(_panelHub);
        }
    }
}
