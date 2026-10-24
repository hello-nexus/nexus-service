using System.Collections.Generic;
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

    /// <summary>
    /// A copy of <paramref name="appData"/> with the record added, or the input
    /// itself when there is no saved record or adding it would put the bundle
    /// over the cap the other app data already has to fit in.
    /// </summary>
    public static Dictionary<string, Dictionary<string, JsonElement>> Attach(
        Dictionary<string, Dictionary<string, JsonElement>> appData, PcSetupStore? store)
    {
        if (store?.LoadSaved() is not { } saved)
        {
            return appData;
        }
        var doc = JsonSerializer.SerializeToElement(saved.Setup, PersistenceJsonContext.Default.PcSetup);
        var withSetup = new Dictionary<string, Dictionary<string, JsonElement>>(appData, System.StringComparer.Ordinal)
        {
            [AppId] = new Dictionary<string, JsonElement>(System.StringComparer.Ordinal) { [Key] = doc },
        };
        return doc.GetRawText().Length > AppDataStore.MaxDataBytes || CloudProfileSyncService.AppDataTooLarge(withSetup)
            ? appData
            : withSetup;
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
