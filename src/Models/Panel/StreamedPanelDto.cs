using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Panel;

/// <summary>
/// One desired stream session, published on GET /panel/streams/assignments.
/// The overlay reconciles its off-screen render hosts against this list on
/// every prefs poll; sessionIds are boot-scoped and re-minted on any config
/// change or device re-attach, so the diff is pure spawn/close.
/// </summary>
public sealed class StreamAssignmentDto
{
    public string SessionId { get; set; } = "";
    public string PanelDeviceId { get; set; } = "";
    public int CssWidth { get; set; }
    public int CssHeight { get; set; }
    public double Dpr { get; set; } = 1.0;
    public int Fps { get; set; } = 60;
    public int BitrateKbps { get; set; } = 8000;

    /// <summary>"h264" (default) or "rawBgra"; picks the overlay's frame sink.</summary>
    public string Codec { get; set; } = "h264";

    /// <summary>Base name of the Global shared-memory section the overlay may write raw frames into: it appends its writer nonce (the X-Nexus-Frame-Writer ingest header) and the service creates that section when the ingest binds; null keeps HTTP ingest only.</summary>
    public string? FrameSection { get; set; }

    /// <summary>Bytes one frame may occupy in <see cref="FrameSection"/>.</summary>
    public int FrameSectionBytes { get; set; }
}

public sealed class StreamAssignmentsResponse
{
    public List<StreamAssignmentDto> Assignments { get; set; } = new();
}

/// <summary>GET/PUT /panel/streams/settings. A null cap is serialized (the global ignore-null default would drop it) because null means "off".</summary>
public sealed class StreamSettingsDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? FpsCap { get; set; }
}
