using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Sensors;

/// <summary>
/// Names the board partner behind a GPU's PCI subsystem vendor id.
///
/// Source of every id: the PCI ID Repository, https://pci-ids.ucw.cz/v2.2/pci.ids (snapshot dated 2026-10-23).
/// Each entry's trailing comment is that vendor's name in the file. Only ids the file lists are included; a
/// partner the file does not list under a name of its own is left out rather than guessed, so its cards report
/// the chip only. Gainward has no vendor record of its own: the file's GeForce subsystem entries name the
/// vendor id listed here as Gainward, while the vendor record itself reads CardExpert Technology.
/// </summary>
internal static class GpuBoardPartners
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [0x1043] = "ASUS",       // ASUSTeK Computer Inc.
        [0x1462] = "MSI",        // Micro-Star International Co., Ltd. [MSI]
        [0x1458] = "Gigabyte",   // Gigabyte Technology Co., Ltd
        [0x19da] = "Zotac",      // ZOTAC International (MCO) Ltd.
        [0x196e] = "PNY",        // PNY
        [0x3842] = "EVGA",       // EVGA Corporation
        [0x1da2] = "Sapphire",   // Sapphire Technology Limited
        [0x174b] = "Sapphire",   // PC Partner Limited / Sapphire Technology
        [0x148c] = "PowerColor", // Tul Corporation / PowerColor
        [0x1682] = "XFX",        // XFX Pine Group Inc.
        [0x1eae] = "XFX",        // XFX Limited
        [0x1849] = "ASRock",     // ASRock Incorporation
        [0x1569] = "Palit",      // Palit Microsystems Inc.
        [0x10b0] = "Gainward",   // CardExpert Technology (Gainward in the GeForce subsystem entries)
        [0x7377] = "Colorful",   // Shenzhen Colorful Yugong Technology and Development Co.
        [0x1b4c] = "Galax",      // GALAX
        [0x1ed3] = "Yeston",     // Yeston
        [0x1025] = "Acer",       // Acer Incorporated [ALI]
        [0x1565] = "Biostar",    // Biostar Microtech Int'l Corp
        [0x1dd5] = "Manli",      // Manli Technology Group Limited
        [0x107d] = "Leadtek",    // LeadTek Research Inc.
        [0x1028] = "Dell",       // Dell
        [0x103c] = "HP",         // Hewlett-Packard Company
        [0x17aa] = "Lenovo",     // Lenovo
        [0x10de] = "NVIDIA",     // NVIDIA Corporation, the reference board
        [0x1002] = "AMD",        // Advanced Micro Devices, Inc. [AMD/ATI], the reference board
    };

    /// <summary>The partner name for a PCI vendor id, null when the id is unset or not in the table.</summary>
    public static string? NameOf(int vendorId) => Names.TryGetValue(vendorId, out var name) ? name : null;

    /// <summary>A PCI config dword never carries these as a real subsystem vendor: 0 is unprogrammed, all ones is a failed read.</summary>
    public static bool IsPlausibleVendorId(int vendorId) => vendorId is > 0 and < 0xFFFF;

    /// <summary>The boards of the discrete adapters whose subsystem ids were read; an integrated adapter reports its motherboard's.</summary>
    public static IReadOnlyList<GpuBoard> BoardsOf(IEnumerable<GpuReadout> gpus) => gpus
        .Where(g => !g.Integrated && IsPlausibleVendorId(g.SubsystemVendorId))
        .Select(g => new GpuBoard(g.Name, g.SubsystemVendorId, g.SubsystemDeviceId))
        .ToList();

    /// <summary>
    /// Pairs each reported GPU model with its board, consuming boards in order so two identical cards get their own.
    /// A model with no matching board keeps the chip alone.
    /// </summary>
    public static List<GpuCard> Resolve(IReadOnlyList<string> models, IReadOnlyList<GpuBoard> boards)
    {
        var unused = boards.Where(b => IsPlausibleVendorId(b.SubsystemVendorId)).ToList();
        var cards = new List<GpuCard>(models.Count);
        foreach (var model in models)
        {
            var at = unused.FindIndex(b => string.Equals(b.Name, model, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                cards.Add(new GpuCard(model));
                continue;
            }
            var board = unused[at];
            unused.RemoveAt(at);
            cards.Add(new GpuCard(model, NameOf(board.SubsystemVendorId), board.SubsystemVendorId, board.SubsystemDeviceId));
        }
        return cards;
    }
}
