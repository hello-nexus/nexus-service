using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexus.Service.Store;

/// <summary>
/// Adds the service-owned <c>hardware</c> object (name, url, matched) to catalog
/// entries of hardware-bound apps, so the dashboard can show a product link
/// instead of Install.
/// </summary>
public static class HardwareAnnotation
{
    /// <summary>
    /// Annotates a list reply (<c>apps</c> array) or, when <paramref name="detailAppId"/> is given, a detail reply.
    /// Returns the input untouched when it is not catalog JSON or no entry is hardware-bound.
    /// </summary>
    public static string? Apply(string? catalogJson, string? detailAppId, HardwareAppCatalog hardware)
    {
        if (string.IsNullOrEmpty(catalogJson)) return catalogJson;
        JsonNode? root;
        try
        { root = JsonNode.Parse(catalogJson); }
        catch (JsonException)
        { return catalogJson; }
        if (root is not JsonObject obj) return catalogJson;

        var changed = false;
        if (detailAppId is null)
        {
            if (obj["apps"] is JsonArray apps)
            {
                foreach (var entry in apps)
                {
                    if (entry is JsonObject app && app["id"] is JsonValue id && id.TryGetValue<string>(out var appId))
                        changed |= Annotate(app, appId, hardware);
                }
            }
        }
        else
        {
            changed = Annotate(obj, detailAppId, hardware);
        }
        return changed ? obj.ToJsonString() : catalogJson;
    }

    private static bool Annotate(JsonObject entry, string appId, HardwareAppCatalog hardware)
    {
        if (HardwareAppCatalog.ProductFor(appId) is not { } product) return false;
        entry["hardware"] = new JsonObject
        {
            ["name"] = product.Name,
            ["url"] = product.Url,
            ["matched"] = hardware.IsMatched(appId),
        };
        return true;
    }
}
