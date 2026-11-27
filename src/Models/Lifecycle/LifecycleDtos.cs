using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Lifecycle;

public class SetWillStartParams
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
}

public class WillStartResponse : ApiResponse
{
    public bool Enabled { get; set; }
}

public class PawnIoStatus
{
    public bool Installed { get; set; }
    public bool Open { get; set; }

    /// <summary>One of the <c>PawnIoStates</c> wire strings. "ok" whenever the driver is usable and on every non-Windows host.</summary>
    public string State { get; set; } = "ok";

    /// <summary>Win32 or devnode problem code behind a not-ok state, when one is known. Always emitted, null included.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? ErrorCode { get; set; }

    /// <summary>"install", "repair", "restart" or "none": what the one-click fix does for this state.</summary>
    public string FixAction { get; set; } = "none";

    /// <summary>True while POST /pawnio/fix is running.</summary>
    public bool Fixing { get; set; }
}
