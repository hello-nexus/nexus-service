namespace Nexus.Service.Models.Mcp;

/// <summary>Wire shape for GET /ai/status, and the response every /ai/* write route echoes back.</summary>
public sealed class AiStatusResponse
{
    public bool Enabled { get; set; }
    public bool Running { get; set; }
    public int Port { get; set; }
    public bool AllowRemote { get; set; }
    public string Endpoint { get; set; } = "";
    /// <summary>Empty until the token has been minted (first enable or a rotate).</summary>
    public string Token { get; set; } = "";
    public string? LastError { get; set; }
    public AiCapabilitiesDto Capabilities { get; set; } = new();
}

public sealed class AiCapabilitiesDto
{
    public bool Telemetry { get; set; } = true;
    public bool Cooling { get; set; } = true;
    public bool Lighting { get; set; } = true;
    public bool Profiles { get; set; } = true;
    public bool History { get; set; } = true;
    public bool Panels { get; set; } = true;
}

/// <summary>Body for POST /ai/config. Every field is nullable so the handler can
/// distinguish "client left this out" from "client explicitly sent value".</summary>
public sealed class AiConfigPatch
{
    public bool? Enabled { get; set; }
    public bool? AllowRemote { get; set; }
    public AiCapabilitiesPatch? Capabilities { get; set; }
}

public sealed class AiCapabilitiesPatch
{
    public bool? Telemetry { get; set; }
    public bool? Cooling { get; set; }
    public bool? Lighting { get; set; }
    public bool? Profiles { get; set; }
    public bool? History { get; set; }
    public bool? Panels { get; set; }
}
