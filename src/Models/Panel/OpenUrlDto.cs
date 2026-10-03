namespace Nexus.Service.Models.Panel;

public sealed class OpenUrlRequest
{
    public string Url { get; set; } = "";
    /// <summary>Set by the SDK host when an app's Button href is pressed; tags the click for telemetry.</summary>
    public string? AppId { get; set; }
}
