using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLi;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Which OpenRGB detectors overlap a Nexus native driver, and which native
/// handlers drive them. A detector is held off only while one of its handlers
/// is enabled; otherwise OpenRGB keeps its own default (enabled), so a device
/// with a disabled native driver still works through OpenRGB.
/// </summary>
public static class NativeDetectorClaims
{
    public const string Keeb = "keeb";
    public const string Corsair = "corsair";
    public const string LianLi = "lianli";
    public const string Strimer = "strimer";
    public const string Galahad2 = "galahad2";
    public const string Nollie = "nollie";
    public const string Kraken = "kraken";
    public const string StreamDeck = "streamdeck";
    public const string HyteNexus = "hyte-nexus";

    /// <param name="Key">Claim name a lighting provider asks about.</param>
    /// <param name="Detectors">REGISTER_*_DETECTOR names in nexus-rgb/openrgb-headless, verbatim.</param>
    /// <param name="Handlers">Device-control handler ids; the detector is held off while any is enabled.</param>
    /// <param name="NotOnMac">True where the native stack does not exist on macOS, so the detector stays on there.</param>
    public sealed record Claim(string Key, string[] Detectors, string[] Handlers, bool NotOnMac = false);

    public static readonly IReadOnlyList<Claim> All = new Claim[]
    {
        new(Keeb, new[] { "HYTE Keeb TKL" }, new[] { "keeb" }),
        new(Corsair, new[] { "Corsair iCUE Link System Hub" }, new[] { "corsair" }),
        // The original "Lian Li Uni Hub" (0x7750) is absent on purpose: the native HID worker cannot send its libusb control transfers, so OpenRGB is its only driver.
        new(LianLi,
            new[]
            {
                "Lian Li Uni Hub - SL", "Lian Li Uni Hub - AL", "Lian Li Uni Hub - SL V2",
                "Lian Li Uni Hub - AL V2", "Lian Li Uni Hub - SL V2 v0.5", "Lian Li Uni Hub - SL Infinity",
            },
            new[] { LianLiHubSet.IdFor(0), LianLiHubSet.IdFor(1), LianLiHubSet.IdFor(2), LianLiHubSet.IdFor(3) }),
        new(Strimer, new[] { "Lian Li Strimer L Connect" }, new[] { "strimer" }),
        new(Galahad2, new[] { "Lian Li GA II Trinity", "Lian Li GA II Trinity Performance" }, new[] { "lianli-aio" }),
        new(Nollie,
            new[]
            {
                "Nollie 32CH", "Nollie 16CH", "Nollie 8CH", "Nollie 1CH", "Nollie 28 12", "Nollie 28 L1",
                "Nollie 28 L2", "Nollie 32_OS2", "Nollie 16_OS2", "Nollie 8_OS2", "Nollie 1_OS2",
                "Nollie 32_OS2_1", "Nollie 16_OS2_1", "Nollie 8_OS2_1", "Prism8 8_OS2_1", "Nollie 1_OS2_1",
            },
            new[] { "nollie" }),
        new(Kraken, new[] { "NZXT Kraken 2024 ELITE Series RGB" }, new[] { "nzxt-kraken" }),
        new(StreamDeck, new[] { "Elgato Stream Deck MK.2" }, new[] { "streamdeck" }),
        // One detector covers the THICC Q60 and the NP50; macOS has no native discovery for either.
        new(HyteNexus, new[] { "HYTE Nexus" }, new[] { "np50", "qseries" }, NotOnMac: true),
    };

    /// <summary>
    /// Detector names earlier builds always disabled that now follow upstream's default. They stay in the
    /// cleanup universe so an install that already has them written as off gets them back.
    /// </summary>
    private static readonly string[] RetiredForcedOff = { "HID LampArray Device" };

    /// <summary>Every detector a past or present claim could have written as disabled.</summary>
    public static IReadOnlyCollection<string> Universe(bool macOS)
    {
        var all = new List<string>();
        foreach (var claim in All)
        {
            if (macOS && claim.NotOnMac)
            {
                continue;
            }
            all.AddRange(claim.Detectors);
        }
        all.AddRange(RetiredForcedOff);
        return all;
    }

    /// <summary>The detectors to hold off: those whose native handler is enabled.</summary>
    public static List<string> ResolveDisabled(Func<string, bool> handlerEnabled, bool macOS)
    {
        var disabled = new List<string>();
        foreach (var claim in All)
        {
            if (macOS && claim.NotOnMac)
            {
                continue;
            }
            foreach (var handler in claim.Handlers)
            {
                if (handlerEnabled(handler))
                {
                    disabled.AddRange(claim.Detectors);
                    break;
                }
            }
        }
        return disabled;
    }

    /// <summary>True when a toggle of this handler can change which detectors run.</summary>
    public static bool AffectsDetectors(string handlerId)
    {
        foreach (var claim in All)
        {
            foreach (var handler in claim.Handlers)
            {
                if (string.Equals(handler, handlerId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="device"/> was produced by one of the claim's detectors, read from the
    /// daemon's detector map. A row the map does not name is never claimed: its detector is unknown, so
    /// it may be hardware the native driver cannot drive.
    /// </summary>
    public static bool Owns(string claimKey, RgbDevice device, IReadOnlyDictionary<string, string> detectorByDeviceName)
    {
        if (!detectorByDeviceName.TryGetValue(device.Name, out var detector))
        {
            return false;
        }
        foreach (var claim in All)
        {
            if (claim.Key != claimKey)
            {
                continue;
            }
            return Array.IndexOf(claim.Detectors, detector) >= 0;
        }
        return false;
    }

    /// <inheritdoc cref="Owns(string, RgbDevice, IReadOnlyDictionary{string, string})"/>
    public static bool Owns(string claimKey, RgbDevice device)
        => Owns(claimKey, device, OpenRgbDetectorMap.Current);
}
