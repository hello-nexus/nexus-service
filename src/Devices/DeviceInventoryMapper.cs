using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Peripherals.LianLiTl;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Persistence;

namespace Nexus.Service.Devices;

/// <summary>
/// Pure mappers from each source's cached state to inventory rows. Nothing here
/// touches hardware. Final <c>modelKey</c> list, one line per family:
/// <code>
/// lianli-wl:slv3-led            wireless SL V3 LED fan chain
/// lianli-wl:slv3-lcd            wireless SL V3 LCD fan chain
/// lianli-wl:tl-v2-led           wireless TL V2 LED fan chain
/// lianli-wl:tl-v2-lcd           wireless TL V2 LCD fan chain
/// lianli-wl:tl-flex             wireless TL Flex fan chain
/// lianli-wl:tl-flex-lcd         wireless TL Flex chain with screens (fans_type sub-index)
/// lianli-wl:sl-inf              wireless SL-Infinity fan chain
/// lianli-wl:sl-inf-flex         wireless SL-Infinity Flex fan chain
/// lianli-wl:sl-inf-flex-lcd     wireless SL-Infinity Flex chain with screens (fans_type sub-index)
/// lianli-wl:cl                  wireless CL fan chain
/// lianli-wl:cl-v2               wireless CL V2 fan chain
/// lianli-wl:rl120               wireless RL120 fan chain
/// lianli-wl:sl-v4               wireless SL V4 fan chain
/// lianli-wl:p28-v2              wireless P28 V2 fan chain
/// lianli:hydroshift2-lcd-c      HydroShift II LCD-C AIO (wireless dev type 10)
/// lianli:hydroshift2-lcd-s      HydroShift II LCD-S AIO (wireless dev type 11, or USB)
/// lianli:hydroshift-lcd-360s    HydroShift LCD 360S over USB
/// lianli:hydroshift-lcd-360r    HydroShift LCD 360R over USB
/// lianli:hydroshift-lcd-360tl   HydroShift LCD 360TL over USB
/// lianli:galahad2               Galahad II Trinity
/// lianli:galahad2-perf          Galahad II Trinity Performance
/// lianli:galahad2-lcd           Galahad II Vision LCD
/// lianli-uni:sl                 Uni hub SL (also the plain Uni Hub)
/// lianli-uni:al                 Uni hub AL
/// lianli-uni:sl-v2              Uni hub SL V2
/// lianli-uni:al-v2              Uni hub AL V2
/// lianli-uni:sl-inf             Uni hub SL-Infinity
/// lianli-tl:tl-fan              Uni Fan TL wired controller
/// nzxt:kraken-elite-v2          Kraken Elite V2
/// nzxt:kraken-plus-v2           Kraken Plus V2
/// nzxt:kraken-elite-2023        Kraken Elite 2023
/// nzxt:kraken-2023              Kraken 2023
/// nzxt:kraken-z3                Kraken Z3
/// nzxt:kraken-x3                Kraken X3
/// nzxt:rgb-fan-controller       RGB and Fan Controller
/// corsair-link:TYPE:MODEL       iCUE LINK device, numeric (type, model) from the hub enumeration
/// corsair:xc7-elite-lcd         XC7 RGB Elite LCD
/// corsair:elite-capellix-lcd    Elite Capellix LCD
/// corsair:nautilus-lcd-cap      Nautilus LCD Cap
/// idcooling:fx-lcd              ID-Cooling FX-LCD
/// asrock:lcd                    ASRock LCD
/// asus:rog-strix-slc4-lcd       ROG Strix SLC IV LCD
/// asus:ryujin-lcd               ROG Ryujin LCD
/// hyte:thicc-q60                THICC Q60
/// hyte:thicc-q80                THICC Q80
/// </code>
/// </summary>
public static class DeviceInventoryMapper
{
    private const int LianLiVid = 0x0416;
    private const int LianLiUniVid = 0x0CF2;
    private const int NzxtVid = 0x1E71;
    private const int CorsairVid = 0x1B1C;

    private static readonly Regex s_radiator = new(@"\bTITAN (240|280|360|420)\b", RegexOptions.CultureInvariant);

    public static List<SystemDeviceDto> FromWireless(Slv3State state)
    {
        var rows = new List<SystemDeviceDto>();
        foreach (var fan in state.Fans)
        {
            // Unbound chains may belong to a neighbor, and wired-owned ones list under their own handler.
            if (!fan.BoundToUs || fan.WiredOwned || string.IsNullOrEmpty(fan.Mac))
            {
                continue;
            }
            var devType = (byte)fan.DevType;
            var key = "lianli-wireless:" + fan.Mac.ToUpperInvariant();
            if (Slv3Protocol.IsHydroShiftDevType(devType))
            {
                var square = Slv3Protocol.IsHydroShiftSquare(devType);
                rows.Add(new SystemDeviceDto
                {
                    Key = key,
                    ModelKey = square ? "lianli:hydroshift2-lcd-s" : "lianli:hydroshift2-lcd-c",
                    Category = SystemDeviceCategories.Cooler,
                    Vendor = "Lian Li",
                    Model = square ? "HydroShift II LCD-S" : "HydroShift II LCD-C",
                    Family = "hydroshift2",
                });
                continue;
            }
            var family = Slv3Protocol.ClassifyFanFamily((byte)fan.FanType);
            var (suffix, display) = WirelessFamily(family);
            if (fan.FanCount <= 0 || suffix.Length == 0)
            {
                continue;
            }
            // Only the Flex fans encode screen presence in the sub-index; the rest of the sub-index meaning is not in the protocol source.
            if (family is Slv3FanFamily.SlInfFlex or Slv3FanFamily.TlFlex && Slv3FlexLcd.IsLcdFanType(fan.FanType))
            {
                suffix += "-lcd";
                display += " LCD";
            }
            rows.Add(new SystemDeviceDto
            {
                Key = key,
                ModelKey = "lianli-wl:" + suffix,
                Category = SystemDeviceCategories.Fan,
                Vendor = "Lian Li",
                Model = display,
                Family = suffix,
                Count = fan.FanCount,
                CountSource = SystemDeviceCountSources.Detected,
            });
        }
        return rows;
    }

    private static (string Suffix, string Display) WirelessFamily(Slv3FanFamily family) => family switch
    {
        Slv3FanFamily.Slv3Led => ("slv3-led", "Uni Fan SL V3 LED"),
        Slv3FanFamily.Slv3Lcd => ("slv3-lcd", "Uni Fan SL V3 LCD"),
        Slv3FanFamily.Tlv2Led => ("tl-v2-led", "Uni Fan TL V2 LED"),
        Slv3FanFamily.Tlv2Lcd => ("tl-v2-lcd", "Uni Fan TL V2 LCD"),
        Slv3FanFamily.SlInf => ("sl-inf", "Uni Fan SL-Infinity"),
        Slv3FanFamily.Cl => ("cl", "Uni Fan CL"),
        Slv3FanFamily.Rl120 => ("rl120", "Uni Fan RL120"),
        Slv3FanFamily.SlInfFlex => ("sl-inf-flex", "Uni Fan SL-Infinity Flex"),
        Slv3FanFamily.TlFlex => ("tl-flex", "Uni Fan TL Flex"),
        Slv3FanFamily.SlV4 => ("sl-v4", "Uni Fan SL V4"),
        Slv3FanFamily.P28V2 => ("p28-v2", "Uni Fan P28 V2"),
        Slv3FanFamily.ClV2 => ("cl-v2", "Uni Fan CL V2"),
        _ => ("", ""),
    };

    /// <summary>One wired Uni hub. The fan count is the per-port setting, because the hub cannot report how many fans are chained.</summary>
    public static SystemDeviceDto? FromUniHub(string hubId, bool connected, LianLiFanProfile profile, LianLiSettings fans)
    {
        if (!connected)
        {
            return null;
        }
        var suffix = profile.Family switch
        {
            LianLiFanFamily.Sl => "sl",
            LianLiFanFamily.Al => "al",
            LianLiFanFamily.SlV2 => "sl-v2",
            LianLiFanFamily.AlV2 => "al-v2",
            LianLiFanFamily.SlInfinity => "sl-inf",
            _ => null,
        };
        if (suffix is null)
        {
            return null;
        }
        var max = profile.MaxFans > 0 ? profile.MaxFans : int.MaxValue;
        var total = 0;
        var defaults = new LianLiSettings();
        var edited = false;
        for (var port = 0; port < LianLiProtocol.PortCount; port++)
        {
            total += Math.Clamp(fans.GetFans(port), 0, max);
            edited |= fans.GetFans(port) != defaults.GetFans(port);
        }
        return new SystemDeviceDto
        {
            Key = hubId + ":" + profile.ProductId.ToString("x4", CultureInfo.InvariantCulture),
            ModelKey = "lianli-uni:" + suffix,
            Category = SystemDeviceCategories.Fan,
            Vendor = "Lian Li",
            Model = string.IsNullOrEmpty(profile.ModelName) ? "Uni Fan" : profile.ModelName,
            Vid = LianLiUniVid,
            Pid = profile.ProductId,
            Family = suffix,
            // Untouched settings read the defaults, which say nothing about the real chain.
            Count = edited ? total : null,
            CountSource = edited ? SystemDeviceCountSources.User : null,
        };
    }

    public static SystemDeviceDto? FromTlHub(bool connected, TlHubSnapshot snapshot)
    {
        if (!connected || snapshot.ChannelCount == 0)
        {
            return null;
        }
        return new SystemDeviceDto
        {
            Key = "lianli-tl",
            ModelKey = "lianli-tl:tl-fan",
            Category = SystemDeviceCategories.Fan,
            Vendor = "Lian Li",
            Model = "Uni Fan TL",
            Vid = TlFanProtocol.VendorId,
            Count = snapshot.ChannelCount,
            CountSource = SystemDeviceCountSources.Detected,
        };
    }

    public static SystemDeviceDto? FromGalahad2(bool connected, int productId)
    {
        if (!connected)
        {
            return null;
        }
        var perf = productId == 0x7371;
        return new SystemDeviceDto
        {
            Key = "lianli-aio:" + productId.ToString("x4", CultureInfo.InvariantCulture),
            ModelKey = perf ? "lianli:galahad2-perf" : "lianli:galahad2",
            Category = SystemDeviceCategories.Cooler,
            Vendor = "Lian Li",
            Model = perf ? "Galahad II Trinity Performance" : "Galahad II Trinity",
            Vid = LianLiVid,
            Pid = productId == 0 ? null : productId,
        };
    }

    public static SystemDeviceDto? FromKraken(bool connected, KrakenModel model, string? serial)
    {
        if (!connected)
        {
            return null;
        }
        var suffix = model.ProductId switch
        {
            0x3012 => "elite-v2",
            0x3014 => "plus-v2",
            0x300C => "elite-2023",
            0x300E => "2023",
            0x3008 => "z3",
            0x2007 or 0x2014 => "x3",
            _ => null,
        };
        if (suffix is null)
        {
            return null;
        }
        return new SystemDeviceDto
        {
            Key = "nzxt-kraken:" + (string.IsNullOrEmpty(serial) ? model.ProductId.ToString("x4", CultureInfo.InvariantCulture) : serial),
            ModelKey = "nzxt:kraken-" + suffix,
            Category = SystemDeviceCategories.Cooler,
            Vendor = "NZXT",
            Model = model.Name,
            Vid = NzxtVid,
            Pid = model.ProductId,
        };
    }

    /// <summary>The controller itself, with the number of headers that report a fan plugged in.</summary>
    public static SystemDeviceDto? FromNzxtFanController(bool connected, IReadOnlyList<NzxtFanReading> fans)
    {
        if (!connected)
        {
            return null;
        }
        var plugged = 0;
        foreach (var fan in fans)
        {
            if (fan.Mode != NzxtFanControllerProtocol.ModeNone)
            {
                plugged++;
            }
        }
        return new SystemDeviceDto
        {
            Key = NzxtFanControllerHub.DeviceId,
            ModelKey = "nzxt:rgb-fan-controller",
            Category = SystemDeviceCategories.Controller,
            Vendor = "NZXT",
            Model = NzxtFanControllerHub.ProductName,
            Vid = NzxtVid,
            Count = plugged,
            CountSource = SystemDeviceCountSources.Detected,
        };
    }

    /// <summary>
    /// iCUE LINK devices. <c>partOf</c> stays unset: the chain enumeration does not say
    /// which fans belong to an AIO.
    /// </summary>
    public static List<SystemDeviceDto> FromCorsairLink(string hubId, CorsairLinkState state)
    {
        var rows = new List<SystemDeviceDto>();
        if (!state.IsConnected)
        {
            return rows;
        }
        foreach (var device in state.Devices)
        {
            string category;
            switch (device.Class)
            {
                case CorsairLinkClass.Fan:
                    category = SystemDeviceCategories.Fan;
                    break;
                case CorsairLinkClass.Aio:
                case CorsairLinkClass.CpuBlock:
                case CorsairLinkClass.GpuBlock:
                    category = SystemDeviceCategories.Cooler;
                    break;
                case CorsairLinkClass.Pump:
                    // The LCD cap is a screen on a pump, not a cooler of its own.
                    category = device.Type == 6 ? SystemDeviceCategories.Lighting : SystemDeviceCategories.Cooler;
                    break;
                case CorsairLinkClass.Adapter:
                    category = SystemDeviceCategories.Controller;
                    break;
                case CorsairLinkClass.Case:
                    category = SystemDeviceCategories.Lighting;
                    break;
                default:
                    continue;
            }
            var radiator = device.Class == CorsairLinkClass.Aio && s_radiator.Match(device.Name) is { Success: true } m
                ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
                : (int?)null;
            // The RX360 II is one device that carries all three radiator fans.
            var fanCount = device.Class != CorsairLinkClass.Fan ? 0 : device.Type == 26 && device.Model == 0 ? 3 : 1;
            rows.Add(new SystemDeviceDto
            {
                Key = "corsair-link:" + (string.IsNullOrEmpty(device.Serial) ? hubId + ":ch" + device.Channel.ToString(CultureInfo.InvariantCulture) : device.Serial),
                ModelKey = "corsair-link:" + device.Type.ToString(CultureInfo.InvariantCulture) + ":" + device.Model.ToString(CultureInfo.InvariantCulture),
                Category = category,
                Vendor = "Corsair",
                Model = device.Name,
                Vid = CorsairVid,
                RadiatorMm = radiator,
                Count = fanCount > 0 ? fanCount : null,
                CountSource = fanCount > 0 ? SystemDeviceCountSources.Detected : null,
            });
        }
        return rows;
    }

    /// <summary>A connected JPEG-over-HID LCD. <paramref name="productId"/> is the attached unit's id.</summary>
    public static SystemDeviceDto? FromJpegPanel(JpegPanelModel model, bool connected, int productId, string? serial)
    {
        if (!connected)
        {
            return null;
        }
        var (modelKey, display, vendor) = model.HandlerId switch
        {
            "lianli-galahad2-lcd" => ("lianli:galahad2-lcd", "Galahad II Vision LCD", "Lian Li"),
            "lianli-hydroshift-lcd" => productId switch
            {
                0x7398 => ("lianli:hydroshift-lcd-360s", "HydroShift LCD 360S", "Lian Li"),
                0x7399 => ("lianli:hydroshift-lcd-360r", "HydroShift LCD 360R", "Lian Li"),
                0x739A => ("lianli:hydroshift-lcd-360tl", "HydroShift LCD 360TL", "Lian Li"),
                _ => ("", "", ""),
            },
            "corsair-xc7-lcd" => ("corsair:xc7-elite-lcd", "XC7 RGB Elite LCD", "Corsair"),
            "corsair-capellix-lcd" => ("corsair:elite-capellix-lcd", "Elite Capellix LCD", "Corsair"),
            "corsair-nautilus-lcd" => ("corsair:nautilus-lcd-cap", "Nautilus LCD Cap", "Corsair"),
            "idcooling-fx-lcd" => ("idcooling:fx-lcd", "FX-LCD", "ID-Cooling"),
            "asrock-lcd" => ("asrock:lcd", "ASRock LCD", "ASRock"),
            "asus-slc4-lcd" => ("asus:rog-strix-slc4-lcd", "ROG Strix SLC IV LCD", "ASUS"),
            _ => ("", "", ""),
        };
        if (modelKey.Length == 0)
        {
            return null;
        }
        return new SystemDeviceDto
        {
            Key = model.HandlerId + ":" + (string.IsNullOrEmpty(serial) ? productId.ToString("x4", CultureInfo.InvariantCulture) : serial),
            ModelKey = modelKey,
            Category = SystemDeviceCategories.Cooler,
            Vendor = vendor,
            Model = display,
            Vid = model.VendorId,
            Pid = productId == 0 ? null : productId,
        };
    }

    public static SystemDeviceDto? FromRyujin(string handlerId, bool connected, int vendorId, int productId, string? serial)
    {
        if (!connected || handlerId != "asus-ryujin-lcd")
        {
            return null;
        }
        return new SystemDeviceDto
        {
            Key = handlerId + ":" + (string.IsNullOrEmpty(serial) ? productId.ToString("x4", CultureInfo.InvariantCulture) : serial),
            ModelKey = "asus:ryujin-lcd",
            Category = SystemDeviceCategories.Cooler,
            Vendor = "ASUS",
            Model = "ROG Ryujin LCD",
            Vid = vendorId,
            Pid = productId,
        };
    }

    /// <summary>A HydroShift II on its own USB link, which reads as LCD-C when round and LCD-S when square.</summary>
    public static SystemDeviceDto? FromHydroShift2Usb(bool connected, bool round, int vendorId, string? serial)
    {
        if (!connected)
        {
            return null;
        }
        var pid = round ? HydroShift2Protocol.ProductIdCircle : HydroShift2Protocol.ProductIdSquare;
        return new SystemDeviceDto
        {
            Key = HydroShift2LcdDriver.Id + ":" + (string.IsNullOrEmpty(serial) ? pid.ToString("x4", CultureInfo.InvariantCulture) : serial),
            ModelKey = round ? "lianli:hydroshift2-lcd-c" : "lianli:hydroshift2-lcd-s",
            Category = SystemDeviceCategories.Cooler,
            Vendor = "Lian Li",
            Model = round ? "HydroShift II LCD-C" : "HydroShift II LCD-S",
            Family = "hydroshift2",
            Vid = vendorId,
            Pid = pid,
        };
    }

    public static SystemDeviceDto? FromQSeries(bool connected, string variant, string serial)
    {
        if (!connected || (variant != QSeriesCoolerProtocol.VariantQ60 && variant != QSeriesCoolerProtocol.VariantQ80))
        {
            return null;
        }
        return new SystemDeviceDto
        {
            Key = "qseries:" + (string.IsNullOrEmpty(serial) ? variant : serial),
            ModelKey = "hyte:thicc-" + variant,
            Category = SystemDeviceCategories.Cooler,
            Vendor = "HYTE",
            Model = variant == QSeriesCoolerProtocol.VariantQ80 ? "THICC Q80" : "THICC Q60",
        };
    }
}
