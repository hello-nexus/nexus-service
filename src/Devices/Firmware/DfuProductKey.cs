using System;
using Nexus.Service.Peripherals.Hyte.Cnvs;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Peripherals.Hyte.Y70Display;

namespace Nexus.Service.Devices.Firmware;

/// <summary>A product a stranded bootloader can be identified as.</summary>
/// <param name="FirmwareType">Firmware-catalog key of the image it takes (e.g. "q60").</param>
/// <param name="HandlerId">Device handler id, the UI's icon key (e.g. "qseries").</param>
/// <param name="Name">Product name shown to the user.</param>
/// <param name="Category">The handler's device category.</param>
public sealed record DfuDeviceIdentity(string FirmwareType, string HandlerId, string Name, string Category);

/// <summary>
/// Reads the product key a HYTE app leaves in the boot-flag slot before it drops
/// into the bootloader. The app stores the <c>FF DC 06 hi lo 00 DD</c> key as the
/// word <c>0x&lt;hi&gt;&lt;lo&gt;00DD</c> at <see cref="DfuUtil.BootFlagAddress"/>, so
/// the slot reads <c>DD 00 lo hi</c> little-endian. The bootloader only tests the
/// low byte (0xDD = stay in DFU), which is why an interrupted flash keeps the key
/// readable and identifies the board before anything is written.
/// </summary>
public static class DfuProductKey
{
    public const int SlotLength = 16;
    private const byte DfuFlag = 0xDD;

    /// <summary>
    /// The operating USB PID encoded in a boot-flag slot read-back, or null when the
    /// slot holds no key (erased, or not the <c>DD 00 lo hi</c> layout).
    /// </summary>
    public static int? ParseProductId(ReadOnlySpan<byte> slot)
    {
        if (slot.Length < 4) return null;
        if (slot[0] != DfuFlag || slot[1] != 0x00) return null;
        return (slot[3] << 8) | slot[2];
    }

    /// <summary>
    /// The product a key PID belongs to, or null when it is unknown or ambiguous.
    /// 0x0901 is both the NP50's PID and the key the Smart Hub writes
    /// (<see cref="Peripherals.Hyte.SmartHub.SmartHubProtocol.OtaProductId"/>), so it
    /// never auto-resolves: the wrong image on either would be a guess.
    /// </summary>
    public static DfuDeviceIdentity? IdentityForProductId(int productId)
    {
        switch (productId)
        {
            case QSeriesCoolerProtocol.Q60ProductId:
                return new(QSeriesCoolerProtocol.VariantQ60, "qseries", "HYTE Q60", "display");
            case QSeriesCoolerProtocol.Q80ProductId:
                return new(QSeriesCoolerProtocol.VariantQ80, "qseries", "HYTE Q80", "display");
            case MiniHubProtocol.ProductId:
                return new("fan-hub", "fan-hub", "iBUYPOWER MiniHub", "hub");
        }

        var cnvs = CnvsProtocol.VariantForProductId(productId);
        if (cnvs != "cnvs") return new(cnvs, "cnvs", "HYTE CNVS", "controller");

        var y70 = Y70DisplayProtocol.VariantForProductId(productId);
        if (y70 == Y70DisplayProtocol.VariantTouch) return new(y70, "y70", "HYTE Y70 Touch", "display");
        if (y70 != "") return new(y70, "y70", "HYTE Y70 Touch Infinite", "display");

        return null;
    }
}
