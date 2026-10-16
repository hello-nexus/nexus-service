using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Service.Models.Conflicts;

namespace Nexus.Service.Conflicts;

/// <summary>A connected USB device, by numeric id.</summary>
public readonly record struct ConnectedUsb(int VendorId, int ProductId, string Name);

/// <summary>A device a curated first-party handler recognizes, mapped to the catalog app that competes for it.</summary>
public readonly record struct CuratedUsbMapping(string AppId, int VendorId, int ProductId, string Name);

/// <summary>A row of the bundled supported-devices list that has a USB id.</summary>
public readonly record struct SupportedUsbDevice(int VendorId, int ProductId, string Name);

public sealed class ConflictAppRelevance
{
    public bool Qualifies { get; init; }
    public List<ConflictDevice> Devices { get; init; } = new();
}

public sealed class ConflictRelevanceResult
{
    /// <summary>Per catalog app id, whether it competes for hardware Nexus found and which.</summary>
    public Dictionary<string, ConflictAppRelevance> Apps { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hardware no catalog app is attributed to by id (RAM), named for the log.</summary>
    public List<string> Unattributed { get; init; } = new();

    public static readonly ConflictRelevanceResult Empty = new();

    public bool Qualifies(string appId) => Apps.TryGetValue(appId, out var r) && r.Qualifies;
}

/// <summary>
/// The rule that Nexus only touches a conflicting app when it has found that app's hardware connected.
/// Pure and side-effect free: plain data in, per-app verdict out.
/// </summary>
public static partial class ConflictRelevance
{
    public const string SourceCurated = "curated";
    public const string SourceUsb = "usb";
    public const string SourceBoard = "board";

    /// <summary>
    /// An app qualifies when it is always ended, or a connected device is attributed to it: a curated handler
    /// maps the exact VID:PID to it, the VID:PID is a supported device and the app names its vendor id or exact id,
    /// or the board vendor is the app's. A universal RGB app qualifies on any device Nexus would drive.
    /// RAM and other hardware without a USB id is never attributed.
    /// </summary>
    public static ConflictRelevanceResult Evaluate(
        IEnumerable<ConflictAppDefinition> catalog,
        IReadOnlyList<ConnectedUsb> connectedUsb,
        string? boardManufacturer,
        IReadOnlyList<string> ramManufacturers,
        IReadOnlyList<SupportedUsbDevice> supportedDevices,
        IReadOnlyList<CuratedUsbMapping> curatedMappings)
    {
        var supported = new HashSet<int>();
        foreach (var s in supportedDevices)
        {
            supported.Add(Pack(s.VendorId, s.ProductId));
        }
        var curatedIds = new HashSet<int>();
        foreach (var c in curatedMappings)
        {
            curatedIds.Add(Pack(c.VendorId, c.ProductId));
        }

        var board = NormalizeBoardVendor(boardManufacturer);
        var result = new ConflictRelevanceResult();
        foreach (var ram in ramManufacturers)
        {
            result.Unattributed.Add($"RAM {ram}");
        }

        // Every connected device Nexus would drive, for universal RGB apps.
        var drivable = new List<ConflictDevice>();
        var seenDrivable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in connectedUsb)
        {
            var packed = Pack(u.VendorId, u.ProductId);
            var curated = curatedIds.Contains(packed);
            if ((curated || supported.Contains(packed)) && seenDrivable.Add(Key(u)))
            {
                drivable.Add(Describe(u, curated ? SourceCurated : SourceUsb));
            }
        }
        if (board.Length > 0)
        {
            drivable.Add(BoardDevice(boardManufacturer));
        }

        foreach (var def in catalog)
        {
            var devices = new List<ConflictDevice>();
            if (def.ClaimsAllRgb)
            {
                devices.AddRange(drivable);
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var u in connectedUsb)
                {
                    var packed = Pack(u.VendorId, u.ProductId);
                    string? source = null;
                    if (IsCurated(curatedMappings, def.Id, u))
                    {
                        source = SourceCurated;
                    }
                    else if (Array.IndexOf(def.UsbIds, packed) >= 0
                        || (supported.Contains(packed) && Array.IndexOf(def.VendorIds, u.VendorId) >= 0))
                    {
                        source = SourceUsb;
                    }
                    if (source is not null && seen.Add(Key(u)))
                    {
                        devices.Add(Describe(u, source));
                    }
                }
                if (def.BoardVendor.Length > 0 && string.Equals(def.BoardVendor, board, StringComparison.Ordinal))
                {
                    devices.Add(BoardDevice(boardManufacturer));
                }
            }
            result.Apps[def.Id] = new ConflictAppRelevance { Qualifies = def.AlwaysEnded || devices.Count > 0, Devices = devices };
        }
        return result;
    }

    /// <summary>Catalog ids that compete for one USB device already known to be drivable (an OpenRGB card), plus every universal RGB app.</summary>
    public static List<string> AppIdsForUsbId(IEnumerable<ConflictAppDefinition> catalog, int vendorId, int productId)
    {
        var packed = Pack(vendorId, productId);
        var ids = new List<string>();
        foreach (var def in catalog)
        {
            if (def.ClaimsAllRgb || Array.IndexOf(def.VendorIds, vendorId) >= 0 || Array.IndexOf(def.UsbIds, packed) >= 0)
            {
                ids.Add(def.Id);
            }
        }
        return ids;
    }

    /// <summary>The "vid_XXXX&amp;pid_XXXX" ids in a HID device path, or null when the string carries none.</summary>
    public static (int VendorId, int ProductId)? ParseHidPath(string? location)
    {
        if (string.IsNullOrEmpty(location))
        {
            return null;
        }
        var m = HidPathPattern().Match(location);
        if (!m.Success)
        {
            return null;
        }
        return (int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex("vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex HidPathPattern();

    /// <summary>Maps a board manufacturer string (SMBIOS) to the vendor key apps declare, or empty when unrecognized.</summary>
    public static string NormalizeBoardVendor(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            return "";
        }
        if (manufacturer.Contains("ASUS", StringComparison.OrdinalIgnoreCase))
        {
            return "ASUS";
        }
        if (manufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase)
            || string.Equals(manufacturer.Trim(), "MSI", StringComparison.OrdinalIgnoreCase))
        {
            return "MSI";
        }
        if (manufacturer.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase))
        {
            return "Gigabyte";
        }
        if (manufacturer.Contains("ASRock", StringComparison.OrdinalIgnoreCase))
        {
            return "ASRock";
        }
        return "";
    }

    private static bool IsCurated(IReadOnlyList<CuratedUsbMapping> mappings, string appId, ConnectedUsb u)
    {
        foreach (var c in mappings)
        {
            if (c.VendorId == u.VendorId && c.ProductId == u.ProductId && string.Equals(c.AppId, appId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static int Pack(int vid, int pid) => (vid << 16) | pid;

    private static string Key(ConnectedUsb u) => $"{u.VendorId:X4}:{u.ProductId:X4}:{u.Name}";

    private static ConflictDevice Describe(ConnectedUsb u, string source) => new()
    {
        Name = u.Name,
        VendorId = $"0x{u.VendorId:X4}",
        ProductId = $"0x{u.ProductId:X4}",
        Source = source,
    };

    private static ConflictDevice BoardDevice(string? manufacturer) => new()
    {
        Name = $"{manufacturer!.Trim()} motherboard",
        Source = SourceBoard,
    };
}
