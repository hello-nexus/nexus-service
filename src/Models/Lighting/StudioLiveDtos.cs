using System.Collections.Generic;

namespace Nexus.Service.Models.Lighting;

/// <summary>
/// POST body for /lighting/studio-live. The look fields mean what they do on the animate
/// start body: Speed 50 is 1x forward, negative runs in reverse, 0 freezes.
/// </summary>
public sealed class StudioLiveBody
{
    /// <summary>Absent on the POST that opens a session; every renewal sends the id the opening POST returned.</summary>
    public string? SessionId { get; set; }

    public string? Frag { get; set; }
    public int Speed { get; set; } = 50;
    public float Intensity { get; set; } = 1f;
    public float Hue { get; set; }
    public float Colorize { get; set; }
    public float Saturation { get; set; } = 1f;
    public float Contrast { get; set; } = 1f;
    public Dictionary<string, float>? Params { get; set; }

    /// <summary>Per sensor uniform, the pick over the fragment's default binding. Null or absent keeps the defaults.</summary>
    public Dictionary<string, Nexus.Service.Persistence.SensorBinding>? Sensors { get; set; }
}

/// <summary>Answer to POST /lighting/studio-live (Error and Msg come from the envelope).</summary>
public sealed class StudioLiveResponse : ApiResponse
{
    /// <summary>True while the session runs.</summary>
    public bool Active { get; set; }

    /// <summary>Milliseconds the lease lasts after this call; re-POST before it runs out.</summary>
    public int LeaseMs { get; set; }

    /// <summary>True when the engine has no running effect, so the overlay shows only once a mode starts. Recomputed on every call.</summary>
    public bool LightingOff { get; set; }

    /// <summary>Opaque id of the running session. Send it on every renewal and on DELETE.</summary>
    public string SessionId { get; set; } = "";
}
