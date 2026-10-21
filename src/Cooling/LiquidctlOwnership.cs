using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Nexus.Service.Cooling;

public enum OpenRgbOwnershipState
{
    /// <summary>No OpenRGB daemon is wanted; liquidctl may open every device.</summary>
    Off,
    /// <summary>The daemon is wanted but its device list is not read yet; Commander Core family devices stay closed.</summary>
    Unknown,
    /// <summary>The daemon's controller locations are known.</summary>
    Known,
}

/// <summary>What OpenRGB currently holds, as seen by the liquidctl poller.</summary>
internal sealed record OpenRgbOwnership(OpenRgbOwnershipState State, IReadOnlySet<string> HidrawNodes)
{
    public static readonly OpenRgbOwnership Off = new(OpenRgbOwnershipState.Off, new HashSet<string>());

    public static OpenRgbOwnership FromView(bool active, bool listKnown, IEnumerable<string> locations)
    {
        if (!active)
            return Off;
        if (!listKnown)
            return new OpenRgbOwnership(OpenRgbOwnershipState.Unknown, new HashSet<string>());
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loc in locations)
        {
            var node = LiquidctlOwnership.NormalizeHidraw(loc);
            if (node is not null)
                nodes.Add(node);
        }
        return new OpenRgbOwnership(OpenRgbOwnershipState.Known, nodes);
    }
}

internal sealed record LiquidctlListEntry(string Address, string Bus, int? VendorId, int? ProductId, string Description);

internal enum LiquidctlSkip { None, OpenRgbOwns, OpenRgbPending }

internal static class LiquidctlOwnership
{
    private const int CorsairVendorId = 0x1B1C;
    private static readonly int[] CommanderCoreProductIds = { 0x0C1C, 0x0C2A, 0x0C32 };

    /// <summary>"/dev/hidraw14" or "HID: /dev/hidraw14" to "hidraw14"; null when the string names no hidraw node.</summary>
    public static string? NormalizeHidraw(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var idx = raw.IndexOf("hidraw", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;
        var end = idx + "hidraw".Length;
        var digitsStart = end;
        while (end < raw.Length && char.IsAsciiDigit(raw[end]))
            end++;
        if (end == digitsStart)
            return null;
        return "hidraw" + raw.Substring(digitsStart, end - digitsStart);
    }

    public static bool IsCommanderCoreFamily(LiquidctlListEntry e)
    {
        if (e.VendorId == CorsairVendorId && e.ProductId is int pid && Array.IndexOf(CommanderCoreProductIds, pid) >= 0)
            return true;
        return e.Description.Contains("Commander Core", StringComparison.OrdinalIgnoreCase)
            || e.Description.Contains("Commander ST", StringComparison.OrdinalIgnoreCase);
    }

    public static LiquidctlSkip Decide(LiquidctlListEntry e, OpenRgbOwnership ownership)
    {
        switch (ownership.State)
        {
            case OpenRgbOwnershipState.Known:
                var node = NormalizeHidraw(e.Address);
                return node is not null && ownership.HidrawNodes.Contains(node) ? LiquidctlSkip.OpenRgbOwns : LiquidctlSkip.None;
            case OpenRgbOwnershipState.Unknown:
                return IsCommanderCoreFamily(e) ? LiquidctlSkip.OpenRgbPending : LiquidctlSkip.None;
            default:
                return LiquidctlSkip.None;
        }
    }

    /// <summary>Args for a status read that opens only the named device; non-path addresses (libusb) also pin the bus.</summary>
    public static string[] StatusArgs(LiquidctlListEntry e)
    {
        var args = new List<string> { "--json" };
        if (e.Address.Length > 0 && e.Address[0] != '/' && e.Bus.Length > 0)
        {
            args.Add("--bus");
            args.Add(e.Bus);
        }
        args.Add("--address");
        args.Add(e.Address);
        args.Add("status");
        return args.ToArray();
    }

    /// <summary>Parses <c>liquidctl --json list</c> (or any array whose items carry an address).</summary>
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
                result.Add(new LiquidctlListEntry(address, Str(el, "bus"),
                    Id(el, "vendor_id"), Id(el, "product_id"), Str(el, "description")));
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
