using System;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>Pixel format a Kraken's bulk endpoint accepts. Values are the wire bytes.</summary>
public enum KrakenLcdFormat : byte
{
    /// <summary>No panel on this model.</summary>
    None = 0x00,
    Rgba8888 = 0x02,
    Rgb565 = 0x06,
    /// <summary>QOI-derived RGB565. See <see cref="Q565Encoder"/>.</summary>
    Q565 = 0x08,
}

/// <summary>How a Kraken generation takes per-LED colour.</summary>
public enum KrakenLightingProtocol
{
    /// <summary>Model has no addressable LEDs and no RGB header.</summary>
    None,

    /// <summary>
    /// One 0x26 report carries a whole channel and the firmware applies it on arrival.
    /// Elite V2 only; needs 512-byte reports to fit 40 LEDs.
    /// </summary>
    ChannelReport,

    /// <summary>
    /// The pre-Elite-V2 form: colours are staged into two 0x22 tables of 60 bytes and then
    /// latched with a 0x22 0xA0 submit. Fits 64-byte reports.
    /// </summary>
    StreamedTables,
}

/// <summary>
/// Everything that differs between Kraken models. The rest of the protocol - firmware
/// query (0x10), telemetry (0x74/0x75), speed curves (0x72), the bucket/bulk LCD upload
/// (0x30/0x32/0x36/0x38) and the accessory table (0x20/0x21) - is shared across the whole
/// line, which is why one hub drives all of them.
///
/// Every field was reconstructed from third-party protocol documentation and cross-checked
/// against liquidctl's kraken3.py. Only the Elite V2 rows are bench-verified here; the
/// others have had no hardware to run against.
/// </summary>
public sealed record KrakenModel(
    int ProductId,
    string Name,
    int LcdWidth,
    int LcdHeight,
    KrakenLcdFormat LcdFormat,
    KrakenLightingProtocol Lighting,
    bool SpeedChannelsFollowFirmware = false,
    bool DirectStream = false,
    bool StillImageOnly = false,
    bool FirmwareGif = false)
{
    public bool HasLcd => LcdFormat != KrakenLcdFormat.None;

    /// <summary>Bytes in one uncompressed RGBA frame at this model's panel size.</summary>
    public int LcdFrameBytes => LcdWidth * LcdHeight * 4;

    /// <summary>
    /// Every Kraken this service drives.
    ///
    /// Deliberately absent: the Kraken X2/M2 (0x170E / 0x1715). It predates the whole
    /// command set above - no 0x10 firmware query, no 0x74 telemetry, no 0x72 curve, and
    /// lighting is a 0x02 0x4C report on HID interface 0 rate-limited to one write per
    /// 500 ms. It needs its own hub rather than a row here.
    /// </summary>
    public static readonly KrakenModel[] All =
    {
        // Bench unit. Firmware 1.2.0; 512-byte HID reports.
        new(0x3012, "NZXT Kraken Elite V2", 640, 640, KrakenLcdFormat.Q565, KrakenLightingProtocol.ChannelReport),
        // Same panel and same wire format as 0x3012 - NZXT's own driver treats the two SKUs
        // as one device.
        new(0x3014, "NZXT Kraken Elite V2", 640, 640, KrakenLcdFormat.Q565, KrakenLightingProtocol.ChannelReport),

        // 2023 Kraken Elite. Same 640x640 Q565 panel; no ARGB anywhere on the pump, so no
        // lighting to drive. Its 0x72 channel tuples moved at firmware 2.1.1, hence
        // SpeedChannelsFollowFirmware. Bucket uploads are reported to wedge its 2.x firmware
        // at streaming rates, so live frames take the bucketless direct path.
        new(0x300C, "NZXT Kraken Elite", 640, 640, KrakenLcdFormat.Q565, KrakenLightingProtocol.None, true, DirectStream: true),
        // 2023 Kraken. Smaller panel, and the only model that takes uncompressed RGB565 -
        // its firmware has no Q565 decoder.
        new(0x300E, "NZXT Kraken", 240, 240, KrakenLcdFormat.Rgb565, KrakenLightingProtocol.None, true),

        // Kraken Z3 (Z53/Z63/Z73). 320x320, and the only model still on raw RGBA. A used
        // bucket is deleted before it is rewritten, which a stream would do to the pump's
        // flash every frame, so the panel takes still images only. It animates an uploaded
        // GIF on its own.
        new(0x3008, "NZXT Kraken Z3", 320, 320, KrakenLcdFormat.Rgba8888, KrakenLightingProtocol.StreamedTables,
            StillImageOnly: true, FirmwareGif: true),

        // Kraken X3 (X53/X63/X73). Infinity-mirror ring, no panel.
        new(0x2007, "NZXT Kraken X3", 0, 0, KrakenLcdFormat.None, KrakenLightingProtocol.StreamedTables),
        new(0x2014, "NZXT Kraken X3", 0, 0, KrakenLcdFormat.None, KrakenLightingProtocol.StreamedTables),
    };

    public static KrakenModel? Find(int productId)
    {
        foreach (var model in All)
        {
            if (model.ProductId == productId)
            {
                return model;
            }
        }
        return null;
    }
}
