using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>One RGB channel and whatever accessory the cooler reports on it.</summary>
public sealed class KrakenLightingChannel
{
    public KrakenLightingChannel(byte channelId, byte accessoryId, string accessoryName, int ledCount, int rings = 0)
    {
        ChannelId = channelId;
        AccessoryId = accessoryId;
        AccessoryName = accessoryName;
        LedCount = ledCount;
        Rings = rings;
    }

    public byte ChannelId { get; }
    public byte AccessoryId { get; }
    public string AccessoryName { get; }
    public int LedCount { get; }

    /// <summary>
    /// Circles the LEDs sit on - one per fan on a fan chain, one for a pump ring. 0 when
    /// the accessory's geometry is unmeasured, which the LED map reads as a single ring.
    /// </summary>
    public int Rings { get; }
}

// Immutable; swapped atomically under the hub lock. Readers take the reference once.
public sealed class KrakenSnapshot
{
    public static readonly KrakenSnapshot Empty = new(
        0, 0, 0, 0, 0, "", 0, 0, KrakenDisplayMode.Liquid, new List<KrakenLightingChannel>());

    public KrakenSnapshot(
        double liquidTempC,
        int pumpRpm,
        int pumpDuty,
        int fanRpm,
        int fanDuty,
        string firmwareVersion,
        int lcdBrightness,
        int lcdOrientationQuarterTurns,
        KrakenDisplayMode displayMode,
        IReadOnlyList<KrakenLightingChannel> channels,
        bool lcdExtra45 = false)
    {
        LcdExtra45 = lcdExtra45;
        LiquidTempC = liquidTempC;
        PumpRpm = pumpRpm;
        PumpDuty = pumpDuty;
        FanRpm = fanRpm;
        FanDuty = fanDuty;
        FirmwareVersion = firmwareVersion;
        LcdBrightness = lcdBrightness;
        LcdOrientationQuarterTurns = lcdOrientationQuarterTurns;
        DisplayMode = displayMode;
        Channels = channels;
    }

    public double LiquidTempC { get; }
    public int PumpRpm { get; }
    public int PumpDuty { get; }
    public int FanRpm { get; }
    public int FanDuty { get; }
    public string FirmwareVersion { get; }
    public int LcdBrightness { get; }
    public int LcdOrientationQuarterTurns { get; }
    /// <summary>A further 45 degrees applied host-side on top of the quarter turns; round panels only.</summary>
    public bool LcdExtra45 { get; }
    public int LcdOrientationDegrees => KrakenOrientation.ToDegrees(LcdOrientationQuarterTurns, LcdExtra45);
    public KrakenDisplayMode DisplayMode { get; }
    public IReadOnlyList<KrakenLightingChannel> Channels { get; }

    public KrakenSnapshot WithReading(KrakenReading reading) => new(
        reading.LiquidTempC, reading.PumpRpm, reading.PumpDuty, reading.FanRpm, reading.FanDuty,
        FirmwareVersion, LcdBrightness, LcdOrientationQuarterTurns, DisplayMode, Channels, LcdExtra45);

    public KrakenSnapshot WithLcd(int brightness, int orientationQuarterTurns, bool extra45 = false) => new(
        LiquidTempC, PumpRpm, PumpDuty, FanRpm, FanDuty,
        FirmwareVersion, brightness, orientationQuarterTurns, DisplayMode, Channels, extra45);

    public KrakenSnapshot WithDisplayMode(KrakenDisplayMode mode) => new(
        LiquidTempC, PumpRpm, PumpDuty, FanRpm, FanDuty,
        FirmwareVersion, LcdBrightness, LcdOrientationQuarterTurns, mode, Channels, LcdExtra45);
}
