using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Service.Lighting.Scene;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Widgets;

namespace Nexus.Service.Cloud;

/// <summary>
/// Carries the shared PC setup through the profile Back up as one reserved
/// app-data document, so the cloud api and the file format stay unchanged. The
/// document is the record without its machine-local revision. Only a record a
/// client has saved is sent; the migrated view of an old scene is not.
/// </summary>
public static class PcSetupBackup
{
    public const string AppId = "nexus.pc-setup";
    public const string Key = "setup";

    /// <summary>One-way, prefixed digest of an install id: a profile file names its machine by this, never by the id itself.</summary>
    public static string MachineDigest(string installId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("nexus-profile-file-machine-v1:" + installId)));

    /// <summary>
    /// Hash of the saved record's own canonical serialization, so "changed since
    /// the last Back up" never depends on the api's copy. Null with no saved record.
    /// </summary>
    public static string? Digest(PcSetupStore? store) =>
        store?.LoadSaved() is { } saved ? Digest(saved.Setup) : null;

    private static string Digest(PcSetup setup) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Skinny(setup), PersistenceJsonContext.Default.PcSetup))));

    // The Back up copy is user choices only: Build recomputes resolved (objects and case mesh) and re-reads each item's specs by part id.
    private static PcSetup Skinny(PcSetup setup)
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(setup, PersistenceJsonContext.Default.PcSetup), PersistenceJsonContext.Default.PcSetup) ?? new PcSetup();
        copy.Resolved = new PcSetupResolved();
        foreach (var item in copy.Items)
        {
            if (item?.Catalog is { } catalog)
            {
                catalog.Specs = null;
            }
        }
        return copy;
    }

    /// <summary>
    /// A copy of <paramref name="appData"/> with the record added, or the input
    /// itself when there is no saved record or adding it would put the bundle
    /// over the cap the other app data already has to fit in.
    /// <paramref name="digest"/> is the <see cref="Digest(PcSetupStore?)"/> of what was
    /// attached, null when nothing was.
    /// </summary>
    public static Dictionary<string, Dictionary<string, JsonElement>> Attach(
        Dictionary<string, Dictionary<string, JsonElement>> appData, PcSetupStore? store, out string? digest)
    {
        digest = null;
        if (store?.LoadSaved() is not { } saved)
        {
            return appData;
        }
        var doc = JsonSerializer.SerializeToElement(Skinny(saved.Setup), PersistenceJsonContext.Default.PcSetup);
        var withSetup = new Dictionary<string, Dictionary<string, JsonElement>>(appData, System.StringComparer.Ordinal)
        {
            [AppId] = new Dictionary<string, JsonElement>(System.StringComparer.Ordinal) { [Key] = doc },
        };
        if (doc.GetRawText().Length > AppDataStore.MaxDataBytes || CloudProfileSyncService.AppDataTooLarge(withSetup))
        {
            return appData;
        }
        digest = Digest(saved.Setup);
        return withSetup;
    }

    /// <summary>
    /// <paramref name="appData"/> without the reserved document, and the record
    /// it carried when that record is valid. A bundle that reaches app-data
    /// storage must not carry the reserved app, so every restore path strips it.
    /// </summary>
    public static Dictionary<string, Dictionary<string, JsonElement>>? Detach(
        Dictionary<string, Dictionary<string, JsonElement>>? appData, out PcSetup? setup)
    {
        setup = null;
        if (appData is null || !appData.TryGetValue(AppId, out var docs))
        {
            return appData;
        }
        if (docs is not null && docs.TryGetValue(Key, out var doc))
        {
            try
            {
                var parsed = doc.Deserialize(PersistenceJsonContext.Default.PcSetup);
                if (PcSetupValidation.Validate(parsed) is null)
                {
                    setup = parsed;
                }
            }
            catch (JsonException)
            {
                // A malformed document is dropped; the rest of the restore proceeds.
            }
        }
        var rest = new Dictionary<string, Dictionary<string, JsonElement>>(appData, System.StringComparer.Ordinal);
        rest.Remove(AppId);
        return rest;
    }
}
