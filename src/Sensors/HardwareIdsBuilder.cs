using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Sensors;

/// <summary>
/// Builds the model-identifier block of GET /system/specs. Only model ids are copied: the inventory types it reads never
/// carry a serial, and this builder takes no field that could.
/// </summary>
internal static class HardwareIdsBuilder
{
    public static HardwareIdsDto Build(
        IReadOnlyList<GpuCard> cards,
        HardwareInventory inventory,
        (string? Manufacturer, string? Product)? board)
    {
        return new HardwareIdsDto
        {
            Gpus = !cards.Any(HasAnyId) ? null : cards.Select(c => new GpuIdsDto
            {
                Vendor = Hex(c.VendorId),
                Device = Hex(c.DeviceId),
                SubVendor = Hex(c.SubsystemVendorId),
                SubDevice = Hex(c.SubsystemDeviceId),
            }).ToList(),
            RamModules = inventory.RamModules.Count == 0 ? null : inventory.RamModules.Select(m => new RamModuleIdsDto
            {
                Manufacturer = Real(m.Manufacturer),
                PartNumber = Real(m.PartNumber),
                CapacityBytes = m.CapacityBytes,
                ConfiguredSpeedMts = m.ConfiguredSpeedMts,
            }).ToList(),
            Drives = inventory.Drives.Count == 0 ? null : inventory.Drives.Select(d => new DriveIdsDto
            {
                Vendor = d.Vendor,
                Model = d.Model,
            }).ToList(),
            Board = board is { } b && (Real(b.Manufacturer) is not null || Real(b.Product) is not null)
                ? new BoardIdsDto { Manufacturer = Real(b.Manufacturer), Product = Real(b.Product) }
                : null,
        };
    }

    private static bool HasAnyId(GpuCard c) =>
        Hex(c.VendorId) is not null || Hex(c.DeviceId) is not null || Hex(c.SubsystemVendorId) is not null || Hex(c.SubsystemDeviceId) is not null;

    private static readonly string[] Placeholders =
    {
        "Default string", "To be filled by O.E.M.", "System Product Name", "Not Applicable", "None",
    };

    /// <summary>The value trimmed, or null when it is empty or a firmware placeholder a vendor left unset.</summary>
    internal static string? Real(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return Placeholders.Any(p => string.Equals(p, text, System.StringComparison.OrdinalIgnoreCase)) ? null : text;
    }

    /// <summary>A PCI id as four lowercase hex digits; null for zero (unread) and for values outside 16 bits.</summary>
    internal static string? Hex(int id) =>
        id is > 0 and <= 0xFFFF ? id.ToString("x4", CultureInfo.InvariantCulture) : null;
}
