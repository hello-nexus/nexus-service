using System;

namespace Nexus.Service.Peripherals.Hyte.Y70Display;

/// <summary>
/// Pure builders + parsers for the HYTE Y70 Touch display's STM32 controller
/// serial protocol - the controller that the bundled <c>y70*/*.hex</c> images
/// flash. Ported from HYTE's nexus-control-service <c>Y70TouchStm32Commander</c>.
///
/// The Y70 shares the "smart hub" command family with the Q-series/MiniHub, so
/// the firmware-version request is byte-identical (0xFF 0xDD 0x02); the only
/// difference is the Y70 returns a 13-byte response (vs 7), with the version
/// still in bytes [3..6]. Display brightness / on-off use a separate 0xFF 0xCC
/// command set on the serial models (Touch / Infinite) - built below - and
/// DDC/CI VCP codes on the DDC models (Truly / GW), driven through the
/// platform display-brightness provider.
/// </summary>
public static class Y70DisplayProtocol
{
    // ── USB identity (cooler-style serial/CDC COM port) ──

    public const int VendorId = 0x3402;
    public const int Y70TouchProductId = 0x0C00;
    public const int Y70InfiniteProductId = 0x0C01;
    public const int Y70TrulyProductId = 0x0C02;

    /// <summary>
    /// Firmware-catalog keys (bundled-.hex directory names). Deliberately none
    /// equal the handler Id ("y70") so an as-yet-unidentified panel maps to a
    /// keyless Id with no bundle (excluded) rather than defaulting to Touch.
    /// </summary>
    public const string VariantTouch = "y70-touch";
    public const string VariantInfinite = "y70-infinite";
    public const string VariantTruly = "y70-truly";

    /// <summary>
    /// Variant labels for the DDC-only panels (UI/diagnostics). Not
    /// firmware-catalog keys: these panels expose no USB serial function and
    /// carry no host-updatable firmware (reference Y70TouchGwController /
    /// Y70TouchInaController), so no bundle exists and OTA never offers them
    /// an image. Identified only by their monitor EDID fragment.
    /// </summary>
    public const string VariantGw = "y70-gw";
    public const string VariantIna = "y70-ina";

    /// <summary>
    /// EDID/PnP fragment to variant for the DDC-only panels (reference
    /// controllers' SCREEN_NAME constants).
    /// </summary>
    public static readonly (string Fragment, string Variant)[] DdcOnlyPanelVariants =
        { ("RTK1234", VariantGw), ("RTK2345", VariantIna) };

    public static readonly string[] DdcOnlyVariantKeys = { VariantGw, VariantIna };

    /// <summary>Variant key of the DDC-only panel matching this monitor hardware id, or empty.</summary>
    public static string DdcOnlyVariantForHardwareId(string rawHardwareId)
    {
        if (string.IsNullOrEmpty(rawHardwareId)) return "";
        foreach (var (fragment, variant) in DdcOnlyPanelVariants)
        {
            if (rawHardwareId.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                return variant;
        }
        return "";
    }

    // ── Wire constants ──

    private const byte Frame0 = 0xFF;
    private const byte OpControl = 0xDD;
    private const byte SubGetFirmwareVersion = 0x02;

    // Display control (brightness + screen power) on the serial models.
    private const byte OpDisplay = 0xCC;
    private const byte SubSetBrightnessPower = 0x01; // FF CC 01 <on> <pct>
    private const byte SubGetScreenInfo = 0x02;      // FF CC 02 -> screenOn@[4], brightness@[5]

    /// <summary>
    /// Firmware floors brightness to this percent whenever the screen is on
    /// (below it the panel hums / can latch dark), so the host clamps to match.
    /// </summary>
    public const int MinBrightnessOnPercent = 20;

    /// <summary>
    /// Screen-info reply (FF CC 02). Legacy over-reads 13 bytes; the fields we
    /// need (screen-on flag, brightness) sit at [4]/[5], so 6 bytes is the real
    /// minimum to parse.
    /// </summary>
    public const int ScreenInfoMinResponseLength = 6;

    /// <summary>
    /// EDID/PnP hardware-id fragments of every Y70 panel monitor. Matched
    /// against a monitor's PnP DeviceID to identify the Y70 display for
    /// rotation, detection, and DDC/CI VCP writes. Mirrors the reference
    /// Y70TouchMonitor.SCREEN_NAMES and the copy in nexus-overlay
    /// PanelDisplay.cs - keep all three in sync.
    /// </summary>
    public static readonly string[] DdcPanelHardwareNames =
        { "RTK0004", "RTD1100", "RTK1234", "RTK2234", "BOE2143", "RTK409A", "RTK2345" };

    // DDC/CI VCP codes (driven via the platform display-brightness provider).
    // Values match the reference Y70DDCCIHelper: screen-off is Standby (0x04),
    // never hard off (0x05), so the monitor keeps answering DDC/CI and a later
    // screen-on write still reaches it.
    public const byte VcpBrightness = 0x10;
    public const byte VcpPower = 0xD6;
    public const int VcpPowerOn = 0x01;
    public const int VcpPowerStandby = 0x04;

    // The original Touch (0x0C00) dims through the monitor's RGB video-gain
    // registers with the STM32 backlight PWM pinned at 100% - PWM dimming
    // makes that panel's backlight hum (reference Y70TouchDevice /
    // Y70DDCCIHelper). The gain registers only respond after ColorPresetMode
    // is set to UserDefine3 (reference SetToAdjustClockPhaseMode).
    public const byte VcpColorPresetMode = 0x14;
    public const int VcpColorPresetUserDefine3 = 0x0B;
    public const byte VcpVideoGainRed = 0x16;
    public const byte VcpVideoGainGreen = 0x18;
    public const byte VcpVideoGainBlue = 0x1A;

    /// <summary>
    /// Reference mapping (Y70TouchDevice.SetBrightness) from a 0-100 percent
    /// to the value written to all three gain registers, compressing the
    /// percent into the monitor's usable gain band.
    /// </summary>
    public static int TouchRgbGainForPercent(int percent)
        => (int)(Math.Clamp(percent, 0, 100) * 0.8 + 20) / 2;

    /// <summary>
    /// The Y70 controller answers the version query with a 7-byte frame
    /// (FF DD 02 maj min build hw) - bench-confirmed on a Y70 Touch Infinite
    /// (2026-05-27). HYTE's legacy commander over-allocates a 13-byte read
    /// buffer, but the device only sends 7; the version is in bytes [3..6].
    /// </summary>
    public const int FirmwareVersionResponseLength = 7;

    // ── Builders ──

    public static byte[] BuildGetFirmwareVersion() => new byte[] { Frame0, OpControl, SubGetFirmwareVersion };

    /// <summary>
    /// Set the current brightness and screen power in one frame:
    /// <c>FF CC 01 &lt;screenOn 1/0&gt; &lt;percent 0-100&gt;</c>. The firmware floors a
    /// screen-on brightness to <see cref="MinBrightnessOnPercent"/>; callers
    /// should pass an already-clamped percent so GET reflects what they sent.
    /// No reply is returned for this command.
    /// </summary>
    public static byte[] BuildSetBrightnessPower(bool screenOn, int percent)
    {
        var pct = (byte)Math.Clamp(percent, 0, 100);
        return new byte[] { Frame0, OpDisplay, SubSetBrightnessPower, (byte)(screenOn ? 0x01 : 0x00), pct };
    }

    /// <summary>Request the current screen-on flag + brightness: <c>FF CC 02</c>.</summary>
    public static byte[] BuildGetScreenInfo() => new byte[] { Frame0, OpDisplay, SubGetScreenInfo };

    // ── Parsers ──

    /// <summary>
    /// Parse the 13-byte firmware-version response into "Major.Minor.Build.Hw"
    /// (e.g. "1.0.3.1"). Returns empty on a short or mis-echoed reply; the
    /// controller echoes the command header (0xFF 0xDD) in bytes [0..1].
    /// </summary>
    public static string ParseFirmwareVersion(ReadOnlySpan<byte> response)
    {
        if (response.Length < FirmwareVersionResponseLength) return "";
        if (response[0] != Frame0 || response[1] != OpControl) return "";
        return $"{response[3]}.{response[4]}.{response[5]}.{response[6]}";
    }

    /// <summary>Parsed screen-info reply.</summary>
    public readonly record struct Y70ScreenInfo(bool ScreenOn, int Brightness);

    /// <summary>
    /// Parse the FF CC 02 reply. The controller echoes the header (FF CC) in
    /// bytes [0..1]; the screen-on flag is at [4] and brightness percent at [5].
    /// Returns null on a short or mis-echoed frame.
    /// </summary>
    public static Y70ScreenInfo? ParseScreenInfo(ReadOnlySpan<byte> response)
    {
        if (response.Length < ScreenInfoMinResponseLength) return null;
        if (response[0] != Frame0 || response[1] != OpDisplay) return null;
        return new Y70ScreenInfo(response[4] != 0, Math.Clamp((int)response[5], 0, 100));
    }

    /// <summary>Map a matched USB PID to its firmware-catalog variant key, or empty if unrecognized.</summary>
    public static string VariantForProductId(int productId) => productId switch
    {
        Y70TouchProductId => VariantTouch,
        Y70InfiniteProductId => VariantInfinite,
        Y70TrulyProductId => VariantTruly,
        _ => "",
    };

    /// <summary>Operating USB PID for a variant key (for the OTA product key), or -1 if unknown.</summary>
    public static int ProductIdForVariant(string variant) => variant switch
    {
        VariantTouch => Y70TouchProductId,
        VariantInfinite => Y70InfiniteProductId,
        VariantTruly => Y70TrulyProductId,
        _ => -1,
    };
}
