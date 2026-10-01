using System.Text.Json;

namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Body of <c>PUT /apps-api/data/{appId}/{key}</c>. Compare-and-swap: the
/// write is rejected with the current document when <see cref="BaseRevision"/>
/// no longer matches what is stored.
/// </summary>
public sealed class AppDataPutRequest
{
    public int BaseRevision { get; set; }

    /// <summary>The profile id the caller last read this document under (GET response or broadcast). When it is not the active profile the write is refused with 409 <c>profile_switched</c>.</summary>
    public string? ProfileId { get; set; }
    public JsonElement Data { get; set; }
}

/// <summary>
/// Shape returned by <c>GET /apps-api/data/{appId}/{key}</c> and by a
/// rejected PUT's 409 body (the current document the caller must rebase
/// onto). <see cref="Data"/> is null exactly when <see cref="Revision"/> is 0
/// (no document has ever been written for this app/key).
/// </summary>
public sealed class AppDataDocumentDto
{
    /// <summary>The profile this document belongs to (the active one at read time).</summary>
    public string ProfileId { get; set; } = "";
    public int Revision { get; set; }
    public string UpdatedAt { get; set; } = "";
    public JsonElement? Data { get; set; }
}

/// <summary>Success shape of <c>PUT /apps-api/data/{appId}/{key}</c> - the new revision, no payload echo.</summary>
public sealed class AppDataPutResultDto
{
    public int Revision { get; set; }
    public string UpdatedAt { get; set; } = "";
}

/// <summary>409 body of a PUT whose <c>profileId</c> is no longer the active profile. Nothing was written.</summary>
public sealed class AppDataProfileSwitchedResponse : Nexus.Service.Models.ApiResponse
{
    public string Code { get; set; } = "profile_switched";
    public string ProfileId { get; set; } = "";
}

/// <summary>Payload of the <c>app-data-reset</c> topic: every running app instance reloads its data.</summary>
public sealed class AppDataResetFrame
{
    public string ProfileId { get; set; } = "";

    /// <summary>Unique per broadcast, so a restore into the already-active profile still changes the frame.</summary>
    public string ResetId { get; set; } = "";
}
