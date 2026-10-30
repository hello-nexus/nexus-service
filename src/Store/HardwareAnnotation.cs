using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Nexus.Service.Store;

/// <summary>
/// Adds the service-owned <c>hardware</c> object (name, url, matched) to catalog
/// entries of hardware-bound apps, so the dashboard can show a product link
/// instead of Install.
/// </summary>
public static class HardwareAnnotation
{
    /// <summary>Keeps non-ASCII listing copy readable instead of escaping it.</summary>
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Annotates a list reply (<c>apps</c> array) or, when <paramref name="detailAppId"/> is given, a detail reply.
    /// Returns the input untouched when it is not catalog JSON or no entry is hardware-bound.
    /// </summary>
    public static async Task<string?> ApplyAsync(string? catalogJson, string? detailAppId, HardwareAppCatalog hardware)
    {
        if (string.IsNullOrEmpty(catalogJson)) return catalogJson;
        try
        {
            if (JsonNode.Parse(catalogJson) is not JsonObject obj) return catalogJson;
            var changed = false;
            if (detailAppId is null)
            {
                if (obj["apps"] is JsonArray apps)
                {
                    foreach (var entry in apps)
                    {
                        if (entry is JsonObject app && app["id"] is JsonValue id && id.TryGetValue<string>(out var appId))
                            changed |= await AnnotateAsync(app, appId, hardware).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                changed = await AnnotateAsync(obj, detailAppId, hardware).ConfigureAwait(false);
            }
            return changed ? obj.ToJsonString(Relaxed) : catalogJson;
        }
        // Duplicate keys throw ArgumentException on first index, after the parse.
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { return catalogJson; }
    }

    private static async Task<bool> AnnotateAsync(JsonObject entry, string appId, HardwareAppCatalog hardware)
    {
        if (HardwareAppCatalog.ProductFor(appId) is not { } product) return false;
        entry["hardware"] = new JsonObject
        {
            ["name"] = product.Name,
            ["url"] = product.Url,
            ["matched"] = await hardware.IsMatchedAsync(appId).ConfigureAwait(false),
        };
        return true;
    }
}
