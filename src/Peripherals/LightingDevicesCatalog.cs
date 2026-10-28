using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Nexus.Service.Models.Peripherals;
using Nexus.Service.Serialization;

namespace Nexus.Service.Peripherals;

/// <summary>
/// The full catalog of RGB-capable devices OpenRGB can drive, extracted at build
/// time from openrgb-headless/Controllers/**/*Detect*.cpp and embedded as a JSON
/// resource.
///
/// Read once on first access and cached for the process lifetime.
/// </summary>
public static class LightingDevicesCatalog
{
    private static IReadOnlyList<SupportedDeviceDto>? _cache;
    private static readonly object _gate = new();

    public static IReadOnlyList<SupportedDeviceDto> All
    {
        get
        {
            if (_cache is not null)
            {
                return _cache;
            }
            lock (_gate)
            {
                _cache ??= Load();
                return _cache;
            }
        }
    }

    private static IReadOnlySet<int>? _usbVendorIds;
    private static IReadOnlySet<int>? _openRgbUsbIds;

    /// <summary>(vid &lt;&lt; 16) | pid of every catalog device OpenRGB detects by exact USB id (natively driven hardware excluded).</summary>
    public static IReadOnlySet<int> OpenRgbUsbIds
    {
        get
        {
            if (_openRgbUsbIds is not null)
            {
                return _openRgbUsbIds;
            }
            lock (_gate)
            {
                if (_openRgbUsbIds is null)
                {
                    var ids = new HashSet<int>();
                    foreach (var d in All)
                    {
                        if (d.Source == "openrgb" && TryParseHexId(d.VendorId, out var vid) && TryParseHexId(d.ProductId, out var pid))
                        {
                            ids.Add((vid << 16) | pid);
                        }
                    }
                    _openRgbUsbIds = ids;
                }
                return _openRgbUsbIds;
            }
        }
    }

    /// <summary>
    /// Distinct USB vendor ids across every OpenRGB detector row plus the
    /// first-party devices; consulted by the RGB hot-plug relevance filter.
    /// Empty when the embedded resource is missing or unreadable, which the
    /// filter treats as "every change is relevant".
    /// </summary>
    public static IReadOnlySet<int> UsbVendorIds
    {
        get
        {
            if (_usbVendorIds is not null)
            {
                return _usbVendorIds;
            }
            lock (_gate)
            {
                _usbVendorIds ??= LoadUsbVendorIds();
                return _usbVendorIds;
            }
        }
    }

    private static IReadOnlySet<int> LoadUsbVendorIds()
    {
        var ids = new HashSet<int>();
        var asm = typeof(LightingDevicesCatalog).Assembly;
        using (var stream = asm.GetManifestResourceStream("openrgb-supported-devices.json"))
        {
            if (stream is null)
            {
                return ids;
            }
            try
            {
                // Raw rows, not All: the natively-driven controllers All drops
                // still represent hardware whose arrival must trigger a rescan.
                var file = JsonSerializer.Deserialize(stream, AppJsonContext.Default.OpenRgbSupportedDevicesFile);
                if (file?.Devices is null)
                {
                    return ids;
                }
                foreach (var d in file.Devices)
                {
                    if (TryParseHexId(d.Vid, out var vid))
                    {
                        ids.Add(vid);
                    }
                }
            }
            catch
            {
                // Fail open (empty set = every change relevant) rather than
                // silently narrowing relevance to first-party vendors.
                ids.Clear();
                return ids;
            }
        }
        foreach (var d in FirstPartyDevices)
        {
            if (TryParseHexId(d.VendorId, out var vid))
            {
                ids.Add(vid);
            }
        }
        return ids;
    }

    private static bool TryParseHexId(string? raw, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }
        var s = raw.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) ? raw.Substring(2) : raw;
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private static IReadOnlyList<SupportedDeviceDto> Load()
    {
        var asm = typeof(LightingDevicesCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream("openrgb-supported-devices.json");
        if (stream is null)
        {
            return new List<SupportedDeviceDto>();
        }

        OpenRgbSupportedDevicesFile? file;
        try
        {
            file = JsonSerializer.Deserialize(stream, AppJsonContext.Default.OpenRgbSupportedDevicesFile);
        }
        catch
        {
            return new List<SupportedDeviceDto>();
        }

        if (file?.Devices is null)
        {
            return new List<SupportedDeviceDto>(FirstPartyDevices);
        }

        // First-party devices Nexus drives natively lead the list with correct
        // model/category and a "nexus" source. The bundled OpenRGB fork registers
        // some of the same hardware under its own controllers (the "HYTE Nexus"
        // detector groups the THICC Q60 and Nexus Portal NP50 into one mislabeled
        // row; LianLiController and CorsairICueLinkController cover exactly the Uni
        // Fan/Strimer/Galahad and iCUE LINK hubs we now drive ourselves), so those
        // controllers are skipped to avoid duplicate, wrongly-typed rows.
        var result = new List<SupportedDeviceDto>(FirstPartyDevices.Count + file.Devices.Count);
        result.AddRange(FirstPartyDevices);

        foreach (var d in file.Devices)
        {
            var controller = d.Controller ?? "";
            var detector = d.Name ?? "";
            if (controller.StartsWith("HYTE", System.StringComparison.OrdinalIgnoreCase) ||
                NativelyDrivenControllers.Contains(controller) ||
                NativelyDrivenDetectors.Contains(detector))
            {
                continue;
            }
            var (vendor, model) = SplitVendorModel(detector);
            result.Add(new SupportedDeviceDto
            {
                Vendor = vendor,
                Model = model,
                Category = ResolveCategory(detector, d.Controller, d.Kind, d.Type),
                VendorId = d.Vid ?? "-",
                ProductId = d.Pid ?? "-",
                Capabilities = new List<string> { "rgb" },
                Source = "openrgb",
            });
        }
        return result;
    }

    /// <summary>
    /// OpenRGB controllers whose hardware Nexus now drives natively; their rows are
    /// skipped from the OpenRGB catalog so the curated FirstPartyDevices entries are
    /// the single source. Each maps 1:1 to a native driver under src/Peripherals/.
    /// </summary>
    private static readonly HashSet<string> NativelyDrivenControllers = new()
    {
        "LianLiController",           // Uni Fan family, Strimer, Galahad II
        "CorsairICueLinkController",  // iCUE LINK System Hub
        "NollieController",           // Nollie ARGB channel controllers + Prism8
    };

    /// <summary>
    /// Devices Nexus drives natively, curated so they carry the correct model,
    /// category, and VID/PID independent of how the OpenRGB fork happens to register
    /// them. VID/PIDs come from the protocol constants under src/Peripherals/*. One
    /// row per USB PID a native driver claims (the connected dot matches exact
    /// VID:PID); a native lighting/cooling driver adds its rows here so the device
    /// shows in the Supported Devices UI.
    /// </summary>
    private static readonly IReadOnlyList<SupportedDeviceDto> FirstPartyDevices = new List<SupportedDeviceDto>
    {
        // HYTE - PIDs from src/Peripherals/Hyte/*. Q60/Q80 are LCD-screen AIOs.
        Native("HYTE",    "THICC Q60",              "aio",      "0x3402", "0x0400", screen: true),
        Native("HYTE",    "THICC Q80",              "aio",      "0x3402", "0x0403", screen: true),
        Native("HYTE",    "Nexus Portal NP50",      "light",    "0x3402", "0x0901"),
        // CNVS: one row per CnvsProtocol.ProductIds entry - the web connected dot matches exact VID:PID.
        Native("HYTE",    "CNVS",                   "mousemat", "0x3402", "0x0BFF"),
        Native("HYTE",    "CNVS",                   "mousemat", "0x3402", "0x0B00"),
        Native("HYTE",    "CNVS",                   "mousemat", "0x3402", "0x0B01"),
        Native("HYTE",    "CNVS",                   "mousemat", "0x3402", "0x0B02"),
        Native("HYTE",    "Keeb TKL",               "keyboard", "0x3402", "0x0300"),
        Native("HYTE",    "Smart Hub",              "light",    "0x3402", "0x0904"),
        // Y70 cases are screen-only. The GW / Ina panels enumerate no USB
        // function at all (identified by monitor EDID, Y70DisplayProtocol.
        // DdcOnlyPanelVariants), so their rows carry the keyless "-" the
        // OpenRGB rows use.
        Native("HYTE",    "Y70 Touch",              "case",     "0x3402", "0x0C00", screen: true, rgb: false),
        Native("HYTE",    "Y70 Touch Infinite",     "case",     "0x3402", "0x0C01", screen: true, rgb: false),
        Native("HYTE",    "Y70 Touch Infinite",     "case",     "0x3402", "0x0C02", screen: true, rgb: false),
        Native("HYTE",    "Y70 Touch GW",           "case",     "-",      "-",      screen: true, rgb: false),
        Native("HYTE",    "Y70 Ina Touch",          "case",     "-",      "-",      screen: true, rgb: false),

        // iBUYPOWER - MiniHub PID from src/Peripherals/Hyte/MiniHub/MiniHubProtocol.cs;
        // keyboard / mouse PIDs from src/Peripherals/Ibp/IbpPeripheralProtocol.cs
        // (one row per model there - IbpPeripheralCatalogTests pins the pairing).
        Native("iBUYPOWER", "MiniHub",              "light",    "0x3402", "0x0900"),
        Native("iBUYPOWER", "Chimera KM7 Keyboard", "keyboard", "0x3402", "0x0301"),
        Native("iBUYPOWER", "Chimera KM7 Mouse",    "mouse",    "0x3402", "0x0200"),
        Native("iBUYPOWER", "Chimera KM10 Keyboard","keyboard", "0x3402", "0x0305"),
        Native("iBUYPOWER", "Chimera KM10 Mouse",   "mouse",    "0x3402", "0x0201"),
        Native("iBUYPOWER", "MK9 Keyboard",         "keyboard", "0x3402", "0x0303"),
        Native("iBUYPOWER", "MK9 Pro Keyboard",     "keyboard", "0x3402", "0x0304"),
        Native("iBUYPOWER", "MEK 4 Keyboard",       "keyboard", "0x3402", "0x0302"),

        // Lian Li - PIDs from src/Peripherals/LianLi*, Strimer, Galahad2, LianLiTl,
        // LianLiWireless. SL/TL-LCD are the fan-mounted LCD screens.
        Native("Lian Li", "Uni Hub",                "fan",      "0x0CF2", "0x7750"),
        Native("Lian Li", "Uni Fan SL",             "fan",      "0x0CF2", "0xA100"),
        Native("Lian Li", "Uni Fan SL (Redragon)",  "fan",      "0x0CF2", "0xA106"),
        Native("Lian Li", "Uni Fan AL",             "fan",      "0x0CF2", "0xA101"),
        Native("Lian Li", "Uni Fan SL-Infinity",    "fan",      "0x0CF2", "0xA102"),
        Native("Lian Li", "Uni Fan SL v2",          "fan",      "0x0CF2", "0xA103"),
        Native("Lian Li", "Uni Fan AL v2",          "fan",      "0x0CF2", "0xA104"),
        Native("Lian Li", "Uni Fan TL",             "fan",      "0x0416", "0x7372"),
        Native("Lian Li", "TL LCD Flex Fan",        "fan",      "0x43A8", "0x0102", screen: true),
        Native("Lian Li", "TL LCD Flex Fan Screen", "fan",      "0x1CBE", "0xA018", screen: true, rgb: false),
        Native("Lian Li", "SL-INF LCD Flex Fan",    "fan",      "0x43A8", "0x0104", screen: true),
        Native("Lian Li", "SL-INF LCD Flex Fan Screen", "fan",  "0x1CBE", "0xA019", screen: true, rgb: false),
        Native("Lian Li", "P28 V2 Fan",             "fan",      "0x43A8", "0x0105"),
        Native("Lian Li", "Strimer",                "light",    "0x0CF2", "0xA200"),
        Native("Lian Li", "Galahad II Trinity",     "aio",      "0x0416", "0x7373"),
        Native("Lian Li", "Galahad II Performance", "aio",      "0x0416", "0x7371"),
        Native("Lian Li", "L-Wireless Kit",         "fan",      "0x0416", "0x8040"),
        // RF-only behind the kit's dongle: no USB identity of its own, so keyless
        // like the DDC panels (a VID:PID row would merge into the kit's).
        Native("Lian Li", "Strimer Wireless",       "light",    "-",      "-"),
        Native("Lian Li", "SL-LCD",                 "light",    "0x1CBE", "0x0005", screen: true),
        Native("Lian Li", "TL-LCD",                 "light",    "0x1CBE", "0x0006", screen: true),
        Native("Lian Li", "Uni Fan TL LCD",         "light",    "0x04FC", "0x7393", screen: true),

        // Asiahorse - VID/PID from src/Peripherals/AsiahorseUmbra/UmbraProtocol.cs. ARGB + PWM controller.
        Native("Asiahorse", "Umbra V2",             "fan",      "0x1A86", "0xFE04"),

        // Nollie - VID/PIDs from src/Peripherals/Nollie/NollieProtocol.cs. ARGB
        // channel controllers; the channel count is the marketed model number.
        Native("Nollie",  "Nollie 1",               "light",    "0x16D2", "0x1F11"),
        Native("Nollie",  "Nollie 8",               "light",    "0x16D2", "0x1F01"),
        Native("Nollie",  "Nollie 28-12",           "light",    "0x16D2", "0x1616"),
        Native("Nollie",  "Nollie 28 L1",           "light",    "0x16D2", "0x1617"),
        Native("Nollie",  "Nollie 28 L2",           "light",    "0x16D2", "0x1618"),
        Native("Nollie",  "Nollie 16",              "light",    "0x3061", "0x4716"),
        Native("Nollie",  "Nollie 32",              "light",    "0x3061", "0x4714"),
        Native("Nollie",  "Nollie 1 (OS2)",         "light",    "0x16D5", "0x1F11"),
        Native("Nollie",  "Nollie 8 (OS2)",         "light",    "0x16D5", "0x1F01"),
        Native("Nollie",  "Nollie 16 (OS2)",        "light",    "0x16D5", "0x4716"),
        Native("Nollie",  "Nollie 32 (OS2)",        "light",    "0x16D5", "0x4714"),
        Native("Nollie",  "Nollie 1 (OS2.1)",       "light",    "0x16D5", "0x2A01"),
        Native("Nollie",  "Nollie 8 (OS2.1)",       "light",    "0x16D5", "0x2A08"),
        Native("Nollie",  "Nollie 16 (OS2.1)",      "light",    "0x16D5", "0x2A16"),
        Native("Nollie",  "Nollie 32 (OS2.1)",      "light",    "0x16D5", "0x2A32"),
        Native("Nollie",  "Prism8 (OS2.1)",         "light",    "0x16D5", "0x2C08"),

        // Tryx - PIDs from src/Peripherals/Tryx/Panorama. All are LCD-screen AIOs.
        Native("Tryx",    "Panorama",               "aio",      "0x391A", "0x1011", screen: true),
        Native("Tryx",    "Panorama SE",            "aio",      "0x391A", "0x1021", screen: true),
        Native("Tryx",    "Panorama WaterBlock",    "aio",      "0x391A", "0x1031", screen: true),
        Native("Tryx",    "Panorama v2",            "aio",      "0x391A", "0x10B1", screen: true),

        // SteelSeries Apex Pro TKL OLED screen only (a streamed panel); lighting stays with OpenRGB.
        // PIDs from src/Peripherals/SteelSeries/ApexOledProtocol.cs.
        Native("SteelSeries", "Apex Pro TKL 2023 OLED",  "keyboard", "0x1038", "0x1628", screen: true, rgb: false),
        Native("SteelSeries", "Apex Pro TKL Gen 3 OLED", "keyboard", "0x1038", "0x1642", screen: true, rgb: false),
        // Corsair Galleon 100 SD keyboard - IDs from src/Peripherals/Corsair/Galleon.
        Native("Corsair", "Galleon 100 SD Keyboard", "keyboard", "0x1B1C", "0x2B0C"),
        Native("Corsair", "Galleon 100 SD Dials", "keyboard", "0x1B1C", "0x2B18"),

        // Corsair iCUE LINK - PIDs from src/Peripherals/CorsairLink. The LCD/XD5 pumps
        // carry a screen.
        Native("Corsair", "iCUE LINK System Hub",   "fan",      "0x1B1C", "0x0C3F"),
        Native("Corsair", "iCUE LINK LCD",          "aio",      "0x1B1C", "0x0C4E", screen: true),
        Native("Corsair", "iCUE LINK XD5 Elite LCD","aio",      "0x1B1C", "0x0C43", screen: true),

        // NZXT Kraken - PIDs from src/Peripherals/Nzxt/KrakenModel.cs. The 2023 Kraken and
        // Kraken Elite carry an LCD but no addressable LEDs anywhere, so they are screen-only.
        Native("NZXT",    "Kraken Elite V2",        "aio",      "0x1E71", "0x3012", screen: true),
        Native("NZXT",    "Kraken Elite V2",        "aio",      "0x1E71", "0x3014", screen: true),
        Native("NZXT",    "Kraken Elite",           "aio",      "0x1E71", "0x300C", screen: true, rgb: false),
        Native("NZXT",    "Kraken",                 "aio",      "0x1E71", "0x300E", screen: true, rgb: false),
        Native("NZXT",    "Kraken Z3",              "aio",      "0x1E71", "0x3008", screen: true),
        Native("NZXT",    "Kraken X3",              "aio",      "0x1E71", "0x2007"),
        Native("NZXT",    "Kraken X3 RGB",          "aio",      "0x1E71", "0x2014"),

        // ZMatrices LCD AIOs - PID from src/Peripherals/BulkPanels/ZMatricesPanelDriver.cs.
        Native("Aftershock", "Glacier Matrix 360",  "aio",      "0x38C1", "0x0026", screen: true, rgb: false),

        // JPEG-over-HID cooler LCDs - PIDs from src/Peripherals/JpegPanels/JpegPanelModel.cs.
        // Screen only: Nexus drives the glass on these, not their RGB, except the HydroShift
        // 360R, whose three ARGB fans are driven per LED. Only the HydroShift rows have been
        // run against hardware; all ship with Nexus Control off by default.
        Native("Lian Li",    "Galahad II LCD",      "aio",      "0x0416", "0x7395", screen: true, rgb: false),
        Native("Lian Li",    "HydroShift LCD 360S", "aio",      "0x0416", "0x7398", screen: true, rgb: false),
        Native("Lian Li",    "HydroShift LCD 360R", "aio",      "0x0416", "0x7399", screen: true),
        Native("Lian Li",    "HydroShift LCD 360TL","aio",      "0x0416", "0x739A", screen: true, rgb: false),
        Native("Corsair",    "XC7 RGB Elite LCD",   "aio",      "0x1B1C", "0x0C42", screen: true, rgb: false),
        Native("Corsair",    "Elite Capellix LCD",  "aio",      "0x1B1C", "0x0C39", screen: true, rgb: false),
        Native("Corsair",    "Elite Capellix LCD",  "aio",      "0x1B1C", "0x0C33", screen: true, rgb: false),
        Native("Corsair",    "Nautilus LCD Cap",    "aio",      "0x1B1C", "0x0C55", screen: true, rgb: false),
        Native("ID-Cooling", "FX-LCD",              "aio",      "0x2000", "0x3000", screen: true, rgb: false),
        Native("ASRock",     "AIO LCD",             "aio",      "0x26CE", "0x0A10", screen: true, rgb: false),
        Native("ASUS",       "ROG Strix SLC IV LCD","aio",      "0x0B05", "0x1DE6", screen: true, rgb: false),

        // Bulk-pipe cooler LCDs - drivers in src/Peripherals/BulkPanels/. Reachable only
        // where Windows has bound WinUSB, and likewise untested.
        Native("Thermalright", "Vision LCD",        "aio",      "0x87AD", "0x70DB", screen: true, rgb: false),
        Native("ASUS",       "Ryujin LCD",          "aio",      "0x0B05", "0x1AA2", screen: true, rgb: false),
        Native("Lian Li",    "Universal Screen 8.8","light",    "0x1CBE", "0xA088", screen: true, rgb: false),
        Native("ASUS",       "ROG Crosshair X870E Glacial LCD", "light", "0x0B05", "0x1D93", screen: true, rgb: false),
        Native("DeepCool",   "Mystique",            "aio",      "0x3633", "0x0009", screen: true, rgb: false),

        // HydroShift II LCD-S / LCD-C over USB: glass, pump, fan headers and ring on one bulk
        // pipe - src/Peripherals/BulkPanels/HydroShift2LcdDriver.cs.
        Native("Lian Li",    "HydroShift II LCD-S", "aio",      "0x1CBE", "0xA034", screen: true),
        Native("Lian Li",    "HydroShift II LCD-C", "aio",      "0x1CBE", "0xA021", screen: true),

        // HydroShift II OLED Curved: the glass on its own bulk pipe, LEDs, pump and head
        // motors on a second USB function - HydroShift2CurveLcdDriver / HydroShift2CurveBoard.
        Native("Lian Li",    "HydroShift II OLED Curved", "aio", "0x1CBE", "0xA068", screen: true),
    };

    /// <summary>
    /// OpenRGB detector names whose exact hardware Nexus now drives natively, where the
    /// controller they belong to still covers other devices we do not. NZXTHue2Controller
    /// is the case in point: it registers the Hue 2 family and the Smart Device V2
    /// alongside the Krakens, so it cannot be skipped wholesale the way LianLiController is.
    /// The Kraken X2/M2 rows stay - they are on NZXTKrakenController and we do not drive them.
    /// </summary>
    private static readonly HashSet<string> NativelyDrivenDetectors = new()
    {
        "NZXT Kraken 2024 ELITE Series RGB",
        "NZXT Kraken X3 Series",
        "NZXT Kraken X3 Series RGB",
    };

    private static SupportedDeviceDto Native(
        string vendor, string model, string category, string vid, string pid, bool screen = false, bool rgb = true) => new()
    {
        Vendor = vendor,
        Model = model,
        Category = category,
        VendorId = vid,
        ProductId = pid,
        Capabilities = BuildCapabilities(rgb, screen),
        Source = "nexus",
    };

    private static List<string> BuildCapabilities(bool rgb, bool screen)
    {
        var caps = new List<string>(2);
        if (rgb)
        {
            caps.Add("rgb");
        }
        if (screen)
        {
            caps.Add("screen");
        }
        return caps;
    }

    /// <summary>OpenRGB brands whose names contain a space; every other name splits at its first space.</summary>
    // The site's export-devices.mjs regex-parses this `= { "..." }` initializer; keep that shape.
    private static readonly string[] MultiWordVendors =
    {
        "Cooler Master", "Lian Li", "Turtle Beach", "Das Keyboard", "Red Square",
        "Dark Project", "Attack Shark", "Dream Cheeky", "Raspberry Pi", "FL ESPORTS",
    };

    internal static (string vendor, string model) SplitVendorModel(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ("", "");
        }
        foreach (var vendor in MultiWordVendors)
        {
            if (name.Length > vendor.Length && name[vendor.Length] == ' ' &&
                name.StartsWith(vendor, System.StringComparison.Ordinal))
            {
                return (vendor, name.Substring(vendor.Length + 1));
            }
        }
        var sp = name.IndexOf(' ');
        if (sp <= 0)
        {
            return ("", name);
        }
        return (name.Substring(0, sp), name.Substring(sp + 1));
    }

    /// <summary>
    /// The type OpenRGB declares wins, except where it is coarse (light, accessory,
    /// motherboard) and the name says something more specific. Rows the extractor could
    /// not resolve statically (runtime-typed controllers) use the device name; the
    /// controller class is the last resort. Mirrored in nexus-site
    /// scripts/export-devices.mjs (resolveCategory); change both together.
    /// </summary>
    internal static string ResolveCategory(string? name, string? controller, string? kind, string? openRgbType)
    {
        var n = name ?? "";
        if (openRgbType is not null && CategoryByOpenRgbType.TryGetValue(openRgbType, out var declared))
        {
            if (declared == "cooler")
            {
                return AioName.IsMatch(n) ? "aio" : "fan";
            }
            if (declared is "light" or "accessory" or "motherboard")
            {
                return SpecificCategoryFromName(n) ?? declared;
            }
            return declared;
        }
        return CategoryFromName(n) ?? SpecificCategoryFromName(n) ?? CategoryFromController(controller, kind);
    }

    private static readonly Dictionary<string, string> CategoryByOpenRgbType = new()
    {
        ["keyboard"] = "keyboard",
        ["keypad"] = "keyboard",
        ["mouse"] = "mouse",
        ["mousemat"] = "mousemat",
        ["headset"] = "headset",
        ["microphone"] = "headset",
        ["headset_stand"] = "headset stand",
        ["gpu"] = "gpu",
        ["dram"] = "memory",
        ["motherboard"] = "motherboard",
        ["monitor"] = "monitor",
        ["gamepad"] = "gamepad",
        ["case"] = "case",
        ["ledstrip"] = "light",
        ["light"] = "light",
        ["laptop"] = "laptop",
        ["storage"] = "storage",
        ["speaker"] = "speaker",
        ["accessory"] = "accessory",
        ["cooler"] = "cooler",
    };

    private static readonly Regex AioName = new(
        @"\b(AIO|Hydro|Kraken|Ryuo|Ryujin|LC|H\d{2,3}i|Galahad|Waterforce|Hanbo|Elite|Liquid)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AioStrictName = NameRule(
        @"\b(AIO|Hydro|Kraken|Ryuo|Ryujin|LC|Galahad|Waterforce|Hanbo)\b");

    // Fan hubs register as generic LED strips; the name says what they are.
    private static readonly Regex FanHubName = NameRule(@"\bFan\b|Smart Device");

    private static readonly Regex MonitorName = NameRule(@"\bOptix\b|\bMonitor\b(?! Light)");

    private static readonly Regex MotherboardName = NameRule(@"\bMotherboard\b");

    private static string? SpecificCategoryFromName(string name)
    {
        if (MonitorName.IsMatch(name))
        {
            return "monitor";
        }
        if (MotherboardName.IsMatch(name))
        {
            return "motherboard";
        }
        if (AioStrictName.IsMatch(name))
        {
            return "aio";
        }
        return FanHubName.IsMatch(name) ? "fan" : null;
    }

    // Ordered: the first match wins, so the specific shapes (mat, headset, light bar)
    // come before the broad ones.
    private static readonly (Regex Pattern, string Category)[] NameRules =
    {
        (NameRule(@"mouse ?pad|mouse ?mat|powerplay mat|\bMM\d{3}\b"), "mousemat"),
        (NameRule(@"headset|headphone|kraken (7|kitty)"), "headset"),
        (NameRule(@"light ?bar|lightboard|RGB HUB|kasasmart"), "light"),
        (NameRule(@"\bDial\b"), "accessory"),
        (NameRule(@"\bBloody MP\b"), "mousemat"),
        (NameRule(@"\bBloody W\d"), "mouse"),
        (NameRule(@"keyboard|\bK\d{2,3}\b|\bStrafe\b|keyrox"), "keyboard"),
        (NameRule(@"\bmouse\b|\b(Glaive|Harpoon|Ironclaw|Nightsword|Sabre|Scimitar|Aerox|Rival|Katar|Dark Core)\b|\bM\d{2}\b|\bG(203|303|403|502|703|900|903)\b"), "mouse"),
        (NameRule(@"\bST100\b"), "headset stand"),
        (NameRule(@"\bLegion\b|laptop"), "laptop"),
        (NameRule(@"receiver"), "accessory"),
    };

    private static Regex NameRule(string pattern) => new(
        pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? CategoryFromName(string name)
    {
        foreach (var (pattern, category) in NameRules)
        {
            if (pattern.IsMatch(name))
            {
                return category;
            }
        }
        return null;
    }

    private static string CategoryFromController(string? controller, string? kind)
    {
        var c = controller ?? "";
        if (c.Contains("Keyboard") || c.Contains("QMK"))
            return "keyboard";
        if (c.Contains("Mouse") && !c.Contains("Mousemat"))
            return "mouse";
        if (c.Contains("Mousemat"))
            return "mousemat";
        if (c.Contains("Headset") || c.Contains("Microphone"))
            return "headset";
        if (c.Contains("GPU"))
            return "gpu";
        if (c.Contains("DRAM") || c.Contains("Memory") || c.Contains("Vengeance") || c.Contains("Fury"))
            return "memory";
        if (c.Contains("Hydro") || c.Contains("Kraken") || c.Contains("AIO"))
            return "aio";
        if (c.Contains("Fan") || c.Contains("Riing") || c.Contains("Hue"))
            return "fan";
        if (c.Contains("Monitor") || c.Contains("Optix"))
            return "monitor";
        if (c.Contains("Motherboard") || c.Contains("Aura") || c.Contains("MysticLight") || c.Contains("Fusion") || c.Contains("Polychrome"))
            return "motherboard";
        if (c.Contains("Case"))
            return "case";
        if (c.Contains("Gamepad"))
            return "gamepad";
        if (c.Contains("LightStrip") || c.Contains("LEDStrip") || c.Contains("LightBar") || c.Contains("Wiz") || c.Contains("Hue") || c.Contains("Yeelight") || c.Contains("Nanoleaf") || c.Contains("LIFX") || c.Contains("Govee"))
            return "light";
        return kind switch
        {
            "i2c" => "motherboard",
            _ => "controller",
        };
    }
}

public sealed class OpenRgbSupportedDevicesFile
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("devices")] public List<OpenRgbSupportedDeviceEntry> Devices { get; set; } = new();
}

public sealed class OpenRgbSupportedDeviceEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("vid")] public string? Vid { get; set; }
    [JsonPropertyName("pid")] public string? Pid { get; set; }
    [JsonPropertyName("controller")] public string? Controller { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}
