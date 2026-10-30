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
}

public sealed class StreamAssignmentsResponse
{
    public List<StreamAssignmentDto> Assignments { get; set; } = new();
}
