using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class GpuBoardPartnersTests
{
    // Subsystem ids below are entries of the PCI ID Repository (pci.ids), each next to the card it lists.
    [Theory]
    [InlineData(0x1043, 0x0506, "ASUS")]       // TUF Gaming Radeon RX 7900 XTX OC
    [InlineData(0x1462, 0x3881, "MSI")]        // MSI RTX 3090 VENTUS 3X OC
    [InlineData(0x1462, 0x3892, "MSI")]        // RTX 3080 10GB GAMING X TRIO
    [InlineData(0x3842, 0x3973, "EVGA")]       // GeForce RTX 3090 XC3
    [InlineData(0x1da2, 0x471e, "Sapphire")]   // PULSE RX 7900 XTX
    [InlineData(0x148c, 0x2425, "PowerColor")] // HELLHOUND RX 7900 GRE
    [InlineData(0x1eae, 0x7901, "XFX")]        // RX-79XMERCB9 SPEEDSTER MERC 310 RX 7900 XTX
    [InlineData(0x1849, 0x5304, "ASRock")]     // Radeon RX 7900 XTX
    [InlineData(0x10de, 0x147d, "NVIDIA")]     // GeForce RTX 3090 Founders Edition
    [InlineData(0x1002, 0x0e3b, "AMD")]        // RX 7900 XTX / RX 7900 GRE reference board
    public void Names_the_partner_for_a_listed_subsystem_vendor(int vendor, int device, string expected)
    {
        Assert.NotEqual(0, device);
        Assert.Equal(expected, GpuBoardPartners.NameOf(vendor));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xFFFF)]
    [InlineData(0x1414)]
    [InlineData(0x8086)]
    public void Leaves_an_unlisted_or_unset_vendor_without_a_name(int vendor)
    {
        Assert.Null(GpuBoardPartners.NameOf(vendor));
    }

    [Fact]
    public void Every_name_in_the_requested_list_has_an_id()
    {
        var wanted = new[]
        {
            "ASUS", "MSI", "Gigabyte", "Zotac", "PNY", "EVGA", "Sapphire", "PowerColor", "XFX", "ASRock", "Palit", "Gainward",
            "Colorful", "Galax", "Yeston", "Acer", "Biostar", "Manli", "Leadtek", "Dell", "HP", "Lenovo", "NVIDIA", "AMD",
        };
        var named = Enumerable.Range(0, 0x10000).Select(GpuBoardPartners.NameOf).Where(n => n is not null).ToHashSet();
        Assert.All(wanted, w => Assert.Contains(w, named));
    }

    [Fact]
    public void Resolve_attaches_the_maker_and_ids_to_the_matching_model()
    {
        var cards = GpuBoardPartners.Resolve(
            new[] { "AMD Radeon(TM) Graphics", "NVIDIA GeForce RTX 3090" },
            new[] { new GpuBoard("NVIDIA GeForce RTX 3090", 0x1462, 0x3881) });

        Assert.Equal(new GpuCard("AMD Radeon(TM) Graphics"), cards[0]);
        Assert.Equal(new GpuCard("NVIDIA GeForce RTX 3090", "MSI", 0x1462, 0x3881), cards[1]);
        Assert.Equal("AMD Radeon(TM) Graphics + MSI NVIDIA GeForce RTX 3090", string.Join(" + ", cards.Select(c => c.Display)));
    }

    [Fact]
    public void Resolve_gives_two_identical_models_their_own_boards_in_order()
    {
        var cards = GpuBoardPartners.Resolve(
            new[] { "NVIDIA GeForce RTX 3090", "NVIDIA GeForce RTX 3090" },
            new[] { new GpuBoard("NVIDIA GeForce RTX 3090", 0x1462, 0x3881), new GpuBoard("NVIDIA GeForce RTX 3090", 0x3842, 0x3973) });

        Assert.Equal(new[] { "MSI", "EVGA" }, cards.Select(c => c.Maker));
    }

    [Fact]
    public void Resolve_ignores_a_board_with_an_implausible_vendor_and_keeps_the_chip_alone()
    {
        var cards = GpuBoardPartners.Resolve(
            new[] { "NVIDIA GeForce RTX 3090" },
            new[] { new GpuBoard("NVIDIA GeForce RTX 3090", 0, 0), new GpuBoard("NVIDIA GeForce RTX 3090", 0xFFFF, 0xFFFF) });

        Assert.Equal(new GpuCard("NVIDIA GeForce RTX 3090"), cards.Single());
    }

    [Fact]
    public void A_listed_vendor_with_no_name_in_the_table_still_reports_its_ids()
    {
        var cards = GpuBoardPartners.Resolve(new[] { "NVIDIA GeForce RTX 3090" }, new[] { new GpuBoard("NVIDIA GeForce RTX 3090", 0x1234, 0x5678) });

        var card = cards.Single();
        Assert.Null(card.Maker);
        Assert.Equal((0x1234, 0x5678), (card.SubsystemVendorId, card.SubsystemDeviceId));
        Assert.Equal("NVIDIA GeForce RTX 3090", card.Display);
    }

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX", "AMD", "AMD Radeon RX 7900 XTX")]
    [InlineData("NVIDIA GeForce RTX 3090", "EVGA", "EVGA NVIDIA GeForce RTX 3090")]
    [InlineData("AMD Radeon RX 7900 XTX", null, "AMD Radeon RX 7900 XTX")]
    public void Display_puts_the_maker_in_front_unless_the_chip_name_already_starts_with_it(string chip, string? maker, string expected)
    {
        Assert.Equal(expected, new GpuCard(chip, maker).Display);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5080", "NVIDIA Founders Edition GeForce RTX 5080")]
    [InlineData("GeForce RTX 5080", "NVIDIA Founders Edition GeForce RTX 5080")]
    public void An_nvidia_reference_board_reads_founders_edition_without_a_doubled_vendor(string chip, string expected)
    {
        Assert.Equal(expected, new GpuCard(chip, "NVIDIA", 0x10DE, 0x2095).Display);
    }

    [Theory]
    [InlineData("NVIDIA RTX A4000")]
    [InlineData("NVIDIA Quadro RTX 5000")]
    [InlineData("NVIDIA Tesla T4")]
    [InlineData("NVIDIA L4")]
    [InlineData("NVIDIA Corporation AD103 [GeForce RTX 4080]")]
    public void An_nvidia_reference_board_without_a_clean_geforce_name_keeps_the_chip(string chip)
    {
        Assert.Equal(chip, new GpuCard(chip, "NVIDIA", 0x10DE, 0x1234).Display);
    }

    [Fact]
    public void The_profile_gpu_property_stays_chip_only()
    {
        var specs = new SystemSpecsResponse { GraphicsCard = "MSI NVIDIA GeForce RTX 3090" };
        specs.GpuCards.Add(new GpuCard("NVIDIA GeForce RTX 3090", "MSI", 0x1462, 0x3881));
        Assert.Equal("NVIDIA GeForce RTX 3090", Nexus.Service.Telemetry.SystemProfileService.ChipOnlyGraphics(specs));
        Assert.Equal("legacy", Nexus.Service.Telemetry.SystemProfileService.ChipOnlyGraphics(new SystemSpecsResponse { GraphicsCard = "legacy" }));
    }

    [Fact]
    public void An_amd_reference_board_keeps_the_bare_chip()
    {
        Assert.Equal("AMD Radeon RX 7900 XTX", new GpuCard("AMD Radeon RX 7900 XTX", "AMD", 0x1002, 0x0e3b).Display);
        Assert.Equal("Radeon RX 7900 XTX", new GpuCard("Radeon RX 7900 XTX", "AMD", 0x1002, 0x0e3b).Display);
    }

    [Theory]
    [InlineData(0x38811462u, 0x1462, 0x3881)]
    [InlineData(0x05061043u, 0x1043, 0x0506)]
    [InlineData(0u, 0, 0)]
    [InlineData(0xFFFFFFFFu, 0, 0)]
    [InlineData(0x38810000u, 0, 0)]
    public void Splits_the_dxgi_subsystem_dword_into_vendor_low_and_device_high(uint dword, int vendor, int device)
    {
        Assert.Equal((vendor, device), GpuAdapterLuids.SplitSubsystemId(dword));
    }

    [Fact]
    public void A_matched_adapter_hands_its_subsystem_ids_to_the_gpu()
    {
        var gpu = new GpuReadout { Id = "/gpu-nvidia/0", Name = "NVIDIA GeForce RTX 3090", Vendor = "nvidia" };
        var adapters = new List<GpuAdapterLuids.Adapter>
        {
            new("0:1", "NVIDIA GeForce RTX 3090", 0x10DE, 24576, DeviceId: 0x2204, SubSysId: 0x38811462),
        };

        GpuAdapterLuids.MatchAdapterLuids(new List<GpuReadout> { gpu }, adapters,
            new Dictionary<string, string> { [gpu.Id] = gpu.Name }, new HashSet<string>());

        Assert.Equal((0x1462, 0x3881), (gpu.SubsystemVendorId, gpu.SubsystemDeviceId));
    }

    // Read on a lab PC: RTX 5080 PCI\VEN_10DE&DEV_2C02&SUBSYS_209510DE (reference board) and the AMD iGPU PCI\VEN_1002&DEV_13C0&SUBSYS_D0001458 (motherboard maker).
    [Fact]
    public void A_reference_board_reports_its_chip_vendor_once_and_an_integrated_gpu_gets_no_maker()
    {
        Assert.Equal((0x10DE, 0x2095), GpuAdapterLuids.SplitSubsystemId(0x209510DE));
        Assert.Equal((0x1458, 0xD000), GpuAdapterLuids.SplitSubsystemId(0xD0001458));
        var gpus = new[]
        {
            new GpuReadout { Name = "AMD Radeon(TM) Graphics", Integrated = true, SubsystemVendorId = 0x1458, SubsystemDeviceId = 0xD000 },
            new GpuReadout { Name = "NVIDIA GeForce RTX 5080", SubsystemVendorId = 0x10DE, SubsystemDeviceId = 0x2095 },
        };

        var cards = GpuBoardPartners.Resolve(gpus.Select(g => g.Name).ToList(), GpuBoardPartners.BoardsOf(gpus));

        Assert.Equal("AMD Radeon(TM) Graphics + NVIDIA Founders Edition GeForce RTX 5080", string.Join(" + ", cards.Select(c => c.Display)));
        Assert.Null(cards[0].Maker);
        Assert.Equal("NVIDIA", cards[1].Maker);
    }

    [Fact]
    public void Boards_skip_integrated_adapters_and_unread_subsystems()
    {
        var gpus = new[]
        {
            new GpuReadout { Name = "AMD Radeon(TM) Graphics", Integrated = true, SubsystemVendorId = 0x1043, SubsystemDeviceId = 0x8877 },
            new GpuReadout { Name = "NVIDIA GeForce RTX 3090", SubsystemVendorId = 0x1462, SubsystemDeviceId = 0x3881 },
            new GpuReadout { Name = "NVIDIA GeForce GT 1030" },
        };

        var boards = GpuBoardPartners.BoardsOf(gpus);

        Assert.Equal(new GpuBoard("NVIDIA GeForce RTX 3090", 0x1462, 0x3881), boards.Single());
    }
}

public sealed class LinuxPciSubsystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pci-" + Guid.NewGuid().ToString("N"));
    private readonly string _dmi = Path.Combine(Path.GetTempPath(), "dmi-" + Guid.NewGuid().ToString("N"));

    public LinuxPciSubsystemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Directory.Delete(_root, true);
        if (Directory.Exists(_dmi))
            Directory.Delete(_dmi, true);
    }

    private void Chassis(string type)
    {
        Directory.CreateDirectory(_dmi);
        File.WriteAllText(Path.Combine(_dmi, "chassis_type"), type + "\n");
    }

    private void Device(string busId, string vendor, string device, string? chipVendor = null, long? vram = null, string? deviceId = null)
    {
        var dir = Path.Combine(_root, busId);
        Directory.CreateDirectory(dir);
        if (deviceId is not null)
            File.WriteAllText(Path.Combine(dir, "device"), deviceId + "\n");
        if (chipVendor is not null)
            File.WriteAllText(Path.Combine(dir, "vendor"), chipVendor + "\n");
        if (vram is not null)
            File.WriteAllText(Path.Combine(dir, "mem_info_vram_total"), vram + "\n");
        File.WriteAllText(Path.Combine(dir, "subsystem_vendor"), vendor + "\n");
        File.WriteAllText(Path.Combine(dir, "subsystem_device"), device + "\n");
    }

    [Theory]
    [InlineData("00000000:01:00.0", "0000:01:00.0")]
    [InlineData("0000:01:00.0", "0000:01:00.0")]
    [InlineData("01:00.0", "0000:01:00.0")]
    [InlineData("00000000:0A:00.0", "0000:0a:00.0")]
    [InlineData("0001:03:00.0", "0001:03:00.0")]
    [InlineData("", null)]
    [InlineData("N/A", null)]
    public void Normalizes_nvidia_smi_and_lspci_bus_ids_to_the_sysfs_form(string input, string? expected)
    {
        Assert.Equal(expected, LinuxPciSubsystem.NormalizeBusId(input));
    }

    [NonWindowsFact]
    public void Reads_the_subsystem_ids_from_sysfs_for_either_bus_id_spelling()
    {
        Device("0000:01:00.0", "0x1462", "0x3881");

        Assert.Equal((0x1462, 0x3881), LinuxPciSubsystem.Read(_root, "00000000:01:00.0"));
        Assert.Equal((0x1462, 0x3881), LinuxPciSubsystem.Read(_root, "01:00.0"));
    }

    [NonWindowsFact]
    public void A_missing_device_or_garbled_file_reads_as_nothing()
    {
        Device("0000:02:00.0", "garbage", "0x0001");

        Assert.Null(LinuxPciSubsystem.Read(_root, "0000:03:00.0"));
        Assert.Null(LinuxPciSubsystem.Read(_root, "0000:02:00.0"));
        Assert.Null(LinuxPciSubsystem.Read(_root, null));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3090, 00000000:01:00.0", "NVIDIA GeForce RTX 3090", "00000000:01:00.0")]
    [InlineData("NVIDIA GeForce RTX 3090", "NVIDIA GeForce RTX 3090", null)]
    [InlineData("NVIDIA GeForce RTX 3090, [N/A]", "NVIDIA GeForce RTX 3090, [N/A]", null)]
    public void Splits_a_name_and_bus_id_row(string row, string name, string? busId)
    {
        Assert.Equal((name, busId), LinuxPciSubsystem.ParseSmiRow(row));
    }

    private void Smbus(string vendor)
    {
        var dir = Path.Combine(_root, "0000:00:14.0");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "class"), "0x0c0500\n");
        File.WriteAllText(Path.Combine(dir, "subsystem_vendor"), vendor + "\n");
    }

    private void HostBridge(string vendor) => Device("0000:00:00.0", vendor, "0x0000");

    // The AM5 desktop layout measured on a lab PC: host bridge subsystem names the CPU vendor, SMBus names the board maker.
    private void Am5Desktop()
    {
        HostBridge("0x1022");
        Smbus("0x1458");
    }

    // lspci -mm lines in the format the provider parses.
    private const string LspciMm =
        "6e:00.0 \"VGA compatible controller\" \"Advanced Micro Devices, Inc. [AMD/ATI]\" \"Raphael\" -rc1 \"Gigabyte Technology Co., Ltd.\" \"Device d000\"\n" +
        "03:00.0 \"VGA compatible controller\" \"Advanced Micro Devices, Inc. [AMD/ATI]\" \"Navi 31 [Radeon RX 7900 XT/7900 XTX/7900 GRE/7900M]\" -rc8 \"Sapphire Technology Limited\" \"Device 471e\"\n" +
        "00:1f.3 \"Audio device\" \"Intel Corporation\" \"Cannon Lake PCH cAVS\"\n";

    private bool Discrete(string busId, string name = "GPU") => LinuxPciSubsystem.IsDiscrete(_root, busId, name, _dmi);

    private void Amd(string busId, string subsystemVendor, string deviceId, long vram) =>
        Device(busId, subsystemVendor, "0x1234", chipVendor: "0x1002", vram: vram, deviceId: deviceId);

    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public void Parses_the_vga_and_3d_controllers_from_lspci()
    {
        var gpus = LinuxPciSubsystem.ParseLspciGpus(LspciMm);

        Assert.Equal(new[] { "6e:00.0", "03:00.0" }, gpus.Select(g => g.Slot));
        Assert.Contains("Raphael", gpus[0].Name);
    }

    [NonWindowsFact]
    public void On_the_am5_desktop_the_igpu_and_the_boards_own_nvidia_card_get_no_maker_and_a_partner_radeon_does()
    {
        Am5Desktop();
        Amd("0000:6e:00.0", "0x1458", "0x13c0", Gib / 2);
        Amd("0000:03:00.0", "0x1da2", "0x744c", 24 * Gib);
        Device("0000:01:00.0", "0x1458", "0x40bf", chipVendor: "0x10de");

        Assert.False(Discrete("6e:00.0"));
        Assert.True(Discrete("03:00.0"));
        Assert.Equal("Sapphire", GpuBoardPartners.NameOf(0x1da2));
        Assert.False(Discrete("01:00.0", "NVIDIA GeForce RTX 4090"));
    }

    [NonWindowsFact]
    public void On_an_all_one_vendor_intel_board_the_boards_own_card_gets_none_and_another_partners_card_gets_its_maker()
    {
        HostBridge("0x1043");
        Smbus("0x1043");
        Device("0000:01:00.0", "0x1043", "0x889c", chipVendor: "0x10de");
        Device("0000:02:00.0", "0x1462", "0x5107", chipVendor: "0x10de");

        Assert.False(Discrete("01:00.0", "NVIDIA GeForce RTX 4070"));
        Assert.True(Discrete("02:00.0", "NVIDIA GeForce RTX 4070"));
        Assert.Equal("MSI", GpuBoardPartners.NameOf(0x1462));
    }

    [NonWindowsTheory]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU")]
    [InlineData("NVIDIA GeForce RTX 3080 Max-Q")]
    [InlineData("NVIDIA GeForce GTX 1060 Mobile")]
    public void A_laptop_nvidia_gpu_gets_no_maker_even_when_its_subsystem_differs_from_the_board(string name)
    {
        HostBridge("0x1022");
        Smbus("0x1043");
        Device("0000:01:00.0", "0x1043", "0x1234", chipVendor: "0x10de");
        Device("0000:02:00.0", "0x17aa", "0x1234", chipVendor: "0x10de");

        Assert.False(Discrete("01:00.0", name));
        Assert.False(Discrete("02:00.0", name));
    }

    [NonWindowsTheory]
    [InlineData("0x1900", 2)]  // Hawk Point laptop with a small UMA carve-out
    [InlineData("0x13fe", 1)]  // BC-250
    [InlineData("0x164e", 8)]  // desktop Raphael
    [InlineData("0x15bf", 4)]  // handheld Phoenix1
    [InlineData("0x163f", 1)]  // Steam Deck VanGogh
    [InlineData("0x1638", 1)]  // Cezanne
    [InlineData("0x15dd", 2)]  // Raven Ridge
    [InlineData("0x164d", 2)]  // Rembrandt-R
    public void An_amd_apu_that_is_not_on_the_allow_list_never_gets_a_maker_whatever_its_subsystem(string device, long gib)
    {
        Am5Desktop();
        Amd("0000:65:00.0", "0x17aa", device, gib * Gib);

        Assert.False(Discrete("65:00.0"));
    }

    [NonWindowsFact]
    public void A_listed_radeon_needs_a_foreign_subsystem_and_readable_memory()
    {
        Am5Desktop();
        Amd("0000:03:00.0", "0x1458", "0x744c", 24 * Gib);
        Device("0000:04:00.0", "0x1da2", "0x1234", chipVendor: "0x1002", deviceId: "0x744c");

        Assert.False(Discrete("03:00.0"));
        Assert.False(Discrete("04:00.0"));
    }

    [NonWindowsTheory]
    [InlineData("0000:00:02.0", "0x7d55", "Intel Corporation Meteor Lake-P [Intel Arc Graphics]")]
    [InlineData("0000:00:02.0", "0x64a0", "Intel Corporation Core Ultra 200V Series Processors Arc Graphics 130V/140V GPU")]
    [InlineData("0000:00:02.0", "0x56a0", "Intel Corporation DG2 [Arc A770]")]
    [InlineData("0000:03:00.0", "0x7d55", "Intel Corporation Meteor Lake-P [Intel Arc Graphics]")]
    public void An_intel_igpu_never_gets_a_maker(string busId, string device, string name)
    {
        Am5Desktop();
        Device(busId, "0x1da2", "0x1234", chipVendor: "0x8086", deviceId: device);

        Assert.False(Discrete(busId[5..], name));
    }

    [NonWindowsFact]
    public void An_intel_arc_card_off_the_igpu_slot_with_a_dgpu_codename_gets_a_maker()
    {
        Am5Desktop();
        Device("0000:03:00.0", "0x1da2", "0x1234", chipVendor: "0x8086", deviceId: "0xe20b");

        Assert.True(Discrete("03:00.0", "Intel Corporation Battlemage G21 [Arc B580]"));
        Assert.False(Discrete("03:00.0", "Intel Corporation Some New Part"));
    }

    [NonWindowsFact]
    public void With_no_usable_board_vendor_there_is_no_maker()
    {
        Device("0000:01:00.0", "0x1462", "0x3881", chipVendor: "0x10de");
        Assert.False(Discrete("01:00.0", "NVIDIA GeForce RTX 3090"));

        HostBridge("0x1022");
        Assert.False(Discrete("01:00.0", "NVIDIA GeForce RTX 3090"));
    }

    [NonWindowsFact]
    public void A_host_bridge_naming_a_board_maker_stands_in_when_there_is_no_smbus_controller()
    {
        HostBridge("0x1043");
        Device("0000:01:00.0", "0x1043", "0x1234", chipVendor: "0x10de");
        Device("0000:02:00.0", "0x1462", "0x3881", chipVendor: "0x10de");

        Assert.False(Discrete("01:00.0", "NVIDIA GeForce RTX 3090"));
        Assert.True(Discrete("02:00.0", "NVIDIA GeForce RTX 3090"));
    }

    [NonWindowsFact]
    public void The_smbus_controller_wins_over_a_host_bridge()
    {
        HostBridge("0x1043");
        Smbus("0x1458");
        Device("0000:01:00.0", "0x1043", "0x1234", chipVendor: "0x10de");

        Assert.True(Discrete("01:00.0", "NVIDIA GeForce RTX 3090"));
    }

    [NonWindowsTheory]
    [InlineData("8", true)]
    [InlineData("9", true)]
    [InlineData("10", true)]
    [InlineData("14", true)]
    [InlineData("31", true)]
    [InlineData("32", true)]
    [InlineData("3", false)]
    [InlineData("7", false)]
    [InlineData("13", true)]
    [InlineData("35", true)]
    [InlineData("16", false)]
    public void A_portable_chassis_never_gets_a_maker_and_a_desktop_one_still_can(string chassis, bool portable)
    {
        Am5Desktop();
        Chassis(chassis);
        Device("0000:01:00.0", "0x1462", "0x3881", chipVendor: "0x10de");

        Assert.Equal(!portable, Discrete("01:00.0", "NVIDIA GeForce RTX 3090"));
    }

    [NonWindowsFact]
    public void A_laptop_with_a_modular_radeon_and_a_pascal_or_turing_laptop_gpu_named_without_a_laptop_marker_gets_none()
    {
        HostBridge("0x1022");
        Smbus("0x17aa");
        Chassis("10");
        Amd("0000:03:00.0", "0x1da2", "0x7480", 8 * Gib);
        Device("0000:01:00.0", "0x17aa", "0x3a0e", chipVendor: "0x10de");
        Device("0000:02:00.0", "0x1462", "0x3a0e", chipVendor: "0x10de");

        Assert.False(Discrete("03:00.0", "AMD Radeon RX 7700S"));
        Assert.False(Discrete("01:00.0", "NVIDIA GeForce RTX 2060"));
        Assert.False(Discrete("02:00.0", "NVIDIA GeForce RTX 2060"));

        Chassis("3");
        Assert.True(Discrete("03:00.0", "AMD Radeon RX 7700S"));
        Assert.True(Discrete("02:00.0", "NVIDIA GeForce RTX 2060"));
    }

    [NonWindowsFact]
    public void An_unreadable_or_garbled_chassis_type_falls_back_to_the_other_gates()
    {
        Am5Desktop();
        Device("0000:01:00.0", "0x1462", "0x3881", chipVendor: "0x10de");
        Assert.True(Discrete("01:00.0", "NVIDIA GeForce RTX 3090"));

        Chassis("unknown");
        Assert.True(Discrete("01:00.0", "NVIDIA GeForce RTX 3090"));
    }

    [NonWindowsFact]
    public void A_silicon_smbus_controller_is_skipped_for_a_later_board_vendor_one()
    {
        var first = Path.Combine(_root, "0000:00:14.0");
        Directory.CreateDirectory(first);
        File.WriteAllText(Path.Combine(first, "class"), "0x0c0500\n");
        File.WriteAllText(Path.Combine(first, "subsystem_vendor"), "0x1022\n");
        var second = Path.Combine(_root, "0000:00:14.1");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "class"), "0x0c0500\n");
        File.WriteAllText(Path.Combine(second, "subsystem_vendor"), "0x1458\n");
        Device("0000:01:00.0", "0x1458", "0x1234", chipVendor: "0x10de");
        Device("0000:02:00.0", "0x1462", "0x3881", chipVendor: "0x10de");

        Assert.False(Discrete("01:00.0", "NVIDIA GeForce RTX 3090"));
        Assert.True(Discrete("02:00.0", "NVIDIA GeForce RTX 3090"));
    }

    [NonWindowsFact]
    public void Only_silicon_smbus_controllers_fall_back_to_a_board_maker_host_bridge()
    {
        var dir = Path.Combine(_root, "0000:00:14.0");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "class"), "0x0c0500\n");
        File.WriteAllText(Path.Combine(dir, "subsystem_vendor"), "0x1022\n");
        HostBridge("0x1043");
        Device("0000:01:00.0", "0x1043", "0x1234", chipVendor: "0x10de");
        Device("0000:02:00.0", "0x1462", "0x3881", chipVendor: "0x10de");

        Assert.False(Discrete("01:00.0", "NVIDIA GeForce RTX 3090"));
        Assert.True(Discrete("02:00.0", "NVIDIA GeForce RTX 3090"));
    }
}

public class SmbiosChassisTypeTests
{
    // RawSMBIOSData framing (eight header bytes, length at offset 4), a BIOS structure, then a System Enclosure structure.
    private static byte[] Table(byte chassisByte, bool withEnclosure = true)
    {
        var body = new List<byte>();
        body.AddRange(new byte[] { 0, 0x18, 0, 0, 1, 2, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        body.AddRange(new byte[] { (byte)'a', 0, (byte)'b', 0, 0 });
        if (withEnclosure)
        {
            body.AddRange(new byte[] { 3, 0x16, 4, 0, 1, chassisByte, 2, 3, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            body.AddRange(new byte[] { (byte)'m', 0, (byte)'v', 0, (byte)'s', 0, 0 });
        }
        body.AddRange(new byte[] { 127, 4, 0, 0, 0, 0 });
        var table = new byte[8 + body.Count];
        BitConverter.TryWriteBytes(table.AsSpan(4, 4), (uint)body.Count);
        body.CopyTo(table, 8);
        return table;
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(0x8A, 10)]
    [InlineData(3, 3)]
    public void Reads_the_chassis_type_and_drops_the_lock_bit(int raw, int expected)
    {
        Assert.Equal(expected, Nexus.Service.Diagnostics.Memory.SmbiosParser.ParseChassisType(Table((byte)raw)));
    }

    [Fact]
    public void A_table_without_an_enclosure_or_a_missing_table_reads_as_nothing()
    {
        Assert.Null(Nexus.Service.Diagnostics.Memory.SmbiosParser.ParseChassisType(Table(0, withEnclosure: false)));
        Assert.Null(Nexus.Service.Diagnostics.Memory.SmbiosParser.ParseChassisType(null));
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(10, true)]
    [InlineData(14, true)]
    [InlineData(31, true)]
    [InlineData(32, true)]
    [InlineData(11, true)]
    [InlineData(12, true)]
    [InlineData(13, true)]
    [InlineData(30, true)]
    [InlineData(35, true)]
    [InlineData(36, true)]
    [InlineData(3, false)]
    [InlineData(15, false)]
    [InlineData(16, false)]
    [InlineData(null, false)]
    public void Portable_covers_laptop_and_integrated_chassis(int? type, bool portable)
    {
        Assert.Equal(portable, PortableChassis.IsPortable(type));
    }
}
