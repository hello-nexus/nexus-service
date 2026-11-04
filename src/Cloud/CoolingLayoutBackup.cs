using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Service.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Widgets;

namespace Nexus.Service.Cloud;

/// <summary>
/// Carries the cooling layout (user choices only: directions, bindings, tests) through the
/// profile Back up as one reserved app-data document, mirroring <see cref="PcSetupBackup"/>.
/// Only a layout a client has saved is sent; the document omits the machine-local revision.
/// </summary>
public static class CoolingLayoutBackup
{
    public const string AppId = "nexus.cooling-layout";
    public const string Key = "layout";

    /// <summary>Hash of the saved layout's canonical serialization; null with no saved layout.</summary>
    public static string? Digest(CoolingLayoutStore? store) =>
        store?.LoadSaved() is { } saved ? Digest(saved.Layout) : null;

    private static string Digest(CoolingLayout layout) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(layout, PersistenceJsonContext.Default.CoolingLayout))));

    /// <summary>A copy of <paramref name="appData"/> with the layout added, or the input itself when there is none or it would not fit the bundle cap. <paramref name="digest"/> is null when nothing was attached.</summary>
    public static Dictionary<string, Dictionary<string, JsonElement>> Attach(
        Dictionary<string, Dictionary<string, JsonElement>> appData, CoolingLayoutStore? store, out string? digest)
    {
        digest = null;
        if (store?.LoadSaved() is not { } saved)
        {
            return appData;
        }
        var doc = JsonSerializer.SerializeToElement(saved.Layout, PersistenceJsonContext.Default.CoolingLayout);
        var withLayout = new Dictionary<string, Dictionary<string, JsonElement>>(appData, StringComparer.Ordinal)
        {
            [AppId] = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [Key] = doc },
        };
        if (doc.GetRawText().Length > AppDataStore.MaxDataBytes || CloudProfileSyncService.AppDataTooLarge(withLayout))
        {
            return appData;
        }
        digest = Digest(saved.Layout);
        return withLayout;
    }

    /// <summary>
    /// <paramref name="appData"/> without the reserved document, and the layout it carried when valid.
    /// Bindings naming channels this machine lacks are kept; the UI ignores what it cannot match.
    /// </summary>
    public static Dictionary<string, Dictionary<string, JsonElement>>? Detach(
        Dictionary<string, Dictionary<string, JsonElement>>? appData, out CoolingLayout? layout)
    {
        layout = null;
        if (appData is null || !appData.TryGetValue(AppId, out var docs))
        {
            return appData;
        }
        if (docs is not null && docs.TryGetValue(Key, out var doc))
        {
            try
            {
                var parsed = doc.Deserialize(PersistenceJsonContext.Default.CoolingLayout);
                if (CoolingLayoutValidation.Validate(parsed) is null)
                {
                    layout = parsed;
                }
            }
            catch (JsonException)
            {
                // A malformed document is dropped; the rest of the restore proceeds.
            }
        }
        var rest = new Dictionary<string, Dictionary<string, JsonElement>>(appData, StringComparer.Ordinal);
        rest.Remove(AppId);
        return rest;
    }
}
