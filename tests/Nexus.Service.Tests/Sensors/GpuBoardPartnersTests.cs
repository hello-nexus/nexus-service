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

    public LinuxPciSubsystemTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private void Device(string busId, string vendor, string device, string? chipVendor = null, long? vram = null)
    {
        var dir = Path.Combine(_root, busId);
        Directory.CreateDirectory(dir);
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

    [Fact]
    public void Reads_the_subsystem_ids_from_sysfs_for_either_bus_id_spelling()
    {
        Device("0000:01:00.0", "0x1462", "0x3881");

        Assert.Equal((0x1462, 0x3881), LinuxPciSubsystem.Read(_root, "00000000:01:00.0"));
        Assert.Equal((0x1462, 0x3881), LinuxPciSubsystem.Read(_root, "01:00.0"));
    }

    [Fact]
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

    // lspci -mm lines in the format the provider parses; the sysfs ids are the pci.ids entries for each device and a board with the Gigabyte subsystem.
    private const string LspciMm =
        "6e:00.0 \"VGA compatible controller\" \"Advanced Micro Devices, Inc. [AMD/ATI]\" \"Raphael\" -rc1 \"Gigabyte Technology Co., Ltd.\" \"Device d000\"\n" +
        "65:00.0 \"VGA compatible controller\" \"Advanced Micro Devices, Inc. [AMD/ATI]\" \"Phoenix1\" -rc1 \"Gigabyte Technology Co., Ltd.\" \"Device d000\"\n" +
        "05:00.0 \"VGA compatible controller\" \"Advanced Micro Devices, Inc. [AMD/ATI]\" \"Cezanne [Radeon Vega Series / Radeon Vega Mobile Series]\" -rc8 \"ASUSTeK Computer Inc.\" \"Device 16c2\"\n" +
        "03:00.0 \"VGA compatible controller\" \"Advanced Micro Devices, Inc. [AMD/ATI]\" \"Navi 31 [Radeon RX 7900 XT/7900 XTX/7900 GRE/7900M]\" -rc8 \"Sapphire Technology Limited\" \"Device 471e\"\n" +
        "00:1f.3 \"Audio device\" \"Intel Corporation\" \"Cannon Lake PCH cAVS\"\n";

    [Fact]
    public void An_amd_igpu_without_graphics_in_its_name_is_not_discrete_and_a_discrete_radeon_is()
    {
        Device("0000:6e:00.0", "0x1458", "0xd000", chipVendor: "0x1002", vram: 536870912);
        Device("0000:65:00.0", "0x1458", "0xd000", chipVendor: "0x1002", vram: 536870912);
        Device("0000:05:00.0", "0x1043", "0x16c2", chipVendor: "0x1002", vram: 536870912);
        Device("0000:03:00.0", "0x1da2", "0x471e", chipVendor: "0x1002", vram: 25753026560);

        var gpus = LinuxPciSubsystem.ParseLspciGpus(LspciMm);

        Assert.Equal(new[] { "6e:00.0", "65:00.0", "05:00.0", "03:00.0" }, gpus.Select(g => g.Slot));
        Assert.Equal(new[] { false, false, false, true }, gpus.Select(g => LinuxPciSubsystem.IsDiscrete(_root, g.Slot, g.Name)));
        Assert.Contains("Raphael", gpus[0].Name);
    }

    [Fact]
    public void An_amd_function_with_unreadable_memory_is_not_given_a_maker()
    {
        Device("0000:03:00.0", "0x1da2", "0x471e", chipVendor: "0x1002");

        Assert.False(LinuxPciSubsystem.IsDiscrete(_root, "03:00.0", "Navi 31"));
    }

    [Fact]
    public void An_nvidia_function_is_discrete_and_only_an_arc_intel_function_is()
    {
        Device("0000:01:00.0", "0x1462", "0x3881", chipVendor: "0x10de");
        Device("0000:00:02.0", "0x1458", "0xd000", chipVendor: "0x8086");

        Assert.True(LinuxPciSubsystem.IsDiscrete(_root, "00000000:01:00.0", "NVIDIA GeForce RTX 3090"));
        Assert.False(LinuxPciSubsystem.IsDiscrete(_root, "00:02.0", "Intel Corporation Raptor Lake-S GT1 [UHD Graphics 770]"));
        Assert.True(LinuxPciSubsystem.IsDiscrete(_root, "00:02.0", "Intel Corporation DG2 [Arc A770]"));
    }
}
