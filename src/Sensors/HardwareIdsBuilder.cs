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
            Gpus = cards.Count == 0 ? null : cards.Select(c => new GpuIdsDto
            {
                Vendor = Hex(c.VendorId),
                Device = Hex(c.DeviceId),
                SubVendor = Hex(c.SubsystemVendorId),
                SubDevice = Hex(c.SubsystemDeviceId),
            }).ToList(),
            RamModules = inventory.RamModules.Count == 0 ? null : inventory.RamModules.Select(m => new RamModuleIdsDto
            {
                Manufacturer = m.Manufacturer,
                PartNumber = m.PartNumber,
                CapacityBytes = m.CapacityBytes,
                ConfiguredSpeedMts = m.ConfiguredSpeedMts,
            }).ToList(),
            Drives = inventory.Drives.Count == 0 ? null : inventory.Drives.Select(d => new DriveIdsDto
            {
                Vendor = d.Vendor,
                Model = d.Model,
            }).ToList(),
            Board = board is { } b && (!string.IsNullOrEmpty(b.Manufacturer) || !string.IsNullOrEmpty(b.Product))
                ? new BoardIdsDto { Manufacturer = b.Manufacturer, Product = b.Product }
                : null,
        };
    }

    /// <summary>A PCI id as four lowercase hex digits; null for zero (unread) and for values outside 16 bits.</summary>
    internal static string? Hex(int id) =>
        id is > 0 and <= 0xFFFF ? id.ToString("x4", CultureInfo.InvariantCulture) : null;
}
