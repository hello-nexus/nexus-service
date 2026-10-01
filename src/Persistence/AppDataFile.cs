using System.Text.Json;

namespace Nexus.Service.Persistence;

/// <summary>
/// On-disk shape of one app-data document:
/// <c>&lt;NexusRoot&gt;/app-data/profiles/&lt;profileId&gt;/&lt;appId&gt;/&lt;key&gt;.json</c>.
/// A file written by an older build may carry a <c>cloud</c> member; it is ignored.
/// </summary>
public sealed class AppDataFile
{
    public int Revision { get; set; }
    public string UpdatedAt { get; set; } = "";
    public JsonElement Data { get; set; }
}
