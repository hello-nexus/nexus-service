using System.Linq;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

public class HardwareInventoryParsersTests
{
    // Expected names are the JEP106 list in i2c-tools decode-dimms; the ids are the hex strings seen in prod ramModel data (029E, 04CD, 071C).
    [Theory]
    [InlineData("859B", "Crucial Technology")]
    [InlineData("0x859B", "Crucial Technology")]
    [InlineData("04CD", "G Skill Intl")]
    [InlineData("029E", "Corsair")]
    [InlineData("0198", "Kingston")]
    [InlineData("80CE", "Samsung")]
    [InlineData("80AD", "SK Hynix")]
    [InlineData("802C", "Micron Technology")]
    [InlineData("04EF", "Team Group Inc.")]
    [InlineData("04CB", "A-DATA Technology")]
    [InlineData("0502", "Patriot Memory")]
    [InlineData("01BA", "PNY Technologies Inc.")]
    [InlineData("0A76", "Lexar Co Limited")]
    [InlineData("09F7", "Netac Technology Co Ltd")]
    [InlineData("0B92", "Kingbank Technology Co Ltd")]
    [InlineData("071C", "Ramsta")]
    public void Jedec_ids_decode_from_the_count_then_code_bytes(string id, string expected) =>
        Assert.Equal(expected, JedecManufacturers.Decode(id));

    [Theory]
    [InlineData("Corsair")]
    [InlineData("FFFF")] // bad parity
    [InlineData("2C80")] // code byte first is not the SMBIOS order
    [InlineData("108F")] // bank past the end of the list
    [InlineData("0000")]
    [InlineData("")]
    public void Names_and_invalid_ids_do_not_decode(string text) => Assert.Null(JedecManufacturers.Decode(text));

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Not Specified")]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("Manufacturer0")]
    [InlineData("PartNum0")]
    [InlineData("Array1_PartNumber0")]
    [InlineData("   ")]
    [InlineData("0000")]
    public void Oem_placeholders_clean_to_null(string value) => Assert.Null(HardwareInventoryParsers.CleanText(value));

    [Fact]
    public void Part_number_is_trimmed() =>
        Assert.Equal("CMK32GX5M2B6000C36", HardwareInventoryParsers.CleanText("  CMK32GX5M2B6000C36 "));

    [Fact]
    public void Manufacturer_id_string_is_decoded_and_the_raw_id_kept()
    {
        var (name, id) = HardwareInventoryParsers.ResolveManufacturer("859B", null);
        Assert.Equal("Crucial Technology", name);
        Assert.Equal("859B", id);
    }

    [Fact]
    public void Placeholder_manufacturer_falls_back_to_the_module_manufacturer_id_field()
    {
        var (name, id) = HardwareInventoryParsers.ResolveManufacturer("Unknown", "04CD");
        Assert.Equal("G Skill Intl", name);
        Assert.Equal("04CD", id);
    }

    [Fact]
    public void Unlisted_id_keeps_the_raw_id_and_no_name()
    {
        var (name, id) = HardwareInventoryParsers.ResolveManufacturer("1234", null);
        Assert.Null(name);
        Assert.Equal("1234", id);
    }

    [Fact]
    public void Manufacturer_name_passes_through()
    {
        var (name, id) = HardwareInventoryParsers.ResolveManufacturer("Kingston", null);
        Assert.Equal("Kingston", name);
        Assert.Null(id);
    }

    [Fact]
    public void Smbios_module_maps_generation_speeds_and_decoded_vendor()
    {
        var info = new MemoryModuleInfo("DIMM_A1", 17179869184, 6000, 5600, "04CD", " F5-6000J3038F16G ");
        var module = HardwareInventoryParsers.FromSmbios(info, 0x22);

        Assert.Equal("G Skill Intl", module.Manufacturer);
        Assert.Equal("04CD", module.ManufacturerId);
        Assert.Equal("F5-6000J3038F16G", module.PartNumber);
        Assert.Equal(17179869184, module.CapacityBytes);
        Assert.Equal(5600, module.ConfiguredSpeedMts);
        Assert.Equal(6000, module.RatedSpeedMts);
        Assert.Equal("DDR5", module.Generation);
    }

    [Fact]
    public void Smbios_module_with_nothing_known_leaves_fields_absent()
    {
        var module = HardwareInventoryParsers.FromSmbios(new MemoryModuleInfo("A", null, null, null, "Unknown", "Unknown"), 0x02);
        Assert.Null(module.Manufacturer);
        Assert.Null(module.PartNumber);
        Assert.Null(module.CapacityBytes);
        Assert.Null(module.Generation);
    }

    [Theory]
    [InlineData("Msft", "Virtual Disk", 10u)]
    [InlineData("VMware", "Virtual NVMe Disk", 17u)]
    [InlineData("QEMU", "QEMU HARDDISK", 11u)]
    [InlineData("Intel", "Raid 1 Volume", 8u)]
    [InlineData("", "Samsung SSD 990 PRO 2TB", 14u)]
    [InlineData("", "Storage Space", 16u)]
    public void Windows_virtual_and_pooled_disks_are_dropped(string vendor, string product, uint bus) =>
        Assert.Null(HardwareInventoryParsers.FromWindows(vendor, product, bus, "SSD", 1_000_000_000_000));

    [Fact]
    public void Windows_nvme_drive_reports_bus_media_and_capacity_without_the_transport_tag()
    {
        var drive = HardwareInventoryParsers.FromWindows("NVMe", "Samsung SSD 990 PRO 2TB", 17, "Unspecified", 2000398934016)!;
        Assert.Equal("Samsung SSD 990 PRO 2TB", drive.Model);
        Assert.Null(drive.Vendor);
        Assert.Equal("nvme", drive.Bus);
        Assert.Equal("ssd", drive.Media);
        Assert.Equal(2000398934016, drive.CapacityBytes);
    }

    [Fact]
    public void Windows_sata_hdd_keeps_a_separate_vendor()
    {
        var hdd = HardwareInventoryParsers.FromWindows("Seagate", "ST4000DM004-2CV104", 11, "HDD", 4000787030016)!;
        Assert.Equal("Seagate", hdd.Vendor);
        Assert.Equal("hdd", hdd.Media);
        Assert.Equal("sata", hdd.Bus);
    }

    [Fact]
    public void Windows_usb_attached_drive_is_not_reported() =>
        Assert.Null(HardwareInventoryParsers.FromWindows("Samsung", "PSSD T7", 7, "SSD", 1000204886016));

    [Fact]
    public void Linux_usb_attached_drive_is_not_reported() =>
        Assert.Null(HardwareInventoryParsers.FromLinux("sdb", "/sys/devices/pci0000:00/0000:00:14.0/usb2/2-1/host6/target6:0:0/6:0:0:0/block/sdb",
            "Samsung", "PSSD T7", 1_953_525_168, "0", "0"));

    [Fact]
    public void Over_long_strings_are_clamped_to_the_shared_cap()
    {
        var long66 = new string('x', 66);
        var drive = HardwareInventoryParsers.FromWindows("", long66, 17, "SSD", 1)!;
        Assert.Equal(HardwareInventoryParsers.MaxText, drive.Model!.Length);
        var module = HardwareInventoryParsers.FromSmbios(new MemoryModuleInfo("A", 1, null, null, long66, long66), null);
        Assert.Equal(HardwareInventoryParsers.MaxText, module.Manufacturer!.Length);
        Assert.Equal(HardwareInventoryParsers.MaxText, module.PartNumber!.Length);
    }

    [Fact]
    public void Every_jedec_table_name_fits_the_cap_once_resolved()
    {
        foreach (var bank in JedecVendorTable.Banks)
        {
            foreach (var name in bank)
            {
                Assert.True(HardwareInventoryParsers.Clamp(name).Length <= HardwareInventoryParsers.MaxText);
            }
        }
    }

    [Fact]
    public void Prefixed_hex_manufacturer_id_decodes()
    {
        var (name, id) = HardwareInventoryParsers.ResolveManufacturer("0x80CE", null);
        Assert.Equal("Samsung", name);
        Assert.Equal("80CE", id);
    }

    [Theory]
    [InlineData("0x802C000000000000")] // wider than a JEDEC id: no source for a layout, so dropped
    [InlineData("0x436F7273616972")] // hex-encoded text: no real sample on record, so dropped
    [InlineData("DEADBEEF")]
    [InlineData("0x00FF00FF")]
    public void Hex_manufacturer_strings_that_are_not_a_jedec_id_are_dropped(string raw)
    {
        var (name, id) = HardwareInventoryParsers.ResolveManufacturer(raw, null);
        Assert.Null(name);
        Assert.Null(id);
    }

    [Fact]
    public void Hex_manufacturer_string_still_falls_back_to_the_module_manufacturer_id()
    {
        var (name, id) = HardwareInventoryParsers.ResolveManufacturer("0x436F7273616972", "029E");
        Assert.Equal("Corsair", name);
        Assert.Equal("029E", id);
    }

    [Fact]
    public void Hex_literal_part_number_is_dropped_and_a_real_one_kept()
    {
        Assert.Null(HardwareInventoryParsers.CleanPartNumber("0x484D413831475536"));
        Assert.Null(HardwareInventoryParsers.CleanPartNumber("0xFFFFFFFF"));
        Assert.Equal("CMK32GX5M2B6000C36", HardwareInventoryParsers.CleanPartNumber("CMK32GX5M2B6000C36"));
    }

    [Theory]
    [InlineData("Lexar Co Limited", "Lexar")]
    [InlineData("Team Group Inc.", "Team Group")]
    [InlineData("Patriot Memory (PDP Systems)", "Patriot")]
    [InlineData("Netac Technology Co Ltd", "Netac")]
    [InlineData("Kingston", "Kingston")]
    [InlineData("G Skill Intl", "G Skill Intl")]
    [InlineData("Mavrix Technology, Inc.", "Mavrix")]
    public void Brand_label_drops_corporate_suffixes_the_catalog_matcher_would_read_as_part_number(string name, string expected) =>
        Assert.Equal(expected, HardwareInventoryParsers.BrandLabel(name));

    [Fact]
    public void Memory_string_lists_the_most_common_kit_first()
    {
        var brands = new[] { "Kingston KF560C36-16", "Corsair CMH32GX5M2E6000C36", "Corsair CMH32GX5M2E6000C36", "Kingston KF560C36-16", "Corsair CMH32GX5M2E6000C36" };
        Assert.Equal("Corsair CMH32GX5M2E6000C36 + Kingston KF560C36-16", SystemSpecsCollector.PrimaryFirst(brands));
    }

    [Fact]
    public void Linux_nvme_drive_resolves_bus_and_size()
    {
        var drive = HardwareInventoryParsers.FromLinux("nvme0n1", "/sys/devices/pci0000:00/0000:00:01.1/0000:01:00.0/nvme/nvme0/nvme0n1",
            null, "Samsung SSD 990 PRO 2TB", 3907029168, "0", "0")!;
        Assert.Equal("nvme", drive.Bus);
        Assert.Equal("ssd", drive.Media);
        Assert.Equal(3907029168L * 512, drive.CapacityBytes);
        Assert.Equal("Samsung SSD 990 PRO 2TB", drive.Model);
    }

    [Fact]
    public void Linux_sata_hdd_drops_the_ata_vendor_tag()
    {
        var drive = HardwareInventoryParsers.FromLinux("sda", "/sys/devices/pci0000:00/0000:00:17.0/ata1/host0/target0:0:0/0:0:0:0/block/sda",
            "ATA     ", "WDC WD40EZRZ-00G", 7814037168, "1", "0")!;
        Assert.Null(drive.Vendor);
        Assert.Equal("sata", drive.Bus);
        Assert.Equal("hdd", drive.Media);
    }

    [Theory]
    [InlineData("loop0", "/sys/devices/virtual/block/loop0", "", "", "0")]
    [InlineData("dm-0", "/sys/devices/virtual/block/dm-0", "", "", "0")]
    [InlineData("zram0", "/sys/devices/virtual/block/zram0", "", "", "0")]
    [InlineData("sdb", "/sys/devices/pci0000:00/usb1/1-1/block/sdb", "SanDisk", "Cruzer Blade", "1")] // removable flash drive
    [InlineData("vda", "/sys/devices/pci0000:00/virtio2/block/vda", "", "", "0")]
    [InlineData("sda", "/sys/devices/pci0000:00/ata1/block/sda", "QEMU", "QEMU HARDDISK", "0")]
    public void Linux_virtual_removable_and_non_drives_are_dropped(string name, string path, string vendor, string model, string removable) =>
        Assert.Null(HardwareInventoryParsers.FromLinux(name, path, vendor, model, 1_000_000, "0", removable));

    private const string MacNvme = """
NVMExpress:

    Apple SSD Controller:

        APPLE SSD AP0512Z:

          Capacity: 500.28 GB (500,277,792,768 bytes)
          TRIM Support: Yes
          Model: APPLE SSD AP0512Z
          Revision: 561.100.
          Serial Number: SERIAL-MUST-NOT-LEAK
          Detachable Drive: No
          BSD Name: disk0
          Removable Media: No
          Volumes:
            Macintosh HD:
              Capacity: 494.38 GB (494,384,795,648 bytes)
              BSD Name: disk0s2
""";

    [Fact]
    public void Mac_nvme_drive_ignores_serial_volume_label_and_volume_capacity()
    {
        var drive = HardwareInventoryParsers.ParseMacDrives(MacNvme, "nvme").Single();
        Assert.Equal("APPLE SSD AP0512Z", drive.Model);
        Assert.Equal(500277792768, drive.CapacityBytes);
        Assert.Equal("nvme", drive.Bus);
        Assert.Equal("ssd", drive.Media);
        Assert.DoesNotContain("SERIAL", System.Text.Json.JsonSerializer.Serialize(drive.Model));
    }

    [Fact]
    public void Mac_sata_section_reads_medium_type_and_skips_detachable_drives()
    {
        const string sata = """
Serial-ATA:

    Intel 6 Series Chipset:

        Samsung SSD 860 EVO 500GB:

          Capacity: 500.11 GB (500,107,862,016 bytes)
          Model: Samsung SSD 860 EVO 500GB
          Medium Type: Solid State
          Detachable Drive: No
          Removable Media: No
        Backup Disk:

          Capacity: 1 TB (1,000,204,886,016 bytes)
          Model: Portable HDD
          Medium Type: Rotational
          Detachable Drive: Yes
""";
        var drive = HardwareInventoryParsers.ParseMacDrives(sata, "sata").Single();
        Assert.Equal("Samsung SSD 860 EVO 500GB", drive.Model);
        Assert.Equal("ssd", drive.Media);
        Assert.Equal("sata", drive.Bus);
    }

    [Fact]
    public void Mac_apple_silicon_unified_memory_yields_no_modules()
    {
        const string profile = "Memory:\n\n      Memory: 32 GB\n      Type: LPDDR5\n      Manufacturer: Hynix\n";
        Assert.Empty(HardwareInventoryParsers.ParseMacMemory(profile));
    }

    [Fact]
    public void Mac_intel_dimm_blocks_become_modules_and_empty_slots_are_skipped()
    {
        const string profile = """
Memory:

    Memory Slots:

      ECC: Disabled
      Upgradeable Memory: Yes

        BANK 0/DIMM0:

          Size: 8 GB
          Type: DDR4
          Speed: 2667 MHz
          Status: OK
          Manufacturer: 0x802C
          Part Number: 8ATF1G64HZ-2G6E1
          Serial Number: SERIAL-MUST-NOT-LEAK

        BANK 1/DIMM0:

          Size: Empty
          Type: Empty
""";
        var module = HardwareInventoryParsers.ParseMacMemory(profile).Single();
        Assert.Equal("Micron Technology", module.Manufacturer);
        Assert.Equal("802C", module.ManufacturerId);
        Assert.Equal("8ATF1G64HZ-2G6E1", module.PartNumber);
        Assert.Equal(8L * 1024 * 1024 * 1024, module.CapacityBytes);
        Assert.Equal("DDR4", module.Generation);
        Assert.Equal(2667, module.ConfiguredSpeedMts);
    }
}
