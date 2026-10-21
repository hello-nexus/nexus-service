using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Nexus.Service.Cooling;

internal sealed record LiquidctlListEntry(string Address, int? VendorId, int? ProductId, string Description);

internal static class LiquidctlOwnership
{
    private const int CorsairVendorId = 0x1B1C;
    // Every product id the OpenRGB fork's Commander Core detector registers.
    private static readonly int[] CommanderCoreProductIds = { 0x0C1C, 0x0C1D, 0x0C2A, 0x0C32, 0x0C3C, 0x0C3D, 0x0C3E };

    /// <summary>liquidctl wraps each status read in a wake/sleep pair; the sleep is byte-identical to OpenRGB's hardware-mode command.</summary>
    public static bool IsCommanderCoreFamily(LiquidctlListEntry e)
        => (e.VendorId == CorsairVendorId && e.ProductId is int pid && Array.IndexOf(CommanderCoreProductIds, pid) >= 0)
            || e.Description.Contains("Commander Core", StringComparison.OrdinalIgnoreCase)
            || e.Description.Contains("Commander ST", StringComparison.OrdinalIgnoreCase);

    public static string[] StatusArgs(LiquidctlListEntry e) => new[] { "--json", "--address", e.Address, "status" };

    /// <summary>Parses <c>liquidctl --json list</c>.</summary>
    public static List<LiquidctlListEntry> ParseList(string json)
    {
        var result = new List<LiquidctlListEntry>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return result; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    continue;
                var address = Str(el, "address");
                if (address.Length == 0)
                    continue;
                result.Add(new LiquidctlListEntry(address, Id(el, "vendor_id"), Id(el, "product_id"), Str(el, "description")));
            }
        }
        return result;
    }

    private static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? Id(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString() ?? "";
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            if (int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
                return h;
        }
        return null;
    }
}
